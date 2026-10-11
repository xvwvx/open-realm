using System;

namespace MdxPreview.Core.Blp
{
    /// <summary>
    /// Baseline (sequential, SOF0/SOF1) <b>and progressive (SOF2)</b> JPEG decoder
    /// specialised for Warcraft III BLP1 payloads.
    ///
    /// <para>
    /// Warcraft III stores its JPEG textures as a <b>4-component</b> JPEG whose
    /// bands are raw B,G,R,A with no colour transform (SOF <c>Nf=4</c>,
    /// <c>Ns=4</c> scans, all components 1x1). renderer/r_blp1.c
    /// therefore compiles stb_image with
    /// <c>STBI_WARCRAFT3_BLP_JPEG_RGBA_BANDS</c> (renderer/stb/stb_image.h:3957),
    /// which emits the bands as <c>{band2, band1, band0, band3}</c>. This port
    /// reproduces that path bit-for-bit: the integer IDCT, the Huffman tables, the
    /// DC prediction and the progressive scan machinery follow stb_image's generic
    /// C implementation (<c>stbi__parse_entropy_coded_data</c>,
    /// <c>stbi__jpeg_decode_block</c>, <c>stbi__jpeg_decode_block_prog_dc/ac</c>,
    /// <c>stbi__jpeg_finish</c>, <c>stbi__idct_block</c>) so the produced RGBA
    /// bytes match the C reference exactly. Both encodings are common in the wild:
    /// a scan of one user's 15706 custom BLP1 JPEG assets found 14670 progressive
    /// and 1011 baseline streams.
    /// </para>
    ///
    /// <para>
    /// Stock JPEG decoders reject these files (libjpeg-turbo reports "JPEG files
    /// with CMYK or YCCK colorspaces are not currently supported", which is what
    /// Godot's <c>Image.LoadJpgFromBuffer</c> surfaces as
    /// <c>ERR_PARSE_ERROR</c>), hence this decoder exists in the core layer.
    /// </para>
    ///
    /// <para>
    /// Only the shape used by the game is implemented. Anything else throws
    /// <see cref="BlpException"/> naming the exact reason instead of producing a
    /// wrong image: lossless/arithmetic/differential SOF variants, 12-bit samples,
    /// component subsampling, restart intervals, DNL segments, missing tables and
    /// malformed scan parameters.
    /// </para>
    /// </summary>
    internal static class JpegWarcraft3Decoder
    {
        const int FastBits = 9;
        const int MaxComponents = 4;
        const int MarkerNone = 0xFF;

        /// <summary>
        /// Decodes a Warcraft III style 4-component JPEG (baseline or progressive)
        /// into top-down RGBA8 (<c>R = band2, G = band1, B = band0, A = band3</c>).
        /// </summary>
        internal static byte[] DecodeWarcraft3Rgba(byte[] data, int offset, int length, out int width, out int height)
        {
            var decoder = new Decoder(data, offset, length);
            return decoder.Run(out width, out height);
        }

        sealed class Component
        {
            public int Id;
            public int H = 1;
            public int V = 1;
            public int Tq;
            public int Hd;
            public int Ha;
            public int X;   // effective sample width  (stb: img_comp[i].x)
            public int Y;   // effective sample height (stb: img_comp[i].y)
            public int W2;
            public int H2;
            public int CoeffW;   // progressive: coefficient blocks per row
            public int CoeffH;
            public int DcPred;
            public byte[] Plane;
            public short[] Coeff; // progressive: 64 coefficients per block
        }

        sealed class Huffman
        {
            public readonly byte[] Size = new byte[257];
            public readonly ushort[] Code = new ushort[256];
            public readonly byte[] Values = new byte[256];
            public readonly uint[] MaxCode = new uint[18];
            public readonly int[] Delta = new int[18];
            public readonly byte[] Fast = new byte[1 << FastBits];
            public readonly short[] FastAc = new short[1 << FastBits];
        }

        sealed class Decoder
        {
            readonly byte[] _data;
            readonly int _end;
            int _pos;

            readonly Huffman[] _huffDc = new Huffman[4];
            readonly Huffman[] _huffAc = new Huffman[4];
            readonly ushort[][] _dequant = new ushort[4][];
            readonly Component[] _comp = new Component[MaxComponents];

            int _imgX;
            int _imgY;
            int _imgN;
            int _hMax = 1;
            int _vMax = 1;
            int _mcuX;
            int _mcuY;

            int _scanN;
            readonly int[] _order = new int[MaxComponents];

            bool _progressive;
            int _specStart;
            int _specEnd;
            int _succHigh;
            int _succLow;
            int _eobRun;

            uint _codeBuffer;
            int _codeBits;
            bool _nomore;
            byte _marker = MarkerNone;

            readonly short[] _block = new short[64];
            readonly int[] _idct = new int[64];

            internal Decoder(byte[] data, int offset, int length)
            {
                _data = data;
                _pos = offset;
                _end = offset + length;
            }

            // ------------------------------------------------------------ input

            int Get8()
            {
                // stb_image's stbi__get8() returns 0 and stops advancing at the end.
                if (_pos < _end) return _data[_pos++];
                return 0;
            }

            int Get16be()
            {
                int hi = Get8();
                int lo = Get8();
                return (hi << 8) | lo;
            }

            void Skip(int count)
            {
                for (int i = 0; i < count; i++) Get8();
            }

            /// <summary>renderer/stb stb__skip_jpeg_junk_at_end (stb_image.h:3389).</summary>
            byte SkipJpegJunkAtEnd()
            {
                while (_pos < _end)
                {
                    int x = Get8();
                    while (x == 0xFF)
                    {
                        if (_pos >= _end) return MarkerNone;
                        x = Get8();
                        if (x != 0x00 && x != 0xFF) return (byte)x;
                    }
                }
                return MarkerNone;
            }

            byte GetMarker()
            {
                if (_marker != MarkerNone)
                {
                    byte cached = _marker;
                    _marker = MarkerNone;
                    return cached;
                }

                int x = Get8();
                if (x != 0xFF) return MarkerNone;
                while (x == 0xFF) x = Get8();
                return (byte)x;
            }

            // ---------------------------------------------------------- decode

            internal byte[] Run(out int width, out int height)
            {
                ParseImage();

                width = _imgX;
                height = _imgY;
                return BuildRgba();
            }

            /// <summary>
            /// renderer/stb stb__decode_jpeg_image (stb_image.h:3412): walk the
            /// markers, decoding every scan it finds. Baseline streams have one
            /// (or more) sequential scans, progressive streams have a DC scan
            /// followed by spectral-selection/successive-approximation AC scans.
            /// </summary>
            void ParseImage()
            {
                byte marker = GetMarker();
                if (marker != 0xD8)
                    throw new BlpException($"BLP1 JPEG: expected SOI, found 0xFF{marker:X2}.");

                bool haveFrame = false;
                int scanCount = 0;

                marker = GetMarker();
                while (marker != 0xD9)
                {
                    if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2)
                    {
                        if (haveFrame) throw new BlpException("BLP1 JPEG: more than one frame header.");
                        ParseFrameHeader(marker);
                        haveFrame = true;
                    }
                    else if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    {
                        throw new BlpException(
                            $"BLP1 JPEG: marker 0xFF{marker:X2} ({DescribeSof(marker)}) is not supported; " +
                            "only sequential (SOF0/SOF1) and progressive (SOF2) 8-bit streams are decoded.");
                    }
                    else if (marker == 0xDB)
                    {
                        ParseQuantizationTables();
                    }
                    else if (marker == 0xC4)
                    {
                        ParseHuffmanTables();
                    }
                    else if (marker == 0xDD)
                    {
                        int len = Get16be();
                        if (len != 4) throw new BlpException($"BLP1 JPEG: bad DRI length {len}.");
                        int interval = Get16be();
                        if (interval != 0)
                            throw new BlpException("BLP1 JPEG: restart intervals (DRI) are not supported.");
                    }
                    else if (marker == 0xDA)
                    {
                        if (!haveFrame)
                            throw new BlpException("BLP1 JPEG: scan header before the frame header.");
                        ParseScanHeader();
                        ParseEntropyCodedData();
                        scanCount++;

                        // The entropy decoder leaves the next marker in the cache
                        // (or none when the bits ran out exactly at the boundary).
                        if (_marker == MarkerNone) _marker = SkipJpegJunkAtEnd();
                        marker = GetMarker();
                        if (marker >= 0xD0 && marker <= 0xD7) marker = GetMarker(); // RSTn (unreachable: DRI is rejected)
                        continue;
                    }
                    else if ((marker >= 0xE0 && marker <= 0xEF) || marker == 0xFE)
                    {
                        int len = Get16be();
                        if (len < 2) throw new BlpException($"BLP1 JPEG: bad APP/COM length {len}.");
                        Skip(len - 2);
                    }
                    else if (marker == 0xDC)
                    {
                        throw new BlpException("BLP1 JPEG: DNL segments are not supported.");
                    }
                    else if (marker == MarkerNone)
                    {
                        throw new BlpException("BLP1 JPEG: unexpected end of data (expected EOI).");
                    }
                    else
                    {
                        throw new BlpException($"BLP1 JPEG: unsupported marker 0xFF{marker:X2}.");
                    }

                    marker = GetMarker();
                }

                if (scanCount == 0)
                    throw new BlpException("BLP1 JPEG: reached EOI before the image scan.");

                if (_progressive) FinishProgressive();
            }

            static string DescribeSof(int marker) => marker switch
            {
                0xC2 => "progressive",
                0xC3 => "lossless",
                0xC5 or 0xC6 or 0xC7 => "differential",
                0xC9 or 0xCA or 0xCB => "arithmetic",
                0xCD or 0xCE or 0xCF => "arithmetic differential",
                _ => "unknown",
            };

            void ParseFrameHeader(byte sofMarker)
            {
                // renderer/stb: stbi__SOF_progressive(m) == (m == 0xC2)
                _progressive = sofMarker == 0xC2;

                int lf = Get16be();
                if (lf < 11) throw new BlpException($"BLP1 JPEG: bad SOF length {lf}.");

                int precision = Get8();
                if (precision != 8)
                    throw new BlpException($"BLP1 JPEG: {precision}-bit samples are not supported (8-bit only).");

                _imgY = Get16be();
                _imgX = Get16be();
                if (_imgX == 0 || _imgY == 0)
                    throw new BlpException($"BLP1 JPEG: invalid frame size {_imgX}x{_imgY}.");
                if ((uint)_imgX > BlpIo.MaxDimension || (uint)_imgY > BlpIo.MaxDimension)
                    throw new BlpException($"BLP1 JPEG: frame size {_imgX}x{_imgY} is too large.");

                int count = Get8();
                if (count != 4)
                    throw new BlpException(
                        $"BLP1 JPEG: expected the Warcraft III 4-component layout, found {count} component(s).");
                if (lf != 8 + 3 * count)
                    throw new BlpException($"BLP1 JPEG: bad SOF length {lf} for {count} components.");

                _imgN = count;
                for (int i = 0; i < count; i++)
                {
                    var comp = new Component { Id = Get8() };
                    int sampling = Get8();
                    comp.H = sampling >> 4;
                    comp.V = sampling & 15;
                    comp.Tq = Get8();

                    if (comp.H != 1 || comp.V != 1)
                        throw new BlpException(
                            $"BLP1 JPEG: component subsampling ({comp.H}x{comp.V}) is not supported; " +
                            "Warcraft III BLP JPEGs use 1x1 sampling.");
                    if (comp.Tq > 3)
                        throw new BlpException($"BLP1 JPEG: bad quantization table index {comp.Tq}.");

                    _comp[i] = comp;
                }

                _hMax = 1;
                _vMax = 1;
                _mcuX = (_imgX + 7) / 8;
                _mcuY = (_imgY + 7) / 8;

                for (int i = 0; i < count; i++)
                {
                    // 1x1 sampling: the plane covers the whole padded MCU grid and
                    // holds one sample per pixel (renderer/stb stb__process_frame_header).
                    Component comp = _comp[i];
                    comp.X = _imgX;
                    comp.Y = _imgY;
                    comp.W2 = _mcuX * 8;
                    comp.H2 = _mcuY * 8;
                    comp.Plane = new byte[comp.W2 * comp.H2];
                    if (_progressive)
                    {
                        comp.CoeffW = comp.W2 / 8;
                        comp.CoeffH = comp.H2 / 8;
                        comp.Coeff = new short[comp.CoeffW * comp.CoeffH * 64];
                    }
                }
            }

            void ParseQuantizationTables()
            {
                int length = Get16be() - 2;
                while (length > 0)
                {
                    int pq = Get8();
                    int precision = pq >> 4;
                    int t = pq & 15;
                    if (precision != 0 && precision != 1)
                        throw new BlpException($"BLP1 JPEG: bad DQT precision {precision}.");
                    if (t > 3) throw new BlpException($"BLP1 JPEG: bad DQT table index {t}.");

                    var table = new ushort[64];
                    for (int i = 0; i < 64; i++)
                    {
                        // stb_image stores the table de-zigzagged.
                        table[Zigzag[i]] = (ushort)(precision != 0 ? Get16be() : Get8());
                    }
                    _dequant[t] = table;
                    length -= precision != 0 ? 129 : 65;
                }
            }

            void ParseHuffmanTables()
            {
                int length = Get16be() - 2;
                while (length > 0)
                {
                    int tc = Get8();
                    int th = tc & 15;
                    tc >>= 4;
                    if (tc > 1 || th > 3) throw new BlpException($"BLP1 JPEG: bad DHT header (tc={tc}, th={th}).");

                    var counts = new int[16];
                    int total = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        counts[i] = Get8();
                        total += counts[i];
                    }
                    if (total > 256) throw new BlpException("BLP1 JPEG: bad DHT symbol count.");
                    length -= 17;

                    var table = new Huffman();
                    BuildHuffman(table, counts);
                    for (int i = 0; i < total; i++)
                        table.Values[i] = (byte)Get8();
                    if (tc != 0) BuildFastAc(table);

                    if (tc == 0) _huffDc[th] = table;
                    else _huffAc[th] = table;
                    length -= total;
                }
            }

            /// <summary>renderer/stb stb__build_huffman, including the FAST_BITS acceleration table.</summary>
            static void BuildHuffman(Huffman h, int[] count)
            {
                int k = 0;
                for (int i = 0; i < 16; i++)
                {
                    for (int j = 0; j < count[i]; j++)
                    {
                        h.Size[k++] = (byte)(i + 1);
                        if (k >= 257) throw new BlpException("BLP1 JPEG: bad Huffman size list.");
                    }
                }
                h.Size[k] = 0;

                uint code = 0;
                k = 0;
                int j2;
                for (j2 = 1; j2 <= 16; j2++)
                {
                    h.Delta[j2] = k - (int)code;
                    if (h.Size[k] == j2)
                    {
                        while (h.Size[k] == j2)
                            h.Code[k++] = (ushort)code++;
                        if (code - 1 >= (1u << j2)) throw new BlpException("BLP1 JPEG: bad Huffman code lengths.");
                    }
                    h.MaxCode[j2] = code << (16 - j2);
                    code <<= 1;
                }
                h.MaxCode[17] = 0xFFFFFFFF;

                for (int i = 0; i < h.Fast.Length; i++) h.Fast[i] = 255;
                for (int i = 0; i < k; i++)
                {
                    int s = h.Size[i];
                    if (s <= FastBits)
                    {
                        int c = h.Code[i] << (FastBits - s);
                        int m = 1 << (FastBits - s);
                        for (int j3 = 0; j3 < m; j3++)
                            h.Fast[c + j3] = (byte)i;
                    }
                }
            }

            /// <summary>renderer/stb stb__build_fast_ac.</summary>
            static void BuildFastAc(Huffman h)
            {
                for (int i = 0; i < (1 << FastBits); i++)
                {
                    byte fast = h.Fast[i];
                    h.FastAc[i] = 0;
                    if (fast >= 255) continue;

                    int rs = h.Values[fast];
                    int run = (rs >> 4) & 15;
                    int magbits = rs & 15;
                    int len = h.Size[fast];
                    if (magbits == 0 || len + magbits > FastBits) continue;

                    int k = ((i << len) & ((1 << FastBits) - 1)) >> (FastBits - magbits);
                    int m = 1 << (magbits - 1);
                    if (k < m) k += (int)(~0u << magbits) + 1;
                    if (k >= -128 && k <= 127)
                        h.FastAc[i] = (short)((k * 256) + (run * 16) + (len + magbits));
                }
            }

            void ParseScanHeader()
            {
                int ls = Get16be();
                _scanN = Get8();
                if (_scanN < 1 || _scanN > 4 || _scanN > _imgN)
                    throw new BlpException($"BLP1 JPEG: bad SOS component count {_scanN}.");
                if (ls != 6 + 2 * _scanN)
                    throw new BlpException($"BLP1 JPEG: bad SOS length {ls} for {_scanN} components.");

                for (int i = 0; i < _scanN; i++)
                {
                    int id = Get8();
                    int q = Get8();
                    int which = -1;
                    for (int c = 0; c < _imgN; c++)
                        if (_comp[c].Id == id) { which = c; break; }
                    if (which < 0) throw new BlpException($"BLP1 JPEG: SOS references unknown component id {id}.");

                    _comp[which].Hd = q >> 4;
                    _comp[which].Ha = q & 15;
                    if (_comp[which].Hd > 3) throw new BlpException("BLP1 JPEG: bad DC Huffman selector.");
                    if (_comp[which].Ha > 3) throw new BlpException("BLP1 JPEG: bad AC Huffman selector.");
                    _order[i] = which;
                }

                _specStart = Get8();
                _specEnd = Get8();
                int aa = Get8();
                _succHigh = aa >> 4;
                _succLow = aa & 15;

                if (_progressive)
                {
                    // renderer/stb stb__process_scan_header (stb_image.h:3229)
                    if (_specStart > 63 || _specEnd > 63 || _specStart > _specEnd || _succHigh > 13 || _succLow > 13)
                        throw new BlpException(
                            $"BLP1 JPEG: bad progressive scan parameters (Ss={_specStart}, Se={_specEnd}, " +
                            $"Ah={_succHigh}, Al={_succLow}).");
                }
                else
                {
                    if (_specStart != 0)
                        throw new BlpException($"BLP1 JPEG: bad SOS spectral selection Ss={_specStart} for a baseline scan.");
                    if (_succHigh != 0 || _succLow != 0)
                        throw new BlpException("BLP1 JPEG: successive approximation in a baseline scan is not valid.");
                    _specEnd = 63;
                }

                // Only the components in this scan need tables, and a DC scan needs
                // no AC table at all (progressive DC scans carry no AC codes).
                bool needsAc = _specStart != 0;
                for (int i = 0; i < _scanN; i++)
                {
                    Component comp = _comp[_order[i]];
                    if (needsAc)
                    {
                        if (_huffAc[comp.Ha] == null)
                            throw new BlpException($"BLP1 JPEG: missing AC Huffman table {comp.Ha}.");
                    }
                    else if (_succHigh == 0 && _huffDc[comp.Hd] == null)
                    {
                        throw new BlpException($"BLP1 JPEG: missing DC Huffman table {comp.Hd}.");
                    }

                    if (_dequant[comp.Tq] == null)
                        throw new BlpException($"BLP1 JPEG: missing quantization table {comp.Tq}.");
                }
            }

            // ------------------------------------------------------- bitstream

            void GrowBufferUnsafe()
            {
                do
                {
                    int b = _nomore ? 0 : Get8();
                    if (b == 0xFF)
                    {
                        int c = Get8();
                        while (c == 0xFF) c = Get8();
                        if (c != 0)
                        {
                            _marker = (byte)c;
                            _nomore = true;
                            return;
                        }
                    }
                    _codeBuffer |= (uint)b << (24 - _codeBits);
                    _codeBits += 8;
                } while (_codeBits <= 24);
            }

            static readonly uint[] BitMask =
            {
                0, 1, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535,
            };

            int HuffDecode(Huffman h)
            {
                if (_codeBits < 16) GrowBufferUnsafe();

                int c = (int)((_codeBuffer >> (32 - FastBits)) & ((1u << FastBits) - 1));
                int k = h.Fast[c];
                if (k < 255)
                {
                    int s = h.Size[k];
                    if (s > _codeBits) return -1;
                    _codeBuffer <<= s;
                    _codeBits -= s;
                    return h.Values[k];
                }

                uint temp = _codeBuffer >> 16;
                for (k = FastBits + 1; ; k++)
                    if (temp < h.MaxCode[k]) break;
                if (k == 17)
                {
                    _codeBits -= 16;
                    return -1;
                }
                if (k > _codeBits) return -1;

                c = (int)((_codeBuffer >> (32 - k)) & BitMask[k]) + h.Delta[k];
                if (c < 0 || c >= 256) return -1;

                _codeBits -= k;
                _codeBuffer <<= k;
                return h.Values[c];
            }

            static readonly int[] JBias =
            {
                0, -1, -3, -7, -15, -31, -63, -127, -255, -511, -1023, -2047, -4095, -8191, -16383, -32767,
            };

            int ExtendReceive(int n)
            {
                if (_codeBits < n) GrowBufferUnsafe();
                if (_codeBits < n) return 0;

                int sgn = (int)(_codeBuffer >> 31);
                // stbi_lrot(code_buffer, n): rotate left, so that the low n bits hold
                // the top n bits of the buffer (stb_image.h:670).
                uint k = (_codeBuffer << n) | (_codeBuffer >> (32 - n));
                _codeBuffer = k & ~BitMask[n];
                k &= BitMask[n];
                _codeBits -= n;
                return (int)k + (JBias[n] & (sgn - 1));
            }

            /// <summary>renderer/stb stb__jpeg_get_bits (stb_image.h:2169): unsigned bits.</summary>
            int GetBits(int n)
            {
                if (_codeBits < n) GrowBufferUnsafe();
                if (_codeBits < n) return 0;

                uint k = (_codeBuffer << n) | (_codeBuffer >> (32 - n));
                _codeBuffer = k & ~BitMask[n];
                k &= BitMask[n];
                _codeBits -= n;
                return (int)k;
            }

            /// <summary>renderer/stb stb__jpeg_get_bit (stb_image.h:2181): 0 or 0x80000000.</summary>
            int GetBit()
            {
                if (_codeBits < 1) GrowBufferUnsafe();
                if (_codeBits < 1) return 0;

                uint k = _codeBuffer;
                _codeBuffer <<= 1;
                --_codeBits;
                return (int)(k & 0x80000000);
            }

            // --------------------------------------------------------- entropy

            /// <summary>renderer/stb stb__jpeg_reset (stb_image.h:2936), per scan.</summary>
            void ResetEntropyState()
            {
                _codeBits = 0;
                _codeBuffer = 0;
                _nomore = false;
                _marker = MarkerNone;
                _eobRun = 0;
                for (int i = 0; i < _imgN; i++) _comp[i].DcPred = 0;
            }

            /// <summary>
            /// renderer/stb stb__parse_entropy_coded_data (stb_image.h:2949): baseline
            /// has one sequential scan (interleaved or not), progressive decodes a DC
            /// scan first and then per-component spectral-selection AC scans.
            /// </summary>
            void ParseEntropyCodedData()
            {
                ResetEntropyState();

                if (_progressive)
                {
                    if (_scanN == 1)
                    {
                        Component comp = _comp[_order[0]];
                        int blocksX = (comp.X + 7) >> 3;
                        int blocksY = (comp.Y + 7) >> 3;
                        for (int blockY = 0; blockY < blocksY; blockY++)
                        {
                            for (int blockX = 0; blockX < blocksX; blockX++)
                            {
                                int dataOffset = 64 * (blockX + blockY * comp.CoeffW);
                                if (_specStart == 0) DecodeBlockProgDc(comp, dataOffset);
                                else DecodeBlockProgAc(comp, dataOffset);
                            }
                        }
                    }
                    else
                    {
                        // Interleaved progressive scans carry DC only.
                        for (int mcuY = 0; mcuY < _mcuY; mcuY++)
                        {
                            for (int mcuX = 0; mcuX < _mcuX; mcuX++)
                            {
                                for (int k = 0; k < _scanN; k++)
                                {
                                    Component comp = _comp[_order[k]];
                                    int blockX = mcuX * comp.H;
                                    int blockY = mcuY * comp.V;
                                    DecodeBlockProgDc(comp, 64 * (blockX + blockY * comp.CoeffW));
                                }
                            }
                        }
                    }
                    return;
                }

                if (_scanN == 1)
                {
                    Component comp = _comp[_order[0]];
                    int blocksX = (comp.X + 7) >> 3;
                    int blocksY = (comp.Y + 7) >> 3;
                    for (int blockY = 0; blockY < blocksY; blockY++)
                    {
                        for (int blockX = 0; blockX < blocksX; blockX++)
                        {
                            DecodeBlock(comp);
                            IdctBlock(comp.Plane, comp.W2 * (blockY * 8) + blockX * 8, comp.W2, _block);
                        }
                    }
                    return;
                }

                for (int mcuY = 0; mcuY < _mcuY; mcuY++)
                {
                    for (int mcuX = 0; mcuX < _mcuX; mcuX++)
                    {
                        for (int k = 0; k < _scanN; k++)
                        {
                            Component comp = _comp[_order[k]];
                            int x2 = mcuX * 8;
                            int y2 = mcuY * 8;
                            DecodeBlock(comp);
                            IdctBlock(comp.Plane, comp.W2 * y2 + x2, comp.W2, _block);
                        }
                    }
                }
            }

            /// <summary>
            /// renderer/stb stb__jpeg_decode_block_prog_dc (stb_image.h:2265). The DC
            /// coefficient is stored scaled by 1 &lt;&lt; Al and is dequantized later in
            /// <see cref="FinishProgressive"/>.
            /// </summary>
            void DecodeBlockProgDc(Component comp, int dataOffset)
            {
                if (_specEnd != 0)
                    throw new BlpException("BLP1 JPEG: progressive DC scan with Se != 0.");

                if (_codeBits < 16) GrowBufferUnsafe();

                short[] data = comp.Coeff;
                if (_succHigh == 0)
                {
                    Array.Clear(data, dataOffset, 64);

                    int t = HuffDecode(_huffDc[comp.Hd]);
                    if (t < 0 || t > 15) throw new BlpException("BLP1 JPEG: bad Huffman DC code.");

                    int diff = t != 0 ? ExtendReceive(t) : 0;
                    int dc = comp.DcPred + diff;
                    comp.DcPred = dc;
                    data[dataOffset] = unchecked((short)(dc * (1 << _succLow)));
                }
                else
                {
                    if (GetBit() != 0)
                        data[dataOffset] = unchecked((short)(data[dataOffset] + (short)(1 << _succLow)));
                }
            }

            /// <summary>renderer/stb stb__jpeg_decode_block_prog_ac (stb_image.h:2295).</summary>
            void DecodeBlockProgAc(Component comp, int dataOffset)
            {
                if (_specStart == 0)
                    throw new BlpException("BLP1 JPEG: progressive AC scan with Ss == 0.");

                short[] data = comp.Coeff;
                Huffman hac = _huffAc[comp.Ha];

                if (_succHigh == 0)
                {
                    int shift = _succLow;

                    if (_eobRun != 0)
                    {
                        --_eobRun;
                        return;
                    }

                    int k = _specStart;
                    do
                    {
                        if (_codeBits < 16) GrowBufferUnsafe();
                        int c = (int)((_codeBuffer >> (32 - FastBits)) & ((1u << FastBits) - 1));
                        short r = hac.FastAc[c];
                        if (r != 0)
                        {
                            k += (r >> 4) & 15;
                            int s = r & 15;
                            if (s > _codeBits) throw new BlpException("BLP1 JPEG: bad Huffman AC code.");
                            _codeBuffer <<= s;
                            _codeBits -= s;
                            if (k > 63) throw new BlpException("BLP1 JPEG: AC coefficient index out of range.");
                            int zig = Zigzag[k++];
                            data[dataOffset + zig] = unchecked((short)((r >> 8) * (1 << shift)));
                        }
                        else
                        {
                            int rs = HuffDecode(hac);
                            if (rs < 0) throw new BlpException("BLP1 JPEG: bad Huffman AC code.");
                            int s = rs & 15;
                            int run = rs >> 4;
                            if (s == 0)
                            {
                                if (run < 15)
                                {
                                    _eobRun = 1 << run;
                                    if (run != 0) _eobRun += GetBits(run);
                                    --_eobRun;
                                    break;
                                }
                                k += 16;
                            }
                            else
                            {
                                k += run;
                                if (k > 63) throw new BlpException("BLP1 JPEG: AC coefficient index out of range.");
                                int zig = Zigzag[k++];
                                data[dataOffset + zig] = unchecked((short)(ExtendReceive(s) * (1 << shift)));
                            }
                        }
                    } while (k <= _specEnd);
                }
                else
                {
                    // Refinement scan for these AC coefficients.
                    short bit = (short)(1 << _succLow);

                    if (_eobRun != 0)
                    {
                        --_eobRun;
                        for (int k = _specStart; k <= _specEnd; k++)
                        {
                            int p = dataOffset + Zigzag[k];
                            if (data[p] != 0 && GetBit() != 0)
                            {
                                if ((data[p] & bit) == 0)
                                {
                                    if (data[p] > 0) data[p] += bit;
                                    else data[p] -= bit;
                                }
                            }
                        }
                        return;
                    }

                    int k2 = _specStart;
                    do
                    {
                        int rs = HuffDecode(hac);
                        if (rs < 0) throw new BlpException("BLP1 JPEG: bad Huffman AC code.");
                        int s = rs & 15;
                        int r = rs >> 4;
                        if (s == 0)
                        {
                            if (r < 15)
                            {
                                _eobRun = (1 << r) - 1;
                                if (r != 0) _eobRun += GetBits(r);
                                r = 64; // force end of block
                            }
                        }
                        else
                        {
                            if (s != 1) throw new BlpException("BLP1 JPEG: bad refinement Huffman code.");
                            s = GetBit() != 0 ? bit : -bit;
                        }

                        while (k2 <= _specEnd)
                        {
                            int p = dataOffset + Zigzag[k2++];
                            if (data[p] != 0)
                            {
                                if (GetBit() != 0 && (data[p] & bit) == 0)
                                {
                                    if (data[p] > 0) data[p] += bit;
                                    else data[p] -= bit;
                                }
                            }
                            else if (r == 0)
                            {
                                data[p] = (short)s;
                                break;
                            }
                            else
                            {
                                --r;
                            }
                        }
                    } while (k2 <= _specEnd);
                }
            }

            /// <summary>renderer/stb stb__jpeg_finish (stb_image.h:3080), progressive only.</summary>
            void FinishProgressive()
            {
                for (int n = 0; n < _imgN; n++)
                {
                    Component comp = _comp[n];
                    ushort[] dequant = _dequant[comp.Tq];
                    int blocksX = (comp.X + 7) >> 3;
                    int blocksY = (comp.Y + 7) >> 3;
                    for (int blockY = 0; blockY < blocksY; blockY++)
                    {
                        for (int blockX = 0; blockX < blocksX; blockX++)
                        {
                            int dataOffset = 64 * (blockX + blockY * comp.CoeffW);
                            for (int i = 0; i < 64; i++)
                                comp.Coeff[dataOffset + i] = unchecked((short)(comp.Coeff[dataOffset + i] * dequant[i]));
                            IdctBlock(comp.Plane, comp.W2 * (blockY * 8) + blockX * 8, comp.W2, comp.Coeff, dataOffset);
                        }
                    }
                }
            }

            void DecodeBlock(Component comp)
            {
                Huffman hdc = _huffDc[comp.Hd];
                Huffman hac = _huffAc[comp.Ha];
                ushort[] dequant = _dequant[comp.Tq];
                short[] data = _block;

                if (_codeBits < 16) GrowBufferUnsafe();
                int t = HuffDecode(hdc);
                if (t < 0 || t > 15) throw new BlpException("BLP1 JPEG: bad Huffman DC code.");

                Array.Clear(data, 0, 64);

                int diff = t != 0 ? ExtendReceive(t) : 0;
                int dc = comp.DcPred + diff;
                comp.DcPred = dc;
                data[0] = unchecked((short)(dc * dequant[0]));

                int k = 1;
                do
                {
                    if (_codeBits < 16) GrowBufferUnsafe();
                    int c = (int)((_codeBuffer >> (32 - FastBits)) & ((1u << FastBits) - 1));
                    short r = hac.FastAc[c];
                    if (r != 0)
                    {
                        k += (r >> 4) & 15;
                        int s = r & 15;
                        if (s > _codeBits) throw new BlpException("BLP1 JPEG: bad Huffman AC code.");
                        _codeBuffer <<= s;
                        _codeBits -= s;
                        if (k > 63) throw new BlpException("BLP1 JPEG: AC coefficient index out of range.");
                        int zig = Zigzag[k++];
                        data[zig] = unchecked((short)((r >> 8) * dequant[zig]));
                    }
                    else
                    {
                        int rs = HuffDecode(hac);
                        if (rs < 0) throw new BlpException("BLP1 JPEG: bad Huffman AC code.");
                        int s = rs & 15;
                        int run = rs >> 4;
                        if (s == 0)
                        {
                            if (rs != 0xF0) break; // EOB
                            k += 16;
                        }
                        else
                        {
                            k += run;
                            if (k > 63) throw new BlpException("BLP1 JPEG: AC coefficient index out of range.");
                            int zig = Zigzag[k++];
                            data[zig] = unchecked((short)(ExtendReceive(s) * dequant[zig]));
                        }
                    }
                } while (k < 64);
            }

            // ------------------------------------------------------------ idct

            static readonly int F2F_05411961 = (int)(0.5411961f * 4096 + 0.5);
            static readonly int F2F_NEG1847759065 = (int)(-1.847759065f * 4096 + 0.5);
            static readonly int F2F_0765366865 = (int)(0.765366865f * 4096 + 0.5);
            static readonly int F2F_1175875602 = (int)(1.175875602f * 4096 + 0.5);
            static readonly int F2F_0298631336 = (int)(0.298631336f * 4096 + 0.5);
            static readonly int F2F_2053119869 = (int)(2.053119869f * 4096 + 0.5);
            static readonly int F2F_3072711026 = (int)(3.072711026f * 4096 + 0.5);
            static readonly int F2F_1501321110 = (int)(1.501321110f * 4096 + 0.5);
            static readonly int F2F_NEG0899976223 = (int)(-0.899976223f * 4096 + 0.5);
            static readonly int F2F_NEG2562915447 = (int)(-2.562915447f * 4096 + 0.5);
            static readonly int F2F_NEG1961570560 = (int)(-1.961570560f * 4096 + 0.5);
            static readonly int F2F_NEG0390180644 = (int)(-0.390180644f * 4096 + 0.5);

            /// <summary>STBI__IDCT_1D (renderer/stb stb_image.h:2430) — integer, bit-exact.</summary>
            static void Idct1D(
                int s0, int s1, int s2, int s3, int s4, int s5, int s6, int s7,
                out int x0, out int x1, out int x2, out int x3,
                out int t0, out int t1, out int t2, out int t3)
            {
                int p1, p2, p3, p4, p5;

                p2 = s2;
                p3 = s6;
                p1 = (p2 + p3) * F2F_05411961;
                t2 = p1 + p3 * F2F_NEG1847759065;
                t3 = p1 + p2 * F2F_0765366865;
                p2 = s0;
                p3 = s4;
                t0 = (p2 + p3) * 4096;
                t1 = (p2 - p3) * 4096;
                x0 = t0 + t3;
                x3 = t0 - t3;
                x1 = t1 + t2;
                x2 = t1 - t2;
                t0 = s7;
                t1 = s5;
                t2 = s3;
                t3 = s1;
                p3 = t0 + t2;
                p4 = t1 + t3;
                p1 = t0 + t3;
                p2 = t1 + t2;
                p5 = (p3 + p4) * F2F_1175875602;
                t0 = t0 * F2F_0298631336;
                t1 = t1 * F2F_2053119869;
                t2 = t2 * F2F_3072711026;
                t3 = t3 * F2F_1501321110;
                p1 = p5 + p1 * F2F_NEG0899976223;
                p2 = p5 + p2 * F2F_NEG2562915447;
                p3 = p3 * F2F_NEG1961570560;
                p4 = p4 * F2F_NEG0390180644;
                t3 += p1 + p4;
                t2 += p2 + p3;
                t1 += p2 + p4;
                t0 += p1 + p3;
            }

            static byte Clamp(int x)
            {
                if ((uint)x > 255)
                {
                    if (x < 0) return 0;
                    if (x > 255) return 255;
                }
                return (byte)x;
            }

            /// <summary>
            /// renderer/stb stb__idct_block (stb_image.h:2467). <paramref name="coefficients"/>
            /// is either the shared baseline block buffer or a progressive component's
            /// coefficient array at <paramref name="dataOffset"/>.
            /// </summary>
            void IdctBlock(byte[] plane, int planeOffset, int stride, short[] coefficients, int dataOffset = 0)
            {
                int[] val = _idct;

                for (int i = 0; i < 8; i++)
                {
                    int d = dataOffset + i;
                    if (coefficients[d + 8] == 0 && coefficients[d + 16] == 0 && coefficients[d + 24] == 0 && coefficients[d + 32] == 0 &&
                        coefficients[d + 40] == 0 && coefficients[d + 48] == 0 && coefficients[d + 56] == 0)
                    {
                        int dcterm = coefficients[d] * 4;
                        val[i] = val[i + 8] = val[i + 16] = val[i + 24] =
                            val[i + 32] = val[i + 40] = val[i + 48] = val[i + 56] = dcterm;
                    }
                    else
                    {
                        Idct1D(coefficients[d], coefficients[d + 8], coefficients[d + 16], coefficients[d + 24],
                               coefficients[d + 32], coefficients[d + 40], coefficients[d + 48], coefficients[d + 56],
                            out int x0, out int x1, out int x2, out int x3,
                            out int t0, out int t1, out int t2, out int t3);
                        x0 += 512; x1 += 512; x2 += 512; x3 += 512;
                        val[i + 0] = (x0 + t3) >> 10;
                        val[i + 56] = (x0 - t3) >> 10;
                        val[i + 8] = (x1 + t2) >> 10;
                        val[i + 48] = (x1 - t2) >> 10;
                        val[i + 16] = (x2 + t1) >> 10;
                        val[i + 40] = (x2 - t1) >> 10;
                        val[i + 24] = (x3 + t0) >> 10;
                        val[i + 32] = (x3 - t0) >> 10;
                    }
                }

                for (int i = 0; i < 8; i++)
                {
                    int v = i * 8;
                    Idct1D(val[v], val[v + 1], val[v + 2], val[v + 3], val[v + 4], val[v + 5], val[v + 6], val[v + 7],
                        out int x0, out int x1, out int x2, out int x3,
                        out int t0, out int t1, out int t2, out int t3);
                    x0 += 65536 + (128 << 17);
                    x1 += 65536 + (128 << 17);
                    x2 += 65536 + (128 << 17);
                    x3 += 65536 + (128 << 17);

                    int o = planeOffset + i * stride;
                    plane[o + 0] = Clamp((x0 + t3) >> 17);
                    plane[o + 7] = Clamp((x0 - t3) >> 17);
                    plane[o + 1] = Clamp((x1 + t2) >> 17);
                    plane[o + 6] = Clamp((x1 - t2) >> 17);
                    plane[o + 2] = Clamp((x2 + t1) >> 17);
                    plane[o + 5] = Clamp((x2 - t1) >> 17);
                    plane[o + 3] = Clamp((x3 + t0) >> 17);
                    plane[o + 4] = Clamp((x3 - t0) >> 17);
                }
            }

            // ---------------------------------------------------------- output

            /// <summary>
            /// renderer/stb stb_image.h:3957 (<c>STBI_WARCRAFT3_BLP_JPEG_RGBA_BANDS</c>):
            /// emit <c>{band2, band1, band0, band3}</c> — the raw bands, no YCbCr
            /// transform. With 1x1 sampling each component plane row is the output
            /// row (stb's resample_row_1 returns the row unchanged).
            /// </summary>
            byte[] BuildRgba()
            {
                var rgba = new byte[_imgX * _imgY * 4];
                byte[] p0 = _comp[0].Plane;
                byte[] p1 = _comp[1].Plane;
                byte[] p2 = _comp[2].Plane;
                byte[] p3 = _comp[3].Plane;
                int w2 = _comp[0].W2;

                for (int y = 0; y < _imgY; y++)
                {
                    int row = y * w2;
                    int outRow = y * _imgX * 4;
                    for (int x = 0; x < _imgX; x++)
                    {
                        int o = outRow + x * 4;
                        rgba[o + 0] = p2[row + x];
                        rgba[o + 1] = p1[row + x];
                        rgba[o + 2] = p0[row + x];
                        rgba[o + 3] = p3[row + x];
                    }
                }

                return rgba;
            }
        }

        /// <summary>renderer/stb stb__jpeg_dezigzag (stb_image.h:2194).</summary>
        static readonly byte[] Zigzag =
        {
            0, 1, 8, 16, 9, 2, 3, 10,
            17, 24, 32, 25, 18, 11, 4, 5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63,
            63, 63, 63, 63, 63, 63, 63, 63,
            63, 63, 63, 63, 63, 63, 63,
        };
    }
}
