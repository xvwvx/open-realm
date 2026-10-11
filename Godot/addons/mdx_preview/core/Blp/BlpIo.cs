using System;

namespace MdxPreview.Core.Blp
{
    /// <summary>
    /// Little-endian helpers shared by the BLP decoders. The C reference reads
    /// its headers with plain struct memcpy on little-endian hosts
    /// (renderer/r_blp1.c, renderer/r_blp2.c), so all multi-byte fields are LE.
    /// </summary>
    internal static class BlpIo
    {
        internal static uint U32(byte[] data, int offset)
            => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

        internal static ushort U16(byte[] data, int offset)
            => (ushort)(data[offset] | (data[offset + 1] << 8));

        /// <summary>Largest accepted mip 0 edge, matching tools/blp2jpg.c:266.</summary>
        internal const uint MaxDimension = 16384;

        /// <summary>
        /// Mirrors the width/height shift used by <c>blp1_width()</c> /
        /// <c>blp2_width()</c> (renderer/r_blp1.c:76, renderer/r_blp2.c:31).
        /// </summary>
        internal static int MipDimension(uint baseDimension, int level)
            => (int)(baseDimension >> level);

        internal static void ValidateDimensions(string formatName, uint width, uint height)
        {
            if (width == 0 || height == 0)
                throw new BlpException($"{formatName}: invalid texture size {width}x{height}.");
            if (width > MaxDimension || height > MaxDimension)
                throw new BlpException($"{formatName}: texture size {width}x{height} exceeds the {MaxDimension} pixel limit.");
        }

        /// <summary>
        /// Validates one mip slice and returns its offset. Mirrors the r_blp1.c
        /// / r_blp2.c assumption that <c>offset + length</c> lies inside the
        /// file, but fails loudly instead of reading out of bounds.
        /// </summary>
        internal static int CheckMipSlice(byte[] data, uint offset, uint length, int mipLevel, string formatName)
        {
            long end = (long)offset + length;
            if (offset > (uint)data.Length || end > data.Length)
            {
                throw new BlpException(
                    $"{formatName}: mip {mipLevel} data is out of bounds " +
                    $"(offset={offset}, length={length}, fileSize={data.Length}).");
            }
            return (int)offset;
        }

        internal static int PaletteAlphaBytes(int pixelCount, uint alphaBits)
        {
            switch (alphaBits)
            {
                case 0: return 0;
                case 1: return (pixelCount + 7) / 8;
                case 4: return (pixelCount + 1) / 2;
                case 8: return pixelCount;
                default:
                    throw new BlpException(
                        $"Unsupported paletted alpha depth {alphaBits} (expected 0, 1, 4 or 8; " +
                        "the C reference silently falls back to \"no alpha\" via blp1_format()/blp2_format()).");
            }
        }

        /// <summary>
        /// Expands the 11/6/5-bit DXT colour to 8 bits exactly like
        /// <c>blp2_dxt_color()</c> (renderer/r_blp2.c:228).
        /// </summary>
        internal static void Color565(ushort color, out byte r, out byte g, out byte b)
        {
            r = (byte)(((color >> 11) & 0x1F) * 255 / 31);
            g = (byte)(((color >> 5) & 0x3F) * 255 / 63);
            b = (byte)((color & 0x1F) * 255 / 31);
        }

        /// <summary>
        /// Decodes one paletted mip level into top-down RGBA8
        /// (<c>blp1_convert_paletted_*</c> in renderer/r_blp1.c:208-284 and
        /// <c>blp2_convert_paletted_*</c> in renderer/r_blp2.c:149-226).
        /// The palette is stored as BGRA in the file, the alpha plane follows
        /// the index plane.
        /// </summary>
        internal static void DecodePalettedMip(
            byte[] data, int paletteOffset, int mipOffset, uint mipLength,
            int width, int height, uint alphaBits, int mipLevel, string formatName, byte[] dst)
        {
            int pixelCount = width * height;
            int alphaBytes = PaletteAlphaBytes(pixelCount, alphaBits);

            if (mipLength < (ulong)(pixelCount + alphaBytes))
            {
                throw new BlpException(
                    $"{formatName}: mip {mipLevel} ({width}x{height}) is truncated " +
                    $"(need {pixelCount + alphaBytes} bytes for {pixelCount} indices + {alphaBytes} alpha bytes, " +
                    $"have {mipLength}).");
            }

            int indexOffset = mipOffset;
            int alphaOffset = mipOffset + pixelCount;

            for (int i = 0; i < pixelCount; i++)
            {
                int entry = paletteOffset + data[indexOffset + i] * 4;
                dst[i * 4 + 0] = data[entry + 2];
                dst[i * 4 + 1] = data[entry + 1];
                dst[i * 4 + 2] = data[entry + 0];
            }

            ExpandPalettedAlpha(data, alphaOffset, pixelCount, alphaBits, dst, 4);
        }

        /// <summary>
        /// Expands the shared BLP1/BLP2 paletted alpha plane into RGBA alpha
        /// bytes written every <paramref name="dstStride"/> bytes.
        /// </summary>
        internal static void ExpandPalettedAlpha(
            byte[] src, int alphaOffset, int pixelCount, uint alphaBits, byte[] dst, int dstStride)
        {
            switch (alphaBits)
            {
                case 0:
                    for (int i = 0; i < pixelCount; i++)
                        dst[i * dstStride + 3] = 255;
                    break;

                // r_blp1.c:234 / r_blp2.c:189 — LSB-first bit stream, one byte per 8 pixels.
                case 1:
                {
                    int counter = 0;
                    int alpha = alphaOffset;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        dst[i * dstStride + 3] = (byte)((src[alpha] & (1 << counter)) != 0 ? 0xFF : 0x00);
                        if (++counter == 8)
                        {
                            alpha++;
                            counter = 0;
                        }
                    }
                    break;
                }

                // r_blp1.c:257 / r_blp2.c:212 — low nibble first, expanded as (a << 4) | a.
                case 4:
                {
                    int counter = 0;
                    int alpha = alphaOffset;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        byte a = (byte)((src[alpha] >> counter) & 0xF);
                        dst[i * dstStride + 3] = (byte)((a << 4) | a);
                        counter += 4;
                        if (counter == 8)
                        {
                            alpha++;
                            counter = 0;
                        }
                    }
                    break;
                }

                // r_blp1.c:216 / r_blp2.c:157 — one alpha byte per pixel.
                case 8:
                    for (int i = 0; i < pixelCount; i++)
                        dst[i * dstStride + 3] = src[alphaOffset + i];
                    break;

                default:
                    throw new BlpException($"Unsupported paletted alpha depth {alphaBits} (expected 0, 1, 4 or 8).");
            }
        }

        /// <summary>
        /// Counts present mip levels the way both C loaders do: walk the offset
        /// table until a zero entry (renderer/r_blp1.c:138, renderer/r_blp2.c:130).
        /// </summary>
        internal static int CountMipLevels(uint[] offsets)
        {
            int count = 0;
            while (count < offsets.Length && offsets[count] != 0)
                count++;
            return count;
        }

        /// <summary>
        /// Copies the alpha bytes out of an RGBA8 buffer. Used by
        /// <c>BlpImage.AlphaChannel</c>/<c>MipAlpha</c> so a caller can work with
        /// the alpha plane on its own without re-reading the interleaved buffer.
        /// </summary>
        internal static byte[] ExtractAlphaPlane(byte[] rgba, int pixelCount)
        {
            var alpha = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++)
                alpha[i] = rgba[i * 4 + 3];
            return alpha;
        }

        /// <summary>
        /// Reads the component count (SOF <c>Nf</c>) of an assembled JPEG stream
        /// by walking its markers. This is pure header parsing — no entropy
        /// decoding — and exists so callers can detect the Warcraft III
        /// "RGB + alpha in one 4-component JPEG" layout, which stock decoders
        /// (including Godot's libjpeg-turbo based <c>LoadJpgFromBuffer</c>)
        /// reject as CMYK/YCCK. Returns 0 when no SOF marker is found.
        /// </summary>
        internal static int ReadJpegBandCount(byte[] jpeg, int offset, int length)
        {
            int end = offset + length;
            int p = offset;

            if (p + 1 < end && jpeg[p] == 0xFF && jpeg[p + 1] == 0xD8)
                p += 2; // SOI

            while (p + 3 < end)
            {
                if (jpeg[p] != 0xFF)
                {
                    p++;
                    continue;
                }

                // Skip fill bytes (FF FF ...) and stand-alone markers.
                while (p < end && jpeg[p] == 0xFF) p++;
                if (p >= end) break;
                byte marker = jpeg[p++];

                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    continue;
                if (marker == 0xD9 || marker == 0xDA)
                    break; // EOI or start of scan: no SOF can follow.

                if (p + 1 >= end) break;
                int segmentLength = (jpeg[p] << 8) | jpeg[p + 1];

                // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC).
                bool isSof = marker >= 0xC0 && marker <= 0xCF &&
                             marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof)
                {
                    // marker, length(2), precision(1), height(2), width(2), Nf(1)
                    int nfIndex = p + 7;
                    return nfIndex < end ? jpeg[nfIndex] : 0;
                }

                if (segmentLength < 2) break;
                p += segmentLength;
            }

            return 0;
        }
    }
}
