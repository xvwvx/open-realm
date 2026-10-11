using System;
using System.Collections.Generic;
using System.IO;
using MdxPreview.Core.Math3D;

namespace MdxPreview.Core.Mdx
{
    /// <summary>
    /// Raised for any malformed MDX structure: a chunk that runs past the end of the
    /// file, a fixed-size record that does not fit its chunk, a key track whose declared
    /// key count does not fit its record, and so on. The message always names the chunk
    /// (and sub-chunk) plus the file offset so a corrupt asset can be located.
    /// </summary>
    public sealed class MdxParseException : Exception
    {
        public MdxParseException(string message) : base(message)
        {
            Chunk = string.Empty;
            Offset = -1;
        }

        public MdxParseException(string message, string chunk, int offset) : base(message)
        {
            Chunk = chunk ?? string.Empty;
            Offset = offset;
        }

        /// <summary>Chunk/sub-chunk name the failure happened in ("GEOS[2].VRTX"), may be empty.</summary>
        public string Chunk { get; }

        /// <summary>Absolute byte offset of the failure, or -1 when unknown.</summary>
        public int Offset { get; }
    }

    /// <summary>Knobs for <see cref="MdxLoader.Load(byte[], string, MdxLoadOptions)"/>.</summary>
    public sealed class MdxLoadOptions
    {
        public static readonly MdxLoadOptions Default = new MdxLoadOptions();

        /// <summary>
        /// Throw on a record whose declared size does not fit its chunk instead of logging a
        /// diagnostic and resynchronising to the next plausible record. Stock retail models
        /// contain at least one such anomaly (hero models carry a stray CR byte inside EVTS),
        /// which the engine survives precisely because it reads past the declared record size,
        /// so the default is tolerant.
        /// </summary>
        public bool StrictRecords;

        /// <summary>
        /// Throw when the container itself is damaged (a truncated trailing chunk header, or a chunk
        /// whose declared size runs past the end of the file) instead of logging a diagnostic and
        /// keeping the chunks that parsed. The default matches the reference loader, which stops
        /// reading silently and still returns the model it managed to build; custom maps rely on that
        /// tolerance because several shipped assets end with a few stray bytes.
        /// </summary>
        public bool StrictContainer;

        /// <summary>
        /// Throw when a single chunk cannot be parsed instead of dropping that chunk with a
        /// diagnostic and returning the rest of the model.
        /// </summary>
        public bool StrictChunks;

        /// <summary>
        /// Reject any VERS other than 800, mirroring <c>MDLX_ReadVERS</c> in r_mdx_load.c. The
        /// default parses Reforged versions with the classic layout and warns instead, so a
        /// preview can still show something useful.
        /// </summary>
        public bool RejectUnsupportedVersion;

        /// <summary>
        /// Highest node id that is registered in <see cref="MdxModel.Nodes"/>. The engine uses
        /// MDX_MAX_NODES (1024); the larger default tolerates custom assets without letting a
        /// corrupt id allocate an unbounded table.
        /// </summary>
        public int MaxNodeId = 4096;

        /// <summary>Cap on <see cref="MdxModel.Diagnostics"/> entries (junk files cannot flood memory).</summary>
        public int MaxDiagnostics = 128;
    }

    /// <summary>
    /// MDX (MDLX) model parser — pure C# port of
    /// games/warcraft-3/renderer/mdx/r_mdx_load.c, with the record sizes confirmed against
    /// stock Warcraft III models.
    ///
    /// Chunk map (C entry point -> reader here):
    ///   VERS MDLX_ReadVERS      MODL MDLX_ReadMODL     SEQS MDLX_ReadSEQS
    ///   GLBS MDLX_ReadGLBS      TEXS MDLX_ReadTEXS     MTLS MDLX_ReadMTLS  (+LAY2 skipped)
    ///   GEOS MDLX_ReadGEOS      GEOA MDLX_ReadGEOA     BONE MDLX_ReadBONE
    ///   HELP MDLX_ReadHELP      ATCH MDLX_ReadATCH     PIVT MDLX_ReadPIVT
    ///   PREM MDLX_ReadPREM      PRE2 MDLX_ReadPRE2     RIBB MDLX_ReadRIBB
    ///   EVTS MDLX_ReadEVTS      CLID MDLX_ReadCLID     LITE MDLX_ReadLITE
    ///   CAMS MDLX_ReadCAMS      TXAN MDLX_ReadTXAN
    ///
    /// Layout notes taken from the reference source (not from memory):
    ///  * A record's <c>size</c> field covers <c>[size][node body]</c> only for node records
    ///    (BONE/HELP/CLID/EVTS) and for geoset records; the trailing fields of node records
    ///    (bone geoset ids, event KEVT list, collision-shape extras) are stored after the
    ///    declared size and are read with <c>MSG_ReadOverflow</c>.
    ///  * ATCH/PREM/PRE2/RIBB/LITE records carry a second <c>size</c> field for the node body
    ///    before the name; the outer size is the record's real footprint.
    ///  * A key track is <c>[keyCount][interpolation][globalSeqId]</c> followed by
    ///    <c>keyCount</c> keys of <c>4 + componentBytes * typeSize</c> bytes, where typeSize is
    ///    1 for none/linear, 3 for hermite/bezier (value, in-tangent, out-tangent) and 0 for an
    ///    unknown interpolation (only the key time is authored).
    /// </summary>
    public static class MdxLoader
    {
        // ---- public API ---------------------------------------------------------------

        public static MdxModel Load(byte[] data, string sourcePath = "")
            => Load(data, sourcePath, null);

        public static MdxModel Load(byte[] data, string sourcePath, MdxLoadOptions options)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return new ParseState(data, sourcePath ?? string.Empty, options ?? MdxLoadOptions.Default).Run();
        }

        public static MdxModel LoadFromFile(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            return Load(data, path);
        }

        /// <summary>
        /// Port of <c>MDLX_EventObjectId</c> (r_mdx_anim.c): an event object's first three name
        /// characters select the event family, the fourth character is a separator, and the rest
        /// is the row key with surrounding whitespace trimmed. The comparison is ordinal and the
        /// name must be longer than four characters, exactly like the reference.
        /// </summary>
        public static bool TryGetEventObjectId(MdxEventObject eventObject, string type, out string rowKey)
        {
            rowKey = string.Empty;
            if (eventObject?.Node == null || type == null || type.Length != 3) return false;
            string name = eventObject.Node.Name ?? string.Empty;
            if (name.Length <= 4) return false;
            if (string.CompareOrdinal(name, 0, type, 0, 3) != 0) return false;

            int offset = 4;
            while (offset < name.Length && IsAsciiSpace(name[offset])) offset++;
            int length = name.Length - offset;
            while (length > 0 && IsAsciiSpace(name[offset + length - 1])) length--;
            if (length == 0) return false;

            rowKey = name.Substring(offset, length);
            return true;
        }

        static bool IsAsciiSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';

        // ---- four-character tags ------------------------------------------------------

        static uint Tag(char a, char b, char c, char d)
            => (uint)(a | (b << 8) | (c << 16) | (d << 24));

        const uint T_VERS = 'V' | ('E' << 8) | ('R' << 16) | ('S' << 24);
        const uint T_MODL = 'M' | ('O' << 8) | ('D' << 16) | ('L' << 24);
        const uint T_EVTS = 'E' | ('V' << 8) | ('T' << 16) | ('S' << 24);
        const uint T_GEOS = 'G' | ('E' << 8) | ('O' << 16) | ('S' << 24);
        const uint T_MTLS = 'M' | ('T' << 8) | ('L' << 16) | ('S' << 24);
        const uint T_TXAN = 'T' | ('X' << 8) | ('A' << 16) | ('N' << 24);
        const uint T_BONE = 'B' | ('O' << 8) | ('N' << 16) | ('E' << 24);
        const uint T_GEOA = 'G' | ('E' << 8) | ('O' << 16) | ('A' << 24);
        const uint T_HELP = 'H' | ('E' << 8) | ('L' << 16) | ('P' << 24);
        const uint T_CAMS = 'C' | ('A' << 8) | ('M' << 16) | ('S' << 24);
        const uint T_SEQS = 'S' | ('E' << 8) | ('Q' << 16) | ('S' << 24);
        const uint T_GLBS = 'G' | ('L' << 8) | ('B' << 16) | ('S' << 24);
        const uint T_PIVT = 'P' | ('I' << 8) | ('V' << 16) | ('T' << 24);
        const uint T_TEXS = 'T' | ('E' << 8) | ('X' << 16) | ('S' << 24);
        const uint T_CLID = 'C' | ('L' << 8) | ('I' << 16) | ('D' << 24);
        const uint T_PREM = 'P' | ('R' << 8) | ('E' << 16) | ('M' << 24);
        const uint T_PRE2 = 'P' | ('R' << 8) | ('E' << 16) | ('2' << 24);
        const uint T_RIBB = 'R' | ('I' << 8) | ('B' << 16) | ('B' << 24);
        const uint T_ATCH = 'A' | ('T' << 8) | ('C' << 16) | ('H' << 24);
        const uint T_LITE = 'L' | ('I' << 8) | ('T' << 16) | ('E' << 24);

        const uint T_VRTX = 'V' | ('R' << 8) | ('T' << 16) | ('X' << 24);
        const uint T_NRMS = 'N' | ('R' << 8) | ('M' << 16) | ('S' << 24);
        const uint T_UVBS = 'U' | ('V' << 8) | ('B' << 16) | ('S' << 24);
        const uint T_PTYP = 'P' | ('T' << 8) | ('Y' << 16) | ('P' << 24);
        const uint T_PCNT = 'P' | ('C' << 8) | ('N' << 16) | ('T' << 24);
        const uint T_PVTX = 'P' | ('V' << 8) | ('T' << 16) | ('X' << 24);
        const uint T_GNDX = 'G' | ('N' << 8) | ('D' << 16) | ('X' << 24);
        const uint T_MTGC = 'M' | ('T' << 8) | ('G' << 16) | ('C' << 24);
        const uint T_UVAS = 'U' | ('V' << 8) | ('A' << 16) | ('S' << 24);
        const uint T_MATS = 'M' | ('A' << 8) | ('T' << 16) | ('S' << 24);

        const uint T_LAYS = 'L' | ('A' << 8) | ('Y' << 16) | ('S' << 24);
        const uint T_KMTF = 'K' | ('M' << 8) | ('T' << 16) | ('F' << 24);
        const uint T_KMTA = 'K' | ('M' << 8) | ('T' << 16) | ('A' << 24);
        const uint T_KMTE = 'K' | ('M' << 8) | ('T' << 16) | ('E' << 24);

        const uint T_KGAO = 'K' | ('G' << 8) | ('A' << 16) | ('O' << 24);
        const uint T_KGAC = 'K' | ('G' << 8) | ('A' << 16) | ('C' << 24);

        const uint T_KGTR = 'K' | ('G' << 8) | ('T' << 16) | ('R' << 24);
        const uint T_KGRT = 'K' | ('G' << 8) | ('R' << 16) | ('T' << 24);
        const uint T_KGSC = 'K' | ('G' << 8) | ('S' << 16) | ('C' << 24);

        const uint T_KEVT = 'K' | ('E' << 8) | ('V' << 16) | ('T' << 24);

        const uint T_KCTR = 'K' | ('C' << 8) | ('T' << 16) | ('R' << 24);
        const uint T_KTTR = 'K' | ('T' << 8) | ('T' << 16) | ('R' << 24);
        const uint T_KCRL = 'K' | ('C' << 8) | ('R' << 16) | ('L' << 24);

        const uint T_KTAT = 'K' | ('T' << 8) | ('A' << 16) | ('T' << 24);
        const uint T_KTAR = 'K' | ('T' << 8) | ('A' << 16) | ('R' << 24);
        const uint T_KTAS = 'K' | ('T' << 8) | ('A' << 16) | ('S' << 24);

        const uint T_KPEE = 'K' | ('P' << 8) | ('E' << 16) | ('E' << 24);
        const uint T_KPEG = 'K' | ('P' << 8) | ('E' << 16) | ('G' << 24);
        const uint T_KPLN = 'K' | ('P' << 8) | ('L' << 16) | ('N' << 24);
        const uint T_KPLT = 'K' | ('P' << 8) | ('L' << 16) | ('T' << 24);
        const uint T_KPEL = 'K' | ('P' << 8) | ('E' << 16) | ('L' << 24);
        const uint T_KPES = 'K' | ('P' << 8) | ('E' << 16) | ('S' << 24);
        const uint T_KPEV = 'K' | ('P' << 8) | ('E' << 16) | ('V' << 24);

        const uint T_KP2V = 'K' | ('P' << 8) | ('2' << 16) | ('V' << 24);
        const uint T_KP2E = 'K' | ('P' << 8) | ('2' << 16) | ('E' << 24);
        const uint T_KP2W = 'K' | ('P' << 8) | ('2' << 16) | ('W' << 24);
        const uint T_KP2N = 'K' | ('P' << 8) | ('2' << 16) | ('N' << 24);
        const uint T_KP2S = 'K' | ('P' << 8) | ('2' << 16) | ('S' << 24);
        const uint T_KP2L = 'K' | ('P' << 8) | ('2' << 16) | ('L' << 24);
        const uint T_KP2G = 'K' | ('P' << 8) | ('2' << 16) | ('G' << 24);
        const uint T_KP2R = 'K' | ('P' << 8) | ('2' << 16) | ('R' << 24);

        const uint T_KATV = 'K' | ('A' << 8) | ('T' << 16) | ('V' << 24);

        const uint T_KLAV = 'K' | ('L' << 8) | ('A' << 16) | ('V' << 24);
        const uint T_KLAC = 'K' | ('L' << 8) | ('A' << 16) | ('C' << 24);
        const uint T_KLAI = 'K' | ('L' << 8) | ('A' << 16) | ('I' << 24);
        const uint T_KLBC = 'K' | ('L' << 8) | ('B' << 16) | ('C' << 24);
        const uint T_KLBI = 'K' | ('L' << 8) | ('B' << 16) | ('I' << 24);
        const uint T_KLAS = 'K' | ('L' << 8) | ('A' << 16) | ('S' << 24);
        const uint T_KLAE = 'K' | ('L' << 8) | ('A' << 16) | ('E' << 24);

        const uint T_KRVS = 'K' | ('R' << 8) | ('V' << 16) | ('S' << 24);
        const uint T_KRHA = 'K' | ('R' << 8) | ('H' << 16) | ('A' << 24);
        const uint T_KRHB = 'K' | ('R' << 8) | ('H' << 16) | ('B' << 24);
        const uint T_KRAL = 'K' | ('R' << 8) | ('A' << 16) | ('L' << 24);
        const uint T_KRCO = 'K' | ('R' << 8) | ('C' << 16) | ('O' << 24);
        const uint T_KRTX = 'K' | ('R' << 8) | ('T' << 16) | ('X' << 24);

        // Fixed record sizes (sizeof of the matching C structs).
        const int NodeNameLength = 80;              // mdxObjectName_t
        const int FileNameLength = 260;             // mdxFileName_t / MDX_TEXTURE_PATH_LENGTH
        const int TextureRecordSize = 4 + FileNameLength + 4;   // MDX_TEXTURE_RECORD_SIZE
        const int SequenceRecordSize = 80 + 8 + 4 + 4 + 4 + 4 + 4 + 12 + 12; // mdxSequence_t (132)
        const int InfoRecordSize = 80 + FileNameLength + 4 + 12 + 12 + 4;    // mdxInfo_t (372)
        const int BoundsSize = 4 + 12 + 12;         // mdxBounds_t (28)
        const int NodeHeaderSize = 4 + NodeNameLength + 4 + 4 + 4; // record size + node body (96)
        const int AttachmentPathLength = 0x100;     // MODEL_ATTACHMENT_PATH_LENGTH
        const int CameraHeaderSize = 80 + 12 + 4 + 4 + 4 + 12; // 116, before key tracks

        // ---- parser state -------------------------------------------------------------

        sealed class ChunkRef
        {
            public uint Tag;
            public int Offset;   // payload offset
            public int Size;     // payload size
        }

        /// <summary>
        /// One record of a <c>MODEL_READ_LIST</c> chunk: the cursor for everything after the
        /// record's declared size (bounded by the end of the *file*, mirroring MSG_ReadOverflow).
        /// </summary>
        sealed class RecordView
        {
            public MdxChunkReader Extras;
            public int Start;          // offset of the record's size field
            public int DeclaredEnd;    // Start + declared size
            public int ContainerEnd;   // end of the enclosing chunk
        }

        delegate void RecordReader(RecordView record, string context);

        sealed class ParseState
        {
            readonly byte[] _data;
            readonly MdxLoadOptions _options;
            readonly MdxModel _model = new MdxModel();
            readonly MdxChunkReader _fileView;
            readonly List<MdxNode> _registered = new List<MdxNode>();
            int _suppressedDiagnostics;

            public ParseState(byte[] data, string sourcePath, MdxLoadOptions options)
            {
                _data = data;
                _options = options;
                _fileView = new MdxChunkReader(data, 0, data.Length, "file");
                _model.SourcePath = sourcePath;
            }

            MdxParseException Error(string message)
                => new MdxParseException(message + (_model.SourcePath.Length > 0 ? " [" + _model.SourcePath + "]" : string.Empty));

            MdxParseException Error(string message, string context, int offset)
                => new MdxParseException(
                    message + (_model.SourcePath.Length > 0 ? " [" + _model.SourcePath + "]" : string.Empty),
                    context, offset);

            void Diag(string message)
            {
                if (_model.Diagnostics.Count >= _options.MaxDiagnostics)
                {
                    _suppressedDiagnostics++;
                    return;
                }
                _model.Diagnostics.Add(message);
            }

            // ---- top level -----------------------------------------------------------

            public MdxModel Run()
            {
                if (_data.Length < 8)
                {
                    throw Error($"MDX: {_data.Length} byte(s) is too small for an MDLX container " +
                                "(magic plus at least one chunk header are required)");
                }

                uint magic = MdxChunkReader.PeekU32(_data, 0);
                if (magic != Tag('M', 'D', 'L', 'X'))
                {
                    throw Error($"MDX: bad magic '{MdxChunkReader.TagName(magic)}' (0x{magic:X8}) at offset 0; expected 'MDLX'");
                }

                List<ChunkRef> chunks = ReadTopLevelChunkTable();
                bool sawKnownChunk = false;
                foreach (ChunkRef chunk in chunks)
                {
                    if (IsKnownChunk(chunk.Tag)) { sawKnownChunk = true; break; }
                }
                if (!sawKnownChunk)
                {
                    throw Error($"MDX: none of the {chunks.Count} chunk header(s) after the MDLX magic uses a known tag; " +
                                "the file is not a parseable MDX model");
                }

                ResolveVersion(chunks);
                foreach (ChunkRef chunk in chunks) ParseChunk(chunk);
                Finish();

                if (_suppressedDiagnostics > 0)
                {
                    _model.Diagnostics.Add($"{_suppressedDiagnostics} further diagnostic(s) suppressed");
                }
                return _model;
            }

            /// <summary>
            /// Walks <c>[tag][size][payload]</c> pairs.
            ///
            /// The reference loader never fails on a damaged container: MSG_Read stops silently once its
            /// view is exhausted, so a truncated tail or a lying chunk size costs only the chunks after
            /// it. The same tolerance is modelled here — the anomaly becomes a diagnostic and the walk
            /// stops before any out-of-range read. Set <see cref="MdxLoadOptions.StrictContainer"/> to
            /// get an exception instead.
            /// </summary>
            List<ChunkRef> ReadTopLevelChunkTable()
            {
                var chunks = new List<ChunkRef>();
                int position = 4; // the engine skips the MDLX magic the same way
                uint previousTag = 0;
                while (position < _data.Length)
                {
                    if (position + 8 > _data.Length)
                    {
                        string message = $"MDX: trailing {_data.Length - position} byte(s) at offset {position} after " +
                                         $"'{MdxChunkReader.TagName(previousTag)}' do not form a chunk header; ignored";
                        if (_options.StrictContainer) throw Error(message);
                        Diag(message);
                        break;
                    }

                    uint tag = MdxChunkReader.PeekU32(_data, position);
                    uint size = MdxChunkReader.PeekU32(_data, position + 4);
                    int payload = position + 8;
                    long end = (long)payload + size;
                    if (end > _data.Length)
                    {
                        string message = $"MDX: chunk '{MdxChunkReader.TagName(tag)}' at offset {position} declares {size} " +
                                         $"byte(s) but only {_data.Length - payload} byte(s) remain; the chunk is ignored";
                        if (_options.StrictContainer) throw Error(message);
                        Diag(message);
                        break;
                    }

                    chunks.Add(new ChunkRef { Tag = tag, Offset = payload, Size = (int)size });
                    previousTag = tag;
                    position = (int)end;
                }
                return chunks;
            }

            static bool IsKnownChunk(uint tag)
            {
                switch (tag)
                {
                    case T_VERS: case T_MODL: case T_SEQS: case T_GLBS: case T_TEXS: case T_MTLS:
                    case T_TXAN: case T_GEOS: case T_GEOA: case T_BONE: case T_HELP: case T_ATCH:
                    case T_PIVT: case T_PREM: case T_PRE2: case T_RIBB: case T_EVTS: case T_CLID:
                    case T_LITE: case T_CAMS:
                        return true;
                    default:
                        return false;
                }
            }

            void ResolveVersion(List<ChunkRef> chunks)
            {
                bool sawVers = false;
                foreach (ChunkRef chunk in chunks)
                {
                    if (chunk.Tag != T_VERS) continue;
                    sawVers = true;
                    if (chunk.Size < 4)
                    {
                        Diag($"VERS at offset {chunk.Offset - 8}: {chunk.Size} byte(s); expected a 4-byte version value");
                        break;
                    }
                    _model.Version = MdxChunkReader.PeekU32(_data, chunk.Offset);
                    break;
                }

                if (!sawVers) Diag("no VERS chunk: assuming the classic v800 layout");
                _model.IsReforged = _model.Version >= 900;

                if (_model.Version != 800 && _model.Version != 0)
                {
                    if (_options.RejectUnsupportedVersion)
                    {
                        throw Error($"MDX: unsupported version {_model.Version} " +
                                    "(R_LoadModelMDLX only accepts 800)");
                    }
                    Diag($"VERS {_model.Version}: parsed with the classic v800 chunk layout. Reforged-only data " +
                         "(GEOS LOD, 16-bit MATS indices, TEXS flags, LAY2/shader layers) is not interpreted, and a " +
                         "chunk that fails the v800 layout is dropped instead of failing the whole load");
                }
            }

            /// <summary>
            /// Runs one chunk. A chunk that fails internally is dropped with a diagnostic instead of
            /// failing the whole model (the C loader is equally forgiving, it just zero-fills whatever it
            /// cannot read); <see cref="MdxLoadOptions.StrictChunks"/> restores the exception.
            /// </summary>
            void ParseChunk(ChunkRef chunk)
            {
                int before = TotalItemCount();
                var reader = new MdxChunkReader(_data, chunk.Offset, chunk.Size, MdxChunkReader.TagName(chunk.Tag));
                try
                {
                    ParseChunkBody(chunk, reader);
                }
                catch (MdxParseException ex)
                {
                    // Any strict mode must surface the failure: StrictChunks asks for it directly and
                    // StrictRecords asks for record-level anomalies to be fatal.
                    if (_options.StrictChunks || _options.StrictRecords) throw;
                    int kept = TotalItemCount() - before;
                    Diag($"{MdxChunkReader.TagName(chunk.Tag)} at offset {chunk.Offset - 8}: {ex.Message}; " +
                         $"the chunk is abandoned after {kept} item(s)");
                }
            }

            int TotalItemCount()
                => _model.Sequences.Count + _model.GlobalSequences.Count + _model.Textures.Count
                   + _model.Materials.Count + _model.TextureAnims.Count + _model.Geosets.Count
                   + _model.GeosetAnims.Count + _model.Bones.Count + _model.Helpers.Count
                   + _model.Attachments.Count + _model.Pivots.Count + _model.Cameras.Count
                   + _model.Events.Count + _model.CollisionShapes.Count + _model.Lights.Count
                   + _model.ParticleEmitters1.Count + _model.ParticleEmitters2.Count
                   + _model.RibbonEmitters.Count;

            void ParseChunkBody(ChunkRef chunk, MdxChunkReader reader)
            {
                switch (chunk.Tag)
                {
                    case T_VERS: break; // handled by ResolveVersion
                    case T_MODL: ParseModl(reader); break;
                    case T_SEQS: ParseSeqs(reader); break;
                    case T_GLBS: ParseGlbs(reader); break;
                    case T_TEXS: ParseTexs(reader); break;
                    case T_MTLS: ParseMtls(reader); break;
                    case T_TXAN: ParseTxan(reader); break;
                    case T_GEOS: ParseGeos(reader); break;
                    case T_GEOA: ParseGeoa(reader); break;
                    case T_BONE: ParseBone(reader); break;
                    case T_HELP: ParseHelp(reader); break;
                    case T_ATCH: ParseAttachment(reader); break;
                    case T_PIVT: ParsePivt(reader); break;
                    case T_PREM: ParseParticleEmitter1(reader); break;
                    case T_PRE2: ParseParticleEmitter2(reader); break;
                    case T_RIBB: ParseRibbon(reader); break;
                    case T_EVTS: ParseEvents(reader); break;
                    case T_CLID: ParseCollisionShapes(reader); break;
                    case T_LITE: ParseLights(reader); break;
                    case T_CAMS: ParseCameras(reader); break;
                    default:
                        Diag($"'{MdxChunkReader.TagName(chunk.Tag)}' at offset {chunk.Offset - 8}: " +
                             $"unhandled top-level chunk ({chunk.Size} byte(s)), skipped");
                        break;
                }
            }
            // ---- fixed-size chunks ---------------------------------------------------

            void ParseModl(MdxChunkReader r)
            {
                if (r.Length < InfoRecordSize)
                {
                    throw Error("MODL: chunk is " + r.Length + " byte(s); mdxInfo_t needs " + InfoRecordSize,
                                "MODL", r.AbsolutePosition);
                }

                MdxInfo info = _model.Info;
                info.Name = r.ReadFixedString(NodeNameLength);
                info.AnimationFile = r.ReadFixedString(FileNameLength);
                info.Bounds = r.ReadBounds();
                info.BlendTime = r.ReadU32();
            }

            void ParseSeqs(MdxChunkReader r)
            {
                int count = r.Length / SequenceRecordSize;
                if (count <= 0) return;
                if (r.Length % SequenceRecordSize != 0)
                {
                    Diag($"SEQS: {r.Length} byte(s) is not a multiple of the {SequenceRecordSize}-byte sequence record; " +
                         $"parsing {count} record(s)");
                }

                for (int i = 0; i < count; i++)
                {
                    r.Context = $"SEQS[{i}]";
                    var sequence = new MdxSequence();
                    sequence.Name = r.ReadFixedString(NodeNameLength);
                    sequence.Start = r.ReadI32();
                    sequence.End = r.ReadI32();
                    sequence.MoveSpeed = r.ReadF32();
                    sequence.Flags = r.ReadU32();
                    sequence.Rarity = r.ReadF32();
                    sequence.SyncPoint = r.ReadI32();
                    sequence.Bounds = r.ReadBounds();
                    _model.Sequences.Add(sequence);
                }
                r.Context = "SEQS";
            }

            void ParseGlbs(MdxChunkReader r)
            {
                int count = r.Length / 4;
                for (int i = 0; i < count; i++) _model.GlobalSequences.Add(r.ReadU32());
            }

            void ParsePivt(MdxChunkReader r)
            {
                int count = r.Length / 12;
                if (r.Length % 12 != 0)
                {
                    Diag($"PIVT: {r.Length} byte(s) is not a multiple of the 12-byte pivot record; parsing {count} pivot(s)");
                }
                for (int i = 0; i < count; i++) _model.Pivots.Add(r.ReadVec3());
            }

            void ParseTexs(MdxChunkReader r)
            {
                int count = r.Length / TextureRecordSize;
                if (count <= 0) return;
                if (r.Length % TextureRecordSize != 0)
                {
                    Diag($"TEXS: {r.Length} byte(s) is not a multiple of the {TextureRecordSize}-byte texture record; " +
                         $"parsing {count} record(s)");
                }

                for (int i = 0; i < count; i++)
                {
                    r.Context = $"TEXS[{i}]";
                    var texture = new MdxTexture();
                    texture.ReplaceableId = r.ReadU32();
                    texture.Path = r.ReadFixedString(FileNameLength);
                    texture.WrapFlags = r.ReadI32();
                    // R_LoadModelMDLX: R_SetTextureWrap(tex, nWrapping & 0x1, nWrapping & 0x2)
                    texture.WrapWidth = (texture.WrapFlags & 0x1) != 0 ? 1 : 0;
                    texture.WrapHeight = (texture.WrapFlags & 0x2) != 0 ? 1 : 0;
                    _model.Textures.Add(texture);
                }
                r.Context = "TEXS";
            }

            // ---- materials -----------------------------------------------------------

            void ParseMtls(MdxChunkReader r)
            {
                ReadRecordList(r, "MTLS", 12, (record, context) =>
                {
                    var material = new MdxMaterial();
                    material.Priority = record.Extras.ReadI32();
                    material.Flags = record.Extras.ReadI32();
                    ReadMaterialSubChunks(record, material, context);
                    _model.Materials.Add(material);
                });
            }

            void ReadMaterialSubChunks(RecordView record, MdxMaterial material, string context)
            {
                MdxChunkReader r = record.Extras;
                while (r.AbsolutePosition < record.DeclaredEnd)
                {
                    if (record.DeclaredEnd - r.AbsolutePosition < 4)
                    {
                        Diag($"{context}: {record.DeclaredEnd - r.AbsolutePosition} trailing byte(s) at offset " +
                             $"{r.AbsolutePosition}; ignored");
                        break;
                    }

                    int tagOffset = r.AbsolutePosition;
                    uint tag = r.ReadU32();
                    switch (tag)
                    {
                        case T_LAYS: ReadMaterialLayers(record, material, context); break;
                        case T_KMTE: material.Emission = ReadKeyTrack(r, MdxTrackData.Float1, context + ".KMTE"); break;
                        case T_KMTA: material.Alpha = ReadKeyTrack(r, MdxTrackData.Float1, context + ".KMTA"); break;
                        case T_KMTF: material.Flipbook = ReadKeyTrack(r, MdxTrackData.Int1, context + ".KMTF"); break;
                        default:
                            // ReadMaterial returns on an unknown sub-chunk; the record size still tells
                            // us where the next material starts, so skip the rest safely.
                            Diag($"{context}: unknown material sub-chunk '{MdxChunkReader.TagName(tag)}' at offset " +
                                 $"{tagOffset} (LAY2/shader data appears in Reforged files); rest of the record skipped");
                            r.SkipToAbsolute(record.DeclaredEnd);
                            return;
                    }
                }
            }

            void ReadMaterialLayers(RecordView record, MdxMaterial material, string context)
            {
                MdxChunkReader r = record.Extras;
                uint layerCount = r.ReadU32();
                if (layerCount == 0) return;

                for (uint layerId = 0; layerId < layerCount; layerId++)
                {
                    string layerContext = $"{context}.LAYS[{layerId}]";
                    r.Context = layerContext;
                    if (r.Remaining < 4)
                    {
                        throw Error($"{layerContext}: {layerCount} layer(s) declared but only {r.Remaining} byte(s) " +
                                    "remain in the material record", layerContext, r.AbsolutePosition);
                    }

                    int layerStart = r.AbsolutePosition;
                    int layerSize = r.ReadI32();
                    int layerEnd = layerStart + layerSize;
                    if (layerSize < 4 || layerEnd > record.ContainerEnd)
                    {
                        throw Error($"{layerContext}: layer at offset {layerStart} declares {layerSize} byte(s), " +
                                    $"which does not fit the material chunk ({record.ContainerEnd - layerStart} byte(s) left)",
                                    layerContext, layerStart);
                    }
                    if (layerEnd > record.DeclaredEnd)
                    {
                        Diag($"{layerContext}: layer runs {layerEnd - record.DeclaredEnd} byte(s) past its material record");
                    }

                    var layer = new MdxMaterialLayer();
                    layer.FilterMode = r.ReadI32();
                    layer.BlendMode = FilterToBlendMode(layer.FilterMode, layerContext);
                    layer.Flags = (MdxGeoFlags)r.ReadU32();
                    uint textureId = r.ReadU32();
                    uint transformId = r.ReadU32();
                    layer.TextureId = (int)textureId == -1 ? -1 : (int)textureId;
                    layer.TransformId = (int)transformId == -1 ? -1 : (int)transformId;
                    layer.CoordId = r.ReadI32();
                    layer.StaticAlpha = r.ReadF32();

                    if (layer.TextureId >= 0 && layer.TextureId >= _model.Textures.Count && _model.Textures.Count > 0)
                    {
                        Diag($"{layerContext}: texture index {layer.TextureId} is outside the {_model.Textures.Count}-entry TEXS table");
                    }
                    if (layer.TransformId >= 0 && layer.TransformId >= _model.TextureAnims.Count && _model.TextureAnims.Count > 0)
                    {
                        Diag($"{layerContext}: texture-animation index {layer.TransformId} is outside the " +
                             $"{_model.TextureAnims.Count}-entry TXAN table");
                    }

                    while (r.AbsolutePosition < layerEnd)
                    {
                        if (layerEnd - r.AbsolutePosition < 4)
                        {
                            r.SkipToAbsolute(layerEnd);
                            break;
                        }
                        int tagOffset = r.AbsolutePosition;
                        uint tag = r.ReadU32();
                        switch (tag)
                        {
                            case T_KMTA: layer.Alpha = ReadKeyTrack(r, MdxTrackData.Float1, layerContext + ".KMTA"); break;
                            case T_KMTF: layer.Flipbook = ReadKeyTrack(r, MdxTrackData.Int1, layerContext + ".KMTF"); break;
                            default:
                                Diag($"{layerContext}: unknown layer sub-chunk '{MdxChunkReader.TagName(tag)}' at offset " +
                                     $"{tagOffset}; 4 byte(s) skipped");
                                break;
                        }
                    }
                    r.SkipToAbsolute(Math.Max(r.AbsolutePosition, layerEnd));
                    material.Layers.Add(layer);
                }
                r.Context = context;
            }

            /// <summary>
            /// The file stores the authored filter mode in the same slot, and BLEND_MODE
            /// (common/shared.h) numbers it 0..6 exactly like MDX's filter modes, which is why
            /// the loader reads it straight into BLEND_MODE. Values outside the enum keep the
            /// authored number in FilterMode and fall back to None, which is the engine's
            /// "no blend state" entry.
            /// </summary>
            MdxBlendMode FilterToBlendMode(int filterMode, string context)
            {
                if (filterMode >= 0 && filterMode <= 6) return (MdxBlendMode)filterMode;
                Diag($"{context}: blend/filter mode {filterMode} is outside BLEND_MODE (0..6); treated as None");
                return MdxBlendMode.None;
            }

            // ---- texture animation ----------------------------------------------------

            void ParseTxan(MdxChunkReader r)
            {
                ReadRecordList(r, "TXAN", 4, (record, context) =>
                {
                    var animation = new MdxTextureAnim();
                    MdxChunkReader body = record.Extras;
                    while (body.AbsolutePosition < record.DeclaredEnd)
                    {
                        if (record.DeclaredEnd - body.AbsolutePosition < 4)
                        {
                            body.SkipToAbsolute(record.DeclaredEnd);
                            break;
                        }
                        int tagOffset = body.AbsolutePosition;
                        uint tag = body.ReadU32();
                        switch (tag)
                        {
                            case T_KTAT: animation.Translation = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KTAT"); break;
                            case T_KTAR: animation.Rotation = ReadKeyTrack(body, MdxTrackData.Float4, context + ".KTAR"); break;
                            case T_KTAS: animation.Scaling = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KTAS"); break;
                            default:
                                Diag($"{context}: unknown texture-animation sub-chunk '{MdxChunkReader.TagName(tag)}' " +
                                     $"at offset {tagOffset}; 4 byte(s) skipped");
                                break;
                        }
                    }
                    _model.TextureAnims.Add(animation);
                });
            }

            // ---- geosets -------------------------------------------------------------

            /// <summary>
            /// GEOS record: <c>[size][sub-chunks...]</c>.
            ///
            /// Every array sub-chunk stores an *element count* in its 4-byte field, not a byte size:
            /// the engine's <c>SFileReadArray2</c> reads the count and then <c>count * sizeof(element)</c>
            /// bytes (r_mdx_load.c ReadGeoset), so the walk has to advance by what the reader consumed
            /// — exactly like ReadGeoset's readcount-driven <c>while (MSG_Read(&amp;header, 4))</c> loop.
            /// The geoset record's own size <i>is</i> a byte footprint (the loop compares it against
            /// readcount). <c>UVAS</c> carries a bare value with no count; <c>MATS</c> is the
            /// count-prefixed matrix array followed by materialID/group/selectable/bounds and the
            /// count-prefixed per-sequence bounds (ReadGeosetMatrices).
            /// </summary>
            void ParseGeos(MdxChunkReader r)
            {
                ReadRecordList(r, "GEOS", 12, (record, context) =>
                {
                    var geoset = new MdxGeoset();
                    int uvChannelCount = 0;
                    int[] matrixGroupSizes = null;
                    int[] matrices = null;

                    MdxChunkReader body = record.Extras;
                    while (body.AbsolutePosition < record.DeclaredEnd)
                    {
                        if (record.DeclaredEnd - body.AbsolutePosition < 4)
                        {
                            Diag($"{context}: {record.DeclaredEnd - body.AbsolutePosition} trailing byte(s) at offset " +
                                 $"{body.AbsolutePosition}; ignored");
                            break;
                        }

                        int tagOffset = body.AbsolutePosition;
                        uint tag = body.ReadU32();
                        switch (tag)
                        {
                            case T_VRTX:
                            {
                                int count = ReadElementCount(body, record.DeclaredEnd, context, "VRTX", tagOffset, 12);
                                geoset.Vertices = body.ReadFloats(count * 3);
                                geoset.VertexCount = count;
                                break;
                            }
                            case T_NRMS:
                            {
                                int count = ReadElementCount(body, record.DeclaredEnd, context, "NRMS", tagOffset, 12);
                                geoset.Normals = body.ReadFloats(count * 3);
                                break;
                            }
                            case T_UVBS:
                            {
                                int count = ReadElementCount(body, record.DeclaredEnd, context, "UVBS", tagOffset, 8);
                                geoset.Uvs = body.ReadFloats(count * 2);
                                break;
                            }
                            case T_PTYP:
                            {
                                int count = ReadElementCount(body, record.DeclaredEnd, context, "PTYP", tagOffset, 4);
                                geoset.PrimitiveTypes = body.ReadInt32s(count);
                                break;
                            }
                            case T_PCNT:
                            {
                                int count = ReadElementCount(body, record.DeclaredEnd, context, "PCNT", tagOffset, 4);
                                geoset.PrimitiveCounts = body.ReadInt32s(count);
                                break;
                            }
                            case T_PVTX:
                            {
                                int count = ReadElementCount(body, record.DeclaredEnd, context, "PVTX", tagOffset, 2);
                                geoset.Indices = body.ReadUInt16s(count);
                                break;
                            }
                            case T_GNDX:
                            {
                                int count = ReadElementCount(body, record.DeclaredEnd, context, "GNDX", tagOffset, 1);
                                geoset.VertexGroups = body.ReadBytes(count);
                                break;
                            }
                            case T_MTGC:
                            {
                                int count = ReadElementCount(body, record.DeclaredEnd, context, "MTGC", tagOffset, 4);
                                matrixGroupSizes = body.ReadInt32s(count);
                                break;
                            }
                            case T_UVAS:
                                uvChannelCount = body.ReadI32();
                                break;
                            case T_MATS:
                                matrices = ReadGeosetMatrices(geoset, body, record.DeclaredEnd, context, tagOffset);
                                break;
                            default:
                                // ReadGeoset prints the unknown tag and keeps reading 4-byte headers; the
                                // record boundary still bounds the damage.
                                Diag($"{context}: unknown geoset sub-chunk '{MdxChunkReader.TagName(tag)}' at offset " +
                                     $"{tagOffset}; 4 byte(s) skipped");
                                break;
                        }
                    }

                    FinishGeoset(geoset, uvChannelCount, matrixGroupSizes, matrices, context);
                    _model.Geosets.Add(geoset);
                },
                candidate => IsGeosetSubChunk(MdxChunkReader.PeekU32(_data, candidate + 4)));
            }

            static bool IsGeosetSubChunk(uint tag)
            {
                switch (tag)
                {
                    case T_VRTX: case T_NRMS: case T_UVBS: case T_PTYP: case T_PCNT: case T_PVTX:
                    case T_GNDX: case T_MTGC: case T_UVAS: case T_MATS:
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>Reads an array sub-chunk's element count and validates its payload fits.</summary>
            int ReadElementCount(MdxChunkReader body, int recordEnd, string context, string subChunk, int tagOffset, int elementBytes)
            {
                uint count = body.ReadU32();
                long bytes = (long)count * elementBytes;
                long available = recordEnd - body.AbsolutePosition;
                if (bytes > available)
                {
                    throw Error($"{context}.{subChunk} at offset {tagOffset}: {count} element(s) of {elementBytes} byte(s) " +
                                $"need {bytes} byte(s) but only {available} byte(s) remain in the geoset record",
                                context, tagOffset);
                }
                return (int)count;
            }

            int[] ReadGeosetMatrices(MdxGeoset geoset, MdxChunkReader body, int recordEnd, string context, int tagOffset)
            {
                if (recordEnd - body.AbsolutePosition < 4)
                {
                    throw Error($"{context}.MATS at offset {tagOffset}: no matrix count", context, tagOffset);
                }
                uint matrixCount = body.ReadU32();
                long matrixBytes = (long)matrixCount * 4;
                if (matrixBytes > recordEnd - body.AbsolutePosition)
                {
                    throw Error($"{context}.MATS at offset {tagOffset}: {matrixCount} matrix entr(y/ies) need {matrixBytes} " +
                                $"byte(s) but only {recordEnd - body.AbsolutePosition} byte(s) remain in the geoset record",
                                context, tagOffset);
                }
                int[] matrices = body.ReadInt32s((int)matrixCount);

                if (recordEnd - body.AbsolutePosition < 12 + BoundsSize + 4)
                {
                    throw Error($"{context}.MATS at offset {tagOffset}: material/group/selectable/bounds are truncated",
                                context, tagOffset);
                }
                geoset.MaterialId = body.ReadI32();
                geoset.Group = body.ReadI32();
                geoset.Selectable = body.ReadI32();
                geoset.Bounds = body.ReadBounds();

                uint boundsCount = body.ReadU32();
                long boundsBytes = (long)boundsCount * BoundsSize;
                if (boundsBytes > recordEnd - body.AbsolutePosition)
                {
                    throw Error($"{context}.MATS at offset {tagOffset}: {boundsCount} sequence bounds need {boundsBytes} " +
                                $"byte(s) but only {recordEnd - body.AbsolutePosition} byte(s) remain in the geoset record",
                                context, tagOffset);
                }
                var sequenceBounds = new MdxBounds[boundsCount];
                for (int i = 0; i < boundsCount; i++) sequenceBounds[i] = body.ReadBounds();
                geoset.SequenceBounds = sequenceBounds;
                return matrices;
            }

            void FinishGeoset(MdxGeoset geoset, int uvChannelCount, int[] matrixGroupSizes, int[] matrices, string context)
            {
                int vertexCount = geoset.VertexCount;
                float[] raw = geoset.Uvs ?? Array.Empty<float>();
                int pairCount = raw.Length / 2;

                int channels = uvChannelCount >= 1 ? uvChannelCount : 1;
                if (vertexCount > 0 && pairCount >= vertexCount)
                {
                    int channelsInData = pairCount / vertexCount;
                    if (channels != channelsInData && uvChannelCount > 1)
                    {
                        Diag($"{context}: UVAS declares {uvChannelCount} coordinate set(s) but UVBS holds " +
                             $"{channelsInData} pair(s) per vertex; using {channelsInData}");
                    }
                    channels = Math.Max(1, Math.Min(channels, Math.Max(1, channelsInData)));
                }
                else if (vertexCount > 0 && pairCount != vertexCount)
                {
                    Diag($"{context}: {pairCount} uv pair(s) for {vertexCount} vertex/vertices");
                }

                geoset.UvChannelCount = Math.Max(1, channels);
                if (vertexCount > 0 && channels >= 2 && pairCount >= vertexCount * 2)
                {
                    var first = new float[vertexCount * 2];
                    var second = new float[vertexCount * 2];
                    Array.Copy(raw, 0, first, 0, vertexCount * 2);
                    Array.Copy(raw, vertexCount * 2, second, 0, vertexCount * 2);
                    geoset.Uvs = first;
                    geoset.Uvs2 = second;
                }
                else
                {
                    geoset.Uvs = raw;
                    geoset.Uvs2 = null;
                }

                // MATS split by MTGC (MDX_PackModelGeometry walks groups by the authored sizes).
                if (matrixGroupSizes != null && matrices != null && matrixGroupSizes.Length > 0)
                {
                    var groups = new int[matrixGroupSizes.Length][];
                    int offset = 0;
                    for (int i = 0; i < matrixGroupSizes.Length; i++)
                    {
                        int size = matrixGroupSizes[i];
                        if (size < 0)
                        {
                            Diag($"{context}: MTGC[{i}] is negative ({size}); treated as 0");
                            size = 0;
                        }
                        if (offset + size > matrices.Length)
                        {
                            size = Math.Max(0, matrices.Length - offset);
                        }
                        var group = new int[size];
                        if (size > 0) Array.Copy(matrices, offset, group, 0, size);
                        groups[i] = group;
                        offset += matrixGroupSizes[i] < 0 ? 0 : matrixGroupSizes[i];
                    }
                    geoset.MatrixGroups = groups;
                }
                else
                {
                    // mdx_pack_vertices falls back to one implicit group holding matrix 0.
                    geoset.MatrixGroups = new[] { new[] { 0 } };
                }

                // The file has no geoset-level render flags (r_mdx_geoset.c reads layer->flags),
                // so expose the base layer's flags as a convenience mirror.
                if (geoset.MaterialId >= 0 && geoset.MaterialId < _model.Materials.Count)
                {
                    MdxMaterial material = _model.Materials[geoset.MaterialId];
                    if (material.Layers.Count > 0) geoset.Flags = (uint)material.Layers[0].Flags;
                }
            }

            // ---- geoset animations ---------------------------------------------------

            void ParseGeoa(MdxChunkReader r)
            {
                ReadRecordList(r, "GEOA", 28, (record, context) =>
                {
                    var animation = new MdxGeosetAnim();
                    animation.StaticAlpha = record.Extras.ReadF32();
                    animation.Flags = record.Extras.ReadU32();
                    animation.StaticColor = record.Extras.ReadVec3();
                    uint geosetId = record.Extras.ReadU32();
                    animation.GeosetId = geosetId == 0xFFFFFFFF ? -1 : (int)geosetId;

                    MdxChunkReader body = record.Extras;
                    while (body.AbsolutePosition < record.DeclaredEnd)
                    {
                        if (record.DeclaredEnd - body.AbsolutePosition < 4)
                        {
                            body.SkipToAbsolute(record.DeclaredEnd);
                            break;
                        }
                        int tagOffset = body.AbsolutePosition;
                        uint tag = body.ReadU32();
                        switch (tag)
                        {
                            case T_KGAO: animation.Alpha = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KGAO"); break;
                            case T_KGAC: animation.Color = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KGAC"); break;
                            default:
                                Diag($"{context}: unknown geoset-animation sub-chunk '{MdxChunkReader.TagName(tag)}' at " +
                                     $"offset {tagOffset}; 4 byte(s) skipped");
                                break;
                        }
                    }
                    _model.GeosetAnims.Add(animation);
                });
            }

            // ---- node chunks ---------------------------------------------------------

            void ParseBone(MdxChunkReader r)
            {
                ReadRecordList(r, "BONE", NodeHeaderSize, (record, context) =>
                {
                    var bone = new MdxBone { Node = new MdxNode() };
                    ReadNode(record, bone.Node, context);
                    // The geoset bindings sit *after* the record's declared size (MSG_ReadOverflow).
                    uint geosetId = record.Extras.ReadU32();
                    uint geosetAnimationId = record.Extras.ReadU32();
                    bone.GeosetId = geosetId == 0xFFFFFFFF ? -1 : (int)geosetId;
                    bone.GeosetAnimationId = geosetAnimationId == 0xFFFFFFFF ? -1 : (int)geosetAnimationId;
                    _model.Bones.Add(bone);
                });
            }

            void ParseHelp(MdxChunkReader r)
            {
                ReadRecordList(r, "HELP", NodeHeaderSize, (record, context) =>
                {
                    var node = new MdxNode();
                    ReadNode(record, node, context);
                    _model.Helpers.Add(node);
                });
            }

            void ParseCollisionShapes(MdxChunkReader r)
            {
                ReadRecordList(r, "CLID", NodeHeaderSize, (record, context) =>
                {
                    var shape = new MdxCollisionShape { Node = new MdxNode() };
                    ReadNode(record, shape.Node, context);
                    uint type = record.Extras.ReadU32();
                    if (type > 3)
                    {
                        Diag($"{context}: unknown collision shape type {type}; kept as Box");
                        type = 0;
                    }
                    shape.Type = (MdxCollisionShapeType)type;
                    shape.Vertex0 = record.Extras.ReadVec3();
                    if (shape.Type != MdxCollisionShapeType.Sphere) shape.Vertex1 = record.Extras.ReadVec3();
                    if (shape.Type == MdxCollisionShapeType.Sphere || shape.Type == MdxCollisionShapeType.Cylinder)
                    {
                        shape.Radius = record.Extras.ReadF32();
                    }
                    _model.CollisionShapes.Add(shape);
                });
            }

            void ParseEvents(MdxChunkReader r)
            {
                ReadRecordList(r, "EVTS", NodeHeaderSize, (record, context) =>
                {
                    var eventObject = new MdxEventObject { Node = new MdxNode() };
                    ReadNode(record, eventObject.Node, context);

                    uint tag = record.Extras.ReadU32();
                    if (tag == T_KEVT)
                    {
                        uint keyCount = record.Extras.ReadU32();
                        uint globalSeq = record.Extras.ReadU32();
                        eventObject.GlobalSequenceId = globalSeq == 0xFFFFFFFF ? -1 : (int)globalSeq;
                        long keyBytes = (long)keyCount * 4;
                        if (keyBytes > record.Extras.Remaining)
                        {
                            throw Error($"{context}.KEVT: {keyCount} key(s) do not fit the " +
                                        $"{record.Extras.Remaining} byte(s) left in the event chunk",
                                        context, record.Extras.AbsolutePosition);
                        }
                        eventObject.Keys = record.Extras.ReadInt32s((int)keyCount);
                    }
                    else
                    {
                        Diag($"{context}: expected KEVT at offset {record.Extras.AbsolutePosition - 4}, found " +
                             $"'{MdxChunkReader.TagName(tag)}'; the event has no keys");
                    }

                    ResolveEventObjectId(eventObject);
                    _model.Events.Add(eventObject);
                });
            }

            void ResolveEventObjectId(MdxEventObject eventObject)
            {
                string name = eventObject.Node?.Name ?? string.Empty;
                eventObject.EventType = name.Length >= 3 ? name.Substring(0, 3) : name;
                eventObject.EventData = TryGetEventObjectId(eventObject, eventObject.EventType, out string rowKey)
                    ? rowKey
                    : string.Empty;
                if (eventObject.EventData.Length == 0 && name.Length > 0)
                {
                    Diag($"EVTS '{name}': name does not match the 'XXX<sep><row key>' event-object convention");
                }
            }

            void ParseAttachment(MdxChunkReader r)
            {
                ReadRecordList(r, "ATCH", NodeHeaderSize, (record, context) =>
                {
                    var attachment = new MdxAttachment { Node = new MdxNode() };
                    ReadNodeWithSizePrefix(record, attachment.Node, context);
                    attachment.Path = record.Extras.ReadFixedString(AttachmentPathLength);
                    record.Extras.ReadU32(); // reserved field the reference skips
                    attachment.AttachmentId = record.Extras.ReadU32();

                    MdxChunkReader body = record.Extras;
                    while (body.AbsolutePosition < record.DeclaredEnd)
                    {
                        if (record.DeclaredEnd - body.AbsolutePosition < 4)
                        {
                            body.SkipToAbsolute(record.DeclaredEnd);
                            break;
                        }
                        int tagOffset = body.AbsolutePosition;
                        uint tag = body.ReadU32();
                        if (tag == T_KATV)
                        {
                            attachment.Visibility = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KATV");
                        }
                        else
                        {
                            Diag($"{context}: unknown attachment sub-chunk '{MdxChunkReader.TagName(tag)}' at offset " +
                                 $"{tagOffset}; 4 byte(s) skipped");
                        }
                    }
                    _model.Attachments.Add(attachment);
                });
            }

            void ParseLights(MdxChunkReader r)
            {
                ReadRecordList(r, "LITE", NodeHeaderSize, (record, context) =>
                {
                    var light = new MdxLight { Node = new MdxNode() };
                    ReadNodeWithSizePrefix(record, light.Node, context);
                    uint type = record.Extras.ReadU32();
                    if (type > 2)
                    {
                        Diag($"{context}: unknown light type {type}; kept as Omni");
                        type = 0;
                    }
                    light.Type = (MdxLightType)type;
                    light.AttenuationStart = record.Extras.ReadF32();
                    light.AttenuationEnd = record.Extras.ReadF32();
                    light.Color = record.Extras.ReadVec3();
                    light.Intensity = record.Extras.ReadF32();
                    light.AmbColor = record.Extras.ReadVec3();
                    light.AmbIntensity = record.Extras.ReadF32();

                    MdxChunkReader body = record.Extras;
                    while (body.AbsolutePosition < record.DeclaredEnd)
                    {
                        if (record.DeclaredEnd - body.AbsolutePosition < 4)
                        {
                            body.SkipToAbsolute(record.DeclaredEnd);
                            break;
                        }
                        int tagOffset = body.AbsolutePosition;
                        uint tag = body.ReadU32();
                        switch (tag)
                        {
                            case T_KLAV: light.Visibility = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KLAV"); break;
                            case T_KLAC: light.ColorTrack = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KLAC"); break;
                            case T_KLAI: light.IntensityTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KLAI"); break;
                            case T_KLBC: light.AmbColorTrack = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KLBC"); break;
                            case T_KLBI: light.AmbIntensityTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KLBI"); break;
                            case T_KLAS: light.AttenuationStartTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KLAS"); break;
                            case T_KLAE: light.AttenuationEndTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KLAE"); break;
                            default:
                                Diag($"{context}: unknown light sub-chunk '{MdxChunkReader.TagName(tag)}' at offset " +
                                     $"{tagOffset}; 4 byte(s) skipped");
                                break;
                        }
                    }
                    _model.Lights.Add(light);
                });
            }

            void ParseCameras(MdxChunkReader r)
            {
                ReadRecordList(r, "CAMS", CameraHeaderSize + 4, (record, context) =>
                {
                    if (record.DeclaredEnd - (record.Start + 4) < CameraHeaderSize)
                    {
                        throw Error($"{context}: camera record is only {record.DeclaredEnd - record.Start - 4} byte(s); " +
                                    $"{CameraHeaderSize} are required", context, record.Start);
                    }

                    var camera = new MdxCamera();
                    camera.Name = record.Extras.ReadFixedString(NodeNameLength);
                    camera.Pivot = record.Extras.ReadVec3();
                    camera.FieldOfView = record.Extras.ReadF32();
                    camera.FarClip = record.Extras.ReadF32();
                    camera.NearClip = record.Extras.ReadF32();
                    camera.TargetPivot = record.Extras.ReadVec3();

                    MdxChunkReader body = record.Extras;
                    while (body.AbsolutePosition < record.DeclaredEnd)
                    {
                        if (record.DeclaredEnd - body.AbsolutePosition < 4)
                        {
                            body.SkipToAbsolute(record.DeclaredEnd);
                            break;
                        }
                        int tagOffset = body.AbsolutePosition;
                        uint tag = body.ReadU32();
                        switch (tag)
                        {
                            case T_KCTR: camera.Translation = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KCTR"); break;
                            case T_KTTR: camera.TargetTranslation = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KTTR"); break;
                            case T_KCRL: camera.Roll = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KCRL"); break;
                            default:
                                Diag($"{context}: unknown camera sub-chunk '{MdxChunkReader.TagName(tag)}' at offset {tagOffset}");
                                break;
                        }
                    }
                    _model.Cameras.Add(camera);
                });
            }

            // ---- particle emitters ---------------------------------------------------

            void ParseParticleEmitter1(MdxChunkReader r)
            {
                ReadRecordList(r, "PREM", NodeHeaderSize, (record, context) =>
                {
                    var emitter = new MdxParticleEmitter1 { Node = new MdxNode() };
                    ReadNodeWithSizePrefix(record, emitter.Node, context);
                    emitter.EmissionRate = record.Extras.ReadF32();
                    emitter.Gravity = record.Extras.ReadF32();
                    emitter.Longitude = record.Extras.ReadF32();
                    emitter.Latitude = record.Extras.ReadF32();
                    emitter.Path = record.Extras.ReadFixedString(FileNameLength);
                    emitter.LifeSpan = record.Extras.ReadF32();
                    emitter.Speed = record.Extras.ReadF32();

                    MdxChunkReader body = record.Extras;
                    while (body.AbsolutePosition < record.DeclaredEnd)
                    {
                        if (record.DeclaredEnd - body.AbsolutePosition < 4)
                        {
                            body.SkipToAbsolute(record.DeclaredEnd);
                            break;
                        }
                        int tagOffset = body.AbsolutePosition;
                        uint tag = body.ReadU32();
                        switch (tag)
                        {
                            case T_KPEE: emitter.EmissionRateTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KPEE"); break;
                            case T_KPEG: emitter.GravityTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KPEG"); break;
                            case T_KPLN: emitter.LongitudeTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KPLN"); break;
                            case T_KPLT: emitter.LatitudeTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KPLT"); break;
                            case T_KPEL: emitter.LifeSpanTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KPEL"); break;
                            case T_KPES: emitter.SpeedTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KPES"); break;
                            case T_KPEV: emitter.VisibilityTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KPEV"); break;
                            default:
                                Diag($"{context}: unknown PREM sub-chunk '{MdxChunkReader.TagName(tag)}' at offset " +
                                     $"{tagOffset}; 4 byte(s) skipped");
                                break;
                        }
                    }
                    _model.ParticleEmitters1.Add(emitter);
                });
            }

            void ParseParticleEmitter2(MdxChunkReader r)
            {
                ReadRecordList(r, "PRE2", NodeHeaderSize, (record, context) =>
                {
                    var emitter = new MdxParticleEmitter2 { Node = new MdxNode() };
                    ReadNodeWithSizePrefix(record, emitter.Node, context);
                    emitter.Speed = record.Extras.ReadF32();
                    emitter.Variation = record.Extras.ReadF32();
                    emitter.Latitude = record.Extras.ReadF32();
                    emitter.Gravity = record.Extras.ReadF32();
                    emitter.LifeSpan = record.Extras.ReadF32();
                    emitter.EmissionRate = record.Extras.ReadF32();
                    emitter.Length = record.Extras.ReadF32();
                    emitter.Width = record.Extras.ReadF32();

                    uint filterMode = record.Extras.ReadU32();
                    if (filterMode >= 5)
                    {
                        Diag($"{context}: PRE2 FilterMode {filterMode} is outside MDX_PRE2_FILTER_COUNT; using Blend");
                        filterMode = 0;
                    }
                    emitter.FilterMode = (int)filterMode;

                    emitter.Rows = (int)record.Extras.ReadU32();
                    emitter.Columns = (int)record.Extras.ReadU32();
                    if (emitter.Rows <= 0 || emitter.Columns <= 0)
                    {
                        Diag($"{context}: PRE2 declares rows={emitter.Rows} columns={emitter.Columns}");
                    }
                    emitter.FrameFlags = (int)record.Extras.ReadU32();
                    emitter.TailLength = record.Extras.ReadF32();
                    emitter.Time = record.Extras.ReadF32();

                    float[] segmentColor = record.Extras.ReadFloats(9);
                    Array.Copy(segmentColor, emitter.SegmentColor, 9);

                    byte[] alpha = record.Extras.ReadBytes(3);
                    Array.Copy(alpha, emitter.Alpha, 3);

                    float[] scaling = record.Extras.ReadFloats(3);
                    Array.Copy(scaling, emitter.ParticleScaling, 3);
                    // r_mdx_load.c bakes the *2 the shared particle pipeline used to apply globally,
                    // so the *engine* quad size is twice the authored value. Keep the authored
                    // numbers in ParticleScaling and expose the engine-equivalent size here.
                    for (int i = 0; i < 3; i++) emitter.ParticleScalingEngine[i] = scaling[i] * 2f;

                    emitter.LifeSpanUvAnim = ReadUvAnim(record.Extras);
                    emitter.DecayUvAnim = ReadUvAnim(record.Extras);
                    emitter.TailUvAnim = ReadUvAnim(record.Extras);
                    emitter.TailDecayUvAnim = ReadUvAnim(record.Extras);

                    uint textureId = record.Extras.ReadU32();
                    emitter.TextureId = textureId == 0xFFFFFFFF ? -1 : (int)textureId;
                    emitter.Squirt = (int)record.Extras.ReadU32();
                    emitter.PriorityPlane = (int)record.Extras.ReadU32();
                    emitter.ReplaceableId = record.Extras.ReadU32();

                    MdxChunkReader body = record.Extras;
                    while (body.AbsolutePosition < record.DeclaredEnd)
                    {
                        if (record.DeclaredEnd - body.AbsolutePosition < 4)
                        {
                            body.SkipToAbsolute(record.DeclaredEnd);
                            break;
                        }
                        int tagOffset = body.AbsolutePosition;
                        uint tag = body.ReadU32();
                        switch (tag)
                        {
                            case T_KP2V: emitter.VisibilityTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KP2V"); break;
                            case T_KP2E: emitter.EmissionRateTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KP2E"); break;
                            case T_KP2W: emitter.WidthTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KP2W"); break;
                            case T_KP2N: emitter.LengthTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KP2N"); break;
                            case T_KP2S: emitter.SpeedTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KP2S"); break;
                            case T_KP2L: emitter.LatitudeTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KP2L"); break;
                            case T_KP2G: emitter.GravityTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KP2G"); break;
                            case T_KP2R: emitter.VariationTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KP2R"); break;
                            default:
                                Diag($"{context}: unknown PRE2 sub-chunk '{MdxChunkReader.TagName(tag)}' at offset " +
                                     $"{tagOffset}; 4 byte(s) skipped");
                                break;
                        }
                    }
                    _model.ParticleEmitters2.Add(emitter);
                });
            }

            static MdxParticleUvAnim ReadUvAnim(MdxChunkReader r)
            {
                MdxParticleUvAnim anim;
                anim.Start = r.ReadU32();
                anim.End = r.ReadU32();
                anim.Repeat = r.ReadU32();
                return anim;
            }

            void ParseRibbon(MdxChunkReader r)
            {
                ReadRecordList(r, "RIBB", NodeHeaderSize, (record, context) =>
                {
                    var ribbon = new MdxRibbonEmitter { Node = new MdxNode() };
                    ReadNodeWithSizePrefix(record, ribbon.Node, context);
                    ribbon.HeightAbove = record.Extras.ReadF32();
                    ribbon.HeightBelow = record.Extras.ReadF32();
                    ribbon.Alpha = record.Extras.ReadF32();
                    ribbon.Color = record.Extras.ReadVec3();
                    ribbon.Lifespan = record.Extras.ReadF32();
                    ribbon.TextureSlot = (int)record.Extras.ReadU32();
                    ribbon.EmissionRate = (int)record.Extras.ReadU32();
                    ribbon.Rows = (int)record.Extras.ReadU32();
                    ribbon.Columns = (int)record.Extras.ReadU32();
                    uint materialId = record.Extras.ReadU32();
                    ribbon.MaterialId = materialId == 0xFFFFFFFF ? -1 : (int)materialId;
                    ribbon.Gravity = record.Extras.ReadF32();

                    MdxChunkReader body = record.Extras;
                    while (body.AbsolutePosition < record.DeclaredEnd)
                    {
                        if (record.DeclaredEnd - body.AbsolutePosition < 4)
                        {
                            body.SkipToAbsolute(record.DeclaredEnd);
                            break;
                        }
                        int tagOffset = body.AbsolutePosition;
                        uint tag = body.ReadU32();
                        switch (tag)
                        {
                            case T_KRVS: ribbon.VisibilityTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KRVS"); break;
                            case T_KRHA: ribbon.HeightAboveTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KRHA"); break;
                            case T_KRHB: ribbon.HeightBelowTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KRHB"); break;
                            case T_KRAL: ribbon.AlphaTrack = ReadKeyTrack(body, MdxTrackData.Float1, context + ".KRAL"); break;
                            case T_KRCO: ribbon.ColorTrack = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KRCO"); break;
                            case T_KRTX: ribbon.TextureSlotTrack = ReadKeyTrack(body, MdxTrackData.Int1, context + ".KRTX"); break;
                            default:
                                Diag($"{context}: unknown RIBB sub-chunk '{MdxChunkReader.TagName(tag)}' at offset " +
                                     $"{tagOffset}; 4 byte(s) skipped");
                                break;
                        }
                    }
                    _model.RibbonEmitters.Add(ribbon);
                });
            }

            // ---- node plumbing -------------------------------------------------------

            /// <summary>Reads <c>[name][objectId][parentId][flags][tracks]</c> from a node body.</summary>
            void ReadNodeBody(MdxChunkReader body, MdxNode node, string context)
            {
                if (body.Length < NodeNameLength + 12)
                {
                    throw Error($"{context}: node body is {body.Length} byte(s); at least {NodeNameLength + 12} are required",
                                context, body.AbsolutePosition);
                }

                node.Name = body.ReadFixedString(NodeNameLength);

                uint nodeId = body.ReadU32();
                node.NodeId = nodeId <= int.MaxValue ? (int)nodeId : -1;
                if (node.NodeId < 0) Diag($"{context} '{node.Name}': node id {nodeId} is out of range; the node stays unregistered");

                uint parentId = body.ReadU32();
                node.ParentId = parentId == 0xFFFFFFFF || parentId > int.MaxValue ? -1 : (int)parentId;

                node.Flags = (MdxNodeFlags)body.ReadU32();

                while (!body.AtEnd)
                {
                    if (body.Remaining < 4)
                    {
                        Diag($"{context} '{node.Name}': {body.Remaining} trailing byte(s) at offset {body.AbsolutePosition}; ignored");
                        body.SeekToEnd();
                        return;
                    }
                    int tagOffset = body.AbsolutePosition;
                    uint tag = body.ReadU32();
                    switch (tag)
                    {
                        case T_KGTR: node.Translation = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KGTR"); break;
                        case T_KGRT: node.Rotation = ReadKeyTrack(body, MdxTrackData.Float4, context + ".KGRT"); break;
                        case T_KGSC: node.Scaling = ReadKeyTrack(body, MdxTrackData.Float3, context + ".KGSC"); break;
                        default:
                            // ReadNode keeps reading 4-byte headers until the node body ends.
                            Diag($"{context} '{node.Name}': unknown node sub-chunk '{MdxChunkReader.TagName(tag)}' at offset " +
                                 $"{tagOffset}; 4 byte(s) skipped");
                            break;
                    }
                }
            }

            /// <summary>
            /// Node record whose declared size covers <c>[size][node body]</c>, e.g. BONE, EVTS,
            /// CLID and HELP.
            /// </summary>
            void ReadNode(RecordView record, MdxNode node, string context)
            {
                MdxChunkReader body = _fileView.Slice(record.Start + 4, record.DeclaredEnd, context);
                ReadNodeBody(body, node, context);
                if (record.Extras.AbsolutePosition < record.DeclaredEnd)
                {
                    record.Extras.SkipToAbsolute(record.DeclaredEnd);
                }
            }

            /// <summary>
            /// Node record with its own node-size field right after the record size, e.g. ATCH,
            /// PREM, PRE2, RIBB and LITE (<c>ReadAttachment</c>, <c>ReadParticleEmitter</c>, ...).
            /// Returns the absolute offset where the node body ends.
            /// </summary>
            int ReadNodeWithSizePrefix(RecordView record, MdxNode node, string context)
            {
                int nodeSize = record.Extras.ReadI32();
                if (nodeSize < 4)
                {
                    throw Error($"{context}: node size field is {nodeSize}; expected at least 4", context,
                                record.Start + 4);
                }

                int bodyEnd = record.Start + 4 + nodeSize;
                if (bodyEnd > record.ContainerEnd)
                {
                    throw Error($"{context}: node body ends at {bodyEnd}, past the end of the chunk ({record.ContainerEnd})",
                                context, record.Start + 4);
                }

                MdxChunkReader body = _fileView.Slice(record.Start + 8, bodyEnd, context);
                ReadNodeBody(body, node, context);
                record.Extras.SkipToAbsolute(bodyEnd);
                return bodyEnd;
            }

            // ---- key tracks ----------------------------------------------------------

            MdxKeyTrack ReadKeyTrack(MdxChunkReader r, MdxTrackData dataType, string context)
            {
                var track = new MdxKeyTrack { DataType = dataType };

                uint keyCount = r.ReadU32();
                uint interpolationRaw = r.ReadU32();
                uint globalSequenceRaw = r.ReadU32();

                track.Interpolation = interpolationRaw <= 3
                    ? (MdxTrackInterpolation)interpolationRaw
                    : MdxTrackInterpolation.None;
                if (interpolationRaw > 3)
                {
                    Diag($"{context}: unknown key-track interpolation {interpolationRaw}; only the key times are read " +
                         "(GetModelKeyTrackTypeSize returns 0)");
                }
                track.GlobalSequenceId = globalSequenceRaw == 0xFFFFFFFF ? -1 : (int)globalSequenceRaw;

                int componentBytes = dataType switch
                {
                    MdxTrackData.Float3 => 12,
                    MdxTrackData.Float4 => 16,
                    _ => 4,
                };
                int components = componentBytes / 4;
                int typeSize = interpolationRaw <= 1 ? 1 : interpolationRaw <= 3 ? 3 : 0;
                long keyFrameSize = 4 + (long)componentBytes * typeSize;
                long totalBytes = keyFrameSize * keyCount;
                if (totalBytes > r.Remaining)
                {
                    throw Error($"{context}: {keyCount} key(s) need {totalBytes} byte(s) but only {r.Remaining} remain",
                                context, r.AbsolutePosition);
                }

                bool hasTangents = typeSize == 3;
                track.Frames = new int[keyCount];
                track.Values = new float[keyCount * components];
                if (hasTangents)
                {
                    track.InTangents = new float[keyCount * components];
                    track.OutTangents = new float[keyCount * components];
                }

                for (int i = 0; i < keyCount; i++)
                {
                    track.Frames[i] = r.ReadI32();
                    ReadTrackValues(r, track.Values, i * components, components, dataType);
                    if (hasTangents)
                    {
                        ReadTrackValues(r, track.InTangents, i * components, components, dataType);
                        ReadTrackValues(r, track.OutTangents, i * components, components, dataType);
                    }
                }
                return track;
            }

            void ReadTrackValues(MdxChunkReader r, float[] destination, int offset, int components, MdxTrackData dataType)
            {
                if (dataType == MdxTrackData.Int1)
                {
                    for (int i = 0; i < components; i++) destination[offset + i] = r.ReadI32();
                }
                else
                {
                    for (int i = 0; i < components; i++) destination[offset + i] = r.ReadF32();
                }
            }

            // ---- record list driver --------------------------------------------------

            /// <summary>
            /// Port of <c>MODEL_READ_LIST</c>: repeatedly reads a <c>[size][body]</c> record until
            /// the chunk is exhausted. <paramref name="read"/> sees the record's declared end and a
            /// cursor that may read the trailing fields stored after the declared size, exactly like
            /// the reference's <c>MSG_ReadOverflow</c>.
            /// </summary>
            void ReadRecordList(MdxChunkReader container, string listName, int minimumDeclaredSize, RecordReader read,
                                Func<int, bool> plausibleRecord = null)
            {
                int containerEnd = container.AbsoluteEnd;
                int index = 0;

                while (container.AbsolutePosition < containerEnd)
                {
                    int recordStart = container.AbsolutePosition;
                    if (containerEnd - recordStart < 4)
                    {
                        Diag($"{listName}: {containerEnd - recordStart} trailing byte(s) at offset {recordStart} " +
                             "are not a record; ignored");
                        container.SeekToEnd();
                        return;
                    }

                    int declared = MdxChunkReader.PeekI32(_data, recordStart);
                    int declaredEnd = recordStart + declared;
                    if (declared < 4 || declaredEnd > containerEnd)
                    {
                        int resyncSize = 0;
                        int resync = _options.StrictRecords
                            ? -1
                            : FindNextRecordStart(recordStart, containerEnd, minimumDeclaredSize, plausibleRecord, out resyncSize);

                        if (resync < 0)
                        {
                            if (_options.StrictRecords)
                            {
                                throw Error($"{listName}: record at offset {recordStart} declares {declared} byte(s), " +
                                            $"which does not fit the {containerEnd - recordStart} byte(s) left in the chunk",
                                            listName, recordStart);
                            }
                            Diag($"{listName}: record at offset {recordStart} declares {declared} byte(s), which does not fit " +
                                 $"the {containerEnd - recordStart} byte(s) left in the chunk; no further record could be " +
                                 $"resynchronised, the rest of the chunk ({containerEnd - recordStart} byte(s)) is ignored");
                            container.SeekToEnd();
                            return;
                        }

                        Diag($"{listName}: record at offset {recordStart} declares {declared} byte(s), which does not fit the " +
                             $"chunk; resynchronised to offset {resync} (record size {resyncSize}) — stock hero models contain " +
                             "such a stray byte");
                        container.SkipToAbsolute(resync);
                        continue;
                    }

                    var record = new RecordView
                    {
                        Extras = _fileView.Slice(recordStart + 4, _data.Length, listName + "[" + index + "]"),
                        Start = recordStart,
                        DeclaredEnd = declaredEnd,
                        ContainerEnd = containerEnd,
                    };

                    read(record, listName + "[" + index + "]");
                    index++;

                    int next = Math.Max(record.Extras.AbsolutePosition, declaredEnd);
                    if (next >= containerEnd)
                    {
                        container.SeekToEnd();
                        return;
                    }
                    container.SkipToAbsolute(next);
                }
            }

            /// <summary>
            /// Byte-wise scan for the next plausible record header inside a damaged list (bounded by
            /// the chunk, so a malformed record can never cause an unbounded search).
            /// </summary>
            int FindNextRecordStart(int fromOffset, int containerEnd, int minimumDeclaredSize,
                                    Func<int, bool> plausibleRecord, out int declaredSize)
            {
                for (int candidate = fromOffset + 1; candidate + 4 <= containerEnd; candidate++)
                {
                    int size = MdxChunkReader.PeekI32(_data, candidate);
                    if (size < minimumDeclaredSize || candidate + (long)size > containerEnd) continue;
                    // A caller-supplied shape check keeps a damaged chunk from turning unrelated bytes
                    // into fabricated records (a geoset candidate must start with a real sub-chunk).
                    if (plausibleRecord != null && !plausibleRecord(candidate)) continue;
                    declaredSize = size;
                    return candidate;
                }
                declaredSize = 0;
                return -1;
            }

            // ---- post-processing (R_LoadModelMDLX tail) --------------------------------

            void Finish()
            {
                RegisterNodes();
                LinkHierarchy();
                ApplyPivots();
                BindGeosetAnimations();
                _model.Bounds = CalculateBounds();
                _model.NodeCount = _model.NodeList.Count;
                if (_model.ParticleEmitters1.Count > 0)
                {
                    Diag($"{_model.ParticleEmitters1.Count} PREM particle emitter(s) retained; the engine has no runtime " +
                         "model emission for ParticleEmitter1");
                }
            }

            /// <summary>
            /// MDLX_AddNode runs once per list after every chunk is parsed, in the order
            /// R_LoadModelMDLX walks them (bones, helpers, collision shapes, PREM, PRE2, ribbons,
            /// attachments, lights, events). The node list keeps that order, with parents moved
            /// ahead of their children so a single pass can evaluate the hierarchy.
            /// </summary>
            void RegisterNodes()
            {
                var candidates = new List<MdxNode>(_model.Bones.Count + _model.Helpers.Count + _model.Attachments.Count);
                foreach (MdxBone bone in _model.Bones) candidates.Add(bone.Node);
                candidates.AddRange(_model.Helpers);
                foreach (MdxCollisionShape shape in _model.CollisionShapes) candidates.Add(shape.Node);
                foreach (MdxParticleEmitter1 emitter in _model.ParticleEmitters1) candidates.Add(emitter.Node);
                foreach (MdxParticleEmitter2 emitter in _model.ParticleEmitters2) candidates.Add(emitter.Node);
                foreach (MdxRibbonEmitter ribbon in _model.RibbonEmitters) candidates.Add(ribbon.Node);
                foreach (MdxAttachment attachment in _model.Attachments) candidates.Add(attachment.Node);
                foreach (MdxLight light in _model.Lights) candidates.Add(light.Node);
                foreach (MdxEventObject eventObject in _model.Events) candidates.Add(eventObject.Node);

                int maxNodeId = -1;
                foreach (MdxNode node in candidates)
                {
                    if (node == null || node.NodeId < 0) continue;
                    if (node.NodeId > _options.MaxNodeId)
                    {
                        Diag($"'{node.Name}': node id {node.NodeId} exceeds the supported maximum ({_options.MaxNodeId}); " +
                             "the node is not registered (the engine caps at MDX_MAX_NODES)");
                        continue;
                    }
                    if (node.NodeId > maxNodeId) maxNodeId = node.NodeId;
                }

                var nodes = new MdxNode[maxNodeId + 1];
                foreach (MdxNode node in candidates)
                {
                    if (node == null || node.NodeId < 0 || node.NodeId > _options.MaxNodeId) continue;
                    if (nodes[node.NodeId] == null)
                    {
                        nodes[node.NodeId] = node;
                        _registered.Add(node);
                    }
                    else
                    {
                        Diag($"node id {node.NodeId} is used by both '{nodes[node.NodeId].Name}' and '{node.Name}'; " +
                             "the first registration wins");
                    }
                }
                _model.Nodes = nodes;
            }

            void LinkHierarchy()
            {
                foreach (MdxNode node in _registered)
                {
                    int parentId = node.ParentId;
                    MdxNode parent = parentId >= 0 && parentId < _model.Nodes.Length ? _model.Nodes[parentId] : null;
                    if (parentId >= 0 && parent == null)
                    {
                        // The reference leaves the node unparented when model->nodes[parent_id] is empty.
                        node.ParentId = -1;
                        continue;
                    }
                    if (parent == node)
                    {
                        Diag($"'{node.Name}': node {node.NodeId} is its own parent; the link is dropped");
                        node.ParentId = -1;
                        continue;
                    }
                    node.Parent = parent;
                }

                // Reject parent cycles so a single-pass hierarchy evaluation cannot hang.
                foreach (MdxNode node in _registered)
                {
                    MdxNode walker = node.Parent;
                    int guard = _registered.Count + 1;
                    while (walker != null && guard-- > 0) walker = walker.Parent;
                    if (walker != null)
                    {
                        Diag($"'{node.Name}': parent chain from node {node.NodeId} does not terminate; the parent link is dropped");
                        node.Parent = null;
                        node.ParentId = -1;
                    }
                }

                foreach (MdxNode node in _registered)
                {
                    node.Children.Clear();
                }
                foreach (MdxNode node in _registered)
                {
                    node.Parent?.Children.Add(node);
                }
            }

            void ApplyPivots()
            {
                // MDLX_GetModelNodeWithObjectID / R_CalculateNodeMatrix: pivots[node_id].
                foreach (MdxNode node in _registered)
                {
                    if (node.NodeId >= 0 && node.NodeId < _model.Pivots.Count) node.Pivot = _model.Pivots[node.NodeId];
                }

                // NodeList: registration order with parents ahead of children.
                _model.NodeList.Clear();
                var emitted = new HashSet<MdxNode>();
                foreach (MdxNode node in _registered)
                {
                    EmitNode(node, emitted, _registered.Count + 1);
                }
            }

            void EmitNode(MdxNode node, HashSet<MdxNode> emitted, int depthGuard)
            {
                if (node == null || emitted.Contains(node)) return;
                if (depthGuard <= 0)
                {
                    emitted.Add(node);
                    _model.NodeList.Add(node);
                    return;
                }
                if (node.Parent != null) EmitNode(node.Parent, emitted, depthGuard - 1);
                if (emitted.Add(node)) _model.NodeList.Add(node);
            }

            void BindGeosetAnimations()
            {
                // R_LoadModelMDLX binds each geoset anim to geosets[geosetId] (0xFFFFFFFF = none).
                for (int i = 0; i < _model.GeosetAnims.Count; i++)
                {
                    int geosetId = _model.GeosetAnims[i].GeosetId;
                    if (geosetId < 0 || geosetId >= _model.Geosets.Count)
                    {
                        if (geosetId >= 0)
                        {
                            Diag($"GEOA[{i}]: geoset id {geosetId} is outside the {_model.Geosets.Count}-geoset model");
                        }
                        continue;
                    }
                    _model.Geosets[geosetId].GeosetAnimId = i;
                }

                for (int i = 0; i < _model.Geosets.Count; i++)
                {
                    MdxGeoset geoset = _model.Geosets[i];
                    if (geoset.MaterialId < 0 || geoset.MaterialId >= _model.Materials.Count)
                    {
                        if (geoset.MaterialId >= 0)
                        {
                            Diag($"GEOS[{i}]: material index {geoset.MaterialId} is outside the " +
                                 $"{_model.Materials.Count}-material model");
                        }
                    }
                }
            }

            /// <summary>
            /// Port of MDX_CalculateBounds. Note the reference starts from a zeroed box, so the
            /// result always contains the origin and <c>radius</c> is the largest axis extent
            /// rather than a sphere radius; mdxtool --dump-all prints the same numbers.
            /// </summary>
            MdxBounds CalculateBounds()
            {
                var bounds = new MdxBounds { Radius = 0f, Min = Vec3.Zero, Max = Vec3.Zero };
                foreach (MdxGeoset geoset in _model.Geosets)
                {
                    float[] vertices = geoset.Vertices;
                    for (int i = 0; i + 2 < vertices.Length; i += 3)
                    {
                        float x = vertices[i], y = vertices[i + 1], z = vertices[i + 2];
                        if (x < bounds.Min.X) bounds.Min.X = x;
                        if (y < bounds.Min.Y) bounds.Min.Y = y;
                        if (z < bounds.Min.Z) bounds.Min.Z = z;
                        if (x > bounds.Max.X) bounds.Max.X = x;
                        if (y > bounds.Max.Y) bounds.Max.Y = y;
                        if (z > bounds.Max.Z) bounds.Max.Z = z;
                    }
                }
                bounds.Radius = Math.Max(bounds.Radius, bounds.Max.X - bounds.Min.X);
                bounds.Radius = Math.Max(bounds.Radius, bounds.Max.Y - bounds.Min.Y);
                bounds.Radius = Math.Max(bounds.Radius, bounds.Max.Z - bounds.Min.Z);
                return bounds;
            }
        }
    }
}
