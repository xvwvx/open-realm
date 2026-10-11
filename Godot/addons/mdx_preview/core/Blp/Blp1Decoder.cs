using System;

namespace MdxPreview.Core.Blp
{
    /// <summary>
    /// BLP1 ("BLP1") loader, ported from renderer/r_blp1.c and the header
    /// description in renderer/r_blp.h.
    ///
    /// <code>
    /// offset  size  field
    /// 0x00    4     magic "BLP1"
    /// 0x04    4     type       0 = shared-header JPEG, non-zero = paletted
    /// 0x08    4     alphaBits  0, 1, 4 or 8
    /// 0x0C    4     width
    /// 0x10    4     height
    /// 0x14    4     extra      (4 or 5, unused)
    /// 0x18    4     hasMipmaps (unused: the offset table decides)
    /// 0x1C    64    offsets[16]
    /// 0x5C    64    lengths[16]
    /// 0x9C    1024  palette[256] BGRA  -- or: uint32 jpegHeaderSize + jpegHeader
    /// </code>
    ///
    /// JPEG files keep the 1024-byte palette slot for a shared JPEG header whose
    /// last two bytes are the SOF marker 0xFF 0xC0; each mip slice starts with
    /// the SOF payload (per-mip dimensions), followed by SOS, entropy data and
    /// EOI. r_blp1.c:170 therefore simply concatenates the shared header with
    /// the mip slice, and so does this port.
    /// </summary>
    internal static class Blp1Decoder
    {
        /// <summary>"BLP1" as a little-endian uint32.</summary>
        internal const uint Magic = 0x31504C42;

        internal const int HeaderSize = 0x9C;
        internal const int OffsetsOffset = 0x1C;
        internal const int LengthsOffset = 0x5C;
        internal const int DataOffset = 0x9C;       // palette, or jpegHeaderSize
        internal const int JpegHeaderOffset = 0xA0; // jpegHeaderSize + 4
        internal const int PaletteBytes = 256 * 4;

        internal static BlpImage Decode(byte[] data)
        {
            int fileSize = data.Length;
            if (fileSize < HeaderSize)
                throw new BlpException($"BLP1: file too small ({fileSize} bytes, header needs {HeaderSize}).");

            uint type = BlpIo.U32(data, 4);
            uint alphaBits = BlpIo.U32(data, 8);
            uint width = BlpIo.U32(data, 12);
            uint height = BlpIo.U32(data, 16);

            var offsets = new uint[16];
            var lengths = new uint[16];
            for (int i = 0; i < 16; i++)
            {
                offsets[i] = BlpIo.U32(data, OffsetsOffset + i * 4);
                lengths[i] = BlpIo.U32(data, LengthsOffset + i * 4);
            }

            int mipCount = BlpIo.CountMipLevels(offsets);
            if (mipCount == 0)
                throw new BlpException("BLP1: no mip levels present (every offset entry is zero).");

            BlpIo.ValidateDimensions("BLP1", width, height);

            var image = new BlpImage
            {
                Version = BlpVersion.Blp1,
                Width = (int)width,
                Height = (int)height,
                MipCount = mipCount,
                RawEncoding = type,
                RawAlphaDepth = alphaBits,
                RawAlphaEncoding = 0, // BLP1 has no alphaEncoding field (r_blp1.c:14).
            };

            if (type == 0)
            {
                DecodeJpeg(data, image, offsets, lengths, mipCount);
            }
            else
            {
                DecodePaletted(data, image, offsets, lengths, mipCount, alphaBits);
            }

            return image;
        }

        /// <summary>
        /// r_blp1.c:170-206: libjpeg-style shared header + mip entropy data.
        /// The core layer only reassembles the stream; the Godot layer hands it
        /// to Image.LoadJpgFromBuffer.
        /// </summary>
        static void DecodeJpeg(byte[] data, BlpImage image, uint[] offsets, uint[] lengths, int mipCount)
        {
            uint jpegHeaderSize = BlpIo.U32(data, DataOffset);
            long jpegHeaderEnd = JpegHeaderOffset + (long)jpegHeaderSize;
            if (jpegHeaderEnd > data.Length)
            {
                throw new BlpException(
                    $"BLP1: shared JPEG header is out of bounds " +
                    $"(jpegHeaderSize={jpegHeaderSize}, fileSize={data.Length}).");
            }

            int mip0Offset = BlpIo.CheckMipSlice(data, offsets[0], lengths[0], 0, "BLP1");

            var jpeg = new byte[(int)jpegHeaderSize + (int)lengths[0]];
            if (jpegHeaderSize > 0)
                Buffer.BlockCopy(data, JpegHeaderOffset, jpeg, 0, (int)jpegHeaderSize);
            Buffer.BlockCopy(data, mip0Offset, jpeg, (int)jpegHeaderSize, (int)lengths[0]);

            image.Kind = BlpPayloadKind.Jpeg;
            image.JpegData = jpeg;
            // Diagnostics: the SOF of a Warcraft III BLP1 JPEG declares 4
            // components, the 4th being alpha. Stock JPEG decoders — Godot's
            // LoadJpgFromBuffer included — classify such a stream as CMYK/YCCK
            // and refuse it, which is why the 4-band case is decoded here.
            image.JpegBandCount = BlpIo.ReadJpegBandCount(jpeg, 0, jpeg.Length);
            image.MipRgba = new byte[mipCount][];

            if (image.JpegBandCount == 4)
            {
                // Warcraft III layout (raw B,G,R,A bands in one stream: baseline
                // SOF0/SOF1 or progressive SOF2), decoded byte-identically to
                // r_blp1.c's patched stb_image.
                byte[] rgba = JpegWarcraft3Decoder.DecodeWarcraft3Rgba(
                    jpeg, 0, jpeg.Length, out int jpegWidth, out int jpegHeight);

                if (jpegWidth != image.Width || jpegHeight != image.Height)
                {
                    throw new BlpException(
                        $"BLP1: JPEG payload is {jpegWidth}x{jpegHeight} but the header declares " +
                        $"{image.Width}x{image.Height} for mip 0.");
                }

                image.Kind = BlpPayloadKind.Rgba8;
                image.Rgba = rgba;
                image.MipRgba[0] = rgba;
            }
            else if (image.JpegBandCount == 3)
            {
                // Ordinary YCbCr JPEG: Godot's Image.LoadJpgFromBuffer handles it.
                // Lower mips stay null (only mip 0 is assembled, as before).
            }
            else if (image.JpegBandCount == 0)
            {
                // No SOF marker could be located. Seen in the wild: assets whose
                // frame header segment has a corrupted marker byte (0xDE instead of
                // 0xC0/0xC2) — stb_image rejects them with "unknown marker", so the
                // C reference cannot render them either.
                throw new BlpException(
                    "BLP1: JPEG payload has no usable SOF frame header (0 components detected); " +
                    "the stream uses an unsupported segment or is corrupted " +
                    "(stb_image/renderer/r_blp1.c rejects it too).");
            }
            else
            {
                throw new BlpException(
                    $"BLP1: JPEG payload declares {image.JpegBandCount} component(s); " +
                    "expected 3 (YCbCr, left to Godot) or 4 (Warcraft III raw bands).");
            }
        }

        /// <summary>r_blp1.c:208-284.</summary>
        static void DecodePaletted(
            byte[] data, BlpImage image, uint[] offsets, uint[] lengths, int mipCount, uint alphaBits)
        {
            if (data.Length < DataOffset + PaletteBytes)
            {
                throw new BlpException(
                    $"BLP1: palette is out of bounds (file has {data.Length} bytes, " +
                    $"palette needs {DataOffset + PaletteBytes}).");
            }

            // Fail before allocating when the alpha plane is not a known depth.
            BlpIo.PaletteAlphaBytes(1, alphaBits);

            var mips = new byte[mipCount][];
            for (int level = 0; level < mipCount; level++)
            {
                int offset = BlpIo.CheckMipSlice(data, offsets[level], lengths[level], level, "BLP1");

                int width = BlpIo.MipDimension((uint)image.Width, level);
                int height = BlpIo.MipDimension((uint)image.Height, level);
                if (width < 1 || height < 1)
                {
                    // Real Warcraft III files keep a full 16-entry mip table for
                    // non-square textures, so 512x128 declares levels 8 and 9
                    // whose shifted size is 2x0 / 1x0. The C loader decodes zero
                    // pixels for them and the renderer drops the upload
                    // (renderer/r_texture.c:275 "if (!mip->width || !mip->height) return;").
                    mips[level] = null;
                    continue;
                }

                var dst = new byte[width * height * 4];
                BlpIo.DecodePalettedMip(
                    data, DataOffset, offset, lengths[level], width, height, alphaBits, level, "BLP1", dst);
                mips[level] = dst;
            }

            image.Kind = BlpPayloadKind.Rgba8;
            image.Rgba = mips[0];
            image.MipRgba = mips;
        }
    }
}
