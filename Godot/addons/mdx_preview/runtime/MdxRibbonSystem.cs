using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Math3D;
using MdxPreview.Core.Mdx;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// RIBB ribbon emitter preview.
    ///
    /// Port of games/warcraft-3/renderer/mdx/r_mdx_ribbons.c on top of the engine
    /// trail store (renderer/r_trail.c + renderer/r_trail.h):
    ///  - per emitter edge history advanced once per frame: edges are emitted at
    ///    EmissionRate per second, expire after `lifespan` seconds, and gravity pulls
    ///    live edges along -Z
    ///  - edge endpoints come from the emitter node's matrix applied to
    ///    pivot +/- (0, heightAbove/heightBelow, 0) with the KRHA/KRHB tracks
    ///  - KRAL/KRCO tint each edge at emission time (so animated colours stay
    ///    historic instead of repainting the strip), KRTX selects the atlas column
    ///  - KRVS below epsilon stops emission but leaves live edges to expire
    ///  - the strip is drawn through the emitter's MDX material (materialId, first
    ///    layer: blend mode, two sided, texture) with U = age/lifespan inside the
    ///    cell so adding or expiring an edge never rescales the unwrap
    ///
    /// The strip mesh is rebuilt every frame (one quad per consecutive edge pair);
    /// MdxModelNode.SetSequence calls Reset() so a sequence switch starts clean.
    /// </summary>
    public partial class MdxRibbonSystem : Node3D
    {
        protected MdxAssetLibrary Assets;

        readonly List<RibbonRuntime> _emitters = new List<RibbonRuntime>();
        bool _enabled;
        double _clockMs;

        /// <summary>Parameterless constructor for Godot's assembly-reload rebuild.</summary>
        public MdxRibbonSystem()
        {
            Name = "Ribbons";
            ProcessMode = ProcessModeEnum.Always;
        }

        public MdxRibbonSystem(MdxAssetLibrary assets) : this() => Assets = assets;

        /// <summary>Supplies the asset library for instances Godot created itself.</summary>
        public void Initialize(MdxAssetLibrary assets) => Assets = assets;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                ApplyVisibility();
            }
        }

        public MdxModel Model { get; private set; }
        public MdxInstance Instance { get; private set; }

        /// <summary>Ribbon emitters currently simulated.</summary>
        public int EmitterCount => _emitters.Count;

        /// <summary>Live edges across every emitter (diagnostics/UI).</summary>
        public int LiveEdgeCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _emitters.Count; i++) count += _emitters[i].Trail.Count;
                return count;
            }
        }

        /// <summary>Simulation clock in milliseconds (diagnostics).</summary>
        public double ClockMs => _clockMs;

        public virtual void Build(MdxModel model, MdxInstance instance)
        {
            Model = model;
            Instance = instance;
            ClearEmitters();
            if (model == null) return;

            foreach (MdxRibbonEmitter ribbon in model.RibbonEmitters) AddEmitter(ribbon);
            ApplyVisibility();
        }

        public virtual void Tick(float delta)
        {
            if (Model == null || Instance == null) return;
            if (delta < 0f) delta = 0f;
            _clockMs += delta * 1000.0;

            for (int i = 0; i < _emitters.Count; i++) TickEmitter(_emitters[i], delta);
        }

        public virtual void Reset()
        {
            for (int i = 0; i < _emitters.Count; i++)
            {
                RibbonRuntime emitter = _emitters[i];
                emitter.Trail.Clear();
                if (emitter.ArrayMesh != null && emitter.ArrayMesh.GetSurfaceCount() > 0) emitter.ArrayMesh.ClearSurfaces();
                if (emitter.Mesh != null) emitter.Mesh.Visible = false;
            }
            _clockMs = 0.0;
        }

        // ---- construction ---------------------------------------------------------------

        void ClearEmitters()
        {
            for (int i = 0; i < _emitters.Count; i++)
            {
                MeshInstance3D mesh = _emitters[i].Mesh;
                if (mesh == null) continue;
                if (mesh.GetParent() == this) RemoveChild(mesh);
                mesh.QueueFree();
            }
            _emitters.Clear();
        }

        void AddEmitter(MdxRibbonEmitter ribbon)
        {
            var runtime = new RibbonRuntime
            {
                Label = ribbon.Node?.Name ?? "RIBB",
                Emitter = ribbon,
                Rows = Math.Max(1, ribbon.Rows),
                Columns = Math.Max(1, ribbon.Columns),
                Slot = ribbon.TextureSlot,
                Strip = new MdxRibbonVertex[MdxRibbonTrail.MaxEdges * 6],
            };
            AttachEmitter(runtime, BuildRibbonMaterial(ribbon));
        }

        void AttachEmitter(RibbonRuntime runtime, StandardMaterial3D material)
        {
            var mesh = new ArrayMesh();
            var node = new MeshInstance3D
            {
                Name = SanitizeName($"ribbon_{_emitters.Count}_{runtime.Label}"),
                Mesh = mesh,
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = 32f,
                Visible = false,
            };
            runtime.Mesh = node;
            runtime.ArrayMesh = mesh;
            runtime.Material = material;
            AddChild(node);
            _emitters.Add(runtime);
        }

        static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "ribbon";
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return builder.ToString();
        }

        /// <summary>
        /// The strip uses the emitter's own MDX material (materialId, first layer), so
        /// blend mode, two-sided flag and texture all come from the file �?the engine
        /// draws ribbons through MDLX_MaterialAt for exactly that reason. Vertex colours
        /// have to reach the albedo because KRAL/KRCO are stored per edge.
        /// </summary>
        StandardMaterial3D BuildRibbonMaterial(MdxRibbonEmitter ribbon)
        {
            MdxMaterial material = Model.GetMaterial(ribbon.MaterialId);
            MdxMaterialLayer layer = material != null && material.Layers.Count > 0 ? material.Layers[0] : null;

            StandardMaterial3D result = layer != null
                ? MdxMaterialBuilder.CreateLayerMaterial(Assets, Model, layer, out _)
                : new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    AlbedoColor = Colors.White,
                };

            result.VertexColorUseAsAlbedo = true;
            if (material != null) result.RenderPriority = Math.Clamp(material.Priority, -128, 127);
            return result;
        }

        void ApplyVisibility()
        {
            for (int i = 0; i < _emitters.Count; i++)
            {
                RibbonRuntime emitter = _emitters[i];
                if (emitter.Mesh == null) continue;
                emitter.Mesh.Visible = _enabled && emitter.Trail.Count >= 2;
            }
        }

        // ---- per frame ------------------------------------------------------------------

        void TickEmitter(RibbonRuntime runtime, float delta)
        {
            MdxRibbonEmitter ribbon = runtime.Emitter;

            float visibility = Instance.SampleFloatTrack(ribbon.VisibilityTrack, 1f);
            float heightAbove = Instance.SampleFloatTrack(ribbon.HeightAboveTrack, ribbon.HeightAbove);
            float heightBelow = Instance.SampleFloatTrack(ribbon.HeightBelowTrack, ribbon.HeightBelow);
            float alpha = MdxParticleMath.Clamp01(Instance.SampleFloatTrack(ribbon.AlphaTrack, ribbon.Alpha));
            // KRCO is consumed exactly like the reference (no BGR swizzle on this path).
            Vec3 color = Instance.SampleVec3Track(ribbon.ColorTrack, ribbon.Color);
            runtime.Slot = Instance.SampleIntTrack(ribbon.TextureSlotTrack, ribbon.TextureSlot);

            // KRVS below epsilon stops emission; edges already in flight live out their lifespan.
            float rate = visibility < MdxParticleMath.Epsilon ? 0f : ribbon.EmissionRate;

            int nodeId = ribbon.Node?.NodeId ?? -1;
            Mat4 world = Instance.GetNodeWorld(nodeId);
            Vec3 pivot = nodeId >= 0 && nodeId < Model.Pivots.Count ? Model.Pivots[nodeId] : Vec3.Zero;
            Vec3 above = world.TransformPoint(pivot + new Vec3(0f, heightAbove, 0f));
            Vec3 below = world.TransformPoint(pivot + new Vec3(0f, -heightBelow, 0f));

            runtime.Trail.Advance(above, below, color, alpha, ribbon.Lifespan, rate, ribbon.Gravity, _clockMs, delta);
            RebuildMesh(runtime);
        }

        void RebuildMesh(RibbonRuntime runtime)
        {
            int vertexCount = MdxRibbonMath.BuildStrip(
                runtime.Trail, runtime.Emitter.Lifespan, runtime.Columns, runtime.Rows, runtime.Slot, runtime.Strip);

            if (vertexCount == 0)
            {
                if (runtime.ArrayMesh.GetSurfaceCount() > 0) runtime.ArrayMesh.ClearSurfaces();
                runtime.Mesh.Visible = false;
                return;
            }

            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var colors = new Color[vertexCount];

            for (int i = 0; i < vertexCount; i++)
            {
                MdxRibbonVertex vertex = runtime.Strip[i];
                positions[i] = new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                // MDLX_RibbonConvertStrip binds a face normal of (0,0,1).
                normals[i] = new Vector3(0f, 0f, 1f);
                uvs[i] = new Vector2(vertex.U, vertex.V);
                colors[i] = new Color(vertex.Color.X, vertex.Color.Y, vertex.Color.Z, vertex.Alpha);
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Color] = colors;

            if (runtime.ArrayMesh.GetSurfaceCount() > 0) runtime.ArrayMesh.ClearSurfaces();
            runtime.ArrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            runtime.Mesh.Visible = _enabled;
        }

        // ---- emitter state --------------------------------------------------------------

        sealed class RibbonRuntime
        {
            public string Label = string.Empty;
            public MdxRibbonEmitter Emitter;
            public MeshInstance3D Mesh;
            public ArrayMesh ArrayMesh;
            public StandardMaterial3D Material;
            public readonly MdxRibbonTrail Trail = new MdxRibbonTrail();
            public MdxRibbonVertex[] Strip;
            public int Rows = 1;
            public int Columns = 1;
            public int Slot;
        }
    }
}
