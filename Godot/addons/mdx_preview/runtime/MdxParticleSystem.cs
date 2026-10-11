using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Math3D;
using MdxPreview.Core.Mdx;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// PRE2 (and, with reduced fidelity, PREM) particle emitter preview.
    ///
    /// Port of games/warcraft-3/renderer/mdx/r_mdx_particles.c on top of the shared
    /// particle rules in renderer/r_emit.h + renderer/r_particles.c:
    ///  - frame-relative emission accumulator (rate * delta) with fractional carry
    ///    over, a 100 ms catch-up clamp and a 256 particle per call budget
    ///  - spawn origin ((rand-0.5)*Length, (rand-0.5)*Width, 0) + node pivot,
    ///    transformed by the emitter node's model-space matrix
    ///  - Latitude cone (FX_GenerateRandomDirection) rotated by the node's rotation
    ///    only, so a rotating emitter swings its velocity
    ///  - speed + (rand-0.5)*Variation, accel (0,0,-Gravity), LifeSpan lifetime
    ///  - three-segment SegmentColor/Alpha plus ParticleScaling split at Time
    ///  - Rows x Columns atlas with the LifeSpan/Decay/Tail/TailDecay UV animation
    ///    triples (see MdxParticleMath.ResolveAtlasCell)
    ///  - FrameFlags: 0 head only, 1 tail only, 2 head + tail (tail = velocity *
    ///    TailLength), plus the XYQuad node flag
    ///  - Squirt bursts, PriorityPlane render ordering, ReplaceableId textures and
    ///    the PRE2 filter-mode blend modes (MdxMaterialBuilder.ApplyParticleFilter)
    ///
    /// Rendering uses one MeshInstance3D per emitter with a per-frame rebuilt
    /// ArrayMesh (the ImmediateMesh scheme: six explicit vertices per quad carrying
    /// the per-particle colour and atlas UV). A MultiMesh cannot express this pass:
    /// StandardMaterial3D has no per-instance UV rect, which the Rows x Columns atlas
    /// needs, and its billboard mode drops the per-instance scale.
    ///
    /// Everything is computed in the node's own space, which is the MDX model space
    /// because MdxModelNode applies the Z-up -> Y-up rotation itself.
    /// </summary>
    public partial class MdxParticleSystem : Node3D
    {
        /// <summary>Per-emitter safety cap; excess spawns are dropped with one warning.</summary>
        public const int MaxParticlesPerEmitter = 2000;

        /// <summary>
        /// 粒子亮度缩放（1 = 按作者数值）。加性粒子在预览里叠加后容易过亮，而游戏里的峰值
        /// 还受贴图与引擎混合细节影响；做成可调参数便于与游戏/Retera 现场对齐。
        /// </summary>
        public float Brightness = 1f;

        /// <summary>发射密度缩放（1 = 按作者 emissionRate）。</summary>
        public float Density = 1f;

        /// <summary>
        /// true（默认）= 使用**作者原始** ParticleScaling，与 Retera Model Studio 一致
        /// （RenderParticle2 直接 lerp 作者的 scaling）。
        /// false = 使用引擎 loader 烘入的 ×2（r_mdx_load.c:412），粒子会大一倍。
        /// </summary>
        public bool ReteraScaling = true;

        /// <summary>
        /// Extra multiplier applied on top of
        /// <see cref="MdxParticleEmitter2.ParticleScalingEngine"/>. The core loader already
        /// bakes the legacy 2x the engine's shared particle pipeline expects
        /// (r_mdx_load.c:412, "MDX particle sizes are authored as small ParticleScaling
        /// floats"), so this stays at 1 and only exists as a preview-side size tweak.
        /// </summary>
        public float ParticleSizeScale = 1f;

        protected MdxAssetLibrary Assets;

        readonly List<EmitterRuntime> _emitters = new List<EmitterRuntime>();
        MdxParticleRng _rng = new MdxParticleRng(0x51ED2701u);
        bool _enabled;
        double _clockMs;
        int _frameIndex;
        bool _warnedPremModel;

        // Camera basis in the node's local space, refreshed once per frame.
        Vec3 _viewRight = Vec3.UnitX;
        Vec3 _viewUp = Vec3.UnitY;
        Vec3 _eyeLocal = new Vec3(0f, 0f, 1000f);

        /// <summary>Parameterless constructor for Godot's assembly-reload rebuild.</summary>
        public MdxParticleSystem()
        {
            Name = "Particles";
            ProcessMode = ProcessModeEnum.Always;
        }

        public MdxParticleSystem(MdxAssetLibrary assets) : this() => Assets = assets;

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

        /// <summary>Emitters currently simulated (PRE2 first, then PREM).</summary>
        public int EmitterCount => _emitters.Count;

        /// <summary>Live particles across every emitter (diagnostics/UI).</summary>
        public int LiveParticleCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _emitters.Count; i++) count += _emitters[i].Particles.Count;
                return count;
            }
        }

        /// <summary>Spawns refused by <see cref="MaxParticlesPerEmitter"/>.</summary>
        public int DroppedParticleCount { get; private set; }

        /// <summary>Simulation clock in milliseconds (diagnostics).</summary>
        public double ClockMs => _clockMs;

        public virtual void Build(MdxModel model, MdxInstance instance)
        {
            Model = model;
            Instance = instance;
            ClearEmitters();
            if (model == null) return;

            foreach (MdxParticleEmitter2 emitter in model.ParticleEmitters2) AddEmitter2(emitter);
            foreach (MdxParticleEmitter1 emitter in model.ParticleEmitters1) AddEmitter1(emitter);
            ApplyVisibility();
        }

        public virtual void Tick(float delta)
        {
            if (Model == null || Instance == null) return;
            if (delta < 0f) delta = 0f;
            _clockMs += delta * 1000.0;
            _frameIndex++;

            UpdateViewBasis();
            for (int i = 0; i < _emitters.Count; i++) TickEmitter(_emitters[i], delta);
        }

        public virtual void Reset()
        {
            for (int i = 0; i < _emitters.Count; i++)
            {
                EmitterRuntime emitter = _emitters[i];
                emitter.Particles.Clear();
                emitter.Accumulator = 0f;
                emitter.Appearance = null;
                emitter.AppearanceFrame = -1;
                if (emitter.ArrayMesh != null && emitter.ArrayMesh.GetSurfaceCount() > 0) emitter.ArrayMesh.ClearSurfaces();
                if (emitter.Mesh != null) emitter.Mesh.Visible = false;
            }
            _rng = new MdxParticleRng(0x51ED2701u);
            _clockMs = 0.0;
            _frameIndex = 0;
            DroppedParticleCount = 0;
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
            DroppedParticleCount = 0;
        }

        void AddEmitter2(MdxParticleEmitter2 emitter)
        {
            var runtime = new EmitterRuntime
            {
                Label = emitter.Node?.Name ?? "PRE2",
                NodeId = emitter.Node?.NodeId ?? -1,
                Pre2 = emitter,
                Rows = Math.Max(1, emitter.Rows),
                Columns = Math.Max(1, emitter.Columns),
                FrameFlags = emitter.FrameFlags,
                Lifespan = emitter.LifeSpan,
                TailLength = emitter.TailLength,
                Squirt = emitter.Squirt != 0,
                XyQuad = emitter.Node != null && (emitter.Node.Flags & MdxNodeFlags.XYQuad) != 0,
            };
            AttachEmitter(runtime, BuildParticleMaterial(emitter));
        }

        /// <summary>True when a PREM path points at a model (sub-model particles).</summary>
        static bool IsModelParticlePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return extension == ".mdx" || extension == ".mdl";
        }

        void AddEmitter1(MdxParticleEmitter1 emitter)
        {
            // PREM emitters that spawn a *model* instead of a sprite (EmitterUsesMDL /
            // a .mdx path) are NOT previewed: the engine has no runtime model emission
            // either, and substituting a textured quad drew large mis-mapped quads over
            // the model (it looked like a scrambled texture). Log once and skip.
            if (IsModelParticlePath(emitter.Path))
            {
                if (!_warnedPremModel)
                {
                    _warnedPremModel = true;
                    GD.Print($"[MDX Preview] PREM 子模型粒子不预览（与引擎一致）: {emitter.Path}");
                }
                return;
            }

            var runtime = new EmitterRuntime
            {
                Label = emitter.Node?.Name ?? "PREM",
                NodeId = emitter.Node?.NodeId ?? -1,
                Pre1 = emitter,
                Rows = 1,
                Columns = 1,
                FrameFlags = 0, // PREM has no tail: head quads only
                Lifespan = emitter.LifeSpan,
                XyQuad = false,
            };
            AttachEmitter(runtime, BuildPremMaterial(emitter));
        }

        void AttachEmitter(EmitterRuntime runtime, StandardMaterial3D material)
        {
            var mesh = new ArrayMesh();
            var node = new MeshInstance3D
            {
                Name = SanitizeName($"particles_{_emitters.Count}_{runtime.Label}"),
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
            if (string.IsNullOrEmpty(name)) return "particles";
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return builder.ToString();
        }

        /// <summary>
        /// One StandardMaterial3D per emitter: unshaded, two sided, repeat sampled and
        /// tinted by the per-particle vertex colour, with the authored PRE2 filter mode
        /// mapped by <see cref="MdxMaterialBuilder.ApplyParticleFilter"/>.
        /// </summary>
        StandardMaterial3D BuildParticleMaterial(MdxParticleEmitter2 emitter)
        {
            StandardMaterial3D material = CreateParticleMaterial();
            MdxMaterialBuilder.ApplyParticleFilter(material, emitter.FilterMode);
            // PriorityPlane orders emitters against each other (and the geosets).
            material.RenderPriority = Math.Clamp(emitter.PriorityPlane, -128, 127);

            Texture2D texture = ResolveTexture(emitter.TextureId, emitter.ReplaceableId);
            if (texture != null) material.AlbedoTexture = texture;
            return material;
        }

        /// <summary>
        /// PREM has no texture field: the authored Path is either a TGA/BLP sprite or a
        /// child model. Model particles are not previewed (see the class remarks), so a
        /// non-image path falls back to the white texture and warns once.
        /// </summary>
        StandardMaterial3D BuildPremMaterial(MdxParticleEmitter1 emitter)
        {
            StandardMaterial3D material = CreateParticleMaterial();
            MdxMaterialBuilder.ApplyParticleFilter(material, 0);

            Texture2D texture = ResolvePremTexture(emitter.Path);
            if (texture != null) material.AlbedoTexture = texture;
            return material;
        }

        static StandardMaterial3D CreateParticleMaterial() => new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureRepeat = true,
            AlbedoColor = Colors.White,
            Roughness = 1f,
            Metallic = 0f,
            MetallicSpecular = 0f,
        };

        Texture2D ResolveTexture(int textureId, uint replaceableId)
        {
            MdxTexture texture = Model != null && textureId >= 0 && textureId < Model.Textures.Count
                ? Model.Textures[textureId]
                : null;
            uint id = texture != null ? texture.ReplaceableId : replaceableId;

            // Replaceable slots are generated textures, never file lookups.
            if (id != 0) return Assets.LoadTexture(null, id);
            if (texture != null && !string.IsNullOrEmpty(texture.Path)) return Assets.LoadTexture(texture.Path, 0);
            return Assets.GetWhiteTexture();
        }

        Texture2D ResolvePremTexture(string path)
        {
            if (string.IsNullOrEmpty(path)) return Assets.GetWhiteTexture();
            switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
            {
                case ".blp":
                case ".tga":
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".dds":
                case ".bmp":
                    return Assets.LoadTexture(path, 0);
                default:
                    if (!_warnedPremModel)
                    {
                        _warnedPremModel = true;
                        GD.PushWarning($"[MDX Preview] PREM 发射器使用子模型粒子 ({path})，预览以内建四边形代替");
                    }
                    return Assets.GetWhiteTexture();
            }
        }

        void ApplyVisibility()
        {
            for (int i = 0; i < _emitters.Count; i++)
            {
                EmitterRuntime emitter = _emitters[i];
                if (emitter.Mesh == null) continue;
                emitter.Mesh.Visible = _enabled && emitter.Particles.Count > 0;
            }
        }

        // ---- per frame ------------------------------------------------------------------

        void TickEmitter(EmitterRuntime emitter, float delta)
        {
            MdxParticleParams parameters = SampleParameters(emitter);
            float lifespan = ResolveLifespan(emitter);

            if (emitter.XyQuad)
            {
                Mat4 world = Instance.GetNodeWorld(emitter.NodeId);
                emitter.QuadRight = NormalizeOr(world.TransformDirection(Vec3.UnitX), Vec3.UnitX);
                emitter.QuadUp = NormalizeOr(world.TransformDirection(Vec3.UnitY), Vec3.UnitY);
                emitter.QuadNormal = NormalizeOr(world.TransformDirection(Vec3.UnitZ), Vec3.UnitZ);
            }

            float emissionRate = parameters.EmissionRate * MathF.Max(0f, Density);
            if (parameters.Visible && emissionRate > 0f)
            {
                int spawn;
                if (emitter.Squirt)
                {
                    // Retera Model Studio（RenderParticleEmitter2View.fill）：
                    // Squirt 发射器在"采样到的 emissionRate 发生变化"时一次性加上整份 rate
                    // （对应阶梯式 EmissionRate 轨道），而不是每个 lifespan 爆发一批。
                    if (!Mathf.IsEqualApprox(emissionRate, emitter.LastSquirtRate))
                        emitter.Accumulator += emissionRate;
                    emitter.LastSquirtRate = emissionRate;

                    spawn = (int)MathF.Floor(emitter.Accumulator);
                    if (spawn > 0) emitter.Accumulator -= spawn;
                }
                else
                {
                    spawn = MdxParticleMath.AdvanceAccumulator(ref emitter.Accumulator, emissionRate, delta);
                }

                for (int i = 0; i < spawn; i++) SpawnParticle(emitter, in parameters, lifespan);
            }

            List<ParticleState> particles = emitter.Particles;
            for (int i = particles.Count - 1; i >= 0; i--)
            {
                ParticleState particle = particles[i];
                particle.Time += delta;
                if (particle.Time > particle.Lifespan)
                {
                    particles.RemoveAt(i);
                    continue;
                }
                particles[i] = particle;
            }

            RebuildMesh(emitter);
        }

        static Vec3 NormalizeOr(Vec3 value, Vec3 fallback)
        {
            float lengthSquared = value.LengthSquared;
            return lengthSquared > 1e-12f ? value / MathF.Sqrt(lengthSquared) : fallback;
        }

        MdxParticleParams SampleParameters(EmitterRuntime emitter)
        {
            var parameters = new MdxParticleParams();
            if (emitter.Pre2 != null)
            {
                MdxParticleEmitter2 pre2 = emitter.Pre2;
                parameters.Visible = SampleVisibility(pre2.VisibilityTrack);
                parameters.EmissionRate = Instance.SampleFloatTrack(pre2.EmissionRateTrack, pre2.EmissionRate);
                parameters.Speed = Instance.SampleFloatTrack(pre2.SpeedTrack, pre2.Speed);
                parameters.Variation = Instance.SampleFloatTrack(pre2.VariationTrack, pre2.Variation);
                parameters.Latitude = Instance.SampleFloatTrack(pre2.LatitudeTrack, pre2.Latitude);
                parameters.Gravity = Instance.SampleFloatTrack(pre2.GravityTrack, pre2.Gravity);
                parameters.Length = Instance.SampleFloatTrack(pre2.LengthTrack, pre2.Length);
                parameters.Width = Instance.SampleFloatTrack(pre2.WidthTrack, pre2.Width);
                parameters.Longitude = 0f;
            }
            else if (emitter.Pre1 != null)
            {
                MdxParticleEmitter1 pre1 = emitter.Pre1;
                parameters.Visible = SampleVisibility(pre1.VisibilityTrack);
                parameters.EmissionRate = Instance.SampleFloatTrack(pre1.EmissionRateTrack, pre1.EmissionRate);
                parameters.Speed = Instance.SampleFloatTrack(pre1.SpeedTrack, pre1.Speed);
                parameters.Latitude = Instance.SampleFloatTrack(pre1.LatitudeTrack, pre1.Latitude);
                parameters.Gravity = Instance.SampleFloatTrack(pre1.GravityTrack, pre1.Gravity);
                parameters.Longitude = Instance.SampleFloatTrack(pre1.LongitudeTrack, pre1.Longitude);
                parameters.Variation = 0f;
                parameters.Length = 0f;
                parameters.Width = 0f;
            }
            return parameters;
        }

        /// <summary>
        /// KP2V/KPEV semantics: an absent track means visible, and any non-zero value
        /// counts as visible (the engine only tests visibility &lt; EPSILON).
        /// </summary>
        bool SampleVisibility(MdxKeyTrack track)
            => Instance.SampleFloatTrack(track, 1f) >= MdxParticleMath.Epsilon;

        float ResolveLifespan(EmitterRuntime emitter)
            => emitter.Pre1 != null
                ? Instance.SampleFloatTrack(emitter.Pre1.LifeSpanTrack, emitter.Pre1.LifeSpan)
                : emitter.Lifespan;

        // ---- spawn ----------------------------------------------------------------------

        /// <summary>
        /// Spawns one particle. Spawn position, velocity, gravity and lifespan mirror
        /// Retera Model Studio's <c>RenderParticle2.reset()</c> (lifespan is the emitter's
        /// own LifeSpan, with no per-particle jitter).
        /// </summary>
        void SpawnParticle(EmitterRuntime emitter, in MdxParticleParams parameters, float lifespan)
        {
            List<ParticleState> particles = emitter.Particles;
            if (particles.Count >= MaxParticlesPerEmitter)
            {
                DroppedParticleCount++;
                if (!emitter.WarnedCapacity)
                {
                    emitter.WarnedCapacity = true;
                    GD.PushWarning($"[MDX Preview] 粒子发射�?{emitter.Label} 超过 {MaxParticlesPerEmitter} 个粒子，多余粒子被丢弃");
                }
                return;
            }

            if (emitter.Appearance == null || emitter.AppearanceFrame != _frameIndex)
            {
                emitter.Appearance = CreateAppearance(emitter);
                emitter.AppearanceFrame = _frameIndex;
            }

            Mat4 nodeWorld = Instance.GetNodeWorld(emitter.NodeId);
            Vec3 pivot = emitter.NodeId >= 0 && emitter.NodeId < Model.Pivots.Count
                ? Model.Pivots[emitter.NodeId]
                : Vec3.Zero;

            // mdx_spawn_particle shares one draw between the X offset and the speed.
            float r = MdxParticleMath.SpawnRandom(ref _rng);
            Vec3 offset = MdxParticleMath.SpawnOffset(r, ref _rng, parameters.Length, parameters.Width);
            Vec3 direction = parameters.Longitude > 0f
                ? MdxParticleMath.RandomConeDirection(ref _rng, parameters.Latitude * MdxParticleMath.DegToRad,
                    parameters.Longitude * MdxParticleMath.DegToRad)
                : MdxParticleMath.RandomConeDirection(ref _rng, parameters.Latitude * MdxParticleMath.DegToRad);

            float speed = MdxParticleMath.SpawnSpeed(r, parameters.Speed, parameters.Variation);
            // The orientation matrix has its translation stripped, so the velocity and
            // the XYQuad axes follow the node's rotation but not its position.
            Vec3 velocity = nodeWorld.TransformDirection(direction) * speed;


            particles.Add(new ParticleState
            {
                Origin = nodeWorld.TransformPoint(pivot + offset),
                Velocity = velocity,
                Accel = new Vec3(0f, 0f, -parameters.Gravity),
                Tail = emitter.FrameFlags != 0 ? velocity * emitter.TailLength : Vec3.Zero,
                Time = 0f,
                Lifespan = lifespan,
                Appearance = emitter.Appearance,
            });
        }

        MdxParticleAppearance CreateAppearance(EmitterRuntime emitter)
        {
            var appearance = new MdxParticleAppearance
            {
                Rows = emitter.Rows,
                Columns = emitter.Columns,
            };

            if (emitter.Pre2 != null)
            {
                MdxParticleEmitter2 pre2 = emitter.Pre2;
                appearance.MidTime = MdxParticleMath.Clamp01(pre2.Time);
                for (int segment = 0; segment < 3; segment++)
                {
                    appearance.SetColors(segment, new Vec3(
                        MdxParticleMath.Clamp01(pre2.SegmentColor[segment * 3 + 0]),
                        MdxParticleMath.Clamp01(pre2.SegmentColor[segment * 3 + 1]),
                        MdxParticleMath.Clamp01(pre2.SegmentColor[segment * 3 + 2])));
                    byte alpha = pre2.Alpha != null && segment < pre2.Alpha.Length ? pre2.Alpha[segment] : (byte)255;
                    // ParticleScalingEngine is the authored triple with the loader's
                    // legacy 2x factor already applied (r_mdx_load.c:412).
                    // Retera 用作者原始值；引擎 loader 会把 ParticleScaling 乘 2（见 ReteraScaling）。
            float[] scaling = ReteraScaling
                ? (pre2.ParticleScaling ?? pre2.ParticleScalingEngine)
                : (pre2.ParticleScalingEngine ?? pre2.ParticleScaling);
                    float scale = scaling != null && segment < scaling.Length ? scaling[segment] : 1f;
                    appearance.SetAlphas(segment, alpha / 255f);
                    appearance.SetSizes(segment, MathF.Max(0f, scale * ParticleSizeScale));
                }
                appearance.LifeSpanUv = ToRange(pre2.LifeSpanUvAnim);
                appearance.DecayUv = ToRange(pre2.DecayUvAnim);
                appearance.TailUv = ToRange(pre2.TailUvAnim);
                appearance.TailDecayUv = ToRange(pre2.TailDecayUvAnim);
            }
            return appearance;
        }

        static MdxParticleUvRange ToRange(MdxParticleUvAnim anim)
            => new MdxParticleUvRange(anim.Start, anim.End, anim.Repeat);

        // ---- rendering ------------------------------------------------------------------

        void RebuildMesh(EmitterRuntime emitter)
        {
            List<ParticleState> particles = emitter.Particles;
            int quads = 0;
            for (int i = 0; i < particles.Count; i++)
            {
                ParticleState particle = particles[i];
                if ((particle.Appearance ?? emitter.Appearance) == null) continue;
                if (emitter.FrameFlags != 1) quads++;
                if (emitter.FrameFlags != 0 && particle.Tail.LengthSquared > 1e-8f) quads++;
            }

            if (quads == 0)
            {
                if (emitter.ArrayMesh.GetSurfaceCount() > 0) emitter.ArrayMesh.ClearSurfaces();
                emitter.Mesh.Visible = false;
                return;
            }

            int vertexCount = quads * 6;
            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var colors = new Color[vertexCount];
            int write = 0;

            for (int i = 0; i < particles.Count; i++)
            {
                ParticleState particle = particles[i];
                MdxParticleAppearance appearance = particle.Appearance ?? emitter.Appearance;
                if (appearance == null) continue;

                // A staggered burst gives particles a negative age until their birth moment:
                // clamp it so they wait at the emitter instead of extrapolating backwards.
                float age = MathF.Max(0f, particle.Time);
                float k = MdxParticleMath.NormalizedLife(age, particle.Lifespan);
                Vec3 position = MdxParticleMath.PositionAt(particle.Origin, particle.Velocity, particle.Accel, age);
                Vec3 color = MdxParticleMath.BlendTriple(appearance.Color0, appearance.Color1, appearance.Color2, k, appearance.MidTime);
                float alpha = MdxParticleMath.Clamp01(
                    MdxParticleMath.BlendTriple(appearance.Alpha0, appearance.Alpha1, appearance.Alpha2, k, appearance.MidTime));
                float size = MathF.Max(0f,
                    MdxParticleMath.BlendTriple(appearance.Size0, appearance.Size1, appearance.Size2, k, appearance.MidTime));
                // 亮度缩放：加性粒子叠加后容易过亮（峰值取决于贴图与混合细节），
                // 这里统一乘到顶点色上，便于在显示页把峰值调到与游戏一致。
                float brightness = MathF.Max(0f, Brightness);
                var vertexColor = new Color(color.X * brightness, color.Y * brightness,
                                            color.Z * brightness, MathF.Min(1f, alpha * brightness));

                if (emitter.FrameFlags != 1)
                {
                    Vec3 right = emitter.XyQuad ? emitter.QuadRight : _viewRight;
                    Vec3 up = emitter.XyQuad ? emitter.QuadUp : _viewUp;
                    Vec3 normal = emitter.XyQuad ? emitter.QuadNormal : Vec3.Cross(_viewRight, _viewUp);
                    MdxAtlasCell cell = MdxParticleMath.ResolveAtlasCell(
                        appearance.Rows, appearance.Columns, k, appearance.MidTime,
                        appearance.LifeSpanUv, appearance.DecayUv);
                    AddHeadQuad(position, right, up, size, normal, cell, vertexColor,
                        positions, normals, uvs, colors, ref write);
                }

                if (emitter.FrameFlags != 0 && particle.Tail.LengthSquared > 1e-8f)
                {
                    MdxAtlasCell cell = MdxParticleMath.ResolveAtlasCell(
                        appearance.Rows, appearance.Columns, k, appearance.MidTime,
                        appearance.TailUv, appearance.TailDecayUv);
                    AddTailQuad(position, particle.Tail, size, cell, vertexColor,
                        positions, normals, uvs, colors, ref write);
                }
            }

            if (write < vertexCount)
            {
                Array.Resize(ref positions, write);
                Array.Resize(ref normals, write);
                Array.Resize(ref uvs, write);
                Array.Resize(ref colors, write);
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Color] = colors;

            if (emitter.ArrayMesh.GetSurfaceCount() > 0) emitter.ArrayMesh.ClearSurfaces();
            emitter.ArrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            emitter.Mesh.Visible = _enabled;
        }

        /// <summary>
        /// Camera-facing (or XYQuad axis aligned) sprite quad. Corner order, winding
        /// and UVs follow R_AddParticle's billboard table: the lower vertices take the
        /// cell's Bottom V, the upper vertices its Top V.
        /// </summary>
        static void AddHeadQuad(
            Vec3 position,
            Vec3 right,
            Vec3 up,
            float size,
            Vec3 normal,
            MdxAtlasCell cell,
            Color color,
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uvs,
            Color[] colors,
            ref int write)
        {
            float half = size * 0.5f;
            Vec3 halfRight = right * half;
            Vec3 halfUp = up * half;
            var godotNormal = new Vector3(normal.X, normal.Y, normal.Z);

            WriteVertex(positions, normals, uvs, colors, ref write,
                position - halfRight - halfUp, new Vector2(cell.Left, cell.Bottom), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write,
                position + halfRight - halfUp, new Vector2(cell.Right, cell.Bottom), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write,
                position + halfRight + halfUp, new Vector2(cell.Right, cell.Top), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write,
                position + halfRight + halfUp, new Vector2(cell.Right, cell.Top), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write,
                position - halfRight + halfUp, new Vector2(cell.Left, cell.Top), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write,
                position - halfRight - halfUp, new Vector2(cell.Left, cell.Bottom), color, godotNormal);
        }

        /// <summary>
        /// Tail stretched quad: point = position - tail*(1 - axis.y), side =
        /// cross(normalize(tail), eye - point) evaluated at both ends, which is exactly
        /// the ribbon branch of the shared particle vertex shader (r_particles.c).
        /// </summary>
        void AddTailQuad(
            Vec3 position,
            Vec3 tail,
            float size,
            MdxAtlasCell cell,
            Color color,
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uvs,
            Color[] colors,
            ref int write)
        {
            Vec3 start = position - tail;
            Vec3 tailDirection = NormalizeOr(tail, Vec3.UnitZ);
            Vec3 sideStart = SideVector(tailDirection, start);
            Vec3 sideHead = SideVector(tailDirection, position);
            float half = size * 0.5f;
            Vec3 normal = NormalizeOr(Vec3.Cross(sideHead, tailDirection), Vec3.UnitZ);
            var godotNormal = new Vector3(normal.X, normal.Y, normal.Z);

            Vec3 v0 = start - sideStart * half;
            Vec3 v1 = start + sideStart * half;
            Vec3 v2 = position + sideHead * half;
            Vec3 v3 = position - sideHead * half;

            WriteVertex(positions, normals, uvs, colors, ref write, v0, new Vector2(cell.Left, cell.Top), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write, v1, new Vector2(cell.Right, cell.Top), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write, v2, new Vector2(cell.Right, cell.Bottom), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write, v2, new Vector2(cell.Right, cell.Bottom), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write, v3, new Vector2(cell.Left, cell.Bottom), color, godotNormal);
            WriteVertex(positions, normals, uvs, colors, ref write, v0, new Vector2(cell.Left, cell.Top), color, godotNormal);
        }

        static void WriteVertex(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uvs,
            Color[] colors,
            ref int write,
            Vec3 vertex,
            Vector2 uv,
            Color color,
            Vector3 normal)
        {
            if (write >= positions.Length) return;
            positions[write] = new Vector3(vertex.X, vertex.Y, vertex.Z);
            normals[write] = normal;
            uvs[write] = uv;
            colors[write] = color;
            write++;
        }

        Vec3 SideVector(Vec3 tailDirection, Vec3 point)
        {
            Vec3 side = Vec3.Cross(tailDirection, _eyeLocal - point);
            float length = side.Length;
            return length > 1e-4f ? side / length : _viewRight;
        }

        // ---- camera ---------------------------------------------------------------------

        /// <summary>
        /// Camera right/up and eye expressed in this node's local (MDX) space. The
        /// engine takes the camera axes from the view matrix columns, which is the same
        /// basis; without a camera the quad falls back to the node's own XY plane.
        /// </summary>
        void UpdateViewBasis()
        {
            Camera3D camera = GetViewport()?.GetCamera3D();
            if (camera == null && GetParent() is MdxModelNode model && model.ReferenceCamera != null)
                camera = model.ReferenceCamera;

            if (camera == null || !camera.IsInsideTree())
            {
                _viewRight = Vec3.UnitX;
                _viewUp = Vec3.UnitY;
                _eyeLocal = new Vec3(0f, 0f, 1000f);
                return;
            }

            Basis local = GlobalTransform.Basis.Inverse() * camera.GlobalTransform.Basis;
            _viewRight = NormalizeOr(new Vec3(local.X.X, local.X.Y, local.X.Z), Vec3.UnitX);
            _viewUp = NormalizeOr(new Vec3(local.Y.X, local.Y.Y, local.Y.Z), Vec3.UnitY);
            Vector3 eye = ToLocal(camera.GlobalPosition);
            _eyeLocal = new Vec3(eye.X, eye.Y, eye.Z);
        }

        // ---- emitter state --------------------------------------------------------------

        struct ParticleState
        {
            public Vec3 Origin;
            public Vec3 Velocity;
            public Vec3 Accel;
            public Vec3 Tail;
            public float Time;
            public float Lifespan;
            public MdxParticleAppearance Appearance;
        }

        sealed class EmitterRuntime
        {
            public string Label = string.Empty;
            public int NodeId = -1;
            public MdxParticleEmitter2 Pre2;
            public MdxParticleEmitter1 Pre1;
            public MeshInstance3D Mesh;
            public ArrayMesh ArrayMesh;
            public StandardMaterial3D Material;
            public readonly List<ParticleState> Particles = new List<ParticleState>();
            public float Accumulator;
            public int Rows = 1;
            public int Columns = 1;
            public int FrameFlags;
            public float Lifespan;
            public float TailLength;
            public bool Squirt;
        /// <summary>RMS lastEmissionRate：Squirt 发射器只在采样值变化时爆发。</summary>
        public float LastSquirtRate = -1f;
            public bool XyQuad;
            public bool WarnedCapacity;
            public MdxParticleAppearance Appearance;
            public int AppearanceFrame = -1;

            // Cached XYQuad basis, refreshed every frame for XYQuad emitters.
            public Vec3 QuadRight = Vec3.UnitX;
            public Vec3 QuadUp = Vec3.UnitY;
            public Vec3 QuadNormal = Vec3.UnitZ;
        }
    }
}
