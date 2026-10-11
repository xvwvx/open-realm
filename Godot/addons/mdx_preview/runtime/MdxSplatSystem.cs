using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Mdx;
using MdxPreview.Core.Sheet;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// Ground decals for the `SPL` / `FPT` / `UBR` MDX event families.
    ///
    /// PORT TASK (see docs/games/warcraft-3/mdx-event-objects.md and
    /// games/warcraft-3/renderer/r_game.c):
    ///  - `SPL` / `FPT` resolve `Splats\SplatData.slk`: `Dir`+`file` atlas texture,
    ///    `Rows`x`Columns` atlas, `Scale` half extent, `Lifespan` + `Decay`
    ///    milliseconds, the lifespan/decay UV frame ranges and the
    ///    Start/Middle/End RGBA phases (R_W3EmitSplatEvent / R_W3RenderEventSplat);
    ///  - `UBR` resolves `Splats\UberSplatData.slk`: `Scale`, `BirthTime`,
    ///    `PauseTime`, `Decay` and the same three colour phases, without an atlas
    ///    (R_W3EmitUberSplatEvent).
    ///
    /// Geometry: the engine draws a terrain-conforming rectangle from
    /// `origin - scale` to `origin + scale` in the MDX XY plane
    /// (R_RenderRectSplatUV / R_RenderSplat). The preview has no terrain mesh, so
    /// each decal is one horizontal quad in the event node's MDX frame. The event
    /// node lives under `MdxEventSystem` (the `Events` node), which the model node
    /// already rotated -90 degrees about X, so a quad in the local XY plane lands
    /// flat on Godot's XZ ground plane. It sits at local z = 0.5 to clear the
    /// preview's ground plane instead of z-fighting with it.
    ///
    /// Known differences from the C consumer (all data is still parsed):
    ///  - `LifespanRepeat` / `UVDecayRepeat` are not applied: the retail
    ///    repeat-count/wrap contract is unverified in the reference too;
    ///  - `BlendMode` is approximated by the preview's alpha-blended unshaded
    ///    material; non-default values are warned about once per row;
    ///  - `Water` placement is not implemented (no water surface in the preview);
    ///  - `Sound` is not played by the splat path (the C consumer retains it
    ///    without an audio contract);
    ///  - a row whose lifespan and decay are both zero draws for its event frame
    ///    and retires immediately, matching the reference approximation.
    /// </summary>
    public partial class MdxSplatSystem : Node3D
    {
        /// <summary>WC3_EVENT_SPLAT_MAX: bounded transient decal pool.</summary>
        public const int MaxSplats = 128;

        /// <summary>Local Z of the decal quad above the MDX ground plane (z = 0).</summary>
        public const float DecalHeight = 0.5f;

        protected readonly MdxAssetLibrary Assets;
        readonly List<Decal> _decals = new List<Decal>();
        readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int _serial;

        public MdxSplatSystem(MdxAssetLibrary assets)
        {
            Assets = assets;
            Name = "Splats";
            ProcessMode = ProcessModeEnum.Always;
        }

        public bool Enabled { get; set; } = true;

        /// <summary>Live decals, newest last (debug/UI).</summary>
        public int ActiveCount => _decals.Count;

        /// <summary>Decals retired since the last <see cref="Reset"/> (debug/UI).</summary>
        public int RetiredCount { get; private set; }

        /// <summary>Authored lifetime of the oldest live decal in ms, or -1 (debug/UI).</summary>
        public float DebugOldestLifetimeMs => _decals.Count > 0 ? _decals[0].TotalMs : -1f;

        /// <summary>Age of the oldest live decal in ms, or -1 (debug/UI).</summary>
        public float DebugOldestAgeMs => _decals.Count > 0 ? _decals[0].AgeMs : -1f;

        /// <summary>Quad vertices of the oldest live decal (debug/UI).</summary>
        public string DebugOldestVertices()
        {
            if (_decals.Count == 0 || _decals[0].Quad?.Mesh is not ArrayMesh mesh || mesh.GetSurfaceCount() == 0)
                return "(none)";
            var arrays = mesh.SurfaceGetArrays(0);
            if (arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array() is not { Length: > 0 } vertices) return "(none)";
            var text = new System.Text.StringBuilder();
            foreach (Vector3 vertex in vertices) text.Append($"({vertex.X:0.##}, {vertex.Y:0.##}, {vertex.Z:0.##}) ");
            return text.ToString().TrimEnd();
        }

        /// <summary>Local Z of the oldest live decal's quad (its height above the MDX ground plane).</summary>
        public float DebugOldestQuadHeight()
        {
            if (_decals.Count == 0 || _decals[0].Quad?.Mesh is not ArrayMesh mesh || mesh.GetSurfaceCount() == 0)
                return -1f;
            var arrays = mesh.SurfaceGetArrays(0);
            return arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array() is { Length: > 0 } vertices
                ? vertices[0].Z
                : -1f;
        }

        /// <summary>Raised once per distinct degraded case, so the UI can show it.</summary>
        public event Action<string> WarningLogged;

        // ---- lifetime --------------------------------------------------------------------

        /// <summary>Retires decals whose authored lifetime has elapsed.</summary>
        public void Tick(float deltaSeconds)
        {
            if (!Enabled || _decals.Count == 0) return;
            for (int i = _decals.Count - 1; i >= 0; i--)
            {
                Decal decal = _decals[i];
                decal.AgeMs += deltaSeconds * 1000f;
                // R_W3RenderEventSplat retires a transient once its authored lifetime
                // elapsed; a row whose lifespan and decay are both zero draws on its
                // event frame and retires on the next tick (TotalMs <= 0).
                if (decal.TotalMs > 0f && decal.AgeMs < decal.TotalMs)
                {
                    ApplyPhase(decal);
                    continue;
                }
                Retire(i);
            }
        }

        public void Reset()
        {
            foreach (Decal decal in _decals) decal.Node?.QueueFree();
            _decals.Clear();
            _warned.Clear();
            RetiredCount = 0;
            _serial = 0;
        }

        void Retire(int index)
        {
            Decal decal = _decals[index];
            _decals.RemoveAt(index);
            RetiredCount++;
            if (decal.Node != null && GodotObject.IsInstanceValid(decal.Node))
            {
                decal.Node.QueueFree();
                decal.Node = null;
            }
        }

        // ---- spawning --------------------------------------------------------------------

        /// <summary>
        /// Creates one `SPL`/`FPT` decal from a SplatData row.
        /// <paramref name="localTransform"/> is the decal's transform inside the
        /// MDX event frame (see <see cref="MdxEventSystem"/>).
        /// </summary>
        public bool SpawnSplat(SlkRow row, in Transform3D localTransform, string eventName)
        {
            if (!Enabled || row == null) return false;

            string name = row.Key;
            int rows = row.GetInt("Rows", 0);
            int columns = row.GetInt("Columns", 0);
            if (rows <= 0 || columns <= 0)
            {
                Warn("splat-atlas", $"{eventName}: SplatData '{name}' 的 Rows/Columns 无效 ({rows}x{columns})");
                return false;
            }
            float scale = row.GetFloat("Scale", 0f);
            if (!(scale > 0f))
            {
                Warn("splat-scale", $"{eventName}: SplatData '{name}' 的 Scale 无效 ({scale})");
                return false;
            }

            WarnUnsupportedSplatFields(row, name);
            Texture2D texture = ResolveTexture("SplatData", name, row.TryGetString("Dir"), row.TryGetString("file"));
            if (texture == null) return false;

            float lifespanMs = Math.Max(0f, row.GetFloat("Lifespan", 0f)) * 1000f;
            float decayMs = Math.Max(0f, row.GetFloat("Decay", 0f)) * 1000f;
            if (lifespanMs <= 0f && decayMs <= 0f)
                Warn("zero-splat-lifetime", $"{eventName}: SplatData '{name}' 的 Lifespan/Decay 均为 0, 仅显示一帧 (与参考实现一致的近似)");

            // R_W3RenderEventSplat samples the atlas on the lifespan phase first, so
            // the quad's first atlas cell is the lifespan range's start frame.
            var decal = new Decal
            {
                Row = row,
                Kind = DecalKind.Splat,
                Texture = texture,
                Scale = scale,
                Rows = rows,
                Columns = columns,
                LifeMs = lifespanMs,
                DecayMs = decayMs,
                StartColor = SplatColor(row, "Start"),
                MiddleColor = SplatColor(row, "Middle"),
                EndColor = SplatColor(row, "End"),
                Frame = AtlasFrame(row.GetInt("UVLifespanStart", 0), row.GetInt("UVLifespanEnd", 0), 0f),
            };
            return AddDecal(decal, localTransform, eventName, name);
        }

        /// <summary>Creates one `UBR` decal from an UberSplatData row.</summary>
        public bool SpawnUberSplat(SlkRow row, in Transform3D localTransform, string eventName)
        {
            if (!Enabled || row == null) return false;

            string name = row.Key;
            float scale = row.GetFloat("Scale", 0f);
            if (!(scale > 0f))
            {
                Warn("uber-splat-scale", $"{eventName}: UberSplatData '{name}' 的 Scale 无效 ({scale})");
                return false;
            }

            WarnUnsupportedUberSplatFields(row, name);
            Texture2D texture = ResolveTexture("UberSplatData", name, row.TryGetString("Dir"), row.TryGetString("file"));
            if (texture == null) return false;

            float birthMs = Math.Max(0f, row.GetFloat("BirthTime", 0f)) * 1000f;
            float pauseMs = Math.Max(0f, row.GetFloat("PauseTime", 0f)) * 1000f;
            float decayMs = Math.Max(0f, row.GetFloat("Decay", 0f)) * 1000f;
            if (birthMs <= 0f && pauseMs <= 0f && decayMs <= 0f)
                Warn("zero-uber-splat-lifetime", $"{eventName}: UberSplatData '{name}' 的 Birth/Pause/Decay 均为 0, 仅显示一帧 (与参考实现一致的近似)");

            var decal = new Decal
            {
                Row = row,
                Kind = DecalKind.UberSplat,
                Texture = texture,
                Scale = scale,
                Rows = 1,
                Columns = 1,
                BirthMs = birthMs,
                PauseMs = pauseMs,
                DecayMs = decayMs,
                StartColor = SplatColor(row, "Start"),
                MiddleColor = SplatColor(row, "Middle"),
                EndColor = SplatColor(row, "End"),
            };
            return AddDecal(decal, localTransform, eventName, name);
        }

        bool AddDecal(Decal decal, in Transform3D localTransform, string eventName, string name)
        {
            // WC3_EVENT_SPLAT_MAX: the reference recycles the oldest transient once
            // the bounded pool is full (R_W3AllocEventSplat).
            if (_decals.Count >= MaxSplats)
            {
                Warn("splat-pool", $"{eventName}: 贴花池已满 ({MaxSplats}), 回收最早的地面贴花");
                Retire(0);
            }

            var quad = new MeshInstance3D
            {
                Name = $"{decal.Kind}:{name}",
                Mesh = BuildQuadMesh(decal.Scale, decal.Rows, decal.Columns, decal.Frame),
                MaterialOverride = BuildMaterial(decal.Texture),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };

            var node = new Node3D { Name = $"decal_{++_serial}" };
            node.Transform = localTransform;
            node.AddChild(quad);
            AddChild(node);

            decal.Node = node;
            decal.Quad = quad;
            decal.AgeMs = 0f;
            _decals.Add(decal);

            ApplyPhase(decal);
            // A zero-lifetime row is drawn for the crossing frame and retired on the
            // next tick, like the reference's one-frame approximation.
            return true;
        }

        // ---- phase sampling --------------------------------------------------------------

        void ApplyPhase(Decal decal)
        {
            if (decal.Quad == null || !GodotObject.IsInstanceValid(decal.Quad)) return;

            Color color;
            if (decal.Kind == DecalKind.Splat)
            {
                if (decal.LifeMs > 0f && decal.AgeMs < decal.LifeMs)
                {
                    float progress = decal.AgeMs / decal.LifeMs;
                    color = decal.StartColor.Lerp(decal.MiddleColor, Mathf.Clamp(progress, 0f, 1f));
                    SetAtlasFrame(decal, AtlasFrame(decal.Row.GetInt("UVLifespanStart", 0),
                        decal.Row.GetInt("UVLifespanEnd", 0), progress));
                }
                else
                {
                    float progress = decal.DecayMs > 0f ? (decal.AgeMs - decal.LifeMs) / decal.DecayMs : 1f;
                    color = decal.MiddleColor.Lerp(decal.EndColor, Mathf.Clamp(progress, 0f, 1f));
                    SetAtlasFrame(decal, AtlasFrame(decal.Row.GetInt("UVDecayStart", 0),
                        decal.Row.GetInt("UVDecayEnd", 0), progress));
                }
            }
            else if (decal.BirthMs > 0f && decal.AgeMs < decal.BirthMs)
            {
                color = decal.StartColor.Lerp(decal.MiddleColor, decal.AgeMs / decal.BirthMs);
            }
            else if (decal.AgeMs < decal.BirthMs + decal.PauseMs || decal.DecayMs <= 0f)
            {
                color = decal.MiddleColor;
            }
            else
            {
                color = decal.MiddleColor.Lerp(decal.EndColor,
                    (decal.AgeMs - decal.BirthMs - decal.PauseMs) / decal.DecayMs);
            }

            decal.Color = color;
            if (decal.Quad.MaterialOverride is StandardMaterial3D material) material.AlbedoColor = color;
        }

        /// <summary>
        /// R_W3SplatAtlasFrame: normalized phase progress inside the authored
        /// inclusive frame range, honouring a reversed (start &gt; end) range.
        /// </summary>
        public static int AtlasFrame(int start, int end, float progress)
            => MdxEventData.AtlasFrame(start, end, progress);

        /// <summary>
        /// R_W3SplatAtlasUV: one clamped atlas frame -> its UV rectangle, cells
        /// numbered left-to-right, top-to-bottom from zero. V grows downward from
        /// the atlas top, which is the same convention the particle sprites use.
        /// </summary>
        public static void AtlasUv(int rows, int columns, int frame, out Vector2 min, out Vector2 max)
        {
            MdxEventData.AtlasUv(rows, columns, frame, out float minU, out float minV, out float maxU, out float maxV);
            min = new Vector2(minU, minV);
            max = new Vector2(maxU, maxV);
        }

        void SetAtlasFrame(Decal decal, int frame)
        {
            if (frame == decal.Frame) return;
            decal.Frame = frame;
            // The atlas cell only changes when the phase walk crosses a frame, so the
            // quad mesh is rebuilt only on those transitions.
            decal.Quad.Mesh = BuildQuadMesh(decal.Scale, decal.Rows, decal.Columns, frame);
        }

        // ---- geometry / material ---------------------------------------------------------

        /// <summary>
        /// Horizontal quad in the MDX XY plane. The half extent is the row's `Scale`
        /// (the reference spans origin +/- scale in both world axes) and the UVs are
        /// the authored atlas cell.
        /// </summary>
        Mesh BuildQuadMesh(float halfExtent, int rows, int columns, int frame)
        {
            AtlasUv(rows, columns, frame, out Vector2 uvMin, out Vector2 uvMax);
            float s = halfExtent;
            float z = DecalHeight;

            var vertices = new Vector3[]
            {
                new Vector3(-s, s, z),  // top-left of the cell
                new Vector3(s, s, z),   // top-right
                new Vector3(s, -s, z),  // bottom-right
                new Vector3(-s, -s, z), // bottom-left
            };
            var normals = new Vector3[] { Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Back };
            var uvs = new Vector2[]
            {
                new Vector2(uvMin.X, uvMin.Y),
                new Vector2(uvMax.X, uvMin.Y),
                new Vector2(uvMax.X, uvMax.Y),
                new Vector2(uvMin.X, uvMax.Y),
            };
            var indices = new int[] { 0, 1, 2, 0, 2, 3 };

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices;

            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }

        static StandardMaterial3D BuildMaterial(Texture2D texture)
        {
            return new StandardMaterial3D
            {
                AlbedoTexture = texture,
                AlbedoColor = Colors.White,
                // Decals are authored art: draw them unlit and flat on the ground.
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Mix,
                // A single-sided horizontal quad would disappear when the preview
                // camera drops below it, so keep the decal two-sided.
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
                // Ground decals must not write depth or they would clip the model's feet.
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                DisableReceiveShadows = true,
            };
        }

        // ---- row decoding ----------------------------------------------------------------

        static Color SplatColor(SlkRow row, string prefix)
        {
            // R_W3SplatColor: authored channels are 0..255, but a value at or below
            // 1.0 is treated as an already normalized fraction.
            return new Color(
                MdxEventData.SplatChannel(row.GetFloat(prefix + "R", 0f)),
                MdxEventData.SplatChannel(row.GetFloat(prefix + "G", 0f)),
                MdxEventData.SplatChannel(row.GetFloat(prefix + "B", 0f)),
                MdxEventData.SplatChannel(row.GetFloat(prefix + "A", 0f)));
        }

        Texture2D ResolveTexture(string family, string rowName, string dir, string file)
        {
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file))
            {
                Warn($"{family}-no-texture", $"{family} 行 '{rowName}' 缺少 Dir/file, 无法生成贴花");
                return null;
            }
            // R_W3LoadSplatTexture: "%s\\%s.blp".
            string path = $"{dir.TrimEnd('\\', '/')}/{file}.blp";
            Texture2D texture = Assets.LoadTexture(path);
            if (texture == null || texture == Assets.GetMissingTexture())
            {
                Warn($"{family}-texture-{path}", $"{family} 行 '{rowName}' 的贴图无法解析: {path}");
                return texture; // the library's placeholder keeps the decal visible
            }
            return texture;
        }

        /// <summary>
        /// R_W3WarnUnsupportedSplatFields: repeat counts, non-default blend modes,
        /// water placement and row sounds are parsed but not applied here.
        /// </summary>
        void WarnUnsupportedSplatFields(SlkRow row, string name)
        {
            if (!MdxEventData.HasUnsupportedSplatFields(row)) return;
            Warn($"splat-unsupported-{name}",
                $"SplatData '{name}' 使用了未实现的字段 (LifespanRepeat/UVDecayRepeat/BlendMode/Water/Sound), 已按默认混合/不重复处理");
        }

        void WarnUnsupportedUberSplatFields(SlkRow row, string name)
        {
            if (!MdxEventData.HasUnsupportedUberSplatFields(row)) return;
            Warn($"uber-splat-unsupported-{name}",
                $"UberSplatData '{name}' 使用了未实现的字段 (BlendMode/Sound), 已按默认 alpha 混合处理");
        }

        void Warn(string key, string message)
        {
            if (!_warned.Add(key)) return;
            GD.PushWarning($"[MDX Preview] {message}");
            WarningLogged?.Invoke(message);
        }

        // ---- state -----------------------------------------------------------------------

        enum DecalKind
        {
            Splat,
            UberSplat,
        }

        sealed class Decal
        {
            public SlkRow Row;
            public DecalKind Kind;
            public Texture2D Texture;
            public Node3D Node;
            public MeshInstance3D Quad;

            /// <summary>Half extent in MDX units (the row's `Scale`).</summary>
            public float Scale;

            /// <summary>Atlas rows (`SplatData` `Rows`; 1 for an UberSplat).</summary>
            public int Rows = 1;

            /// <summary>Atlas columns (`SplatData` `Columns`; 1 for an UberSplat).</summary>
            public int Columns = 1;

            /// <summary>SplatData `Lifespan` in milliseconds.</summary>
            public float LifeMs;

            /// <summary>SplatData `Decay`, or UberSplatData `Decay`, in milliseconds.</summary>
            public float DecayMs;

            /// <summary>UberSplatData `BirthTime` in milliseconds.</summary>
            public float BirthMs;

            /// <summary>UberSplatData `PauseTime` in milliseconds.</summary>
            public float PauseMs;

            public Color StartColor = Colors.White;
            public Color MiddleColor = Colors.White;
            public Color EndColor = Colors.White;
            public Color Color = Colors.White;
            public int Frame;

            public float AgeMs;

            public float TotalMs => Kind == DecalKind.Splat ? LifeMs + DecayMs : BirthMs + PauseMs + DecayMs;
        }
    }
}
