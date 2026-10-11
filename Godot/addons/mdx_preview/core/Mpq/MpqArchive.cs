using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MdxPreview.Core.Assets;

namespace MdxPreview.Core.Mpq
{
    public class MpqException : Exception
    {
        public MpqException(string message) : base(message) { }
        public MpqException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class MpqFileNotFoundException : MpqException
    {
        public MpqFileNotFoundException(string archive, string path)
            : base($"'{path}' not found in archive '{archive}'") { }
    }

    public readonly struct MpqFileInfo
    {
        public MpqFileInfo(string path, uint size, uint compressedSize, uint flags, uint locale, uint blockIndex)
        {
            Path = path;
            Size = size;
            CompressedSize = compressedSize;
            Flags = flags;
            Locale = locale;
            BlockIndex = blockIndex;
        }

        public string Path { get; }
        public uint Size { get; }
        public uint CompressedSize { get; }
        public uint Flags { get; }
        public uint Locale { get; }
        public uint BlockIndex { get; }

        public bool IsCompressed => (Flags & 0x00000200) != 0;
        public bool IsEncrypted => (Flags & 0x00010000) != 0;
        public bool IsSingleUnit => (Flags & 0x01000000) != 0;

        public override string ToString()
            => $"{Path} size={Size} comp={CompressedSize} flags=0x{Flags:X8}";
    }

    /// <summary>
    /// Per-read diagnostics: which decompression branch each sector took. Purely
    /// observational (common/mpq.c prints the same information behind
    /// BZ_MPQ_TRACE) and used by the probe tests as evidence.
    /// </summary>
    internal sealed class MpqReadTrace
    {
        internal readonly List<string> Steps = new List<string>();

        internal void Add(string step) => Steps.Add(step);

        internal string Summary => Steps.Count == 0 ? "(no sectors)" : string.Join(", ", Steps);
    }

    /// <summary>
    /// Reader for Blizzard MPQ archives (format versions 0 and 1, as used by
    /// Warcraft III). Pure C# port of common/mpq.c: hash/block tables, block
    /// encryption, and the sector decompressors (zlib, Blizzard ADPCM, PKWARE
    /// DCL implode, adaptive Huffman, and their combinations).
    ///
    /// Archives are read on demand through <see cref="RandomAccess"/> against the
    /// original file handle; a 400 MB archive is never loaded into memory.
    /// </summary>
    public sealed class MpqArchive : IDisposable
    {
        // common/mpq.c: "MPQ\x1a" as a little-endian dword.
        const uint ArchiveSignature = 0x1A51504D;

        // common/mpq.c: MPQ_HASH_ENTRY_DELETED / MPQ_HASH_ENTRY_FREE / MPQ_BLOCK_INDEX_MASK.
        const uint HashEntryDeleted = 0xFFFFFFFE;
        const uint HashEntryFree = 0xFFFFFFFF;
        const uint BlockIndexMask = 0x0FFFFFFF;

        // common/mpq.c: sizeof(mpqHeaderV1_t) / sizeof(mpqHashEntry_t) / sizeof(mpqBlockEntry_t).
        const int HeaderSize = 32;
        const int HashEntrySize = 16;
        const int BlockEntrySize = 16;

        // common/mpq.c: MPQ_SECTOR_SIZE_SHIFT_MAX.
        const int SectorSizeShiftMax = 15;

        // common/mpq.c: the signature is searched in the first 1024 bytes (maps
        // carry a 512-byte prefix header in front of the archive).
        const int SignatureProbeSize = 1024;

        const string ListFileName = "(listfile)";

        readonly object _sync = new object();
        readonly string _name;
        readonly string _filePath;
        readonly byte[] _memory;            // non-null for OpenFromMemory
        readonly FileStream _stream;        // non-null for Open(path)
        readonly long _sourceLength;
        readonly uint _baseOffset;
        readonly uint _formatVersion;
        readonly ushort _sectorSizeShift;
        readonly uint _sectorSize;
        readonly uint[] _hashTable;         // 4 dwords per entry: hash1, hash2, packed locale/platform/flags, blockIndex
        readonly uint _hashTableSize;       // entry count
        readonly uint[] _blockTable;        // 4 dwords per entry: offset, compressed size, file size, flags
        readonly uint _blockTableSize;      // entry count

        readonly Dictionary<string, uint> _lookupCache = new Dictionary<string, uint>(StringComparer.Ordinal);
        readonly Dictionary<ulong, string> _hashPairIndex = new Dictionary<ulong, string>();
        readonly Lazy<string[]> _listfile;

        int _disposed;

        MpqArchive(string filePath, FileStream stream, string name, byte[] memory)
        {
            _filePath = filePath;
            _stream = stream;
            _name = name;
            _memory = memory;
            _sourceLength = memory != null ? memory.Length : stream.Length;
            _listfile = new Lazy<string[]>(BuildListfileIndex, LazyThreadSafetyMode.ExecutionAndPublication);

            _baseOffset = FindArchiveSignature();

            // common/mpq.c: read mpqHeaderV1_t at base_offset. Note that the C reader
            // reads exactly this 32-byte structure for every format version: the
            // v1 extended header (hi-block table / high table position bits) is not
            // consumed, and sector sizes come from wSectorSizeShift, not the block flags.
            var header = new byte[HeaderSize];
            ReadAt(_baseOffset, header, 0, HeaderSize);

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            if (id != ArchiveSignature)
                throw new MpqException($"{_name}: not an MPQ archive (signature 0x{id:X8})");

            _formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12, 2));
            _sectorSizeShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14, 2));
            uint hashTablePos = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
            uint blockTablePos = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
            _hashTableSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4));
            _blockTableSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4));

            if (_sectorSizeShift > SectorSizeShiftMax)
                throw new MpqException($"{_name}: unsupported wSectorSizeShift {_sectorSizeShift} (max {SectorSizeShiftMax})");
            _sectorSize = 1u << (_sectorSizeShift + 9);

            if (_hashTableSize == 0)
                throw new MpqException($"{_name}: empty hash table");
            if (_blockTableSize == 0)
                throw new MpqException($"{_name}: empty block table");

            // The C reader mallocs blindly and fails when the read comes up short;
            // check the table fits the source first so a damaged header cannot ask
            // for a multi-gigabyte allocation.
            RequireWithinSource(hashTablePos, (long)_hashTableSize * HashEntrySize, "hash table");
            RequireWithinSource(blockTablePos, (long)_blockTableSize * BlockEntrySize, "block table");

            var hashBytes = new byte[checked((int)((long)_hashTableSize * HashEntrySize))];
            ReadAt(_baseOffset + hashTablePos, hashBytes, 0, hashBytes.Length);
            MpqCrypto.DecryptBlock(hashBytes, 0, hashBytes.Length, MpqCrypto.KeyHashTable);
            _hashTable = ToUInt32Array(hashBytes);

            var blockBytes = new byte[checked((int)((long)_blockTableSize * BlockEntrySize))];
            ReadAt(_baseOffset + blockTablePos, blockBytes, 0, blockBytes.Length);
            MpqCrypto.DecryptBlock(blockBytes, 0, blockBytes.Length, MpqCrypto.KeyBlockTable);
            _blockTable = ToUInt32Array(blockBytes);
        }

        // ------------------------------------------------------------- opening

        public static MpqArchive Open(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("MPQ path is required", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException($"MPQ archive not found: {path}", path);

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
            try
            {
                return new MpqArchive(Path.GetFullPath(path), stream, Path.GetFileName(path), null);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public static MpqArchive OpenFromMemory(byte[] archiveBytes, string name)
        {
            if (archiveBytes == null) throw new ArgumentNullException(nameof(archiveBytes));
            string label = string.IsNullOrEmpty(name) ? "<memory>" : name;
            return new MpqArchive(null, null, label, archiveBytes);
        }

        /// <summary>Display name: the file name for disk archives, or the given name.</summary>
        public string Name => _name;

        /// <summary>Full path of the backing file, or null for memory archives.</summary>
        public string FilePath => _filePath;

        /// <summary>MPQ format version (0 or 1).</summary>
        public uint FormatVersion => _formatVersion;

        /// <summary>
        /// Number of block table entries: the archive's file slot count, which is
        /// the MPQ-level file count (common/mpq.c: header.dwBlockTableSize). Use
        /// <see cref="ListFiles"/> for the number of named entries.
        /// </summary>
        public int FileCount => (int)Math.Min(_blockTableSize, int.MaxValue);

        /// <summary>
        /// Archive sector size in bytes (512 &lt;&lt; wSectorSizeShift), as
        /// common/mpq.c derives it from the header. Kept internal so the public
        /// surface stays exactly what the API contract declares.
        /// </summary>
        internal uint SectorSize => _sectorSize;

        /// <summary>Offset of the archive inside the file (non-zero for .w3m/.w3x maps).</summary>
        internal uint BaseOffset => _baseOffset;

        /// <summary>Diagnostics for the most recent read on this archive.</summary>
        internal MpqReadTrace LastTrace { get; private set; }

        // ------------------------------------------------------------- reading

        /// <summary>
        /// Reads a whole entry. Returns false when the entry does not exist; an
        /// entry that exists but cannot be decoded raises <see cref="MpqException"/>
        /// (the C reader reports both as a failed read, but silently truncating a
        /// damaged file is not useful to callers).
        /// </summary>
        public bool TryReadFile(string archivePath, out byte[] data)
        {
            data = null;
            ThrowIfDisposed();
            LastTrace = new MpqReadTrace();

            if (!TryResolveEntry(archivePath, out uint blockIndex, out string canonicalName)) return false;

            // A name that resolves but cannot be decoded is an archive integrity
            // problem, not a missing file: surface it instead of returning nothing.
            if (!ReadEntry(blockIndex, canonicalName, out data, out string error))
                throw new MpqException($"{_name}: cannot read '{archivePath}': {error}");
            return true;
        }

        /// <summary>
        /// Reads a whole entry. Throws <see cref="MpqFileNotFoundException"/> when
        /// the archive has no such entry and <see cref="MpqException"/> when the
        /// stored data cannot be decoded.
        /// </summary>
        public byte[] ReadFile(string archivePath)
        {
            ThrowIfDisposed();
            LastTrace = new MpqReadTrace();

            if (!TryResolveEntry(archivePath, out uint blockIndex, out string canonicalName))
                throw new MpqFileNotFoundException(_name, archivePath);

            if (!ReadEntry(blockIndex, canonicalName, out byte[] data, out string error))
                throw new MpqException($"{_name}: cannot read '{archivePath}': {error}");
            return data;
        }

        /// <summary>
        /// True when the archive's tables contain the entry. This matches the C
        /// reader's name resolution (SFileFindFirstFile/SFileFindNextFile); a
        /// decoded read can still fail if the stored data is damaged.
        /// </summary>
        public bool Contains(string archivePath)
        {
            ThrowIfDisposed();
            return TryResolveEntry(archivePath, out _, out _);
        }

        public bool TryGetFileInfo(string archivePath, out MpqFileInfo info)
        {
            ThrowIfDisposed();
            info = default;
            string canonical = MpqCrypto.CanonicalizeKey(archivePath);
            if (canonical.Length == 0) return false;

            uint hash1 = MpqCrypto.HashString(canonical, MpqHashType.NameA);
            uint hash2 = MpqCrypto.HashString(canonical, MpqHashType.NameB);

            // Resolved without the listfile cache so the owning hash entry (and thus
            // the locale) is available, mirroring SFileFindNextFile's lookup.
            if (!FindBlockIndex(hash1, hash2, out uint blockIndex, out uint hashEntry)) return false;
            if (blockIndex >= _blockTableSize) return false;

            uint locale = _hashTable[(hashEntry * 4) + 2] & 0xFFFF;
            info = new MpqFileInfo(
                VirtualPath.Normalize(archivePath),
                _blockTable[(blockIndex * 4) + 2],
                _blockTable[(blockIndex * 4) + 1],
                _blockTable[(blockIndex * 4) + 3],
                locale,
                blockIndex);
            return true;
        }

        /// <summary>Paths from "(listfile)"; empty when the archive has none.</summary>
        public IReadOnlyList<string> ListFiles()
        {
            ThrowIfDisposed();
            return _listfile.Value;
        }

        /// <summary>Enumerates (listfile) entries matching a mask such as "*.blp".</summary>
        public IEnumerable<string> FindFiles(string mask)
        {
            ThrowIfDisposed();
            foreach (string path in _listfile.Value)
                if (VirtualPath.Matches(path, mask)) yield return path;
        }

        // --------------------------------------------------------- name lookup

        /// <summary>
        /// common/mpq.c: SFileOpenFileDirect() name resolution — canonicalize,
        /// hash with NAME_A/NAME_B, consult the listfile cache, then probe the hash
        /// table (with the C reader's full-scan fallback).
        /// </summary>
        bool TryResolveEntry(string archivePath, out uint blockIndex, out string canonicalName)
        {
            blockIndex = 0;
            canonicalName = MpqCrypto.CanonicalizeKey(archivePath);
            if (canonicalName.Length == 0) return false;

            // Builds (and caches) the listfile-derived name index on first use; the
            // factory never re-enters this method, so there is no recursion.
            _ = _listfile.Value;

            lock (_sync)
            {
                if (_lookupCache.TryGetValue(canonicalName, out blockIndex))
                    return blockIndex < _blockTableSize;
            }

            uint hash1 = MpqCrypto.HashString(canonicalName, MpqHashType.NameA);
            uint hash2 = MpqCrypto.HashString(canonicalName, MpqHashType.NameB);
            if (!FindBlockIndex(hash1, hash2, out blockIndex, out _)) return false;

            lock (_sync)
            {
                _lookupCache[canonicalName] = blockIndex;
            }
            return blockIndex < _blockTableSize;
        }

        /// <summary>
        /// common/mpq.c: FindBlockIndex(). Probes from (hash1 &amp; mask), stops at
        /// the first free slot, then falls back to a full table scan — both of
        /// which the C reader performs.
        /// </summary>
        bool FindBlockIndex(uint hash1, uint hash2, out uint blockIndex, out uint hashEntry)
        {
            blockIndex = 0;
            hashEntry = 0;
            if (_hashTableSize == 0) return false;

            uint mask = _hashTableSize - 1;
            uint hashPos = hash1 & mask;

            for (uint index = 0; index < _hashTableSize; index++)
            {
                uint slot = (hashPos + index) & mask;
                uint entryBlock = _hashTable[(slot * 4) + 3];

                if (entryBlock == HashEntryFree) break;
                if (entryBlock == HashEntryDeleted) continue;

                if (_hashTable[slot * 4] == hash1 && _hashTable[(slot * 4) + 1] == hash2)
                {
                    blockIndex = entryBlock & BlockIndexMask;
                    hashEntry = slot;
                    return true;
                }
            }

            for (uint index = 0; index < _hashTableSize; index++)
            {
                uint entryBlock = _hashTable[(index * 4) + 3];
                if (entryBlock == HashEntryDeleted || entryBlock == HashEntryFree) continue;

                if (_hashTable[index * 4] == hash1 && _hashTable[(index * 4) + 1] == hash2)
                {
                    blockIndex = entryBlock & BlockIndexMask;
                    hashEntry = index;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// common/mpq.c: PreloadListfileCache(). The C reader caches listfile names
        /// against matching hash entries at open time; the same index is built here
        /// on first use (observationally equivalent, just lazy) and shared through
        /// <see cref="Lazy{T}"/> so concurrent readers agree on the result.
        /// </summary>
        string[] BuildListfileIndex()
        {
            string[] entries;
            MpqReadTrace savedTrace = LastTrace;
            LastTrace = null; // the listfile read must not pollute the caller's trace
            try
            {
                if (!TryResolveEntryDirect(ListFileName, out uint blockIndex, out string canonicalName) ||
                    !ReadEntry(blockIndex, canonicalName, out byte[] data, out _))
                {
                    entries = Array.Empty<string>();
                }
                else
                {
                    entries = SplitListfile(data);
                }
            }
            catch (MpqException)
            {
                entries = Array.Empty<string>();
            }
            finally
            {
                LastTrace = savedTrace;
            }

            lock (_sync)
            {
                // Names seen in (listfile) that own a hash entry: cache name -> block
                // index exactly like the C preload, so later lookups skip the probe.
                foreach (string entry in entries)
                {
                    string canonical = MpqCrypto.CanonicalizeKey(entry);
                    if (canonical.Length == 0) continue;
                    uint hash1 = MpqCrypto.HashString(canonical, MpqHashType.NameA);
                    uint hash2 = MpqCrypto.HashString(canonical, MpqHashType.NameB);
                    _hashPairIndex[PairKey(hash1, hash2)] = canonical;
                }

                if (_hashPairIndex.Count != 0)
                {
                    for (uint i = 0; i < _hashTableSize; i++)
                    {
                        uint entryBlock = _hashTable[(i * 4) + 3];
                        if (entryBlock == HashEntryFree || entryBlock == HashEntryDeleted) continue;

                        if (_hashPairIndex.TryGetValue(PairKey(_hashTable[i * 4], _hashTable[(i * 4) + 1]), out string name))
                            _lookupCache[name] = entryBlock & BlockIndexMask;
                    }
                }
            }

            return entries;
        }

        /// <summary>Name resolution that never consults the listfile index (no recursion).</summary>
        bool TryResolveEntryDirect(string archivePath, out uint blockIndex, out string canonicalName)
        {
            blockIndex = 0;
            canonicalName = MpqCrypto.CanonicalizeKey(archivePath);
            if (canonicalName.Length == 0) return false;

            uint hash1 = MpqCrypto.HashString(canonicalName, MpqHashType.NameA);
            uint hash2 = MpqCrypto.HashString(canonicalName, MpqHashType.NameB);
            if (!FindBlockIndex(hash1, hash2, out blockIndex, out _)) return false;
            return blockIndex < _blockTableSize;
        }

        static ulong PairKey(uint hash1, uint hash2) => ((ulong)hash1 << 32) | hash2;

        /// <summary>
        /// common/mpq.c: the line splitting in PreloadListfileCache()/SFileFindFirstFile()
        /// — CR/LF separated, empty lines dropped, paths normalized to backslashes
        /// with edge separators trimmed.
        /// </summary>
        static string[] SplitListfile(byte[] data)
        {
            var list = new List<string>();
            int i = 0;
            int length = data.Length;

            while (i < length)
            {
                while (i < length && (data[i] == (byte)'\r' || data[i] == (byte)'\n')) i++;
                if (i >= length) break;

                int start = i;
                while (i < length && data[i] != (byte)'\r' && data[i] != (byte)'\n') i++;

                string line = System.Text.Encoding.Latin1.GetString(data, start, i - start);
                line = MpqCrypto.NormalizeMpqPath(line);
                line = MpqCrypto.TrimEdgeSlashes(line);
                if (line.Length == 0) continue;
                list.Add(line);
            }

            return list.ToArray();
        }

        // -------------------------------------------------------- entry decode

        /// <summary>
        /// common/mpq.c: SFileReadFile() for a whole file. Returns the decoded
        /// bytes, or false with a descriptive <paramref name="error"/>.
        /// </summary>
        bool ReadEntry(uint blockIndex, string canonicalName, out byte[] data, out string error)
        {
            data = null;
            error = null;

            if (blockIndex >= _blockTableSize)
            {
                error = $"block index {blockIndex} is outside the block table ({_blockTableSize} entries)";
                return false;
            }

            uint blockOffset = _blockTable[blockIndex * 4];
            uint blockSize = _blockTable[(blockIndex * 4) + 1];
            uint fileSize = _blockTable[(blockIndex * 4) + 2];
            uint flags = _blockTable[(blockIndex * 4) + 3];

            if (fileSize > int.MaxValue || blockSize > int.MaxValue)
            {
                error = $"file is too large for the managed reader (size={fileSize}, compressed={blockSize})";
                return false;
            }

            if (fileSize == 0) return Succeed(Array.Empty<byte>(), out data);

            // common/mpq.c: file key derivation, including the optional FIX_KEY
            // adjustment (MPQ_FILE_KEY_V2) that mixes in the block offset and size.
            uint fileKey = 0;
            if ((flags & MpqFileFlags.Encrypted) != 0)
            {
                fileKey = MpqCrypto.HashString(MpqCrypto.BaseName(canonicalName), MpqHashType.FileKey);
                if ((flags & MpqFileFlags.KeyV2) != 0)
                {
                    unchecked { fileKey = (fileKey + blockOffset) ^ fileSize; }
                }
            }

            if ((flags & MpqFileFlags.Compress) == 0)
            {
                // Uncompressed: read the plain range and decrypt it in place.
                var plain = new byte[fileSize];
                if (!TryReadRange(blockOffset, plain, 0, plain.Length, out error)) return false;
                if ((flags & MpqFileFlags.Encrypted) != 0)
                    MpqCrypto.DecryptBlock(plain, 0, plain.Length, fileKey);

                LastTrace?.Add($"stored size={fileSize} flags=0x{flags:X8}");
                return Succeed(plain, out data);
            }

            if ((flags & MpqFileFlags.SingleUnit) != 0)
                return ReadSingleUnit(blockOffset, blockSize, fileSize, flags, fileKey, out data, out error);

            return ReadSectors(blockOffset, blockSize, fileSize, flags, fileKey, out data, out error);
        }

        bool Succeed(byte[] value, out byte[] data)
        {
            data = value;
            return true;
        }

        /// <summary>common/mpq.c: the MPQ_FILE_SINGLE_UNIT branch of SFileReadFile().</summary>
        bool ReadSingleUnit(uint blockOffset, uint blockSize, uint fileSize, uint flags, uint fileKey,
            out byte[] data, out string error)
        {
            data = null;
            error = null;

            if (blockSize == 0)
            {
                error = "single-unit block has zero compressed size";
                return false;
            }

            var compressed = new byte[blockSize];
            if (!TryReadRange(blockOffset, compressed, 0, compressed.Length, out error)) return false;

            var whole = new byte[fileSize];
            bool ok = MpqDecompress.TryInflateSector(compressed, 0, compressed.Length, whole, 0, whole.Length,
                out int produced, out string method);

            if (!ok && (flags & MpqFileFlags.Encrypted) != 0)
            {
                // The C reader retries after decrypting the whole block in place.
                MpqCrypto.DecryptBlock(compressed, 0, compressed.Length, fileKey);
                ok = MpqDecompress.TryInflateSector(compressed, 0, compressed.Length, whole, 0, whole.Length,
                    out produced, out method);
                if (ok) method += "+decrypted";
            }

            if (!ok && blockSize >= fileSize)
            {
                Buffer.BlockCopy(compressed, 0, whole, 0, (int)fileSize);
                produced = (int)fileSize;
                method = "stored(block>=file)";
                ok = true;
            }

            if (!ok)
            {
                error = $"single-unit decompression failed (compressed={blockSize}, size={fileSize}, flags=0x{flags:X8})";
                return false;
            }

            LastTrace?.Add($"single-unit {method} {produced}/{fileSize} bytes");
            if (produced != fileSize)
            {
                error = $"single-unit data is truncated: decoded {produced} of {fileSize} bytes ({method})";
                return false;
            }

            data = whole;
            return true;
        }

        /// <summary>common/mpq.c: the sector-offset-table branch of SFileReadFile().</summary>
        bool ReadSectors(uint blockOffset, uint blockSize, uint fileSize, uint flags, uint fileKey,
            out byte[] data, out string error)
        {
            data = null;
            error = null;

            uint sectorSize = _sectorSize;
            uint sectorCount = (fileSize + sectorSize - 1) / sectorSize;
            uint offsetCount = sectorCount + 1;
            if (offsetCount > int.MaxValue / 4)
            {
                error = $"sector table is implausibly large ({offsetCount} offsets)";
                return false;
            }

            var offsetBytes = new byte[offsetCount * 4];
            if (!TryReadRange(blockOffset, offsetBytes, 0, offsetBytes.Length, out error)) return false;

            uint[] offsets = ToUInt32Array(offsetBytes);
            bool valid = SectorTableLooksValid(offsets, sectorCount, blockSize);

            if (!valid && (flags & MpqFileFlags.Encrypted) != 0)
            {
                // Sector offsets of encrypted files are encrypted with fileKey - 1.
                MpqCrypto.DecryptBlock(offsetBytes, 0, offsetBytes.Length, fileKey - 1);
                offsets = ToUInt32Array(offsetBytes);
                valid = SectorTableLooksValid(offsets, sectorCount, blockSize);
            }

            if (!valid)
            {
                error = $"invalid sector offset table ({sectorCount} sectors, compressed={blockSize}, flags=0x{flags:X8})";
                return false;
            }

            var result = new byte[fileSize];
            var sectorBuffer = new byte[sectorSize];
            int written = 0;

            for (uint sector = 0; sector < sectorCount && written < result.Length; sector++)
            {
                uint startOffset = offsets[sector];
                uint endOffset = offsets[sector + 1];
                if (endOffset < startOffset)
                {
                    error = $"sector {sector} offset table goes backwards ({startOffset} > {endOffset})";
                    return false;
                }

                uint compressedSize = endOffset - startOffset;
                uint uncompressedSize = sectorSize;
                if (sector == sectorCount - 1)
                {
                    uint tail = fileSize % sectorSize;
                    if (tail != 0) uncompressedSize = tail;
                }
                if (uncompressedSize > sectorBuffer.Length) uncompressedSize = (uint)sectorBuffer.Length;

                var compressed = new byte[compressedSize];
                if (!TryReadRange(blockOffset + startOffset, compressed, 0, compressed.Length, out error)) return false;

                if ((flags & MpqFileFlags.Encrypted) != 0)
                    MpqCrypto.DecryptBlock(compressed, 0, compressed.Length, fileKey + sector);

                int produced;
                string method;
                string maskError = null;

                // common/mpq.c order: zlib fast path, then the method-byte
                // dispatcher, then a stored sector when the payload cannot be
                // smaller than its uncompressed size.
                bool ok = MpqDecompress.TryInflateSector(compressed, 0, compressed.Length, sectorBuffer, 0,
                    (int)uncompressedSize, out produced, out method);

                if (!ok)
                {
                    ok = MpqDecompress.TryDecompressSector(compressed, 0, compressed.Length, sectorBuffer, 0,
                        (int)uncompressedSize, out produced, out method, out maskError);
                }

                if (!ok && compressedSize >= uncompressedSize)
                {
                    Buffer.BlockCopy(compressed, 0, sectorBuffer, 0, (int)uncompressedSize);
                    produced = (int)uncompressedSize;
                    method = "stored(sector>=uncompressed)";
                    ok = true;
                }

                if (!ok)
                {
                    error = $"sector {sector} of {sectorCount} failed: " +
                            $"{(maskError ?? "decompression failed")} " +
                            $"(compressed={compressedSize}, uncompressed={uncompressedSize}, " +
                            $"first byte 0x{(compressed.Length > 0 ? compressed[0] : 0):X2}, flags=0x{flags:X8})";
                    return false;
                }

                int available = produced;
                int toCopy = Math.Min(available, result.Length - written);
                if (toCopy > 0)
                {
                    Buffer.BlockCopy(sectorBuffer, 0, result, written, toCopy);
                    written += toCopy;
                }

                LastTrace?.Add($"sector {sector} first=0x{(compressed.Length > 0 ? compressed[0] : 0):X2} via={method} {produced}/{uncompressedSize}");

                if (produced < (int)uncompressedSize && written < result.Length)
                {
                    error = $"sector {sector} decoded short ({produced} of {uncompressedSize} bytes, {method})";
                    return false;
                }
            }

            if (written != result.Length)
            {
                error = $"decoded {written} of {fileSize} bytes ({sectorCount} sectors)";
                return false;
            }

            data = result;
            return true;
        }

        /// <summary>common/mpq.c: SectorTableLooksValid().</summary>
        static bool SectorTableLooksValid(uint[] offsets, uint sectorCount, uint compressedSize)
        {
            if (offsets == null || sectorCount == 0) return false;
            if (offsets[0] == 0 || offsets[0] > compressedSize) return false;

            for (uint i = 0; i < sectorCount; i++)
            {
                if (offsets[i] > offsets[i + 1]) return false;
                if (offsets[i + 1] > compressedSize) return false;
            }

            return true;
        }

        // ----------------------------------------------------------- raw access

        uint FindArchiveSignature()
        {
            int probeLength = (int)Math.Min(SignatureProbeSize, _sourceLength);
            var probe = new byte[probeLength];
            if (probeLength > 0) ReadAt(0, probe, 0, probeLength);

            for (int i = 0; i + 4 <= probeLength; i++)
            {
                if (probe[i] == (byte)'M' && probe[i + 1] == (byte)'P' &&
                    probe[i + 2] == (byte)'Q' && probe[i + 3] == 0x1A)
                {
                    return (uint)i;
                }
            }

            throw new MpqException($"{_name}: no MPQ signature in the first {probeLength} bytes");
        }

        void RequireWithinSource(uint relativeOffset, long length, string what)
        {
            long end = (long)_baseOffset + relativeOffset + length;
            if (end > _sourceLength)
                throw new MpqException($"{_name}: {what} extends past the end of the archive ({end} > {_sourceLength})");
        }

        /// <summary>
        /// Positional read. Disk archives go through RandomAccess so the shared
        /// handle keeps working for concurrent readers; memory archives read the
        /// backing array directly.
        /// </summary>
        void ReadAt(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || offset + count > _sourceLength)
                throw new MpqException($"{_name}: read of {count} bytes at {offset} is outside the archive ({_sourceLength} bytes)");

            if (_memory != null)
            {
                Buffer.BlockCopy(_memory, (int)offset, buffer, index, count);
                return;
            }

            int done = 0;
            while (done < count)
            {
                int read = RandomAccess.Read(_stream.SafeFileHandle, buffer.AsSpan(index + done, count - done), offset + done);
                if (read <= 0)
                    throw new MpqException($"{_name}: unexpected end of archive at {offset + done}");
                done += read;
            }
        }

        bool TryReadRange(long offset, byte[] buffer, int index, int count, out string error)
        {
            error = null;
            if (offset < 0 || offset + count > _sourceLength)
            {
                error = $"read of {count} bytes at {offset} is outside the archive ({_sourceLength} bytes)";
                return false;
            }

            ReadAt(offset, buffer, index, count);
            return true;
        }

        static uint[] ToUInt32Array(byte[] bytes)
        {
            var values = new uint[bytes.Length / 4];
            for (int i = 0; i < values.Length; i++)
                values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
            return values;
        }

        void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(MpqArchive), $"{_name} has been disposed");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stream?.Dispose();
            lock (_sync)
            {
                _lookupCache.Clear();
                _hashPairIndex.Clear();
            }
        }
    }
}
