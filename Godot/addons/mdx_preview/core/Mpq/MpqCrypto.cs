using System;
using System.Buffers.Binary;

namespace MdxPreview.Core.Mpq
{
    /// <summary>
    /// MPQ hash types (common/mpq.c: MPQ_HASH_TABLE_OFFSET, MPQ_HASH_NAME_A,
    /// MPQ_HASH_NAME_B, MPQ_HASH_FILE_KEY).
    /// </summary>
    internal static class MpqHashType
    {
        internal const uint TableOffset = 0;
        internal const uint NameA = 1;
        internal const uint NameB = 2;
        internal const uint FileKey = 3;
    }

    /// <summary>
    /// Block table file flags (common/mpq.c: MPQ_FILE_*). The reader only tests
    /// <see cref="Compress"/>, <see cref="Encrypted"/>, <see cref="KeyV2"/> and
    /// <see cref="SingleUnit"/>; the rest are kept for completeness/telemetry.
    /// </summary>
    internal static class MpqFileFlags
    {
        internal const uint Implode = 0x00000100;
        internal const uint Compress = 0x00000200;
        internal const uint Encrypted = 0x00010000;

        /// <summary>
        /// common/mpq.c: MPQ_FILE_KEY_V2 (a.k.a. FIX_KEY). When set, the file key
        /// is adjusted with the block offset and the file size.
        /// </summary>
        internal const uint KeyV2 = 0x00020000;

        internal const uint SingleUnit = 0x01000000;
        internal const uint Exists = 0x80000000;
    }

    /// <summary>
    /// Storm hashing/encryption primitives, ported line by line from
    /// common/mpq.c (InitStormBuffer, HashString, DecryptBlock, EncryptBlock,
    /// NormalizeMpqPath, TrimEdgeSlashes, CanonicalizeMpqKey, BaseNamePtr).
    /// All arithmetic is intentionally wrapping 32-bit like the C originals.
    /// </summary>
    internal static class MpqCrypto
    {
        /// <summary>common/mpq.c: MPQ_KEY_HASH_TABLE.</summary>
        internal const uint KeyHashTable = 0xC3AF3770;

        /// <summary>common/mpq.c: MPQ_KEY_BLOCK_TABLE.</summary>
        internal const uint KeyBlockTable = 0xEC83B3A3;

        /// <summary>common/mpq.c: MPQ_HASH_KEY2_MIX (second seed salt table offset).</summary>
        const int Key2Mix = 0x400;

        const int StormBufferSize = 0x500;

        /// <summary>Canonical key buffer size used by common/mpq.c (char key[1024]).</summary>
        const int CanonicalKeyCapacity = 1024;

        /// <summary>common/mpq.c: storm_buffer (0x500 entries built by InitStormBuffer).</summary>
        static readonly uint[] StormBuffer = CreateStormBuffer();

        /// <summary>common/mpq.c: InitStormBuffer().</summary>
        static uint[] CreateStormBuffer()
        {
            var table = new uint[StormBufferSize];
            uint seed = 0x00100001;
            for (uint index1 = 0; index1 < 0x100; index1++)
            {
                for (uint index2 = index1, i = 0; i < 5; i++, index2 += 0x100)
                {
                    unchecked
                    {
                        seed = (seed * 125 + 3) % 0x2AAAAB;
                        uint temp1 = (seed & 0xFFFF) << 0x10;
                        seed = (seed * 125 + 3) % 0x2AAAAB;
                        uint temp2 = seed & 0xFFFF;
                        table[index2] = temp1 | temp2;
                    }
                }
            }
            return table;
        }

        /// <summary>common/mpq.c: AsciiToUpper(). '/', '/' are folded to '\'.</summary>
        internal static byte AsciiToUpper(byte ch)
        {
            if (ch >= (byte)'a' && ch <= (byte)'z') return (byte)(ch - 0x20);
            if (ch == (byte)'/') return (byte)'\\';
            return ch;
        }

        /// <summary>common/mpq.c: HashString().</summary>
        internal static uint HashString(string str, uint hashType)
        {
            uint seed1 = 0x7FED7FED;
            uint seed2 = 0xEEEEEEEE;
            int tableBase = (int)(hashType * 0x100);

            for (int i = 0; i < str.Length; i++)
            {
                byte ch = AsciiToUpper((byte)str[i]);
                unchecked
                {
                    seed1 = StormBuffer[tableBase + ch] ^ (seed1 + seed2);
                    seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
                }
            }

            return seed1;
        }

        /// <summary>
        /// common/mpq.c: DecryptBlock(). Only whole 32-bit words are processed;
        /// a trailing 1-3 byte remainder is left untouched, exactly like the C
        /// implementation ("leftover bytes stay ciphertext/plaintext as stored").
        /// </summary>
        internal static void DecryptBlock(byte[] data, int offset, int size, uint seed1)
        {
            uint seed2 = 0xEEEEEEEE;
            int nblocks = size >> 2;

            for (int i = 0; i < nblocks; i++)
            {
                int pos = offset + (i << 2);
                Span<byte> span = data.AsSpan(pos, 4);
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(span);
                unchecked
                {
                    uint seed2Base = seed2 + StormBuffer[Key2Mix + (int)(seed1 & 0xFF)];
                    value ^= seed1 + seed2Base;
                    seed1 = ((~seed1 << 0x15) + 0x11111111) | (seed1 >> 0x0B);
                    seed2 = value + seed2Base + (seed2Base << 5) + 3;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            }
        }

        /// <summary>
        /// common/mpq.c: EncryptBlock(). Mirrors <see cref="DecryptBlock"/>; the
        /// reader never encrypts, but the writer's counterpart is needed to build
        /// test archives and is kept symmetric with the C source.
        /// </summary>
        internal static void EncryptBlock(byte[] data, int offset, int size, uint seed1)
        {
            uint seed2 = 0xEEEEEEEE;
            int nblocks = size >> 2;

            for (int i = 0; i < nblocks; i++)
            {
                int pos = offset + (i << 2);
                Span<byte> span = data.AsSpan(pos, 4);
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(span);
                unchecked
                {
                    uint seed2Base = seed2 + StormBuffer[Key2Mix + (int)(seed1 & 0xFF)];
                    BinaryPrimitives.WriteUInt32LittleEndian(span, value ^ (seed1 + seed2Base));
                    seed1 = ((~seed1 << 0x15) + 0x11111111) | (seed1 >> 0x0B);
                    seed2 = value + seed2Base + (seed2Base << 5) + 3;
                }
            }
        }

        /// <summary>common/mpq.c: NormalizeMpqPath() — '/' becomes '\'.</summary>
        internal static string NormalizeMpqPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            if (s.IndexOf('/') < 0) return s;
            return s.Replace('/', '\\');
        }

        /// <summary>common/mpq.c: TrimEdgeSlashes() — strips leading/trailing separators.</summary>
        internal static string TrimEdgeSlashes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            int start = 0;
            int end = s.Length;
            while (end > start && (s[end - 1] == '\\' || s[end - 1] == '/')) end--;
            while (start < end && (s[start] == '\\' || s[start] == '/')) start++;
            if (start == 0 && end == s.Length) return s;
            return s.Substring(start, end - start);
        }

        /// <summary>
        /// common/mpq.c: CanonicalizeMpqKey(). MPQ names are case-insensitive and
        /// backslash separated, and every lookup hashes this exact form. C writes
        /// into a 1024-byte buffer; the same cap is applied here.
        /// </summary>
        internal static string CanonicalizeKey(string src)
        {
            if (string.IsNullOrEmpty(src)) return string.Empty;

            int n = 0;
            int limit = Math.Min(src.Length, CanonicalKeyCapacity - 1);
            var buffer = new char[limit];
            for (int i = 0; i < limit; i++)
            {
                char ch = src[i];
                if (ch == '/') ch = '\\';
                else if (ch >= 'A' && ch <= 'Z') ch = (char)(ch + 0x20);
                buffer[n++] = ch;
            }

            return TrimEdgeSlashes(new string(buffer, 0, n));
        }

        /// <summary>common/mpq.c: BaseNamePtr() — the part after the last separator.</summary>
        internal static string BaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? string.Empty;
            int slash1 = path.LastIndexOf('\\');
            int slash2 = path.LastIndexOf('/');
            if (slash2 >= 0 && (slash1 < 0 || slash2 > slash1)) return path.Substring(slash2 + 1);
            if (slash1 >= 0) return path.Substring(slash1 + 1);
            return path;
        }
    }
}
