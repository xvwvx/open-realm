using System;
using System.Collections.Generic;
using MdxPreview.Core.Math3D;

namespace MdxPreview.Core.Mdx
{
    public enum MdxTrackInterpolation
    {
        None = 0,
        Linear = 1,
        Hermite = 2,
        Bezier = 3,
    }

    /// <summary>Mirrors the engine's MODELKEYTRACKDATATYPE (renderer/r_local.h).</summary>
    public enum MdxTrackData
    {
        Int1 = 0,
        Float1 = 1,
        Float3 = 2,
        Float4 = 3,
    }

    /// <summary>
    /// One MDX keyframe track ("KXXX" chunk). Values are stored flat
    /// (KeyCount * Components) so a single interpolation routine can serve
    /// scalars, vectors and quaternions — this mirrors the C reference where a
    /// key is <c>{ int time; char data[]; }</c>.
    /// </summary>
    public sealed class MdxKeyTrack
    {
        public MdxTrackInterpolation Interpolation;
        public MdxTrackData DataType;
        /// <summary>GLBS index, or -1 when the track is driven by the sequence clock.</summary>
        public int GlobalSequenceId = -1;
        public int[] Frames = Array.Empty<int>();
        public float[] Values = Array.Empty<float>();
        /// <summary>Hermite/Bezier in-tangents, null otherwise.</summary>
        public float[] InTangents;
        /// <summary>Hermite/Bezier out-tangents, null otherwise.</summary>
        public float[] OutTangents;

        public int KeyCount => Frames.Length;

        public int Components => DataType switch
        {
            MdxTrackData.Float3 => 3,
            MdxTrackData.Float4 => 4,
            _ => 1,
        };

        public float GetComponent(int keyIndex, int component)
            => Values[keyIndex * Components + component];

        public void SetComponent(int keyIndex, int component, float value)
            => Values[keyIndex * Components + component] = value;

        public override string ToString()
            => $"{DataType} {Interpolation} keys={KeyCount} gseq={(GlobalSequenceId < 0 ? "-" : GlobalSequenceId.ToString())}";
    }

    [Flags]
    public enum MdxNodeFlags : uint
    {
        None = 0,
        DontInheritTranslation = 0x1,
        DontInheritRotation = 0x2,
        DontInheritScaling = 0x4,
        Billboarded = 0x8,
        BillboardedLockX = 0x10,
        BillboardedLockY = 0x20,
        BillboardedLockZ = 0x40,
        CameraAnchored = 0x80,
        Bone = 0x100,
        Light = 0x200,
        EventObject = 0x400,
        Attachment = 0x800,
        ParticleEmitter = 0x1000,
        CollisionShape = 0x2000,
        RibbonEmitter = 0x4000,
        Unshaded_EmitterUsesMdl = 0x8000,
        SortPrimitivesFarZ_EmitterUsesTga = 0x10000,
        LineEmitter = 0x20000,
        Unfogged = 0x40000,
        ModelSpace = 0x80000,
        XYQuad = 0x100000,
    }

    /// <summary>
    /// A node in the MDX hierarchy (MDLGENOBJECT). Bones, helpers, attachments,
    /// emitters, event objects, collision shapes and lights all embed one.
    /// </summary>
    public sealed class MdxNode
    {
        public string Name = string.Empty;
        /// <summary>Global node index (also the PIVT index).</summary>
        public int NodeId;
        /// <summary>Parent node index or -1 for a root node.</summary>
        public int ParentId = -1;
        public MdxNodeFlags Flags;

        public MdxKeyTrack Translation;
        public MdxKeyTrack Rotation;
        public MdxKeyTrack Scaling;

        public MdxNode Parent;
        public readonly List<MdxNode> Children = new List<MdxNode>();

        /// <summary>Pivot (rotation origin) in model space, filled from PIVT.</summary>
        public Vec3 Pivot;

        /// <summary>True when any of the node's tracks contains keys.</summary>
        public bool IsAnimated => (Translation?.KeyCount ?? 0) > 0
                                  || (Rotation?.KeyCount ?? 0) > 0
                                  || (Scaling?.KeyCount ?? 0) > 0;

        public override string ToString() => $"{Name}#{NodeId} flags=0x{(uint)Flags:X}";
    }

    public enum MdxLightType
    {
        Omni = 0,
        Direct = 1,
        Ambient = 2,
    }

    public enum MdxCollisionShapeType
    {
        Box = 0,
        Plane = 1,
        Sphere = 2,
        Cylinder = 3,
    }

    /// <summary>Mirrors the engine's BLEND_MODE (common/shared.h).</summary>
    public enum MdxBlendMode
    {
        None = 0,
        AlphaKey = 1,
        Blend = 2,
        Add = 3,
        AddAlpha = 4,
        Modulate = 5,
        Modulate2x = 6,
    }

    [Flags]
    public enum MdxGeoFlags
    {
        None = 0,
        Unshaded = 0x1,
        SphereEnvMap = 0x2,
        WrapWidth = 0x4,
        WrapHeight = 0x8,
        TwoSided = 0x10,
        Unfogged = 0x20,
        NoDepthTest = 0x40,
        NoDepthSet = 0x80,
        NoFallback = 0x100,
    }

    public struct MdxBounds
    {
        public float Radius;
        public Vec3 Min;
        public Vec3 Max;
    }

    public sealed class MdxSequence
    {
        public string Name = string.Empty;
        public int Start;
        public int End;
        public float MoveSpeed;
        public uint Flags;
        public float Rarity;
        public int SyncPoint;
        public MdxBounds Bounds;

        public int Duration => Math.Max(0, End - Start);
        public bool NonLooping => (Flags & 1) != 0;

        public override string ToString() => $"{Name} [{Start}..{End}]";
    }

    public sealed class MdxInfo
    {
        public string Name = string.Empty;
        public string AnimationFile = string.Empty;
        public MdxBounds Bounds;
        public uint BlendTime;
    }

    public sealed class MdxTexture
    {
        public uint ReplaceableId;
        public string Path = string.Empty;
        /// <summary>Wrap width bit (nWrapping &amp; 0x1) as 1/0, matching R_SetTextureWrap.</summary>
        public int WrapWidth;
        /// <summary>Wrap height bit (nWrapping &amp; 0x2) as 1/0, matching R_SetTextureWrap.</summary>
        public int WrapHeight;
        /// <summary>The authored nWrapping value as stored in TEXS (bit 0 width, bit 1 height).</summary>
        public int WrapFlags;
        /// <summary>Resolved image path after applying the engine's fallbacks.</summary>
        public string ResolvedPath;

        public override string ToString()
            => ReplaceableId != 0 ? $"<replaceable {ReplaceableId}>" : Path;
    }

    public sealed class MdxMaterialLayer
    {
        /// <summary>Authored MDX filter mode (0..6) as written in the file.</summary>
        public int FilterMode;
        /// <summary>Engine blend mode derived from <see cref="FilterMode"/>.</summary>
        public MdxBlendMode BlendMode;
        public MdxGeoFlags Flags;
        /// <summary>TEXS index or -1.</summary>
        public int TextureId = -1;
        /// <summary>TXAN index or -1.</summary>
        public int TransformId = -1;
        /// <summary>UVAS/coord id or -1.</summary>
        public int CoordId = -1;
        public float StaticAlpha = 1f;
        public MdxKeyTrack Alpha;
        /// <summary>KMTF integer flipbook track (texture frame index).</summary>
        public MdxKeyTrack Flipbook;
    }

    public sealed class MdxMaterial
    {
        public int Priority;
        public int Flags;
        public List<MdxMaterialLayer> Layers = new List<MdxMaterialLayer>();
        public MdxKeyTrack Emission;
        public MdxKeyTrack Alpha;
        public MdxKeyTrack Flipbook;
    }

    public sealed class MdxTextureAnim
    {
        public MdxKeyTrack Translation;
        public MdxKeyTrack Rotation;
        public MdxKeyTrack Scaling;
    }

    public sealed class MdxGeosetAnim
    {
        public float StaticAlpha = 1f;
        public uint Flags;
        public Vec3 StaticColor = Vec3.One;
        /// <summary>GEOS index or -1.</summary>
        public int GeosetId = -1;
        public MdxKeyTrack Alpha;
        public MdxKeyTrack Color;
        /// <summary>Rough animated flag check for renderers that skip static cases.</summary>
        public bool IsAnimated => (Alpha?.KeyCount ?? 0) > 0 || ((Flags & 2) != 0 && (Color?.KeyCount ?? 0) > 0);
    }

    public sealed class MdxGeoset
    {
        /// <summary>Flat xyz triples.</summary>
        public float[] Vertices = Array.Empty<float>();
        public float[] Normals = Array.Empty<float>();
        /// <summary>Flat uv pairs (first coordinate set).</summary>
        public float[] Uvs = Array.Empty<float>();
        /// <summary>Optional second UV set (UVBS with two channels).</summary>
        public float[] Uvs2;
        public int VertexCount;

        /// <summary>Flattened triangle list (per-primitive PVTX blocks concatenated).</summary>
        public ushort[] Indices = Array.Empty<ushort>();
        public int[] PrimitiveTypes = Array.Empty<int>();
        public int[] PrimitiveCounts = Array.Empty<int>();

        /// <summary>Per vertex matrix-group index (GNDX).</summary>
        public byte[] VertexGroups = Array.Empty<byte>();
        /// <summary>Bone/node ids per matrix group (MATS split by MTGC).</summary>
        public int[][] MatrixGroups = Array.Empty<int[]>();

        public int MaterialId = -1;
        public int GeosetAnimId = -1;
        public int Group;
        public int Selectable;
        public uint Flags;

        public MdxBounds Bounds;
        public MdxBounds[] SequenceBounds = Array.Empty<MdxBounds>();
        public int UvChannelCount = 1;

        public override string ToString()
            => $"verts={VertexCount} tris={Indices.Length / 3} groups={MatrixGroups.Length} material={MaterialId}";
    }

    public sealed class MdxBone
    {
        public MdxNode Node;
        public int GeosetId = -1;
        public int GeosetAnimationId = -1;
    }

    public sealed class MdxAttachment
    {
        public MdxNode Node;
        public string Path = string.Empty;
        public uint AttachmentId;
        public MdxKeyTrack Visibility;
    }

    public sealed class MdxCamera
    {
        public string Name = string.Empty;
        public Vec3 Pivot;
        public float FieldOfView = 0.9500215f;
        public float FarClip = 27.7777786f;
        public float NearClip = 0.222222224f;
        public Vec3 TargetPivot;
        public MdxKeyTrack Translation;
        public MdxKeyTrack Roll;
        public MdxKeyTrack TargetTranslation;
    }

    public sealed class MdxEventObject
    {
        public MdxNode Node;
        public int[] Keys = Array.Empty<int>();
        public int GlobalSequenceId = -1;
        /// <summary>Resolved event id: the "SND"/"SPN"/"SPL"/"FPT"/"UBR"/"FTP" prefix.</summary>
        public string EventType = string.Empty;
        /// <summary>Payload after the type prefix (a sound label, SLK row key, ...).</summary>
        public string EventData = string.Empty;
    }

    public sealed class MdxCollisionShape
    {
        public MdxNode Node;
        public MdxCollisionShapeType Type;
        public Vec3 Vertex0;
        public Vec3 Vertex1;
        public float Radius;
    }

    public sealed class MdxLight
    {
        public MdxNode Node;
        public MdxLightType Type;
        public float AttenuationStart;
        public float AttenuationEnd;
        public Vec3 Color = Vec3.One;
        public float Intensity = 1f;
        public Vec3 AmbColor = Vec3.One;
        public float AmbIntensity = 1f;
        public MdxKeyTrack Visibility;
        public MdxKeyTrack ColorTrack;
        public MdxKeyTrack IntensityTrack;
        public MdxKeyTrack AmbColorTrack;
        public MdxKeyTrack AmbIntensityTrack;
        public MdxKeyTrack AttenuationStartTrack;
        public MdxKeyTrack AttenuationEndTrack;
    }

    /// <summary>Particle emitter v1 (PREM). Parsed for completeness; the plugin
    /// previews it with the same simulator as PRE2 where the authored data allows.</summary>
    public sealed class MdxParticleEmitter1
    {
        public MdxNode Node;
        public float EmissionRate;
        public float Gravity;
        public float Longitude;
        public float Latitude;
        public string Path = string.Empty;
        public float LifeSpan;
        public float Speed;
        public MdxKeyTrack EmissionRateTrack;
        public MdxKeyTrack GravityTrack;
        public MdxKeyTrack LongitudeTrack;
        public MdxKeyTrack LatitudeTrack;
        public MdxKeyTrack LifeSpanTrack;
        public MdxKeyTrack SpeedTrack;
        public MdxKeyTrack VisibilityTrack;
    }

    public struct MdxParticleUvAnim
    {
        public uint Start;
        public uint End;
        public uint Repeat;
    }

    /// <summary>Particle emitter v2 (PRE2) — the emitter type retail WC3 actually uses.</summary>
    public sealed class MdxParticleEmitter2
    {
        public MdxNode Node;
        public float Speed;
        public float Variation;
        public float Latitude;
        public float Gravity;
        public float LifeSpan;
        public float EmissionRate;
        public float Length;
        public float Width;
        public int FilterMode;
        public int Rows = 1;
        public int Columns = 1;
        /// <summary>0 head, 1 tail, 2 both (BZ_MDX_PARTICLE_*).</summary>
        public int FrameFlags;
        public float TailLength;
        public float Time;                 // middle time of the three segment colours
        public float[] SegmentColor = new float[9];
        public byte[] Alpha = { 255, 255, 255 };
        /// <summary>Authored ParticleScaling values, exactly as stored in the file.</summary>
        public float[] ParticleScaling = { 1f, 1f, 1f };
        /// <summary>
        /// The same three scalars multiplied by 2, which is what r_mdx_load.c leaves in
        /// <c>pe-&gt;ParticleScaling</c> so the shared particle pipeline draws Warcraft III's
        /// authored sizes. Use this when the consumer does not apply its own 2x factor.
        /// </summary>
        public float[] ParticleScalingEngine = { 2f, 2f, 2f };
        public MdxParticleUvAnim LifeSpanUvAnim;
        public MdxParticleUvAnim DecayUvAnim;
        public MdxParticleUvAnim TailUvAnim;
        public MdxParticleUvAnim TailDecayUvAnim;
        public int TextureId = -1;
        public int Squirt;
        public int PriorityPlane;
        public uint ReplaceableId;

        public MdxKeyTrack VisibilityTrack;
        public MdxKeyTrack EmissionRateTrack;
        public MdxKeyTrack WidthTrack;
        public MdxKeyTrack LengthTrack;
        public MdxKeyTrack SpeedTrack;
        public MdxKeyTrack LatitudeTrack;
        public MdxKeyTrack GravityTrack;
        public MdxKeyTrack VariationTrack;

        public MdxBlendMode BlendMode => FilterMode switch
        {
            0 => MdxBlendMode.Blend,
            1 => MdxBlendMode.Add,
            2 => MdxBlendMode.Modulate,
            3 => MdxBlendMode.Modulate2x,
            4 => MdxBlendMode.AlphaKey,
            _ => MdxBlendMode.Blend,
        };
    }

    public sealed class MdxRibbonEmitter
    {
        public MdxNode Node;
        public float HeightAbove;
        public float HeightBelow;
        public float Alpha = 1f;
        public Vec3 Color = Vec3.One;
        public float Lifespan = 1f;
        public int TextureSlot;
        public int EmissionRate;
        public int Rows = 1;
        public int Columns = 1;
        public int MaterialId = -1;
        public float Gravity;
        public MdxKeyTrack VisibilityTrack;
        public MdxKeyTrack HeightAboveTrack;
        public MdxKeyTrack HeightBelowTrack;
        public MdxKeyTrack AlphaTrack;
        public MdxKeyTrack ColorTrack;
        public MdxKeyTrack TextureSlotTrack;
    }

    /// <summary>
    /// A parsed MDX (MDLX) model. Layout mirrors the engine's mdxModel_t
    /// (games/warcraft-3/renderer/mdx/r_mdx.h) with lists instead of linked lists.
    /// </summary>
    public sealed class MdxModel
    {
        public uint Version;
        public MdxInfo Info = new MdxInfo();
        public MdxBounds Bounds;
        /// <summary>Virtual path the model was loaded from (diagnostics / re-export).</summary>
        public string SourcePath = string.Empty;

        /// <summary>True for Reforged containers (VERS &gt;= 900), which are parsed with the classic layout.</summary>
        public bool IsReforged;

        /// <summary>
        /// Non-fatal load notes from the parser: unhandled chunks, Reforged-only data, records that
        /// had to be resynchronised, values that were normalised. Diagnostics never abort a load.
        /// </summary>
        public readonly List<string> Diagnostics = new List<string>();

        public bool HasDiagnostics => Diagnostics.Count > 0;

        public string DiagnosticsText => Diagnostics.Count == 0 ? string.Empty : string.Join("\n", Diagnostics);

        public readonly List<MdxSequence> Sequences = new List<MdxSequence>();
        public readonly List<uint> GlobalSequences = new List<uint>();
        public readonly List<MdxTexture> Textures = new List<MdxTexture>();
        public readonly List<MdxMaterial> Materials = new List<MdxMaterial>();
        public readonly List<MdxTextureAnim> TextureAnims = new List<MdxTextureAnim>();
        public readonly List<MdxGeoset> Geosets = new List<MdxGeoset>();
        public readonly List<MdxGeosetAnim> GeosetAnims = new List<MdxGeosetAnim>();
        public readonly List<MdxBone> Bones = new List<MdxBone>();
        public readonly List<MdxNode> Helpers = new List<MdxNode>();
        public readonly List<MdxAttachment> Attachments = new List<MdxAttachment>();
        public readonly List<Vec3> Pivots = new List<Vec3>();
        public readonly List<MdxCamera> Cameras = new List<MdxCamera>();
        public readonly List<MdxEventObject> Events = new List<MdxEventObject>();
        public readonly List<MdxCollisionShape> CollisionShapes = new List<MdxCollisionShape>();
        public readonly List<MdxLight> Lights = new List<MdxLight>();
        public readonly List<MdxParticleEmitter1> ParticleEmitters1 = new List<MdxParticleEmitter1>();
        public readonly List<MdxParticleEmitter2> ParticleEmitters2 = new List<MdxParticleEmitter2>();
        public readonly List<MdxRibbonEmitter> RibbonEmitters = new List<MdxRibbonEmitter>();

        /// <summary>All nodes indexed by node id; absent ids are null.</summary>
        public MdxNode[] Nodes = Array.Empty<MdxNode>();
        public int NodeCount;
        /// <summary>Nodes in file order (parents before children) for hierarchy evaluation.</summary>
        public readonly List<MdxNode> NodeList = new List<MdxNode>();

        public bool HasParticles => ParticleEmitters2.Count > 0 || ParticleEmitters1.Count > 0;
        public bool HasRibbons => RibbonEmitters.Count > 0;

        public MdxSequence FindSequence(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (MdxSequence sequence in Sequences)
                if (string.Equals(sequence.Name, name, StringComparison.OrdinalIgnoreCase)) return sequence;
            return null;
        }

        public int FindSequenceIndex(string name)
        {
            for (int i = 0; i < Sequences.Count; i++)
                if (string.Equals(Sequences[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>
        /// Sequence whose interval contains the frame, else the first sequence.
        /// The interval is half-open ([Start, End)) like the engine's
        /// R_FindSequenceAtTime, so a frame exactly on End belongs to the next one.
        /// </summary>
        public MdxSequence FindSequenceAtTime(int time)
        {
            foreach (MdxSequence sequence in Sequences)
                if (time >= sequence.Start && time < sequence.End) return sequence;
            return Sequences.Count > 0 ? Sequences[0] : null;
        }

        public MdxNode GetNode(int nodeId)
            => nodeId >= 0 && nodeId < Nodes.Length ? Nodes[nodeId] : null;

        public MdxMaterial GetMaterial(int index)
            => index >= 0 && index < Materials.Count ? Materials[index] : null;

        public override string ToString()
            => $"{Info.Name} v{Version} seqs={Sequences.Count} geosets={Geosets.Count} bones={Bones.Count} " +
               $"mats={Materials.Count} texs={Textures.Count} pre2={ParticleEmitters2.Count} ribbons={RibbonEmitters.Count}";
    }
}
