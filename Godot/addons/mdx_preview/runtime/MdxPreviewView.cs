using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Math3D;
using MdxPreview.Core.Mdx;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// The 3D scene shown in the preview dock: environment, ground, key light,
    /// an orbit camera and the loaded <see cref="MdxModelNode"/>.
    /// It lives inside a SubViewport owned by the dock.
    /// </summary>
    public partial class MdxPreviewView : Node3D
    {
        Node3D _modelPivot;
        Camera3D _camera;
        DirectionalLight3D _keyLight;
        WorldEnvironment _environment;
        MeshInstance3D _ground;

        // Default framing: a front three-quarter view of an MDX model. Warcraft
        // models face +X in model space, which MdxModelNode maps onto Godot -Z;
        // this angle matches the reference renderer's preview look (verified
        // against mdxtool -o output for Peon.mdx).
        float _yaw = -150f;
        float _pitch = 15f;
        float _distance = 220f;
        Vector3 _target = Vector3.Zero;
        bool _framePending;

        /// <summary>
        /// Parameterless constructor required by Godot when it rebuilds script
        /// instances during an assembly reload; use <see cref="Initialize"/> or the
        /// two-argument constructor for real setup.
        /// </summary>
        public MdxPreviewView()
        {
            Name = "MdxPreviewView";
            ProcessMode = ProcessModeEnum.Always;
        }

        public MdxPreviewView(MdxAssetLibrary assets, MdxPreviewSettings settings) : this()
        {
            Assets = assets;
            Settings = settings;
        }

        /// <summary>Supplies the dependencies for instances created by Godot itself.</summary>
        public void Initialize(MdxAssetLibrary assets, MdxPreviewSettings settings)
        {
            Assets = assets;
            Settings = settings;
            if (IsInsideTree()) ApplySettings();
        }

        /// <summary>
        /// Non-null defaults: Godot can rebuild this script instance on assembly
        /// reload, in which case the dock's Initialize() call never happened and
        /// these would otherwise be null (the previous crash: null Assets while
        /// opening a model).
        /// </summary>
        public MdxAssetLibrary Assets { get; private set; } = new MdxAssetLibrary();
        public MdxPreviewSettings Settings { get; private set; } = MdxPreviewSettings.Load();
        public MdxModelNode Current { get; private set; }
        public Camera3D Camera => _camera;

        public bool AutoRotate { get; set; } = true;
        public float AutoRotateSpeed { get; set; } = 25f;
        public bool UseModelCamera { get; set; }
        public bool ShowGround
        {
            get => _ground != null && _ground.Visible;
            set { if (_ground != null) _ground.Visible = value; }
        }

        /// <summary>Manually rotates the model (degrees). Independent from the camera.</summary>
        public Vector3 ModelRotationDegrees
        {
            get => _modelPivot?.RotationDegrees ?? Vector3.Zero;
            set { if (_modelPivot != null) _modelPivot.RotationDegrees = value; }
        }

        public event Action<string> EventLogged;
        public event Action<string> ModelLoaded;
        public event Action<string> LoadFailed;

        /// <summary>Parser diagnostics of the model currently shown (may be empty).</summary>
        public IReadOnlyList<string> LastDiagnostics { get; private set; } = new List<string>();

        public override void _Ready()
        {
            BuildEnvironment();
            BuildGround();

            _keyLight = new DirectionalLight3D
            {
                Name = "KeyLight",
                LightEnergy = 1.15f,
                ShadowEnabled = true,
                RotationDegrees = new Vector3(-50f, 35f, 0f),
            };
            AddChild(_keyLight);

            var fill = new DirectionalLight3D
            {
                Name = "FillLight",
                LightEnergy = 0.35f,
                ShadowEnabled = false,
                RotationDegrees = new Vector3(-25f, -140f, 0f),
            };
            AddChild(fill);

            _modelPivot = new Node3D { Name = "ModelPivot" };
            AddChild(_modelPivot);

            _camera = new Camera3D
            {
                Name = "PreviewCamera",
                Current = true,
                Fov = 45f,
                Near = 0.5f,
                Far = 20000f,
            };
            AddChild(_camera);
            UpdateCamera();

            ApplySettings();
        }

        void BuildEnvironment()
        {
            var sky = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.35f, 0.45f, 0.62f),
                SkyHorizonColor = new Color(0.62f, 0.68f, 0.75f),
                GroundBottomColor = new Color(0.16f, 0.17f, 0.19f),
                GroundHorizonColor = new Color(0.30f, 0.31f, 0.33f),
                SunAngleMax = 30f,
            };
            var environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightSkyContribution = 0.85f,
                AmbientLightEnergy = 0.6f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                SsaoEnabled = true,
                GlowEnabled = false,
            };
            _environment = new WorldEnvironment { Name = "Environment", Environment = environment };
            AddChild(_environment);
        }

        void BuildGround()
        {
            var plane = new PlaneMesh { Size = new Vector2(1024f, 1024f) };
            var material = new StandardMaterial3D
            {
                AlbedoTexture = CreateGridTexture(),
                TextureRepeat = true,
                Uv1Scale = new Vector3(16f, 16f, 1f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
                Roughness = 1f,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(1f, 1f, 1f, 0.85f),
            };
            _ground = new MeshInstance3D { Name = "Ground", Mesh = plane, MaterialOverride = material };
            AddChild(_ground);
        }

        static Texture2D CreateGridTexture()
        {
            const int size = 64;
            var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            var line = new Color(0.30f, 0.32f, 0.35f, 1f);
            var fill = new Color(0.20f, 0.21f, 0.23f, 0.9f);
            image.Fill(fill);
            for (int i = 0; i < size; i++)
            {
                image.SetPixel(i, 0, line);
                image.SetPixel(0, i, line);
            }
            return ImageTexture.CreateFromImage(image);
        }

        public void ApplySettings()
        {
            AutoRotate = Settings.AutoRotate;
            AutoRotateSpeed = Settings.AutoRotateSpeed;
            UseModelCamera = Settings.UseModelCamera;
            ShowGround = Settings.ShowGround;
            if (_camera != null) _camera.Fov = Mathf.Clamp(Settings.CameraFov, 5f, 120f);
        }

        // ---- model loading ---------------------------------------------------------------

        /// <summary>Loads a model by virtual path (MPQ) or by res:// / absolute file path.</summary>
        public string LoadModel(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return "模型路径为空";

            MdxModel model;
            string sourcePath = modelPath;
            try
            {
                if (modelPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                    || modelPath.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
                    || System.IO.File.Exists(modelPath))
                {
                    string diskPath = ProjectSettings.GlobalizePath(modelPath);
                    // Loose-file models normally reference their textures relative to
                    // their own folder or the folder above it (model in "zq\", textures
                    // in "jn\"), so both are added as transient lookup roots.
                    string modelDir = System.IO.Path.GetDirectoryName(diskPath);
                    if (!string.IsNullOrEmpty(modelDir))
                    {
                        Assets.AddTransientRoot(modelDir);
                        string parentDir = System.IO.Path.GetDirectoryName(modelDir);
                        if (!string.IsNullOrEmpty(parentDir)) Assets.AddTransientRoot(parentDir);
                    }
                    byte[] data = System.IO.File.ReadAllBytes(diskPath);
                    model = MdxLoader.Load(data, modelPath);
                }
                else
                {
                    model = Assets.LoadModel(modelPath);
                }
            }
            catch (Exception ex)            {
                string message = $"解析模型失败: {modelPath}\n{ex.Message}";
                GD.PushError($"[MDX Preview] {message}");
                LoadFailed?.Invoke(message);
                return message;
            }

            if (model == null)
            {
                string message = $"找不到模型: {modelPath}（请检查游戏路径/MPQ 配置与资源搜索路径）";
                GD.PushWarning($"[MDX Preview] {message}");
                LoadFailed?.Invoke(message);
                return message;
            }

            ClearModel();
            LastDiagnostics = model.HasDiagnostics ? new List<string>(model.Diagnostics) : new List<string>();
            if (model.HasDiagnostics)
            {
                // Trailing junk, resynchronised records and unknown chunks are normal
                // in custom map assets; only report them as a warning when the parser
                // had to drop or repair something, otherwise keep the console quiet.
                bool benign = true;
                foreach (string diagnostic in model.Diagnostics)
                {
                    string lower = diagnostic.ToLowerInvariant();
                    if (lower.Contains("dropped") || lower.Contains("resync") || lower.Contains("failed") ||
                        lower.Contains("unsupported") || lower.Contains("invalid"))
                    {
                        benign = false;
                        break;
                    }
                }

                string text = $"[MDX Preview] {modelPath} 解析提示({model.Diagnostics.Count}，模型已正常加载):\n{model.DiagnosticsText}";
                if (benign) GD.Print(text);
                else GD.PushWarning(text);
            }
            Current = new MdxModelNode();
            try
            {
                Current.Build(Assets, model, sourcePath);
            }
            catch (Exception ex)
            {
                // Never leave a half-built node (and never let the exception escape
                // into the editor UI as a bare NullReferenceException).
                Current.QueueFree();
                Current = null;
                string message = $"构建模型失败: {modelPath}\n{ex.Message}";
                GD.PushError($"[MDX Preview] {message}");
                LoadFailed?.Invoke(message);
                return message;
            }

            Current.ReferenceCamera = _camera;
            Current.SetGizmoOptions(Settings.ShowAttachments, Settings.ShowCollision);
            Current.ShowBoneLines = Settings.ShowBones;
            // Setter (not the property) so a model loaded while the option is on is rebuilt.
            Current.SetHideReplaceableShellGeosets(Settings.HideReplaceableShellGeosets);
            Current.ViewerStyleMaterialOrder = Settings.ViewerStyleMaterialOrder;
            Current.ShowParticles = Settings.ShowParticles;
            Current.ParticleBrightness = Settings.ParticleBrightness;
            Current.ParticleDensity = Settings.ParticleDensity;
            Current.ShowRibbons = Settings.ShowRibbons;
            Current.ShowLights = Settings.ShowLights;
            Current.EventLogged += message => EventLogged?.Invoke(message);
            _modelPivot.AddChild(Current);

            FrameModel();
            ModelLoaded?.Invoke(modelPath);
            return null;
        }

        public void ClearModel()
        {
            if (Current == null) return;
            // Godot errors out when removing a node that is not actually a child
            // ("p_child->data.parent != this"), which happens after a failed build.
            if (Current.GetParent() == _modelPivot) _modelPivot.RemoveChild(Current);
            Current.QueueFree();
            Current = null;
        }

        public void RefreshFromSettings()
        {
            if (Current == null) return;
            Current.SetGizmoOptions(Settings.ShowAttachments, Settings.ShowCollision);
            Current.ShowBoneLines = Settings.ShowBones;
            // Setter (not the property) so a model loaded while the option is on is rebuilt.
            Current.SetHideReplaceableShellGeosets(Settings.HideReplaceableShellGeosets);
            Current.ViewerStyleMaterialOrder = Settings.ViewerStyleMaterialOrder;
            Current.ShowParticles = Settings.ShowParticles;
            Current.ParticleBrightness = Settings.ParticleBrightness;
            Current.ParticleDensity = Settings.ParticleDensity;
            Current.ShowRibbons = Settings.ShowRibbons;
            Current.ShowLights = Settings.ShowLights;
            ApplySettings();
        }

        // ---- camera ---------------------------------------------------------------------

        public void Orbit(Vector2 delta)
        {
            _yaw -= delta.X * 0.4f;
            _pitch = Mathf.Clamp(_pitch - delta.Y * 0.4f, -89f, 89f);
            UpdateCamera();
        }

        public void Pan(Vector2 delta)
        {
            Basis basis = _camera.GlobalTransform.Basis;
            float scale = _distance * 0.0022f;
            _target += basis.X * (-delta.X * scale) + basis.Y * (delta.Y * scale);
            UpdateCamera();
        }

        public void Zoom(float amount)
        {
            _distance = Mathf.Clamp(_distance * Mathf.Pow(1.12f, -amount), 1f, 20000f);
            UpdateCamera();
        }

        public void SetDistance(float distance)
        {
            _distance = Mathf.Clamp(distance, 1f, 20000f);
            UpdateCamera();
        }

        /// <summary>Sets the orbit angles directly (verification harness / presets).</summary>
        public void SetOrbit(float yawDegrees, float pitchDegrees)
        {
            _yaw = yawDegrees;
            _pitch = Mathf.Clamp(pitchDegrees, -89f, 89f);
            UpdateCamera();
        }

        /// <summary>
        /// Deterministic front orthographic view used for image diffing against
        /// mdxtool --front-ortho (same frame, same scale, no perspective).
        /// </summary>
        public void SetFrontOrtho(Aabb worldBounds)
        {
            if (_camera == null) return;
            Vector3 size = worldBounds.Size;
            float extent = Mathf.Max(1f, Mathf.Max(size.X, Mathf.Max(size.Y, size.Z)));
            Vector3 center = worldBounds.GetCenter();
            _camera.Projection = Camera3D.ProjectionType.Orthogonal;
            _camera.Size = extent * 1.1f;
            _camera.Near = 0.05f;
            _camera.Far = extent * 8f;
            // The preview maps MDX +X onto Godot -Z, so the model's front faces -Z.
            _camera.Position = center - new Vector3(0f, 0f, extent * 2f);
            _camera.LookAt(center, Vector3.Up);
        }

        /// <summary>
        /// Plain black background with flat ambient light, matching the reference
        /// renderer's preview output so image diffing compares the model only.
        /// </summary>
        public void SetPlainBackground()
        {
            ShowGround = false;
            if (_environment?.Environment == null) return;
            _environment.Environment.BackgroundMode = Godot.Environment.BGMode.Color;
            _environment.Environment.BackgroundColor = Colors.Black;
            _environment.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            _environment.Environment.AmbientLightColor = Colors.White;
            _environment.Environment.AmbientLightEnergy = 0.35f;
        }

        /// <summary>Current orbit distance (harness: used to match mdxtool's orbit_distance).</summary>
        public float CameraDistance => _distance;

        /// <summary>Multiplies the orbit distance (used by the verification harness).</summary>
        public void ZoomBy(float factor)
        {
            if (factor <= 0f) return;
            SetDistance(_distance * factor);
        }

        public void ResetCamera()
        {
            _yaw = -150f;
            _pitch = 15f;
            _target = Vector3.Zero;
            FrameModel();
        }

        /// <summary>Frames the loaded model (bounds in world space).</summary>
        public void FrameModel()
        {
            if (Current == null)
            {
                _distance = 220f;
                _target = Vector3.Zero;
                UpdateCamera();
                return;
            }

            Aabb bounds = Current.WorldBounds();
            _target = bounds.GetCenter();
            float radius = Mathf.Max(8f, bounds.Size.Length() * 0.5f);
            _distance = radius / Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) * 1.35f;
            UpdateCamera();
        }

        void UpdateCamera()
        {
            if (_camera == null) return;
            float yaw = Mathf.DegToRad(_yaw);
            float pitch = Mathf.DegToRad(_pitch);
            var offset = new Vector3(
                Mathf.Cos(pitch) * Mathf.Cos(yaw),
                Mathf.Sin(pitch),
                Mathf.Cos(pitch) * Mathf.Sin(yaw)) * _distance;
            _camera.Position = _target + offset;
            _camera.LookAt(_target, Vector3.Up);
        }

        public override void _Process(double delta)
        {
            if (AutoRotate && !UseModelCamera && _modelPivot != null)
                _modelPivot.RotateY(Mathf.DegToRad(AutoRotateSpeed * (float)delta));
            if (_framePending)
            {
                _framePending = false;
                FrameModel();
            }
            if (UseModelCamera) ApplyModelCamera();
        }

        /// <summary>Drives the preview camera from the model's first CAMS record.</summary>
        void ApplyModelCamera()
        {
            if (Current == null || Current.Model.Cameras.Count == 0) return;
            MdxCameraPose pose = Current.Instance.SampleCamera(0);
            Transform3D modelTransform = Current.GlobalTransform;
            Vector3 position = modelTransform * new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
            Vector3 target = modelTransform * new Vector3(pose.Target.X, pose.Target.Y, pose.Target.Z);
            if (position.DistanceSquaredTo(target) < 1e-6f) return;
            _camera.Position = position;
            _camera.LookAt(target, Vector3.Up);
            _camera.Fov = Mathf.RadToDeg(pose.FieldOfView);
            _camera.Near = Mathf.Max(0.05f, pose.NearClip);
            _camera.Far = Mathf.Max(_camera.Near + 1f, pose.FarClip * 100f);
        }

        public void RequestFrame() => _framePending = true;
    }
}
