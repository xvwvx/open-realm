using System;

namespace MdxPreview.Core.Blp
{
    /// <summary>
    /// Block decoders for the S3TC/DXT formats used by BLP2, ported 1:1 from
    /// <c>blp2_dxt_decode_color_block()</c>, <c>blp2_dxt_decode_alpha_dxt3()</c>,
    /// <c>blp2_dxt_decode_alpha_dxt5()</c> and <c>blp2_convert_dxt()</c>
    /// (renderer/r_blp2.c:228-362).
    /// </summary>
    internal static class DxtDecoder
    {
        /// <summary>
        /// Decodes one mip level into a top-down RGBA8 buffer.
        /// <paramref name="dxtFormat"/> uses the C <c>format</c> argument:
        /// 0 = DXT1 opaque, 1 = DXT1 with 1-bit alpha (also used for the
        /// degenerate DXT3/alphaDepth 0 case), 3 = DXT3, 5 = DXT5.
        /// </summary>
        internal static void Decode(
            byte[] src, int srcOffset, uint srcLength, int width, int height, int dxtFormat, byte[] dst)
        {
            var colors = new byte[4 * 4];
            Span<byte> alpha = stackalloc byte[16];

            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            int blockSize = dxtFormat == 3 || dxtFormat == 5 ? 16 : 8;

            long required = (long)blocksX * blocksY * blockSize;
            if (srcLength < (ulong)required)
            {
                throw new BlpException(
                    $"BLP2: DXT{dxtFormat} mip {width}x{height} is truncated " +
                    $"(need {required} bytes for {blocksX}x{blocksY} blocks, have {srcLength}).");
            }

            int block = srcOffset;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int colorBlock;
                    alpha.Fill(255);

                    if (dxtFormat == 3)
                    {
                        DecodeAlphaDxt3(src, block, alpha);
                        colorBlock = block + 8;
                        block += 16;
                    }
                    else if (dxtFormat == 5)
                    {
                        DecodeAlphaDxt5(src, block, alpha);
                        colorBlock = block + 8;
                        block += 16;
                    }
                    else
                    {
                        colorBlock = block;
                        block += 8;
                    }

                    DecodeColorBlock(src, colorBlock, colors, fourColor: dxtFormat != 1);

                    uint indices = BlpIo.U32(src, colorBlock + 4);

                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) continue;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) continue;

                            int i = py * 4 + px;
                            int c = (int)((indices >> (2 * i)) & 3) * 4;
                            int dstOffset = (y * width + x) * 4;

                            // r_blp2.c:352 — every format but the 1-bit alpha one
                            // overwrites the colour entry alpha with the block
                            // alpha table. For format 1 the table stays at 255
                            // (r_blp2.c:328) and the alpha comes from the colour
                            // entry itself, i.e. the transparent entry of the
                            // 3-colour mode (r_blp2.c:268).
                            byte a = dxtFormat != 1 ? alpha[i] : colors[c + 3];

                            dst[dstOffset + 0] = colors[c + 0];
                            dst[dstOffset + 1] = colors[c + 1];
                            dst[dstOffset + 2] = colors[c + 2];
                            dst[dstOffset + 3] = a;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// r_blp2.c:242. <paramref name="fourColor"/> corresponds to
        /// <c>format != 1</c> in the C switch, i.e. DXT1 without 1-bit alpha,
        /// DXT3 and DXT5 always use the 4-colour table; only DXT1 with 1-bit
        /// alpha applies the classic <c>c0 &gt; c1</c> test.
        /// </summary>
        static void DecodeColorBlock(byte[] src, int block, byte[] colors, bool fourColor)
        {
            ushort c0 = BlpIo.U16(src, block);
            ushort c1 = BlpIo.U16(src, block + 2);

            BlpIo.Color565(c0, out byte r0, out byte g0, out byte b0);
            BlpIo.Color565(c1, out byte r1, out byte g1, out byte b1);

            colors[0] = r0; colors[1] = g0; colors[2] = b0; colors[3] = 255;
            colors[4] = r1; colors[5] = g1; colors[6] = b1; colors[7] = 255;

            if (fourColor || c0 > c1)
            {
                colors[8] = (byte)((2 * r0 + r1) / 3);
                colors[9] = (byte)((2 * g0 + g1) / 3);
                colors[10] = (byte)((2 * b0 + b1) / 3);
                colors[11] = 255;

                colors[12] = (byte)((r0 + 2 * r1) / 3);
                colors[13] = (byte)((g0 + 2 * g1) / 3);
                colors[14] = (byte)((b0 + 2 * b1) / 3);
                colors[15] = 255;
            }
            else
            {
                colors[8] = (byte)((r0 + r1) / 2);
                colors[9] = (byte)((g0 + g1) / 2);
                colors[10] = (byte)((b0 + b1) / 2);
                colors[11] = 255;

                // r_blp2.c:268 — the transparent entry.
                colors[12] = 0;
                colors[13] = 0;
                colors[14] = 0;
                colors[15] = 0;
            }
        }

        /// <summary>r_blp2.c:272 — 16 explicit 4-bit alpha values, low nibble first.</summary>
        static void DecodeAlphaDxt3(byte[] src, int block, Span<byte> alpha)
        {
            for (int i = 0; i < 16; i++)
            {
                byte value = (byte)((src[block + i / 2] >> ((i & 1) * 4)) & 0x0F);
                alpha[i] = (byte)((value << 4) | value);
            }
        }

        /// <summary>r_blp2.c:279 — interpolated DXT5 alpha.</summary>
        static void DecodeAlphaDxt5(byte[] src, int block, Span<byte> alpha)
        {
            Span<byte> table = stackalloc byte[8];
            table[0] = src[block];
            table[1] = src[block + 1];

            if (table[0] > table[1])
            {
                table[2] = (byte)((6 * table[0] + 1 * table[1]) / 7);
                table[3] = (byte)((5 * table[0] + 2 * table[1]) / 7);
                table[4] = (byte)((4 * table[0] + 3 * table[1]) / 7);
                table[5] = (byte)((3 * table[0] + 4 * table[1]) / 7);
                table[6] = (byte)((2 * table[0] + 5 * table[1]) / 7);
                table[7] = (byte)((1 * table[0] + 6 * table[1]) / 7);
            }
            else
            {
                table[2] = (byte)((4 * table[0] + 1 * table[1]) / 5);
                table[3] = (byte)((3 * table[0] + 2 * table[1]) / 5);
                table[4] = (byte)((2 * table[0] + 3 * table[1]) / 5);
                table[5] = (byte)((1 * table[0] + 4 * table[1]) / 5);
                table[6] = 0;
                table[7] = 255;
            }

            ulong bits = 0;
            for (int i = 0; i < 6; i++)
                bits |= (ulong)src[block + 2 + i] << (8 * i);

            for (int i = 0; i < 16; i++)
                alpha[i] = table[(int)((bits >> (3 * i)) & 7)];
        }
    }
}
