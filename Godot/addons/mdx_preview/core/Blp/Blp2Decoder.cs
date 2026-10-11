using System;

namespace MdxPreview.Core.Blp
{
    /// <summary>
    /// BLP2 ("BLP2", World of Warcraft / later Warcraft III textures) loader,
    /// ported from renderer/r_blp2.c and the encoding enums in renderer/r_blp.h.
    ///
    /// <code>
    /// offset  size  field
    /// 0x00    4     magic "BLP2"
    /// 0x04    4     type         0 = JPEG (unsupported here, see below)
    /// 0x08    1     encoding     1 = paletted, 2 = DXT, 3 = raw BGRA
    /// 0x09    1     alphaDepth   0, 1, 4 or 8
    /// 0x0A    1     alphaEncoding 0 = DXT1, 1 = DXT3, 7 = DXT5
    /// 0x0B    1     hasMipLevels
    /// 0x0C    4     width
    /// 0x10    4     height
    /// 0x14    64    offsets[16]
    /// 0x54    64    lengths[16]
    /// 0x94    1024  palette[256] BGRA
    /// </code>
    /// </summary>
    internal static class Blp2Decoder
    {
        /// <summary>"BLP2" as a little-endian uint32.</summary>
        internal const uint Magic = 0x32504C42;

        internal const int HeaderSize = 0x494;      // 1172
        internal const int OffsetsOffset = 0x14;
        internal const int LengthsOffset = 0x54;
        internal const int PaletteOffset = 0x94;

        internal const int EncodingPaletted = 1;
        internal const int EncodingDxt = 2;
        internal const int EncodingRawBgra = 3;

        internal static BlpImage Decode(byte[] data)
        {
            int fileSize = data.Length;
            if (fileSize < HeaderSize)
                throw new BlpException($"BLP2: file too small ({fileSize} bytes, header needs {HeaderSize}).");

            uint type = BlpIo.U32(data, 4);
            byte encoding = data[8];
            byte alphaDepth = data[9];
            byte alphaEncoding = data[10];
            byte hasMipLevels = data[11];
            uint width = BlpIo.U32(data, 12);
            uint height = BlpIo.U32(data, 16);

            var offsets = new uint[16];
            var lengths = new uint[16];
            for (int i = 0; i < 16; i++)
            {
                offsets[i] = BlpIo.U32(data, OffsetsOffset + i * 4);
                lengths[i] = BlpIo.U32(data, LengthsOffset + i * 4);
            }

            // r_blp2.c:126 — no mip flag means a single level.
            int mipCount = hasMipLevels != 0 ? BlpIo.CountMipLevels(offsets) : 1;
            if (mipCount == 0)
                throw new BlpException("BLP2: hasMipLevels is set but every offset entry is zero.");

            BlpIo.ValidateDimensions("BLP2", width, height);

            var image = new BlpImage
            {
                Version = BlpVersion.Blp2,
                Kind = BlpPayloadKind.Rgba8,
                Width = (int)width,
                Height = (int)height,
                MipCount = mipCount,
                RawEncoding = encoding,
                RawAlphaDepth = alphaDepth,
                RawAlphaEncoding = alphaEncoding,
            };

            if (type == 0)
            {
                // r_blp2.c:79-81 routes type 0 ("JPEG") into
                // blp2_convert_paletted_no_alpha(), which reads the JPEG entropy
                // stream as palette indices and returns a plausible-looking but
                // meaningless image. Refuse it instead of shipping garbage.
                throw new BlpException(
                    "BLP2: type 0 (JPEG) content is not supported; the C reference " +
                    "decodes it as palette indices (renderer/r_blp2.c:79).");
            }

            var mips = new byte[mipCount][];
            for (int level = 0; level < mipCount; level++)
            {
                int offset = BlpIo.CheckMipSlice(data, offsets[level], lengths[level], level, "BLP2");

                int mipWidth = BlpIo.MipDimension(width, level);
                int mipHeight = BlpIo.MipDimension(height, level);
                if (mipWidth < 1 || mipHeight < 1)
                {
                    // Same non-square mip-tail case as BLP1: the C loader decodes
                    // zero pixels and the renderer drops the upload
                    // (renderer/r_texture.c:275).
                    mips[level] = null;
                    continue;
                }

                var dst = new byte[mipWidth * mipHeight * 4];

                switch (encoding)
                {
                    case EncodingPaletted:
                        BlpIo.DecodePalettedMip(
                            data, PaletteOffset, offset, lengths[level],
                            mipWidth, mipHeight, alphaDepth, level, "BLP2", dst);
                        break;

                    case EncodingRawBgra:
                        DecodeRawBgraMip(data, offset, lengths[level], mipWidth, mipHeight, level, dst);
                        break;

                    case EncodingDxt:
                        DxtDecoder.Decode(
                            data, offset, lengths[level], mipWidth, mipHeight,
                            ResolveDxtFormat(alphaDepth, alphaEncoding), dst);
                        break;

                    default:
                        throw new BlpException(
                            $"BLP2: unsupported encoding {encoding} " +
                            "(expected 1 = paletted, 2 = DXT, 3 = raw BGRA).");
                }

                mips[level] = dst;
            }

            image.Rgba = mips[0];
            image.MipRgba = mips;
            return image;
        }

        /// <summary>
        /// Port of the renderer/r_blp2.c:78-113 format switch. The returned value
        /// is the C <c>format</c> argument of <c>blp2_convert_dxt()</c>:
        /// 0 = DXT1 opaque, 1 = DXT1 with 1-bit alpha (also selected for
        /// "DXT3 without alpha"), 3 = DXT3, 5 = DXT5.
        /// </summary>
        internal static int ResolveDxtFormat(uint alphaDepth, uint alphaEncoding)
        {
            const uint AlphaEncodingDxt1 = 0;
            const uint AlphaEncodingDxt3 = 1;
            const uint AlphaEncodingDxt5 = 7;

            if (alphaEncoding == AlphaEncodingDxt1)
            {
                if (alphaDepth == 0) return 0;  // BLP_FORMAT_DXT1_NO_ALPHA
                if (alphaDepth == 1) return 1;  // BLP_FORMAT_DXT1_ALPHA_1
            }
            else if (alphaEncoding == AlphaEncodingDxt3)
            {
                if (alphaDepth == 0) return 1;  // BLP_FORMAT_DXT3_NO_ALPHA (r_blp2.c:101)
                if (alphaDepth == 4) return 3;  // BLP_FORMAT_DXT3_ALPHA_4
                if (alphaDepth == 8) return 3;  // BLP_FORMAT_DXT3_ALPHA_8
            }
            else if (alphaEncoding == AlphaEncodingDxt5)
            {
                if (alphaDepth == 8) return 5;  // BLP_FORMAT_DXT5_ALPHA_8
            }

            throw new BlpException(
                $"BLP2: unsupported DXT header combination (alphaDepth={alphaDepth}, " +
                $"alphaEncoding={alphaEncoding}); the C reference returns no pixels for it " +
                "(renderer/r_blp2.c:111).");
        }

        /// <summary>
        /// r_blp2.c:307 — the payload is a straight BGRA pixel array. Only the
        /// channel order is normalised here (the C side keeps BGRA and uploads
        /// it as PIXEL_BGRA).
        /// </summary>
        static void DecodeRawBgraMip(
            byte[] data, int offset, uint length, int width, int height, int mipLevel, byte[] dst)
        {
            int pixelCount = width * height;
            if (length < (ulong)(pixelCount * 4))
            {
                throw new BlpException(
                    $"BLP2: raw BGRA mip {mipLevel} ({width}x{height}) is truncated " +
                    $"(need {pixelCount * 4} bytes, have {length}).");
            }

            for (int i = 0; i < pixelCount; i++)
            {
                dst[i * 4 + 0] = data[offset + i * 4 + 2];
                dst[i * 4 + 1] = data[offset + i * 4 + 1];
                dst[i * 4 + 2] = data[offset + i * 4 + 0];
                dst[i * 4 + 3] = data[offset + i * 4 + 3];
            }
        }
    }
}
