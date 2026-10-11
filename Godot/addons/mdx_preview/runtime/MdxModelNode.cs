using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Math3D;
using MdxPreview.Core.Mdx;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// One live MDX model: skeleton, geosets, materials, particle emitters,
    /// ribbons, lights, attachment gizmos and event objects.
    ///
    /// The node's own rotation maps Warcraft's Z-up model space onto Godot's
    /// Y-up world, so every child (including particles and ribbons) can keep
    /// working in authored MDX coordinates.
    /// </summary>
    public partial class MdxModelNode : Node3D
    {
        MdxAssetLibrary _assets;
        Skeleton3D _skeleton;
        Node3D _geosetRoot;
        Node3D _lightRoot;
        Node3D _gizmoRoot;

        readonly List<MdxGeosetBinding> _geosets = new List<MdxGeosetBinding>();
        readonly List<Light3D> _lights = new List<Light3D>();
        float _previousFrame;
        float _previousGlobalMs;
        bool _showGizmos;
        bool _showAttachments;
        bool _showCollision;
        bool _showBones;
        readonly List<MdxAttachmentChild> _attachmentChildren = new List<MdxAttachmentChild>();

        public MdxModel Model { get; private set; }
        public MdxInstance Instance { get; private set; }
        public string SourcePath { get; private set; } = string.Empty;
        public MdxParticleSystem Particles { get; private set; }
        public MdxRibbonSystem Ribbons { get; private set; }
        public MdxEventSystem Events { get; private set; }
        public Camera3D ReferenceCamera { get; set; }
        public Aabb ModelBounds { get; private set; } = new Aabb();

        /// <summary>Raised for every authored EVTS record the playhead crossed.</summary>
        public event Action<string> EventLogged;

        public IReadOnlyList<MdxGeosetBinding> GeosetBindings => _geosets;
        public Skeleton3D Skeleton => _skeleton;

        public bool ShowParticles
        {
            get => Particles != null && Particles.Enabled;
            set { if (Particles != null) Particles.Enabled = value; }
        }

        /// <summary>粒子亮度缩放（显示页可调）。</summary>
        public float ParticleBrightness
        {
            get => Particles?.Brightness ?? 1f;
            set { if (Particles != null) Particles.Brightness = value; }
        }

        /// <summary>粒子发射密度缩放（显示页可调）。</summary>
        public float ParticleDensity
        {
            get => Particles?.Density ?? 1f;
            set { if (Particles != null) Particles.Density = value; }
        }

        public bool ShowRibbons
        {
            get => Ribbons != null && Ribbons.Enabled;
            set { if (Ribbons != null) Ribbons.Enabled = value; }
        }

        public bool ShowLights
        {
            get => _lightRoot != null && _lightRoot.Visible;
            set { if (_lightRoot != null) _lightRoot.Visible = value; }
        }

        public bool ShowGizmos
        {
            get => _showGizmos;
            set
            {
                _showGizmos = value;
                if (_gizmoRoot != null) _gizmoRoot.Visible = value;
            }
        }

        public void Build(MdxAssetLibrary assets, MdxModel model, string sourcePath)
        {
            _assets = assets;
            Model = model;
            SourcePath = sourcePath ?? string.Empty;
            Instance = new MdxInstance(model);

            // Warcraft models are Z-up with +X forward (see AXIS.md: "WC3 MDX +X / +Z").
            // -90 deg about X stands the model up (MDX +Z -> Godot +Y) and the extra
            // +90 deg about Y maps MDX +X (forward) onto Godot's -Z forward axis.
            RotationDegrees = new Vector3(-90f, 90f, 0f);
            ProcessMode = ProcessModeEnum.Always;

            ClearChildren();

            ModelBounds = MdxMeshBuilder.ComputeModelBounds(model);

            _skeleton = new Skeleton3D { Name = "Skeleton" };
            AddChild(_skeleton);
            BuildSkeleton();

            _geosetRoot = new Node3D { Name = "Geosets" };
            _skeleton.AddChild(_geosetRoot);
            BuildGeosets();

            _lightRoot = new Node3D { Name = "Lights" };
            AddChild(_lightRoot);
            BuildLights();

            _gizmoRoot = new Node3D { Name = "Gizmos" };
            AddChild(_gizmoRoot);
            BuildGizmos();

            Particles = new MdxParticleSystem(_assets);
            AddChild(Particles);
            Particles.Build(model, Instance);

            Ribbons = new MdxRibbonSystem(_assets);
            AddChild(Ribbons);
            Ribbons.Build(model, Instance);

            Events = new MdxEventSystem(_assets);
            AddChild(Events);
            Events.Build(model, Instance, this);

            Instance.SetSequence(model.Sequences.Count > 0 ? 0 : -1);
            Instance.Evaluate();
            PushBonePoses();
            UpdateMaterials();

            SetProcess(true);
        }

        void ClearChildren()
        {
            foreach (Node child in GetChildren())
            {
                if (child.GetParent() == this) RemoveChild(child);
                child.QueueFree();
            }
            _geosets.Clear();
            _lights.Clear();
            _skeleton = null;
            _geosetRoot = null;
            _lightRoot = null;
            _gizmoRoot = null;
            Particles = null;
            Ribbons = null;
            Events = null;
        }

        void BuildSkeleton()
        {
            int nodeCount = Math.Max(1, Model.Nodes.Length);
            for (int i = 0; i < nodeCount; i++)
            {
                MdxNode node = Model.GetNode(i);
                // Godot rejects bone names containing ':' or '/', and MDX node names
                // come straight from the file, so sanitise them.
                string name = SanitizeBoneName(node?.Name, i);
                int bone = _skeleton.AddBone(name);
                if (bone < 0)
                {
                    // AddBone can still refuse a name (duplicates are fine, others are not).
                    bone = _skeleton.AddBone($"node_{i}");
                }
                _skeleton.SetBoneRest(bone, Transform3D.Identity);
            }
            for (int i = 0; i < nodeCount; i++)
            {
                MdxNode node = Model.GetNode(i);
                if (node == null) continue;
                // Only link to parents that actually exist as nodes.
                if (node.ParentId >= 0 && node.ParentId < nodeCount && Model.GetNode(node.ParentId) != null)
                    _skeleton.SetBoneParent(i, node.ParentId);
            }
        }

        static string SanitizeBoneName(string name, int index)
        {
            if (string.IsNullOrWhiteSpace(name)) return $"node_{index}";
            var builder = new System.Text.StringBuilder(name.Length + 8);
            builder.Append(index).Append('_');
            foreach (char c in name)
            {
                if (c == ':' || c == '/' || c == '\\' || c == '"' || char.IsControl(c)) builder.Append('_');
                else builder.Append(c);
            }
            return builder.ToString();
        }

        void BuildGeosets()
        {
            for (int geosetIndex = 0; geosetIndex < Model.Geosets.Count; geosetIndex++)
            {
                MdxGeoset geoset = Model.Geosets[geosetIndex];
                ArrayMesh mesh = MdxMeshBuilder.Build(geoset);
                if (mesh == null) continue;

                var binding = new MdxGeosetBinding
                {
                    Geoset = geoset,
                    MeshNode = null,
                    HasGeosetAnimation = Instance.FindGeosetAnim(geosetIndex) != null,
                };

                // Material layers are chained through Material.NextPass so a single
                // MeshInstance3D draws the authored layer stack in order.
                MdxMaterial material = Model.GetMaterial(geoset.MaterialId);

                // Viewer aid: some custom models paint the body with a ReplaceableTextures
                // shell (team colour) that hides the textured geoset underneath. Skipping
                // those geosets reveals the model's own skin, like a model viewer does.
                if (HideReplaceableShellGeosets && IsReplaceableShellOnly(material)) continue;

                // Viewer-style ordering: Warcraft's material priority is the authored
                // draw order and model viewers paint strictly by it (without depth tests
                // between geosets), which is what Retera Model Studio shows. Opt-in,
                // because it bypasses depth for the higher-priority layers.
                int minPriority = int.MaxValue;
                if (ViewerStyleMaterialOrder)
                {
                    foreach (MdxMaterial candidate in Model.Materials)
                        if (candidate != null) minPriority = Math.Min(minPriority, candidate.Priority);
                    if (minPriority == int.MaxValue) minPriority = 0;
                }

                if (material != null)
                {
                    foreach (MdxMaterialLayer layer in material.Layers)
                    {
                        bool usesShader = MdxShaderFactory.NeedsUvRotation(Model, layer);
                        Material layerMaterial;
                        int textureId;
                        if (usesShader)
                        {
                            layerMaterial = MdxShaderFactory.CreateLayerMaterial(_assets, Model, layer, out textureId);
                        }
                        else
                        {
                            StandardMaterial3D standard = MdxMaterialBuilder.CreateLayerMaterial(
                                _assets, Model, layer, out textureId);
                            layerMaterial = standard;
                        }

                        if (UvDebugView) layerMaterial = MdxShaderFactory.CreateUvDebugMaterial();
                        if (CullOverride != 0 && layerMaterial is BaseMaterial3D cullMaterial)
                            cullMaterial.CullMode = (BaseMaterial3D.CullModeEnum)(CullOverride - 1);

                        Color baseColor;
                        if (layerMaterial is StandardMaterial3D standard2) baseColor = standard2.AlbedoColor;
                        else baseColor = ((ShaderMaterial)layerMaterial).GetShaderParameter("albedo_color").AsColor();

                        // MDX material priority is the authored draw order (higher =
                        // drawn later/on top). Without it a translucent team-colour
                        // shell can hide the feathered skin underneath.
                        layerMaterial.RenderPriority = Math.Clamp(material.Priority, -128, 127);

                        if (ViewerStyleMaterialOrder && layerMaterial is BaseMaterial3D baseMaterial)
                        {
                            // Mirrors the engine's per-layer render state while keeping one
                            // ordered pass: every layer joins the transparent pass (so
                            // RenderPriority == MDX material priority decides the order) and
                            // only opaque layers (filter 0) write depth, exactly like
                            // r_mdx_geoset.c. That is what makes the feathered skin win over
                            // the blended team-colour shell, matching mdxtool's output.
                            baseMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                            baseMaterial.NoDepthTest = false;
                            baseMaterial.DepthDrawMode = layer.FilterMode == 0
                                ? BaseMaterial3D.DepthDrawModeEnum.Always
                                : BaseMaterial3D.DepthDrawModeEnum.Disabled;
                        }

                        var layerBinding = new MdxLayerBinding
                        {
                            Layer = layer,
                            Material = layerMaterial,
                            UsesShader = usesShader,
                            ResolvedTextureId = textureId,
                            TextureAnimated = layer.Flipbook != null && layer.Flipbook.KeyCount > 0,
                            HasUvTransform = layer.TransformId >= 0,
                            HasAlphaTrack = layer.Alpha != null,
                            BaseColor = baseColor,
                        };
                        binding.Layers.Add(layerBinding);

                        // One MeshInstance3D per MDX layer, added in authored layer order.
                        // filter 0 layers are opaque (opaque queue, depth write) and blended
                        // layers join the transparent queue afterwards without depth writes,
                        // which is the engine's "opaque surface + additive overlay" result.
                        // Material.NextPass chaining could not express that (the whole stack
                        // inherited the first layer's queue, so an additive overlay merged
                        // the front and back faces of the model).
                        var layerNode = new MeshInstance3D
                        {
                            Name = $"geoset_{geosetIndex}_layer_{binding.Layers.Count - 1}",
                            Mesh = mesh,
                            MaterialOverride = layerMaterial,
                        };
                        _geosetRoot.AddChild(layerNode);
                        // The mesh AABB is the bind pose, but the skeleton can move vertices
                        // well outside it; without a cull margin Godot culls a whole geoset
                        // depending on the view angle (parts "disappear" while orbiting).
                        layerNode.ExtraCullMargin = CullMargin;
                        // Skinned geosets are still culled from their bind-pose AABB; give
                        // each layer an explicit, generous AABB so no view angle can cull a
                        // geoset whose vertices the skeleton moved outside the bind box.
                        {
                            Aabb bindBounds = MdxMeshBuilder.ComputeBounds(geoset);
                            float aabbRadius = Mathf.Max(ModelBounds.Size.Length(), bindBounds.Size.Length()) * 0.5f;
                            Vector3 aabbCenter = bindBounds.GetCenter();
                            layerNode.CustomAabb = new Aabb(
                                aabbCenter - Vector3.One * aabbRadius,
                                Vector3.One * (aabbRadius * 2f));
                        }
                        // The skeleton path is only meaningful once both nodes share a tree.
                        layerNode.Skeleton = layerNode.GetPathTo(_skeleton);
                        binding.Nodes.Add(layerNode);
                        if (binding.MeshNode == null) binding.MeshNode = layerNode;

                        if (VerboseLayerDiagnostics) PrintLayerDiagnostics(geosetIndex, layer, layerMaterial, layerNode);
                    }
                }

                if (binding.MeshNode == null)
                {
                    // No material or no layers: keep one placeholder surface so the geoset
                    // is still visible (and counted in the info panel).
                    var placeholder = new MeshInstance3D
                    {
                        Name = $"geoset_{geosetIndex}",
                        Mesh = mesh,
                        MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1, 1, 1, 0.5f) },
                    };
                    _geosetRoot.AddChild(placeholder);
                    placeholder.Skeleton = placeholder.GetPathTo(_skeleton);
                    binding.Nodes.Add(placeholder);
                    binding.MeshNode = placeholder;
                }

                _geosets.Add(binding);
            }
        }

        /// <summary>Prints one line per MDX layer (enabled by the verification harness).</summary>
        public static bool VerboseLayerDiagnostics { get; set; }

        /// <summary>Extra cull margin for skinned geosets (bind-pose AABB is too tight).</summary>
        float CullMargin => Mathf.Max(32f, ModelBounds.Size.Length() * 0.5f);

        /// <summary>Harness: 0 = as authored, 1 = cull back, 2 = cull front, 3 = two-sided.</summary>
        public static int CullOverride { get; set; }

        /// <summary>Harness: paint UVs as colours to validate the UV stream.</summary>
        public static bool UvDebugView { get; set; }

        /// <summary>Harness: ignore TXAN/KMTF texture animation.</summary>
        public static bool DisableTextureAnim { get; set; }

        void PrintLayerDiagnostics(int geosetIndex, MdxMaterialLayer layer, Material material, MeshInstance3D node)
        {
            if (material is BaseMaterial3D baseMaterial)
            {
                string queue = baseMaterial.Transparency == BaseMaterial3D.TransparencyEnum.Disabled
                    ? "opaque"
                    : "transparent";
                GD.Print($"[layer] geo={geosetIndex} filter={layer.FilterMode} queue={queue} " +
                         $"transparency={baseMaterial.Transparency} blend={baseMaterial.BlendMode} " +
                         $"depth={baseMaterial.DepthDrawMode} noDepthTest={baseMaterial.NoDepthTest} " +
                         $"cull={baseMaterial.CullMode} priority={baseMaterial.RenderPriority} node={node.Name}");
            }
            else
            {
                GD.Print($"[layer] geo={geosetIndex} filter={layer.FilterMode} shader-material node={node.Name}");
            }
        }

        void BuildLights()
        {
            foreach (MdxLight light in Model.Lights)
            {
                Light3D node = light.Type switch
                {
                    MdxLightType.Direct => new DirectionalLight3D(),
                    _ => new OmniLight3D { OmniRange = Math.Max(1f, light.AttenuationEnd) },
                };
                node.Name = $"light_{light.Node.Name}";
                node.LightColor = new Color(light.Color.X, light.Color.Y, light.Color.Z);
                node.LightEnergy = Math.Max(0f, light.Intensity);
                node.ShadowEnabled = light.Type != MdxLightType.Ambient;
                _lightRoot.AddChild(node);
                _lights.Add(node);
            }
        }

        MeshInstance3D _boneLines;
        ImmediateMesh _boneMesh;
        StandardMaterial3D _boneMaterial;

        /// <summary>Model-viewer style draw order (material priority, depth bypassed).</summary>
        public bool ViewerStyleMaterialOrder { get; set; }

        /// <summary>Switches draw order and rebuilds materials (viewer-style option).</summary>
        public void SetViewerStyleMaterialOrder(bool on)
        {
            if (ViewerStyleMaterialOrder == on) return;
            ViewerStyleMaterialOrder = on;
            RebuildMaterials();
        }

        /// <summary>
        /// Hides geosets whose every layer is a ReplaceableTextures texture (a team
        /// colour / glow shell). Custom models often paint the whole body that way,
        /// which hides the textured geoset underneath; enabling this shows the model's
        /// own skin, matching how model viewers (Retera) display it.
        /// </summary>
        public bool HideReplaceableShellGeosets { get; set; }

        public void SetHideReplaceableShellGeosets(bool on)
        {
            if (HideReplaceableShellGeosets == on) return;
            HideReplaceableShellGeosets = on;
            RebuildMaterials();
        }

        /// <summary>True when every layer of the material resolves to a replaceable texture.</summary>
        bool IsReplaceableShellOnly(MdxMaterial material)
        {
            if (material == null || material.Layers.Count == 0) return false;
            foreach (MdxMaterialLayer layer in material.Layers)
            {
                if (layer.TextureId < 0 || layer.TextureId >= Model.Textures.Count) return false;
                MdxTexture texture = Model.Textures[layer.TextureId];
                if (texture.ReplaceableId == 0 &&
                    !MdxAssetLibrary.ClassifyReplaceablePath(texture.Path, out _)) return false;
            }
            return true;
        }

        public bool ShowBoneLines
        {
            get => _boneLines != null && _boneLines.Visible;
            set
            {
                if (_boneLines != null) _boneLines.Visible = value;
                if (value) ShowGizmos = true;
            }
        }

        void BuildGizmos()
        {
            foreach (MdxAttachment attachment in Model.Attachments)
            {
                var marker = new MeshInstance3D
                {
                    Name = $"attach_{attachment.Node.Name}",
                    Mesh = new SphereMesh { Radius = 1.5f, Height = 3f },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(0.2f, 1f, 0.4f, 0.75f),
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    },
                };
                _gizmoRoot.AddChild(marker);
            }

            foreach (MdxCollisionShape shape in Model.CollisionShapes)
            {
                MeshInstance3D marker = shape.Type switch
                {
                    MdxCollisionShapeType.Sphere => new MeshInstance3D
                    {
                        Mesh = new SphereMesh { Radius = Math.Max(0.1f, shape.Radius), Height = Math.Max(0.2f, shape.Radius * 2f) },
                    },
                    MdxCollisionShapeType.Box => new MeshInstance3D { Mesh = new BoxMesh() },
                    _ => new MeshInstance3D { Mesh = new CylinderMesh() },
                };
                marker.Name = $"collision_{shape.Node.Name}";
                marker.MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(1f, 0.6f, 0.1f, 0.35f),
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                };
                _gizmoRoot.AddChild(marker);
            }

            // Bone/skeleton overlay: one line per parent-child node pair.
            _boneMesh = new ImmediateMesh();
            _boneMaterial = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 0.85f, 0.2f, 0.9f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true,
            };
            _boneLines = new MeshInstance3D
            {
                Name = "BoneLines",
                Mesh = _boneMesh,
                MaterialOverride = _boneMaterial,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _gizmoRoot.AddChild(_boneLines);

            _gizmoRoot.Visible = false;
        }

        void UpdateBoneLines()
        {
            if (_boneMesh == null) return;
            _boneMesh.ClearSurfaces();
            if (!ShowBoneLines) return;

            _boneMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _boneMaterial);
            foreach (MdxNode node in Model.NodeList)
            {
                if (node.Parent == null) continue;
                Vec3 child = Instance.GetNodePivotWorld(node.NodeId);
                Vec3 parent = Instance.GetNodePivotWorld(node.Parent.NodeId);
                _boneMesh.SurfaceAddVertex(new Vector3(parent.X, parent.Y, parent.Z));
                _boneMesh.SurfaceAddVertex(new Vector3(child.X, child.Y, child.Z));
            }
            _boneMesh.SurfaceEnd();
        }

        public override void _Process(double delta)
        {
            if (Model == null || Instance == null) return;
            float step = (float)delta;

            _previousFrame = Instance.Frame;
            _previousGlobalMs = Instance.GlobalTimeMs;

            UpdateBillboardBasis();
            Instance.Advance(step);
            PushBonePoses();
            UpdateMaterials();
            UpdateLights();
            UpdateAttachmentChildren();
            UpdateGizmos();

            if (Particles != null && Particles.Enabled) Particles.Tick(step);
            if (Ribbons != null && Ribbons.Enabled) Ribbons.Tick(step);
            if (Events != null)
            {
                // Keep the event system's lifespan clock in step with the model.
                Events.LastTickDelta = step;
                Events.Tick(Instance, _previousFrame, _previousGlobalMs);
            }
        }

        /// <summary>Feeds the camera basis in model space so billboarded nodes face the view.</summary>
        void UpdateBillboardBasis()
        {
            if (ReferenceCamera == null || !ReferenceCamera.IsInsideTree()) return;
            Basis rootBasis = GlobalTransform.Basis;
            Basis cameraBasis = ReferenceCamera.GlobalTransform.Basis;
            if (Mathf.Abs(rootBasis.Determinant()) < 1e-9f) return;
            Basis relative = rootBasis.Inverse() * cameraBasis;
            Instance.CameraRotationInModelSpace = MdxMeshBuilder.ToRotationMatrix(relative);
        }

        void PushBonePoses()
        {
            if (_skeleton == null) return;
            int count = _skeleton.GetBoneCount();
            for (int i = 0; i < count; i++)
            {
                Transform3D pose = MdxMeshBuilder.ToTransform(Instance.GetNodeWorld(i));
                _skeleton.SetBoneGlobalPoseOverride(i, pose, 1f, true);
            }
            _skeleton.ForceUpdateAllBoneTransforms();
        }

        void UpdateMaterials()
        {
            for (int geosetIndex = 0; geosetIndex < _geosets.Count; geosetIndex++)
            {
                MdxGeosetBinding binding = _geosets[geosetIndex];
                float geosetAlpha = binding.HasGeosetAnimation ? Instance.GetGeosetAlpha(geosetIndex) : 1f;
                Vec3 geosetColor = binding.HasGeosetAnimation ? Instance.GetGeosetColor(geosetIndex) : Vec3.One;

                bool visible = geosetAlpha > 0.001f && binding.Geoset.Selectable != 4;
                foreach (MeshInstance3D node in binding.Nodes) if (node.Visible != visible) node.Visible = visible;
                if (!visible) continue;

                foreach (MdxLayerBinding layer in binding.Layers)
                {
                    Material target = layer.Material;
                    float alpha = geosetAlpha * Instance.GetLayerAlpha(layer.Layer);
                    Color color = new Color(
                        layer.BaseColor.R * geosetColor.X,
                        layer.BaseColor.G * geosetColor.Y,
                        layer.BaseColor.B * geosetColor.Z,
                        layer.BaseColor.A * alpha);

                    if (layer.UsesShader)
                    {
                        var shaderMaterial = (ShaderMaterial)target;
                        shaderMaterial.SetShaderParameter("albedo_color", color);
                        if (layer.HasUvTransform)
                        {
                            MdxTextureAnimState uvState = Instance.GetTextureAnim(layer.Layer.TransformId);
                            shaderMaterial.SetShaderParameter("uv_offset", new Vector2(uvState.Translation.X, uvState.Translation.Y));
                            shaderMaterial.SetShaderParameter("uv_scale", new Vector2(uvState.Scale.X, uvState.Scale.Y));
                            // TXAN rotation is a Z-axis rotation in texture space;
                            // 2*atan2(z, w) recovers the angle from the quaternion.
                            shaderMaterial.SetShaderParameter("uv_rotation",
                                2f * Mathf.Atan2(uvState.Rotation.Z, uvState.Rotation.W));
                        }
                    }
                    else
                    {
                        target.Set("albedo_color", color);
                    }

                    if (layer.TextureAnimated)
                    {
                        MdxTexture texture = ResolveTextureLayer(layer);
                        if (texture != null)
                        {
                            Texture2D albedo = _assets.LoadTexture(texture.Path, texture.ReplaceableId);
                            if (albedo != null)
                            {
                                if (layer.UsesShader) ((ShaderMaterial)target).SetShaderParameter("albedo_tex", albedo);
                                else target.Set("albedo_texture", albedo);
                            }
                        }
                    }

                    if (layer.HasUvTransform && !layer.UsesShader && !DisableTextureAnim)
                    {
                        MdxTextureAnimState uv = Instance.GetTextureAnim(layer.Layer.TransformId);
                        target.Set("uv1_scale", new Vector3(uv.Scale.X, uv.Scale.Y, 1f));
                        target.Set("uv1_offset", new Vector3(uv.Translation.X, uv.Translation.Y, 0f));
                    }
                }
            }
        }

        MdxTexture ResolveTextureLayer(MdxLayerBinding layer)
        {
            int animated = Instance.SampleIntTrack(layer.Layer.Flipbook, layer.ResolvedTextureId);
            if (animated < 0 || animated >= Model.Textures.Count) return null;
            if (animated == layer.ResolvedTextureId) return null;
            layer.ResolvedTextureId = animated;
            return Model.Textures[animated];
        }

        void UpdateLights()
        {
            for (int i = 0; i < Model.Lights.Count && i < _lights.Count; i++)
            {
                MdxLight light = Model.Lights[i];
                Light3D node = _lights[i];

                MdxSampleContext context = Instance.Context;
                float visibility = MdxTrackSampler.SampleFloat(Model, light.Visibility, context, 1f);
                node.Visible = visibility > 0.001f;
                if (!node.Visible) continue;

                Vec3 color = light.ColorTrack != null
                    ? MdxTrackSampler.SampleColor(Model, light.ColorTrack, context, light.Color)
                    : light.Color;
                float intensity = MdxTrackSampler.SampleFloat(Model, light.IntensityTrack, context, light.Intensity);
                node.LightColor = new Color(color.X, color.Y, color.Z);
                node.LightEnergy = Math.Max(0f, intensity);

                if (node is OmniLight3D omni && light.AttenuationEndTrack != null)
                    omni.OmniRange = Math.Max(1f, MdxTrackSampler.SampleFloat(
                        Model, light.AttenuationEndTrack, context, light.AttenuationEnd));

                Vec3 position = Instance.GetNodePivotWorld(light.Node.NodeId);
                Mat4 world = Instance.GetNodeWorld(light.Node.NodeId);
                node.Position = new Vector3(position.X, position.Y, position.Z);
                if (node is DirectionalLight3D directional)
                {
                    // The authored -Z axis of the node is the light direction.
                    Vec3 direction = world.TransformDirection(new Vec3(0, 0, -1)).Normalized();
                    if (direction.LengthSquared > 0.5f)
                    {
                        var basis = Basis.Identity;
                        basis.Z = new Vector3(direction.X, direction.Y, direction.Z).Normalized();
                        basis.X = basis.Y.Cross(basis.Z).Normalized();
                        if (basis.X.LengthSquared() < 0.5f) basis.X = Vector3.Right;
                        basis.Y = basis.Z.Cross(basis.X).Normalized();
                        directional.Basis = basis;
                    }
                }
            }
        }

        new void UpdateGizmos()
        {
            if (_gizmoRoot == null || !_gizmoRoot.Visible) return;
            UpdateBoneLines();
            int index = 0;

            if (_showAttachments)
            {
                List<MdxAttachmentPose> poses = Instance.CollectAttachmentPoses();
                foreach (MdxAttachmentPose pose in poses)
                {
                    if (index >= _gizmoRoot.GetChildCount()) break;
                    var marker = (Node3D)_gizmoRoot.GetChild(index++);
                    marker.Position = new Vector3(pose.Origin.X, pose.Origin.Y, pose.Origin.Z);
                    marker.Visible = true;
                }
            }
            else
            {
                foreach (MdxAttachment _ in Model.Attachments)
                {
                    if (index >= _gizmoRoot.GetChildCount()) break;
                    ((Node3D)_gizmoRoot.GetChild(index++)).Visible = false;
                }
            }

            foreach (MdxCollisionShape shape in Model.CollisionShapes)
            {
                if (index >= _gizmoRoot.GetChildCount()) break;
                var marker = (Node3D)_gizmoRoot.GetChild(index++);
                Vec3 center = Instance.GetNodePivotWorld(shape.Node.NodeId);
                marker.Position = new Vector3(center.X, center.Y, center.Z);
                marker.Visible = _showCollision;
            }
        }

        // ---- UI helpers ------------------------------------------------------------------

        /// <summary>Selects a sequence by name (case-insensitive). Returns the index, or -1 when absent.</summary>
        public int SetSequenceByName(string sequenceName)
        {
            if (Model == null) return -1;
            int index = Model.FindSequenceIndex(sequenceName);
            if (index >= 0) SetSequence(index);
            return index;
        }

        public void SetSequence(int index)
        {
            Instance.SetSequence(index);
            Instance.Evaluate();
            PushBonePoses();
            UpdateMaterials();
            Particles?.Reset();
            Ribbons?.Reset();
        }

        public void SetPlaying(bool playing)
        {
            if (Instance != null) Instance.Playing = playing;
        }

        public bool IsPlaying => Instance != null && Instance.Playing;
        public void SetSpeed(float speed)
        {
            if (Instance != null) Instance.Speed = speed;
        }

        public float GetSpeed() => Instance?.Speed ?? 1f;
        public void SetLoop(bool loop)
        {
            if (Instance != null) Instance.Loop = loop;
        }

        public bool GetLoop() => Instance?.Loop ?? true;

        public float SequencePositionMs => Instance?.Frame ?? 0f;
        public float SequenceStartMs => Instance?.Sequence?.Start ?? 0f;
        public float SequenceDurationMs => Instance?.Sequence?.Duration ?? 0f;

        public float SequenceRatio
        {
            get
            {
                if (Instance == null) return 0f;
                float duration = SequenceDurationMs;
                return duration <= 0f ? 0f : Mathf.Clamp((Instance.Frame - SequenceStartMs) / duration, 0f, 1f);
            }
            set
            {
                if (Instance == null) return;
                Instance.SetSequenceRatio(value);
                PushBonePoses();
                UpdateMaterials();
            }
        }

        public void SetGizmoOptions(bool attachments, bool collision)
        {
            _showAttachments = attachments;
            _showCollision = collision;
            ShowGizmos = attachments || collision;
        }

        // ---- attachment children (equipment) ---------------------------------------------

        /// <summary>
        /// A node parented to this model that follows one MDX attachment point every frame.
        /// Equipment uses this instead of the attachment gizmos, which are editor markers:
        /// weapons hang off "hand left"/"hand right", wings off "chest", hair off "head".
        /// Offset is applied in the attachment's local space.
        /// </summary>
        public sealed class MdxAttachmentChild
        {
            public Node3D Node;
            public string AttachmentName;
            public Vector3 Offset;
        }

        public IReadOnlyList<MdxAttachmentChild> AttachmentChildren => _attachmentChildren;

        /// <summary>Parents an existing node inside this model and pins it to an attachment point.</summary>
        public bool AttachToAttachment(Node3D child, string attachmentName, Vector3 offset = default(Vector3))
        {
            if (child == null || string.IsNullOrEmpty(attachmentName) || Instance == null) return false;
            if (child.GetParent() != this) AddChild(child);
            _attachmentChildren.Add(new MdxAttachmentChild { Node = child, AttachmentName = attachmentName, Offset = offset });
            UpdateAttachmentChildren();
            return true;
        }

        public void DetachAttachmentChild(Node3D child)
        {
            for (int i = _attachmentChildren.Count - 1; i >= 0; i--)
                if (_attachmentChildren[i].Node == child) _attachmentChildren.RemoveAt(i);
        }

        public void DetachAllAttachmentChildren() => _attachmentChildren.Clear();

        /// <summary>
        /// Current transform of a named attachment point, in model (MDX) space.
        /// Lookup matches the engine's AddSpecialEffectTarget: an exact name first,
        /// then a case-insensitive substring, so "hand left" finds "Hand Left Ref".
        /// </summary>
        public bool TryGetAttachmentTransform(string attachmentName, out Transform3D transform)
        {
            transform = Transform3D.Identity;
            if (Instance == null || string.IsNullOrEmpty(attachmentName)) return false;

            List<MdxAttachmentPose> poses = Instance.CollectAttachmentPoses();
            MdxAttachmentPose match = FindAttachmentPose(poses, attachmentName, true);
            if (match.Attachment == null) match = FindAttachmentPose(poses, attachmentName, false);
            if (match.Attachment == null) return false;

            Transform3D world = MdxMeshBuilder.ToTransform(match.Transform);
            // The pose's origin is the resolved pivot; its basis is the owning bone's.
            world.Origin = new Vector3(match.Origin.X, match.Origin.Y, match.Origin.Z);
            transform = world;
            return true;
        }

        static MdxAttachmentPose FindAttachmentPose(List<MdxAttachmentPose> poses, string name, bool exact)
        {
            foreach (MdxAttachmentPose pose in poses)
            {
                string candidate = pose.Attachment.Node.Name ?? string.Empty;
                bool hit = exact
                    ? string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)
                    : candidate.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hit) return pose;
            }
            return default;
        }

        void UpdateAttachmentChildren()
        {
            for (int i = 0; i < _attachmentChildren.Count; i++)
            {
                MdxAttachmentChild child = _attachmentChildren[i];
                if (child.Node == null || !GodotObject.IsInstanceValid(child.Node)) continue;
                if (!TryGetAttachmentTransform(child.AttachmentName, out Transform3D pose)) continue;
                child.Node.Transform = new Transform3D(pose.Basis, pose.Origin + pose.Basis * child.Offset);
            }
        }

        /// <summary>Re-resolves replaceable textures (team colour change).</summary>
        public void RebuildMaterials()
        {
            _geosetRoot?.QueueFree();
            _geosetRoot = new Node3D { Name = "Geosets" };
            _skeleton?.AddChild(_geosetRoot);
            _geosets.Clear();
            BuildGeosets();
            UpdateMaterials();
        }

        /// <summary>Model-space bounds transformed into world space (camera framing).</summary>
        public Aabb WorldBounds()
        {
            Vector3 min = ModelBounds.Position;
            Vector3 max = ModelBounds.End;
            var corners = new[]
            {
                new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
                new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
                new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
            };
            Aabb result = new Aabb(GlobalTransform * corners[0], Vector3.Zero);
            for (int i = 1; i < corners.Length; i++) result = result.Expand(GlobalTransform * corners[i]);
            return result;
        }

        public void LogEvent(string message) => EventLogged?.Invoke(message);

        /// <summary>World-space position of an event object's node (MDX space -> world).</summary>
        public Vector3 EventWorldPosition(MdxEventObject evt)
        {
            Vec3 local = Instance.GetNodePivotWorld(evt.Node.NodeId);
            return GlobalTransform * new Vector3(local.X, local.Y, local.Z);
        }
    }
}
