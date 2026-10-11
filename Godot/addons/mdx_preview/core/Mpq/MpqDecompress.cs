using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace MdxPreview.Core.Mpq
{
    /// <summary>
    /// Sector compression method bytes (common/mpq.c: MPQ_COMPRESSION_ZLIB,
    /// MPQ_COMP_*).
    /// </summary>
    internal static class MpqCompression
    {
        internal const byte Huffman = 0x01;
        internal const byte Zlib = 0x02;
        internal const byte Pkware = 0x08;
        internal const byte AdpcmMono = 0x40;
        internal const byte AdpcmStereo = 0x80;
    }

    /// <summary>
    /// Sector decompressors ported from common/mpq.c (Mpq_DecompressSector,
    /// Mpq_HuffDecode, Mpq_AdpcmDecode, TryInflateSector) plus Mark Adler's
    /// PKWARE DCL "blast" decompressor vendored in vendor/blast/blast.c, which is
    /// what common/mpq.c links against for mask 0x08.
    ///
    /// Every entry point reports which branch produced the bytes through
    /// <c>method</c> so callers can surface diagnostics (used by the tests to
    /// prove which decompression path a real archive file exercised).
    /// </summary>
    internal static class MpqDecompress
    {
        // ---------------------------------------------------------------- zlib

        /// <summary>
        /// common/mpq.c: TryInflateSector(). Strips a leading MPQ method byte
        /// 0x02, then inflates a zlib stream into <paramref name="dst"/>, requiring
        /// the stream to complete (the C code insists on Z_STREAM_END).
        /// </summary>
        internal static bool TryInflateSector(byte[] src, int srcOffset, int srcSize,
            byte[] dst, int dstOffset, int dstCapacity, out int outSize, out string method)
        {
            outSize = 0;
            method = null;
            if (src == null || dst == null || srcSize <= 0 || dstCapacity <= 0) return false;

            int payloadOffset = srcOffset;
            int payloadSize = srcSize;
            if (srcSize > 1 && src[srcOffset] == MpqCompression.Zlib)
            {
                payloadOffset++;
                payloadSize--;
            }

            if (TryInflate(payloadOffset, payloadSize, raw: false, out outSize, out bool initFailed) &&
                VerifyZlibTrailer(src, payloadOffset, payloadSize, dst, dstOffset, outSize))
            {
                method = "zlib";
                return true;
            }

            // common/mpq.c retries with a raw (-MAX_WBITS) inflater *only* when
            // zlib's inflateInit() itself fails, which a working zlib never does;
            // that branch is therefore unreachable in practice. Retrying raw
            // deflate after a data error is deliberately not done here: it would
            // accept sectors the C reader rejects (verified on
            // Maps\Campaign\*.w3m in War3Patch.mpq, whose stored sectors start
            // with 0xD5 - a byte that happens to be a valid deflate block header).
            if (initFailed && TryInflate(srcOffset, srcSize, raw: true, out outSize, out _))
            {
                method = "raw-deflate";
                return true;
            }

            outSize = 0;
            return false;

            bool TryInflate(int offset, int size, bool raw, out int produced, out bool constructFailed)
            {
                produced = 0;
                constructFailed = false;
                if (size <= 0) return false;

                var input = new MemoryStream(src, offset, size, writable: false);
                Stream inflater;
                try
                {
                    inflater = raw
                        ? (Stream)new DeflateStream(input, CompressionMode.Decompress)
                        : new ZLibStream(input, CompressionMode.Decompress);
                }
                catch (Exception)
                {
                    input.Dispose();
                    constructFailed = true;
                    return false;
                }

                try
                {
                    int total = 0;
                    while (total < dstCapacity)
                    {
                        int n = inflater.Read(dst, dstOffset + total, dstCapacity - total);
                        if (n <= 0) break;
                        total += n;
                    }

                    if (total == 0) produced = 0;

                    // zlib stops at Z_STREAM_END: either the output buffer is exactly
                    // full (avail_out exhausted is an error in the C code, so any
                    // further byte invalidates the sector) or the stream must have
                    // ended cleanly at the byte we stopped on.
                    if (total == dstCapacity)
                    {
                        if (inflater.ReadByte() >= 0) return false;
                    }

                    produced = total;
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
                finally
                {
                    inflater.Dispose();
                    input.Dispose();
                }
            }
        }

        // ------------------------------------------------------- mask dispatch

        /// <summary>
        /// common/mpq.c: Mpq_DecompressSector(). Dispatches on the leading method
        /// byte in Blizzard's prescribed order. Unsupported masks return false and
        /// fill <paramref name="error"/> with the same message the C code prints,
        /// so callers can raise a descriptive <see cref="MpqException"/> instead of
        /// silently yielding nothing.
        /// </summary>
        internal static bool TryDecompressSector(byte[] src, int srcOffset, int srcSize,
            byte[] dst, int dstOffset, int dstSize, out int outSize, out string method, out string error)
        {
            outSize = 0;
            method = null;
            error = null;
            if (src == null || dst == null || srcSize <= 0 || dstSize <= 0) return false;

            if (srcSize == dstSize)
            {
                Buffer.BlockCopy(src, srcOffset, dst, dstOffset, srcSize);
                outSize = srcSize;
                method = "stored(src==dst)";
                return true;
            }

            byte mask = src[srcOffset];
            switch (mask)
            {
                case MpqCompression.Pkware:
                    // common/mpq.c: blast() over the bytes after the method byte.
                    if (Blast(src, srcOffset + 1, srcSize - 1, dst, dstOffset, dstSize, out outSize) == 0)
                    {
                        method = "pkware";
                        return true;
                    }
                    method = "pkware-failed";
                    return false;

                case MpqCompression.Zlib:
                    // common/mpq.c: uncompress() - a plain zlib stream, no 0x02 strip.
                    if (TryInflateSectorZlib(src, srcOffset + 1, srcSize - 1, dst, dstOffset, dstSize, out outSize))
                    {
                        method = "zlib(method-byte)";
                        return true;
                    }
                    method = "zlib-failed";
                    return false;

                case MpqCompression.AdpcmMono:
                case MpqCompression.AdpcmStereo:
                {
                    int channels = mask == MpqCompression.AdpcmStereo ? 2 : 1;
                    if (AdpcmDecode(src, srcOffset + 1, srcSize - 1, dst, dstOffset, dstSize, channels, out outSize))
                    {
                        method = channels == 2 ? "adpcm-stereo" : "adpcm-mono";
                        return true;
                    }
                    method = "adpcm-failed";
                    return false;
                }

                case MpqCompression.Huffman | MpqCompression.AdpcmMono:
                case MpqCompression.Huffman | MpqCompression.AdpcmStereo:
                {
                    int channels = (mask & MpqCompression.AdpcmStereo) != 0 ? 2 : 1;
                    var tmp = new byte[dstSize];
                    if (!HuffmanDecode(src, srcOffset + 1, srcSize - 1, tmp, 0, dstSize, out int tmpSize))
                    {
                        method = "huffman-failed";
                        return false;
                    }
                    if (!AdpcmDecode(tmp, 0, tmpSize, dst, dstOffset, dstSize, channels, out outSize))
                    {
                        method = "huffman+adpcm-failed";
                        return false;
                    }
                    method = channels == 2 ? "huffman+adpcm-stereo" : "huffman+adpcm-mono";
                    return true;
                }
            }

            error = $"unsupported sector compression mask 0x{mask:X2}";
            method = "unsupported";
            return false;
        }

        /// <summary>Pure zlib inflate (common/mpq.c: uncompress()).</summary>
        static bool TryInflateSectorZlib(byte[] src, int srcOffset, int srcSize,
            byte[] dst, int dstOffset, int dstCapacity, out int outSize)
        {
            outSize = 0;
            if (srcSize <= 0 || dstCapacity <= 0) return false;
            try
            {
                using (var input = new MemoryStream(src, srcOffset, srcSize, writable: false))
                using (var inflater = new ZLibStream(input, CompressionMode.Decompress))
                {
                    int total = 0;
                    while (total < dstCapacity)
                    {
                        int n = inflater.Read(dst, dstOffset + total, dstCapacity - total);
                        if (n <= 0) break;
                        total += n;
                    }
                    if (total == dstCapacity && inflater.ReadByte() >= 0) return false;
                    if (!VerifyZlibTrailer(src, srcOffset, srcSize, dst, dstOffset, total)) return false;
                    outSize = total;
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Re-verifies the zlib trailer. zlib's inflate() returns Z_STREAM_END only
        /// after the trailing Adler-32 of the uncompressed data has been read and
        /// matched, so common/mpq.c only accepts sectors that pass this check. .NET's
        /// ZLibStream instead treats exhausted input as end-of-stream, so the
        /// checksum is recomputed here to reject truncated sectors.
        /// </summary>
        static bool VerifyZlibTrailer(byte[] src, int srcOffset, int srcSize, byte[] dst, int dstOffset, int produced)
        {
            if (srcSize < 6) return false;
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(srcOffset + srcSize - 4, 4));
            return Adler32(dst, dstOffset, produced) == expected;
        }

        /// <summary>Adler-32 as defined by RFC 1950 (zlib's trailing checksum).</summary>
        static uint Adler32(byte[] data, int offset, int count)
        {
            const uint mod = 65521;
            const int nmax = 5552; // largest chunk that cannot overflow a 32-bit int

            uint a = 1, b = 0;
            int index = offset;
            int remaining = count;
            while (remaining > 0)
            {
                int chunk = remaining < nmax ? remaining : nmax;
                remaining -= chunk;
                for (int i = 0; i < chunk; i++)
                {
                    a += data[index++];
                    b += a;
                }
                a %= mod;
                b %= mod;
            }
            return (b << 16) | a;
        }

        // -------------------------------------------------------------- ADPCM

        /// <summary>common/mpq.c: MPQ_ADPCM_INIT_STEP.</summary>
        const int AdpcmInitStep = 44;

        /// <summary>common/mpq.c: mpq_adpcm_next[32].</summary>
        static readonly int[] AdpcmNext =
        {
            -1, 0, -1, 4, -1, 2, -1, 6, -1, 1, -1, 5, -1, 3, -1, 7,
            -1, 1, -1, 5, -1, 3, -1, 7, -1, 2, -1, 4, -1, 6, -1, 8
        };

        /// <summary>common/mpq.c: mpq_adpcm_step[89].</summary>
        static readonly int[] AdpcmStep =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
            130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
            1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894,
            6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086,
            29794, 32767
        };

        /// <summary>common/mpq.c: Mpq_AdpcmDecode().</summary>
        internal static bool AdpcmDecode(byte[] src, int srcOffset, int srcSize,
            byte[] dst, int dstOffset, int cap, int channels, out int outSize)
        {
            outSize = 0;
            if (channels < 1 || channels > 2 || srcSize < 2 + channels * 2 || cap < channels * 2) return false;

            int cur = srcOffset + 2;
            int end = srcOffset + srcSize;
            int outPos = dstOffset;
            int outEnd = dstOffset + cap;
            int shift = src[srcOffset + 1];
            int channel = channels - 1;
            var predicted = new int[2];
            var stepIndex = new int[2];
            stepIndex[0] = AdpcmInitStep;
            stepIndex[1] = AdpcmInitStep;

            for (int i = 0; i < channels; i++)
            {
                predicted[i] = (short)(src[cur] | (src[cur + 1] << 8));
                cur += 2;
                WriteShort(dst, outPos, predicted[i]);
                outPos += 2;
            }

            while (cur < end)
            {
                int code = src[cur++];
                channel = (channel + 1) % channels;

                if (code == 0x80)
                {
                    if (stepIndex[channel] != 0) stepIndex[channel]--;
                }
                else if (code == 0x81)
                {
                    stepIndex[channel] += 8;
                    if (stepIndex[channel] > 88) stepIndex[channel] = 88;
                    channel = (channel + 1) % channels;
                    continue;
                }
                else
                {
                    int step = AdpcmStep[stepIndex[channel]];
                    int diff = step >> shift;
                    for (int bit = 0; bit < 6; bit++)
                        if ((code & (1 << bit)) != 0) diff += step >> bit;

                    predicted[channel] += (code & 0x40) != 0 ? -diff : diff;
                    if (predicted[channel] < -32768) predicted[channel] = -32768;
                    if (predicted[channel] > 32767) predicted[channel] = 32767;

                    stepIndex[channel] += AdpcmNext[code & 0x1f];
                    if (stepIndex[channel] < 0) stepIndex[channel] = 0;
                    if (stepIndex[channel] > 88) stepIndex[channel] = 88;
                }

                if (outEnd - outPos < 2) break;
                WriteShort(dst, outPos, predicted[channel]);
                outPos += 2;
            }

            outSize = outPos - dstOffset;
            return outSize != 0;
        }

        /// <summary>common/mpq.c: Mpq_WriteShort() - little-endian PCM16.</summary>
        static void WriteShort(byte[] dst, int pos, int value)
        {
            dst[pos] = (byte)value;
            dst[pos + 1] = (byte)(value >> 8);
        }

        // ------------------------------------------------------------ Huffman

        /// <summary>common/mpq.c: mpq_huff_profile7[256].</summary>
        static readonly byte[] HuffProfile7 = BuildProfile7();

        static byte[] BuildProfile7()
        {
            var table = new byte[256];
            byte[] first = { 0xc3, 0xd9, 0xef, 0x3d, 0xf9, 0x7c, 0xe9, 0x1e, 0xfd, 0xab, 0xf1, 0x2c, 0xfc, 0x5b, 0xfe, 0x17 };
            byte[] mid = { 0xbd, 0xd9, 0xec, 0x3d, 0xf5, 0x7d, 0xe8, 0x1d, 0xfb, 0xae, 0xf0, 0x2c, 0xfb, 0x5c, 0xff, 0x18 };
            for (int i = 0; i < first.Length; i++) table[i] = first[i];
            for (int i = 0; i < mid.Length; i++) table[64 + i] = mid[i];
            table[128] = 0x70;
            table[129] = 0x6c;
            return table;
        }

        [ThreadStatic] static HuffTree _huffmanTree;

        /// <summary>
        /// common/mpq.c: Mpq_HuffDecode(). Adaptive (FGK) Huffman decoding with the
        /// fixed profile-7 initial tree; profile bytes other than 7 are rejected.
        /// </summary>
        internal static bool HuffmanDecode(byte[] src, int srcOffset, int srcSize,
            byte[] dst, int dstOffset, int cap, out int outSize)
        {
            outSize = 0;
            var bits = new BitReader(src, srcOffset, srcSize);
            if (!bits.Read(8, out uint profile)) return false;

            HuffTree tree = _huffmanTree ?? (_huffmanTree = new HuffTree());
            if (!tree.Build(profile)) return false;

            int used = 0;
            while (used < cap)
            {
                HuffNode node = tree.Head.Next;
                while (node.Low != null)
                {
                    if (!bits.Read(1, out uint bit)) return false;
                    node = bit != 0 ? node.Low.Prev : node.Low;
                }

                uint value = node.Value;
                if (value == 256) { outSize = used; return true; }
                if (value == 257)
                {
                    if (!bits.Read(8, out value) || !tree.Insert(value)) return false;
                    node = tree.Symbols[value];
                    tree.Rebalance(node);
                }

                dst[dstOffset + used++] = (byte)value;
            }

            outSize = used;
            return true;
        }

        /// <summary>common/mpq.c: mpqBitReader_t / Mpq_ReadBits().</summary>
        sealed class BitReader
        {
            readonly byte[] _src;
            readonly int _end;
            int _pos;
            uint _bits;
            int _count;

            internal BitReader(byte[] src, int offset, int size)
            {
                _src = src;
                _pos = offset;
                _end = offset + size;
            }

            internal bool Read(int want, out uint value)
            {
                value = 0;
                while (_count < want)
                {
                    if (_pos >= _end) return false;
                    _bits |= (uint)_src[_pos++] << _count;
                    _count += 8;
                }

                value = _bits & ((1u << want) - 1);
                _bits >>= want;
                _count -= want;
                return true;
            }
        }

        /// <summary>common/mpq.c: mpqHuffNode_t.</summary>
        sealed class HuffNode
        {
            internal HuffNode Next, Prev, Parent, Low;
            internal uint Value, Weight;
        }

        /// <summary>common/mpq.c: MPQ_HUFF_SYMBOLS / MPQ_HUFF_NODES.</summary>
        const int HuffSymbolCount = 258;
        const int HuffNodeCount = 515;

        /// <summary>common/mpq.c: mpqHuffTree_t and its Mpq_Huff* helpers.</summary>
        sealed class HuffTree
        {
            internal readonly HuffNode Head = new HuffNode();
            readonly HuffNode[] _nodes = new HuffNode[HuffNodeCount];
            internal readonly HuffNode[] Symbols = new HuffNode[HuffSymbolCount];
            int _used;

            internal HuffTree()
            {
                for (int i = 0; i < HuffNodeCount; i++) _nodes[i] = new HuffNode();
            }

            /// <summary>common/mpq.c: Mpq_HuffLink().</summary>
            static void Link(HuffNode at, HuffNode node)
            {
                node.Next = at.Next;
                node.Prev = at;
                at.Next.Prev = node;
                at.Next = node;
            }

            /// <summary>common/mpq.c: Mpq_HuffRemove().</summary>
            static void Remove(HuffNode node)
            {
                if (node.Next == null) return;
                node.Prev.Next = node.Next;
                node.Next.Prev = node.Prev;
                node.Next = node.Prev = null;
            }

            /// <summary>common/mpq.c: Mpq_HuffNew().</summary>
            HuffNode New(uint value, uint weight, HuffNode at)
            {
                if (_used >= HuffNodeCount) return null;
                HuffNode node = _nodes[_used++];
                node.Next = node.Prev = node.Parent = node.Low = null;
                node.Value = value;
                node.Weight = weight;
                Link(at, node);
                return node;
            }

            /// <summary>common/mpq.c: Mpq_HuffHigher().</summary>
            HuffNode Higher(HuffNode node, uint weight)
            {
                for (; node != Head; node = node.Prev)
                    if (node.Weight >= weight) return node;
                return Head;
            }

            /// <summary>common/mpq.c: Mpq_HuffFix().</summary>
            uint Fix(HuffNode node, uint maxWeight)
            {
                if (node.Weight >= maxWeight) return node.Weight;
                HuffNode higher = Higher(Head.Prev, node.Weight);
                Remove(node);
                Link(higher, node);
                return maxWeight;
            }

            /// <summary>common/mpq.c: Mpq_HuffBuild().</summary>
            internal bool Build(uint profile)
            {
                if (profile != 7) return false;

                _used = 0;
                Array.Clear(Symbols, 0, Symbols.Length);
                Head.Next = Head.Prev = Head;
                Head.Parent = Head.Low = null;
                Head.Value = 0;
                Head.Weight = 0;

                uint maxWeight = 0;
                for (uint i = 0; i < 256; i++)
                {
                    if (HuffProfile7[i] == 0) continue;
                    HuffNode node = New(i, HuffProfile7[i], Head);
                    if (node == null) return false;
                    Symbols[i] = node;
                    maxWeight = Fix(node, maxWeight);
                }

                Symbols[256] = New(256, 1, Head.Prev);
                Symbols[257] = New(257, 1, Head.Prev);

                for (HuffNode low = Head.Prev; low != Head;)
                {
                    HuffNode high = low.Prev;
                    if (high == Head) break;
                    HuffNode parent = New(0, high.Weight + low.Weight, Head);
                    if (parent == null) return false;
                    low.Parent = parent;
                    high.Parent = parent;
                    parent.Low = low;
                    maxWeight = Fix(parent, maxWeight);
                    low = high.Prev;
                }

                return true;
            }

            /// <summary>common/mpq.c: Mpq_HuffRebalance().</summary>
            internal bool Rebalance(HuffNode node)
            {
                for (; node != null; node = node.Parent)
                {
                    node.Weight++;
                    HuffNode higher = Higher(node.Prev, node.Weight);
                    HuffNode swap = higher.Next;
                    if (swap == node) continue;

                    // The C original dereferences these unconditionally and would
                    // fault on a damaged tree; report a failed decode instead.
                    if (swap == null || swap.Parent == null || node.Parent == null) return false;

                    HuffNode low = swap.Parent.Low;
                    HuffNode parent = node.Parent;
                    Remove(swap);
                    Link(node, swap);
                    Remove(node);
                    Link(higher, node);

                    if (parent.Low == node) parent.Low = swap;
                    if (low == swap) swap.Parent.Low = node;
                    node.Parent = swap.Parent;
                    swap.Parent = parent;
                }

                return true;
            }

            /// <summary>common/mpq.c: Mpq_HuffInsert().</summary>
            internal bool Insert(uint value)
            {
                HuffNode escape = Head.Prev;
                HuffNode high = New(escape.Value, escape.Weight, Head.Prev);
                HuffNode low = New(value, 0, Head.Prev);
                if (high == null || low == null) return false;
                high.Parent = escape;
                low.Parent = escape;
                escape.Low = low;
                Symbols[value] = low;
                return Rebalance(low);
            }
        }

        // ------------------------------------------------------- PKWARE implode

        /// <summary>
        /// vendor/blast/blast.c: blast(). Returns the C error codes unchanged
        /// (2 input exhausted, 1 output error, 0 success, negative format errors).
        /// </summary>
        internal static int Blast(byte[] input, int inputOffset, int inputSize,
            byte[] dst, int dstOffset, int dstCapacity, out int outSize)
        {
            var state = new BlastState
            {
                In = input,
                InPos = inputOffset,
                Left = inputSize,
                Dst = dst,
                DstOffset = dstOffset,
                DstCapacity = dstCapacity,
                Next = 0,
                First = 1
            };

            int err;
            try
            {
                err = BlastDecomp(state);
            }
            catch (BlastInputExhausted)
            {
                err = 2;
            }

            if (err != 1 && state.Next != 0 && state.Flush() && err == 0) err = 1;
            outSize = state.DstPos;
            return err;
        }

        const int BlastMaxBits = 13;
        const int BlastMaxWin = 4096;

        sealed class BlastInputExhausted : Exception
        {
            internal static readonly BlastInputExhausted Instance = new BlastInputExhausted();
            BlastInputExhausted() : base("blast: out of input") { }
        }

        sealed class BlastState
        {
            internal byte[] In;
            internal int InPos;
            internal int Left;
            internal int BitBuf;
            internal int BitCnt;
            internal int Next;
            internal int First;
            internal readonly byte[] Window = new byte[BlastMaxWin];
            internal byte[] Dst;
            internal int DstOffset;
            internal int DstCapacity;
            internal int DstPos;

            /// <summary>vendor/blast/blast.c: outfun callback (Mpq_BlastOutput).</summary>
            internal bool Flush()
            {
                int len = Next;
                if (len > DstCapacity - DstPos) return true;
                Buffer.BlockCopy(Window, 0, Dst, DstOffset + DstPos, len);
                DstPos += len;
                return false;
            }
        }

        /// <summary>vendor/blast/blast.c: bits().</summary>
        static int BlastBits(BlastState s, int need)
        {
            int val = s.BitBuf;
            while (s.BitCnt < need)
            {
                if (s.Left == 0) throw BlastInputExhausted.Instance;
                val |= s.In[s.InPos++] << s.BitCnt;
                s.Left--;
                s.BitCnt += 8;
            }

            s.BitBuf = val >> need;
            s.BitCnt -= need;
            return val & ((1 << need) - 1);
        }

        sealed class BlastHuffman
        {
            internal readonly short[] Count = new short[BlastMaxBits + 1];
            internal readonly short[] Symbol;

            internal BlastHuffman(int symbolCount) { Symbol = new short[symbolCount]; }
        }

        static readonly BlastHuffman BlastLitCode;
        static readonly BlastHuffman BlastLenCode;
        static readonly BlastHuffman BlastDistCode;

        /// <summary>vendor/blast/blast.c: the one-time table build in decomp().</summary>
        static MpqDecompress()
        {
            byte[] litlen =
            {
                11, 124, 8, 7, 28, 7, 188, 13, 76, 4, 10, 8, 12, 10, 12, 10, 8, 23, 8,
                9, 7, 6, 7, 8, 7, 6, 55, 8, 23, 24, 12, 11, 7, 9, 11, 12, 6, 7, 22, 5,
                7, 24, 6, 11, 9, 6, 7, 22, 7, 11, 38, 7, 9, 8, 25, 11, 8, 11, 9, 12,
                8, 12, 5, 38, 5, 38, 5, 11, 7, 5, 6, 21, 6, 10, 53, 8, 7, 24, 10, 27,
                44, 253, 253, 253, 252, 252, 252, 13, 12, 45, 12, 45, 12, 61, 12, 45,
                44, 173
            };
            byte[] lenlen = { 2, 35, 36, 53, 38, 23 };
            byte[] distlen = { 2, 20, 53, 230, 247, 151, 248 };

            BlastLitCode = new BlastHuffman(256);
            BlastLenCode = new BlastHuffman(16);
            BlastDistCode = new BlastHuffman(64);
            BlastConstruct(BlastLitCode, litlen);
            BlastConstruct(BlastLenCode, lenlen);
            BlastConstruct(BlastDistCode, distlen);
        }

        /// <summary>vendor/blast/blast.c: construct().</summary>
        static void BlastConstruct(BlastHuffman h, byte[] rep)
        {
            var length = new short[256];
            int symbol = 0;
            int n = rep.Length;
            int idx = 0;
            do
            {
                int len = rep[idx++];
                int left = (len >> 4) + 1;
                len &= 15;
                do
                {
                    length[symbol++] = (short)len;
                } while (--left != 0);
            } while (--n != 0);

            n = symbol;
            for (int len = 0; len <= BlastMaxBits; len++) h.Count[len] = 0;
            for (symbol = 0; symbol < n; symbol++) h.Count[length[symbol]]++;
            if (h.Count[0] == n) return;

            int remaining = 1;
            for (int len = 1; len <= BlastMaxBits; len++)
            {
                remaining <<= 1;
                remaining -= h.Count[len];
                if (remaining < 0) return;
            }

            var offs = new short[BlastMaxBits + 1];
            offs[1] = 0;
            for (int len = 1; len < BlastMaxBits; len++)
                offs[len + 1] = (short)(offs[len] + h.Count[len]);

            for (symbol = 0; symbol < n; symbol++)
                if (length[symbol] != 0)
                    h.Symbol[offs[length[symbol]]++] = (short)symbol;
        }

        /// <summary>vendor/blast/blast.c: decode().</summary>
        static int BlastDecode(BlastState s, BlastHuffman h)
        {
            int bitbuf = s.BitBuf;
            int left = s.BitCnt;
            int code = 0, first = 0, index = 0, len = 1;
            int next = 1;

            while (true)
            {
                while (left-- != 0)
                {
                    code |= (bitbuf & 1) ^ 1;
                    bitbuf >>= 1;
                    int count = h.Count[next++];
                    if (code < first + count)
                    {
                        s.BitBuf = bitbuf;
                        s.BitCnt = (s.BitCnt - len) & 7;
                        int sym = index + (code - first);
                        if (sym < 0 || sym >= h.Symbol.Length) return -9;
                        return h.Symbol[sym];
                    }
                    index += count;
                    first += count;
                    first <<= 1;
                    code <<= 1;
                    len++;
                }

                left = (BlastMaxBits + 1) - len;
                if (left == 0) break;
                if (s.Left == 0) throw BlastInputExhausted.Instance;
                bitbuf = s.In[s.InPos++];
                s.Left--;
                if (left > 8) left = 8;
            }

            return -9;
        }

        static readonly short[] BlastBase = { 3, 2, 4, 5, 6, 7, 8, 9, 10, 12, 16, 24, 40, 72, 136, 264 };
        static readonly byte[] BlastExtraBits = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };

        /// <summary>vendor/blast/blast.c: decomp().</summary>
        static int BlastDecomp(BlastState s)
        {
            int lit = BlastBits(s, 8);
            if (lit > 1) return -1;
            int dict = BlastBits(s, 8);
            if (dict < 4 || dict > 6) return -2;

            while (true)
            {
                if (BlastBits(s, 1) != 0)
                {
                    int symbol = BlastDecode(s, BlastLenCode);
                    if (symbol < 0 || symbol > 15) return symbol < 0 ? symbol : -9;
                    int len = BlastBase[symbol] + BlastBits(s, BlastExtraBits[symbol]);
                    if (len == 519) break;

                    symbol = len == 2 ? 2 : dict;
                    int dec = BlastDecode(s, BlastDistCode);
                    if (dec < 0 || dec > 63) return dec < 0 ? dec : -9;
                    uint dist = (uint)(dec << symbol);
                    dist += (uint)BlastBits(s, symbol);
                    dist++;
                    if (dist > BlastMaxWin) return -3;
                    if (s.First != 0 && dist > (uint)s.Next) return -3;

                    do
                    {
                        int to = s.Next;
                        int from = to - (int)dist;
                        int copy = BlastMaxWin;
                        if (s.Next < (int)dist)
                        {
                            from += copy;
                            copy = (int)dist;
                        }
                        copy -= s.Next;
                        if (copy > len) copy = len;
                        len -= copy;
                        s.Next += copy;
                        do
                        {
                            s.Window[to++] = s.Window[from++];
                        } while (--copy != 0);

                        if (s.Next == BlastMaxWin)
                        {
                            if (s.Flush()) return 1;
                            s.Next = 0;
                            s.First = 0;
                        }
                    } while (len != 0);
                }
                else
                {
                    int symbol = lit != 0 ? BlastDecode(s, BlastLitCode) : BlastBits(s, 8);
                    s.Window[s.Next++] = (byte)symbol;
                    if (s.Next == BlastMaxWin)
                    {
                        if (s.Flush()) return 1;
                        s.Next = 0;
                        s.First = 0;
                    }
                }
            }

            return 0;
        }
    }
}
