using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using MdxPreview.Core.Assets;
using MdxPreview.Core.Blp;
using MdxPreview.Core.Mdx;
using MdxPreview.Core.Mpq;

namespace MdxPreview.Runtime
{
    /// <summary>Warcraft III player colours (TeamColor / TeamGlow pairs).</summary>
    public sealed class MdxTeamColor
    {
        public MdxTeamColor(string name, Color color, Color glow)
        {
            Name = name;
            Color = color;
            Glow = glow;
        }

        public string Name { get; }
        public Color Color { get; }
        public Color Glow { get; }

        public static readonly MdxTeamColor[] All =
        {
            new MdxTeamColor("玩家 1 红", new Color(1.00f, 0.13f, 0.13f), new Color(0.60f, 0.05f, 0.05f)),
            new MdxTeamColor("玩家 2 蓝", new Color(0.13f, 0.30f, 1.00f), new Color(0.05f, 0.15f, 0.60f)),
            new MdxTeamColor("玩家 3 青", new Color(0.13f, 0.90f, 0.95f), new Color(0.05f, 0.45f, 0.55f)),
            new MdxTeamColor("玩家 4 紫", new Color(0.53f, 0.24f, 0.95f), new Color(0.25f, 0.10f, 0.55f)),
            new MdxTeamColor("玩家 5 黄", new Color(1.00f, 0.95f, 0.15f), new Color(0.60f, 0.55f, 0.05f)),
            new MdxTeamColor("玩家 6 橙", new Color(1.00f, 0.55f, 0.10f), new Color(0.60f, 0.28f, 0.03f)),
            new MdxTeamColor("玩家 7 绿", new Color(0.13f, 0.85f, 0.20f), new Color(0.05f, 0.45f, 0.08f)),
            new MdxTeamColor("玩家 8 粉", new Color(0.95f, 0.45f, 0.75f), new Color(0.55f, 0.20f, 0.40f)),
            new MdxTeamColor("玩家 9 灰", new Color(0.60f, 0.60f, 0.60f), new Color(0.30f, 0.30f, 0.30f)),
            new MdxTeamColor("玩家 10 浅蓝", new Color(0.50f, 0.80f, 1.00f), new Color(0.20f, 0.45f, 0.60f)),
            new MdxTeamColor("玩家 11 深绿", new Color(0.10f, 0.45f, 0.15f), new Color(0.04f, 0.22f, 0.07f)),
            new MdxTeamColor("玩家 12 棕", new Color(0.55f, 0.35f, 0.20f), new Color(0.28f, 0.16f, 0.08f)),
        };
    }

    /// <summary>
    /// Resolves and caches everything the preview needs from the configured game
    /// data: MPQ archives (game path), extra BLP search paths, MDX models and
    /// textures. Texture lookups follow the engine's rules — case-insensitive
    /// virtual paths plus the '.mdl'→'.mdx', '.tga'→'.blp' fallbacks.
    /// </summary>
    public sealed class MdxAssetLibrary : IDisposable
    {
        readonly List<MpqArchive> _archives = new List<MpqArchive>();
        readonly List<string> _archivePaths = new List<string>();
        readonly List<DirectoryFileSource> _directories = new List<DirectoryFileSource>();
        readonly List<DirectoryFileSource> _transientDirectories = new List<DirectoryFileSource>();
        readonly List<string> _texturePaths = new List<string>();
        readonly CompositeFileSource _sources = new CompositeFileSource();
        readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, MdxModel> _models = new Dictionary<string, MdxModel>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Texture2D> _opaqueTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _failureReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<uint, Texture2D> _replaceableOverrides = new Dictionary<uint, Texture2D>();

        AssetResolver _resolver = new AssetResolver(new CompositeFileSource());
        Texture2D _missingTexture;
        Texture2D _teamColorTexture;
        Texture2D _teamGlowTexture;
        Texture2D _whiteTexture;

        public MdxTeamColor TeamColor { get; set; } = MdxTeamColor.All[1];
        public bool GenerateMipmaps { get; set; } = true;
        /// <summary>
        /// Vertically flip decoded images before upload. Verified OFF (the default): MDX UVs
        /// use V=0 at the texture top and Godot's Image row 0 is the top row, so no flip is
        /// needed. Flipping sampled the wrong half of every texture (Peon's head showed the
        /// satchel, custom skins looked scrambled); keep the switch for odd assets.
        /// </summary>
        public bool FlipTexturesVertically { get; set; }

        /// <summary>When false, TeamColor/TeamGlow paths load the raw file (model-viewer style).</summary>
        public bool SubstituteReplaceablePaths { get; set; } = true;

        public IReadOnlyList<string> ArchivePaths => _archivePaths;
        public IReadOnlyList<string> TextureSearchPaths => _texturePaths;
        public IReadOnlyCollection<string> MissingAssets => _missing;
        public int TextureCount => _textures.Count;
        public int ModelCount => _models.Count;
        public bool HasSources => _sources.Sources.Count > 0;

        public bool IsReady => _sources.Sources.Count > 0;

        // ---- configuration ---------------------------------------------------------------

        /// <summary>Opens the given MPQ archives in priority order (front = highest).</summary>
        public string SetArchives(IEnumerable<string> archivePaths)
        {
            Reset();
            foreach (string path in archivePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    MpqArchive archive = MpqArchive.Open(path.Trim());
                    _archives.Add(archive);
                    _archivePaths.Add(path.Trim());
                    GD.Print($"[MDX Preview] MPQ 已打开: {archive.Name} ({archive.FileCount} 个文件)");
                }
                catch (Exception ex)
                {
                    Rebuild();
                    return $"打开 MPQ 失败: {path}\n{ex.Message}";
                }
            }
            Rebuild();
            return null;
        }

        public void SetTextureSearchPaths(IEnumerable<string> directories)
        {
            _texturePaths.Clear();
            _texturePaths.AddRange(directories);
            Rebuild();
        }

        void Rebuild()
        {
            _sources.Clear();
            foreach (DirectoryFileSource directory in _directories) _sources.Add(directory);
            foreach (MpqArchive archive in _archives) _sources.Add(new MpqFileSource(archive));
            _resolver = new AssetResolver(_sources);
        }

        /// <summary>Recreates the loose-directory sources after the list changed.</summary>
        public void RefreshDirectories()
        {
            _directories.Clear();
            foreach (string path in _texturePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    if (Directory.Exists(path)) _directories.Add(new DirectoryFileSource(path, path));
                    else if (File.Exists(path)) _directories.Add(new DirectoryFileSource(Path.GetDirectoryName(path), path));
                }
                catch (Exception ex)
                {
                    GD.PushWarning($"[MDX Preview] BLP 搜索路径不可用: {path} ({ex.Message})");
                }
            }
            Rebuild();
            _textures.Clear();
            _opaqueTextures.Clear();
        }

        /// <summary>
        /// Adds a folder as a transient lookup root (never persisted). Used when a model
        /// is opened from disk: the model's own folder and the folder above it are the
        /// usual locations of the textures it references (e.g. model in "zq\", textures
        /// in "jn\").
        /// </summary>
        public void AddTransientRoot(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(directory);
            }
            catch
            {
                return;
            }
            if (!System.IO.Directory.Exists(full)) return;
            foreach (DirectoryFileSource existing in _directories)
                if (string.Equals(existing.Root, full, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                int count = 0;
                foreach (string _ in System.IO.Directory.EnumerateFiles(full, "*", System.IO.SearchOption.AllDirectories))
                    if (++count > 60000) break;
                if (count > 60000)
                {
                    GD.PushWarning($"[MDX Preview] 目录文件过多，已跳过自动索引: {full}（请手动添加为 BLP 搜索路径）");
                    return;
                }
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 无法索引目录 {full}: {ex.Message}");
                return;
            }

            var source = new DirectoryFileSource(full, full);
            _directories.Insert(0, source);
            _transientDirectories.Add(source);
            Rebuild();
            GD.Print($"[MDX Preview] 已自动加入贴图搜索目录: {full}");
        }

        /// <summary>Drops the transient roots added by <see cref="AddTransientRoot"/>.</summary>
        public void ClearTransientRoots()
        {
            if (_transientDirectories.Count == 0) return;
            foreach (DirectoryFileSource source in _transientDirectories) _directories.Remove(source);
            _transientDirectories.Clear();
            Rebuild();
        }

        public void Reset()
        {
            foreach (MpqArchive archive in _archives)
            {
                try { archive.Dispose(); } catch { /* closing a broken archive is not fatal */ }
            }
            _archives.Clear();
            _archivePaths.Clear();
            _models.Clear();
            _textures.Clear();
            _opaqueTextures.Clear();
            _missing.Clear();
            _failed.Clear();
            _failureReasons.Clear();
        }

        public void Dispose()
        {
            Reset();
            _directories.Clear();
            _sources.Clear();
        }

        // ---- lookups ---------------------------------------------------------------------

        public bool TryReadFile(string virtualPath, out byte[] data)
            => _resolver.TryResolve(virtualPath, out data, out _);

        public bool Exists(string virtualPath) => _sources.Exists(VirtualPath.Normalize(virtualPath));

        /// <summary>Enumerates assets across all sources matching a mask such as "*.mdx".</summary>
        public IEnumerable<string> Enumerate(string mask) => _sources.Enumerate(mask);

        public MdxModel LoadModel(string virtualPath)
        {
            string key = VirtualPath.Normalize(virtualPath);
            if (_models.TryGetValue(key, out MdxModel cached)) return cached;

            if (!_resolver.TryResolve(key, out byte[] data, out string resolved))
            {
                RecordMissing(key);
                return null;
            }

            MdxModel model = MdxLoader.Load(data, resolved);
            _models[key] = model;
            return model;
        }

        public Texture2D LoadTexture(string path, uint replaceableId = 0)
        {
            // Runtime equipment slots win over every source lookup: WC3's SetUnitTexture
            // (ids 31..47 on the character models, 41/42 on weapons, 99 for hair colour)
            // repoints a model texture id at another file without touching the model.
            if (replaceableId != 0 && _replaceableOverrides.TryGetValue(replaceableId, out Texture2D overrideTexture))
                return overrideTexture;

            // The engine never loads "ReplaceableTextures\TeamColor\TeamColorNN.blp"
            // or TeamGlowNN.blp from an archive: it substitutes a player-coloured
            // texture for those paths (mdxtool reports them as "not found" for that
            // reason). Models such as custom unit skins rely on it, so classify by
            // path here as well instead of handing out the raw mask file.
            if (SubstituteReplaceablePaths && !string.IsNullOrWhiteSpace(path) && ClassifyReplaceablePath(path, out uint pathKind))
            {
                return pathKind == 2 ? GetTeamGlowTexture() : GetTeamColorTexture();
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                string key = VirtualPath.Normalize(path);
                if (_textures.TryGetValue(key, out Texture2D cached)) return cached;
                // A decode failure is deterministic: remember it so the warning is emitted
                // once per asset instead of once per material rebuild.
                if (_failed.Contains(key)) return GetMissingTexture();

                Texture2D texture = DecodeTexture(key);
                if (texture != null)
                {
                    _textures[key] = texture;
                    return texture;
                }
                if (replaceableId == 0)
                {
                    RecordMissing(key);
                    return GetMissingTexture();
                }
            }

            if (replaceableId == 1) return GetTeamColorTexture();
            if (replaceableId == 2) return GetTeamGlowTexture();
            return GetWhiteTexture();
        }

        /// <summary>
        /// Equipment slots (WC3 replaceable-texture ids 31..47) are switched at runtime by
        /// pointing a model texture id at a different file. Warcraft exposes that as
        /// SetUnitTexture; here the owner registers the override and rebuilds materials.
        /// Passing null removes the override.
        /// </summary>
        public void SetReplaceableOverride(uint id, Texture2D texture)
        {
            if (texture == null) _replaceableOverrides.Remove(id);
            else _replaceableOverrides[id] = texture;
            _opaqueTextures.Clear();
        }

        public void ClearReplaceableOverrides()
        {
            if (_replaceableOverrides.Count == 0) return;
            _replaceableOverrides.Clear();
            _opaqueTextures.Clear();
        }

        public int ReplaceableOverrideCount => _replaceableOverrides.Count;

        public bool TryGetReplaceableOverride(uint id, out Texture2D texture)
            => _replaceableOverrides.TryGetValue(id, out texture);

        /// <summary>
        /// Recognises the engine's replaceable texture paths. Returns 1 for team
        /// colour and 2 for team glow (matching TEXREPL_TEAMCOLOR / TEXREPL_TEAMGLOW).
        /// </summary>
        public static bool ClassifyReplaceablePath(string path, out uint kind)
        {
            string normalized = VirtualPath.Normalize(path).ToLowerInvariant();
            if (normalized.StartsWith("replaceabletextures\\teamcolor\\", StringComparison.Ordinal))
            {
                kind = 1;
                return true;
            }
            if (normalized.StartsWith("replaceabletextures\\teamglow\\", StringComparison.Ordinal))
            {
                kind = 2;
                return true;
            }
            kind = 0;
            return false;
        }

        /// <summary>
        /// Variant of a texture whose alpha is forced to 255. MDX layers with filter 0
        /// (opaque) ignore the texture's alpha in the engine; because layers are chained
        /// with Material.NextPass, a surface whose first layer is translucent renders the
        /// whole stack in the transparent pass, so the opaque layer must drop its alpha
        /// or the model looks washed out.
        /// </summary>
        public Texture2D LoadOpaqueTexture(string path, uint replaceableId = 0)
        {
            string key = VirtualPath.Normalize(path) + "#opaque@" + replaceableId;
            if (_opaqueTextures.TryGetValue(key, out Texture2D cached)) return cached;

            Texture2D source = LoadTexture(path, replaceableId);
            if (source == null) return null;

            Image image = source.GetImage();
            if (image == null) return source;
            if (image.IsCompressed()) image.Decompress();
            image.Convert(Image.Format.Rgba8);

            byte[] data = image.GetData();
            for (int i = 3; i < data.Length; i += 4)
                if (data[i] != 255) data[i] = 255;

            Image opaque = Image.CreateFromData(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8, data);
            var texture = ImageTexture.CreateFromImage(opaque);
            _opaqueTextures[key] = texture;
            return texture;
        }

        Texture2D DecodeTexture(string virtualPath)

        {
            if (!_resolver.TryResolve(virtualPath, out byte[] data, out string resolved))
            {
                // WC3 stores team-colour/replaceable masks under well known names.
                return null;
            }

            try
            {
                string extension = VirtualPath.GetExtension(resolved);
                Image image = extension switch
                {
                    ".blp" => DecodeBlp(data),
                    ".tga" => LoadViaGodot(data, ImageFormat.Tga),
                    ".png" => LoadViaGodot(data, ImageFormat.Png),
                    ".jpg" or ".jpeg" => LoadViaGodot(data, ImageFormat.Jpeg),
                    ".dds" => LoadViaGodot(data, ImageFormat.Dds),
                    ".bmp" => LoadViaGodot(data, ImageFormat.Bmp),
                    ".webp" => LoadViaGodot(data, ImageFormat.Webp),
                    _ => DecodeBlp(data),
                };

                if (image == null)
                {
                    // The file exists but its content could not be decoded; report it
                    // separately from "file not found" so the info panel stays honest.
                    RecordFailure(resolved, "解码返回空图像");
                    return null;
                }
                if (FlipTexturesVertically) image.FlipY();
                if (GenerateMipmaps) image.GenerateMipmaps();
                var texture = ImageTexture.CreateFromImage(image);
                texture.SetMeta("mdx_source", resolved);
                return texture;
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 贴图解码失败 {resolved}: {ex.Message}");
                RecordFailure(resolved, ex.Message);
                return null;
            }
        }

        enum ImageFormat
        {
            Tga, Png, Jpeg, Dds, Bmp, Webp,
        }

        static Image LoadViaGodot(byte[] data, ImageFormat format)
        {
            var image = new Image();
            Error error = format switch
            {
                ImageFormat.Tga => image.LoadTgaFromBuffer(data),
                ImageFormat.Png => image.LoadPngFromBuffer(data),
                ImageFormat.Jpeg => image.LoadJpgFromBuffer(data),
                ImageFormat.Dds => image.LoadDdsFromBuffer(data),
                ImageFormat.Bmp => image.LoadBmpFromBuffer(data),
                ImageFormat.Webp => image.LoadWebpFromBuffer(data),
                _ => Error.Unavailable,
            };
            return error == Error.Ok ? image : null;
        }

        static Image DecodeBlp(byte[] data)
        {
            BlpImage blp = BlpImage.Decode(data);
            if (blp.Kind == BlpPayloadKind.Jpeg)
            {
                if (blp.JpegData == null || blp.JpegData.Length == 0) return null;
                var jpegImage = new Image();
                if (jpegImage.LoadJpgFromBuffer(blp.JpegData) == Error.Ok) return jpegImage;

                // Warcraft III BLP1 JPEG content is a single 4-component baseline
                // JPEG (raw BGRA bands) that libjpeg rejects as CMYK/YCCK. The core
                // decoder handles those; reaching this branch means the band count
                // was not recognised, so report it instead of silently going magenta.
                GD.PushWarning($"[MDX Preview] BLP1 JPEG 解码失败（分量数={blp.JpegBandCount}, " +
                               $"alphaDepth={blp.RawAlphaDepth}）：Godot 的 JPEG 解码器不支持该类文件");
                return null;
            }
            if (blp.Rgba == null) return null;
            return Image.CreateFromData(blp.Width, blp.Height, false, Image.Format.Rgba8, blp.Rgba);
        }

        // ---- generated fallbacks ---------------------------------------------------------

        public Texture2D GetWhiteTexture()
            => _whiteTexture ??= CreateSolidTexture(Colors.White);

        /// <summary>
        /// Flat texture used for team-colour layers without a real mask; the
        /// material supplies the player colour so both paths tint the same way.
        /// </summary>
        public Texture2D GetTeamColorTexture() => _teamColorTexture ??= CreateSolidTexture(Colors.White);

        public Texture2D GetTeamGlowTexture() => _teamGlowTexture ??= CreateSolidTexture(Colors.White);

        static Texture2D CreateSolidTexture(Color color)
        {
            var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            image.Fill(color);
            return ImageTexture.CreateFromImage(image);
        }

        public Texture2D GetMissingTexture()
        {
            if (_missingTexture != null) return _missingTexture;
            const int size = 32;
            var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool checker = ((x / 8) + (y / 8)) % 2 == 0;
                    image.SetPixel(x, y, checker ? new Color(1f, 0f, 1f) : new Color(0.15f, 0.15f, 0.15f));
                }
            _missingTexture = ImageTexture.CreateFromImage(image);
            return _missingTexture;
        }

        void RecordMissing(string path)
        {
            if (_missing.Add(path))
                GD.PushWarning($"[MDX Preview] 找不到资源: {path}");
        }

        /// <summary>File found but its content could not be decoded (kept apart from "missing").</summary>
        void RecordFailure(string path, string reason)
        {
            if (_failed.Add(path))
            {
                _failureReasons[path] = reason;
                GD.PushWarning($"[MDX Preview] 资源无法解码: {path} — {reason}");
            }
        }

        /// <summary>Assets that exist but failed to decode, with the reason (info panel).</summary>
        public IReadOnlyDictionary<string, string> FailedAssets => _failureReasons;

        /// <summary>Drops cached textures so a team-colour change is picked up.</summary>
        public void InvalidateReplaceableTextures()
        {
            _teamColorTexture = null;
            _teamGlowTexture = null;
        }
    }
}
