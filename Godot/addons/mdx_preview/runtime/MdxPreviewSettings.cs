using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// Persisted plugin configuration: game path (MPQ archives), the extra BLP
    /// search paths, preview toggles and the export folder.
    /// Stored in <c>res://addons/mdx_preview/settings.cfg</c>.
    /// </summary>
    public sealed class MdxPreviewSettings
    {
        public const string DefaultPath = "res://addons/mdx_preview/settings.cfg";
        public const string DefaultOutputFolder = "res://Godot";

        /// <summary>Ordered highest priority first; the first archive that has a file wins.</summary>
        public List<string> MpqPaths { get; set; } = new List<string>();

        /// <summary>Loose folders searched for BLP/TGA/PNG textures, before the MPQs.</summary>
        public List<string> BlpSearchPaths { get; set; } = new List<string>();

        public List<string> RecentModels { get; set; } = new List<string>();

        public string OutputFolder { get; set; } = DefaultOutputFolder;

        /// <summary>
        /// 导出缩放：MDX 用 Warcraft III 单位（模型高约 100–200），直接放进 Godot
        /// （1 单位 = 1 米）会显得巨大。默认 0.01 = 1 WC3 单位约 1 厘米。
        /// </summary>
        public float ExportScale { get; set; } = 0.01f;
        public int TeamColorIndex { get; set; } = 1;
        public bool GenerateMipmaps { get; set; } = true;

        public bool ShowParticles { get; set; } = true;

        /// <summary>粒子亮度缩放（1 = 作者数值）。</summary>
        public float ParticleBrightness { get; set; } = 1f;

        /// <summary>粒子发射密度缩放（1 = 作者数值）。</summary>
        public float ParticleDensity { get; set; } = 1f;
        public bool ShowRibbons { get; set; } = true;
        public bool ShowLights { get; set; } = true;
        public bool ShowAttachments { get; set; }
        public bool ShowCollision { get; set; }
        public bool ShowBones { get; set; }
        public bool AutoRotate { get; set; } = true;
        public float AutoRotateSpeed { get; set; } = 25f;
        public bool UseModelCamera { get; set; }
        public float CameraFov { get; set; } = 45f;
        public bool ShowGround { get; set; } = true;

        /// <summary>Engine semantics for ReplaceableTextures\TeamColor|TeamGlow paths; off = raw file (viewer style).</summary>
        public bool ReplaceTeamTextures { get; set; } = true;

        /// <summary>Hide ReplaceableTextures shell geosets so inner skins stay visible.</summary>
        public bool HideReplaceableShellGeosets { get; set; }

        /// <summary>Viewer-style draw order by material priority (Retera-like look).</summary>
        public bool ViewerStyleMaterialOrder { get; set; }

        public string LastModel { get; set; } = string.Empty;

        /// <summary>预览窗口默认尺寸（第一次打开时使用）。</summary>
        public static readonly Vector2I DefaultPreviewWindowSize = new Vector2I(1180, 760);

        /// <summary>表示"尚未记录位置、请居中打开"的哨兵值。</summary>
        public static readonly Vector2I CenteredPreviewWindow = new Vector2I(-1, -1);

        /// <summary>预览窗口尺寸（关闭/退出时写回 settings.cfg）。</summary>
        public Vector2I PreviewWindowSize { get; set; } = DefaultPreviewWindowSize;

        /// <summary>预览窗口左上角位置；等于 <see cref="CenteredPreviewWindow"/> 时居中打开。</summary>
        public Vector2I PreviewWindowPosition { get; set; } = CenteredPreviewWindow;

        /// <summary>记录窗口几何（位置 + 尺寸并写盘）。</summary>
        public void RememberPreviewWindow(Vector2I position, Vector2I size, string path = DefaultPath)
        {
            if (size.X > 0 && size.Y > 0) PreviewWindowSize = size;
            if (position.X > -32000 && position.Y > -32000) PreviewWindowPosition = position;
            Save(path);
        }

        /// <summary>Warcraft III archive priority: patch overrides expansions, expansions override base.</summary>
        static readonly string[] Wc3Priority =
        {
            "War3Patch.mpq", "War3xLocal.mpq", "War3x.mpq", "War3Local.mpq", "war3.mpq",
        };

        /// <summary>Finds every MPQ in a Warcraft III install folder, in engine priority order.</summary>
        public static List<string> DiscoverMpq(string gameFolder)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder)) return result;

            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(gameFolder, "*.mpq", SearchOption.TopDirectoryOnly))
                found[Path.GetFileName(file)] = file;

            foreach (string name in Wc3Priority)
                if (found.TryGetValue(name, out string path))
                {
                    result.Add(path);
                    found.Remove(name);
                }

            var rest = new List<string>(found.Values);
            rest.Sort(StringComparer.OrdinalIgnoreCase);
            result.AddRange(rest);
            return result;
        }

        public void ApplyTo(MdxAssetLibrary library, out string error)
        {
            library.TeamColor = MdxTeamColor.All[Mathf.Clamp(TeamColorIndex, 0, MdxTeamColor.All.Length - 1)];
            library.GenerateMipmaps = GenerateMipmaps;
            library.SubstituteReplaceablePaths = ReplaceTeamTextures;
            library.InvalidateReplaceableTextures();
            library.SetTextureSearchPaths(BlpSearchPaths);
            library.RefreshDirectories();
            error = library.SetArchives(MpqPaths);
        }

        public void AddRecent(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;
            RecentModels.RemoveAll(p => string.Equals(p, modelPath, StringComparison.OrdinalIgnoreCase));
            RecentModels.Insert(0, modelPath);
            if (RecentModels.Count > 16) RecentModels.RemoveRange(16, RecentModels.Count - 16);
            LastModel = modelPath;
        }

        // ---- persistence -----------------------------------------------------------------

        public static MdxPreviewSettings Load(string path = DefaultPath)
        {
            var settings = new MdxPreviewSettings();
            var config = new ConfigFile();
            if (config.Load(path) != Error.Ok) return settings;

            settings.MpqPaths = ReadList(config, "paths", "mpq");
            settings.BlpSearchPaths = ReadList(config, "paths", "blp_search");
            settings.RecentModels = ReadList(config, "paths", "recent_models");
            settings.OutputFolder = (string)config.GetValue("export", "output_folder", DefaultOutputFolder);
            settings.ExportScale = (float)config.GetValue("export", "scale", 0.01f);
            settings.TeamColorIndex = (int)config.GetValue("preview", "team_color", 1);
            settings.GenerateMipmaps = (bool)config.GetValue("preview", "mipmaps", true);
            settings.ShowParticles = (bool)config.GetValue("preview", "particles", true);
            settings.ParticleBrightness = (float)config.GetValue("preview", "particle_brightness", 1f);
            settings.ParticleDensity = (float)config.GetValue("preview", "particle_density", 1f);
            settings.ShowRibbons = (bool)config.GetValue("preview", "ribbons", true);
            settings.ShowLights = (bool)config.GetValue("preview", "lights", true);
            settings.ShowAttachments = (bool)config.GetValue("preview", "attachments", false);
            settings.ShowCollision = (bool)config.GetValue("preview", "collision", false);
            settings.ShowBones = (bool)config.GetValue("preview", "bones", false);
            settings.AutoRotate = (bool)config.GetValue("preview", "auto_rotate", true);
            settings.AutoRotateSpeed = (float)config.GetValue("preview", "auto_rotate_speed", 25f);
            settings.UseModelCamera = (bool)config.GetValue("preview", "model_camera", false);
            settings.CameraFov = (float)config.GetValue("preview", "camera_fov", 45f);
            settings.ShowGround = (bool)config.GetValue("preview", "ground", true);
            settings.ReplaceTeamTextures = (bool)config.GetValue("preview", "replace_team_textures", true);
            settings.HideReplaceableShellGeosets = (bool)config.GetValue("preview", "hide_replaceable_shell", false);
            settings.ViewerStyleMaterialOrder = (bool)config.GetValue("preview", "viewer_material_order", false);
            settings.LastModel = (string)config.GetValue("preview", "last_model", string.Empty);
            settings.PreviewWindowSize = (Vector2I)config.GetValue("window", "size", DefaultPreviewWindowSize);
            settings.PreviewWindowPosition = (Vector2I)config.GetValue("window", "position", CenteredPreviewWindow);
            return settings;
        }

        static List<string> ReadList(ConfigFile config, string section, string key)
        {
            var list = new List<string>();
            if (!config.HasSectionKey(section, key)) return list;
            Variant value = config.GetValue(section, key);
            if (value.VariantType == Variant.Type.PackedStringArray)
            {
                foreach (string item in value.AsStringArray()) list.Add(item);
            }
            else if (value.VariantType == Variant.Type.String)
            {
                string single = value.AsString();
                if (!string.IsNullOrWhiteSpace(single)) list.Add(single);
            }
            return list;
        }

        /// <summary>确保 <c>user://</c> 目录存在（首次运行时它还没被创建，否则存盘会 FileCantOpen）。</summary>
        static void EnsureDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("user://", StringComparison.Ordinal)) return;
            if (!DirAccess.DirExistsAbsolute("user://")) DirAccess.MakeDirRecursiveAbsolute("user://");
        }

        public void Save(string path = DefaultPath)
        {
            EnsureDirectory(path);
            var config = new ConfigFile();
            config.SetValue("paths", "mpq", MpqPaths.ToArray());
            config.SetValue("paths", "blp_search", BlpSearchPaths.ToArray());
            config.SetValue("paths", "recent_models", RecentModels.ToArray());
            config.SetValue("export", "output_folder", OutputFolder);
            config.SetValue("export", "scale", ExportScale);
            config.SetValue("preview", "team_color", TeamColorIndex);
            config.SetValue("preview", "mipmaps", GenerateMipmaps);
            config.SetValue("preview", "particles", ShowParticles);
            config.SetValue("preview", "particle_brightness", ParticleBrightness);
            config.SetValue("preview", "particle_density", ParticleDensity);
            config.SetValue("preview", "ribbons", ShowRibbons);
            config.SetValue("preview", "lights", ShowLights);
            config.SetValue("preview", "attachments", ShowAttachments);
            config.SetValue("preview", "collision", ShowCollision);
            config.SetValue("preview", "bones", ShowBones);
            config.SetValue("preview", "auto_rotate", AutoRotate);
            config.SetValue("preview", "auto_rotate_speed", AutoRotateSpeed);
            config.SetValue("preview", "model_camera", UseModelCamera);
            config.SetValue("preview", "camera_fov", CameraFov);
            config.SetValue("preview", "ground", ShowGround);
            config.SetValue("preview", "replace_team_textures", ReplaceTeamTextures);
            config.SetValue("preview", "hide_replaceable_shell", HideReplaceableShellGeosets);
            config.SetValue("preview", "viewer_material_order", ViewerStyleMaterialOrder);
            config.SetValue("preview", "last_model", LastModel);
            config.SetValue("window", "size", PreviewWindowSize);
            config.SetValue("window", "position", PreviewWindowPosition);

            Error error = config.Save(path);
            if (error != Error.Ok) GD.PushWarning($"[MDX Preview] 保存设置失败: {path} ({error})");
        }
    }
}
