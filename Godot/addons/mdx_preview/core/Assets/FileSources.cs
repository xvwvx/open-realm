using System;
using System.Collections.Generic;
using System.IO;

namespace MdxPreview.Core.Assets
{
    /// <summary>
    /// A source of game files addressed by Warcraft-style virtual paths
    /// (for example <c>Units\Orc\Peon\Peon.blp</c>). Implementations exist for
    /// MPQ archives and for loose directories (the configurable BLP search paths).
    /// Lookups are case-insensitive and accept both '\' and '/' separators.
    /// </summary>
    public interface IFileSource
    {
        /// <summary>Human readable name used in the UI ("war3.mpq", "D:\textures").</summary>
        string Name { get; }

        bool TryRead(string virtualPath, out byte[] data);

        bool Exists(string virtualPath);

        /// <summary>
        /// Files whose virtual path matches the given mask (may contain '*').
        /// Sources that cannot enumerate return an empty sequence.
        /// </summary>
        IEnumerable<string> Enumerate(string mask);
    }

    public static class VirtualPath
    {
        /// <summary>
        /// Canonical form used for every lookup: '\' separators, no leading
        /// separator, no "./" segments. Case is preserved; comparisons are
        /// case-insensitive (Warcraft archives are case-insensitive).
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string p = path.Replace('/', '\\').Trim();
            while (p.StartsWith(".\\", StringComparison.Ordinal)) p = p.Substring(2);
            while (p.StartsWith("\\", StringComparison.Ordinal)) p = p.Substring(1);
            return p;
        }

        public static string GetFileName(string path)
        {
            string p = Normalize(path);
            int i = p.LastIndexOf('\\');
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        public static string GetDirectory(string path)
        {
            string p = Normalize(path);
            int i = p.LastIndexOf('\\');
            return i >= 0 ? p.Substring(0, i) : string.Empty;
        }

        public static string GetExtension(string path)
        {
            string name = GetFileName(path);
            int i = name.LastIndexOf('.');
            return i >= 0 ? name.Substring(i).ToLowerInvariant() : string.Empty;
        }

        public static string ChangeExtension(string path, string extension)
        {
            string p = Normalize(path);
            int i = p.LastIndexOf('.');
            int slash = p.LastIndexOf('\\');
            if (i > slash) p = p.Substring(0, i);
            if (!extension.StartsWith(".", StringComparison.Ordinal)) extension = "." + extension;
            return p + extension;
        }

        public static string Combine(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return Normalize(b);
            if (string.IsNullOrEmpty(b)) return Normalize(a);
            return Normalize(a) + "\\" + Normalize(b);
        }

        /// <summary>
        /// Glob match with '*' (any run) and '?' (one character), case-insensitive.
        /// A single '*' is the common case, but masks such as
        /// "*ziggurat*missile*.mdx" appear in the UI search box, so handle any number.
        /// </summary>
        public static bool Matches(string virtualPath, string mask)
        {
            string path = Normalize(virtualPath).ToLowerInvariant();
            string pattern = Normalize(mask).ToLowerInvariant();
            if (pattern.Length == 0 || pattern == "*") return true;
            return GlobMatch(path, pattern, 0, 0);
        }

        static bool GlobMatch(string path, string pattern, int p, int m)
        {
            while (m < pattern.Length)
            {
                char token = pattern[m];
                if (token == '*')
                {
                    // Collapse consecutive stars, then try every split point.
                    while (m < pattern.Length && pattern[m] == '*') m++;
                    if (m == pattern.Length) return true;
                    for (int skip = p; skip <= path.Length; skip++)
                        if (GlobMatch(path, pattern, skip, m)) return true;
                    return false;
                }
                if (p >= path.Length) return false;
                if (token != '?' && token != path[p]) return false;
                p++;
                m++;
            }
            return p == path.Length;
        }
    }

    /// <summary>Loose files under a directory (an extracted BLP/asset folder).</summary>
    public sealed class DirectoryFileSource : IFileSource
    {
        readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public DirectoryFileSource(string root, string name = null)
        {
            Root = Path.GetFullPath(root);
            Name = name ?? Root;
            Index();
        }

        public string Root { get; }

        public string Name { get; }

        void Index()
        {
            _map.Clear();
            if (!Directory.Exists(Root)) return;
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(Root, file).Replace('/', '\\');
                _map[rel] = file;
            }
        }

        public bool TryRead(string virtualPath, out byte[] data)
        {
            data = null;
            string key = VirtualPath.Normalize(virtualPath);
            if (!_map.TryGetValue(key, out string file))
            {
                // Directory sources also satisfy bare-name lookups ("Peon.blp").
                if (!_map.TryGetValue(VirtualPath.GetFileName(key), out file)) return false;
            }
            data = File.ReadAllBytes(file);
            return true;
        }

        public bool Exists(string virtualPath) => TryRead(virtualPath, out _);

        public IEnumerable<string> Enumerate(string mask)
        {
            foreach (string key in _map.Keys)
                if (VirtualPath.Matches(key, mask)) yield return key;
        }
    }

    /// <summary>In-memory source used by tests and by exported/baked assets.</summary>
    public sealed class MemoryFileSource : IFileSource
    {
        readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public MemoryFileSource(string name = "<memory>") { Name = name; }

        public string Name { get; }

        public void Add(string virtualPath, byte[] data) => _files[VirtualPath.Normalize(virtualPath)] = data;

        public bool TryRead(string virtualPath, out byte[] data)
            => _files.TryGetValue(VirtualPath.Normalize(virtualPath), out data);

        public bool Exists(string virtualPath) => _files.ContainsKey(VirtualPath.Normalize(virtualPath));

        public IEnumerable<string> Enumerate(string mask)
        {
            foreach (string key in _files.Keys)
                if (VirtualPath.Matches(key, mask)) yield return key;
        }
    }

    /// <summary>
    /// Ordered list of sources consulted front to back. The plugin fills this in
    /// as: user BLP search paths (in order) then the configured MPQ archives.
    /// </summary>
    public sealed class CompositeFileSource : IFileSource
    {
        readonly List<IFileSource> _sources = new List<IFileSource>();

        public string Name => "composite";

        public IReadOnlyList<IFileSource> Sources => _sources;

        public void Add(IFileSource source)
        {
            if (source != null) _sources.Add(source);
        }

        public void Clear() => _sources.Clear();

        public bool TryRead(string virtualPath, out byte[] data)
        {
            foreach (IFileSource source in _sources)
                if (source.TryRead(virtualPath, out data)) return true;
            data = null;
            return false;
        }

        public bool Exists(string virtualPath)
        {
            foreach (IFileSource source in _sources)
                if (source.Exists(virtualPath)) return true;
            return false;
        }

        public IEnumerable<string> Enumerate(string mask)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IFileSource source in _sources)
                foreach (string path in source.Enumerate(mask))
                    if (seen.Add(path)) yield return path;
        }
    }

    /// <summary>
    /// Warcraft asset lookup: exact path, then the engine's documented fallbacks
    /// ('.mdl' resolves to '.mdx', '.tga' resolves to '.blp', an extensionless
    /// model stem resolves to '.mdx').
    /// </summary>
    public sealed class AssetResolver
    {
        static readonly string[] ModelFallbacks = { ".mdx", ".mdl" };
        static readonly string[] TextureFallbacks = { ".blp", ".tga", ".png", ".jpg", ".jpeg", ".dds" };

        public AssetResolver(IFileSource source) { Source = source; }

        public IFileSource Source { get; set; }

        /// <summary>Every candidate path tried for a lookup, in order (diagnostics).</summary>
        public List<string> LastCandidates { get; } = new List<string>();

        public bool TryResolve(string virtualPath, out byte[] data, out string resolvedPath)
        {
            LastCandidates.Clear();
            string path = VirtualPath.Normalize(virtualPath);
            foreach (string candidate in Candidates(path))
            {
                LastCandidates.Add(candidate);
                if (Source.TryRead(candidate, out data))
                {
                    resolvedPath = candidate;
                    return true;
                }
            }
            data = null;
            resolvedPath = null;
            return false;
        }

        public byte[] Resolve(string virtualPath)
        {
            return TryResolve(virtualPath, out byte[] data, out _) ? data : null;
        }

        IEnumerable<string> Candidates(string path)
        {
            if (path.Length == 0) yield break;
            yield return path;

            string ext = VirtualPath.GetExtension(path);
            string[] fallbacks = ext switch
            {
                ".mdl" => ModelFallbacks,
                ".mdx" => ModelFallbacks,
                ".tga" => TextureFallbacks,
                ".blp" => TextureFallbacks,
                ".dds" => TextureFallbacks,
                ".png" => TextureFallbacks,
                ".jpg" => TextureFallbacks,
                "" => ModelFallbacks,
                _ => Array.Empty<string>(),
            };

            foreach (string candidateExt in fallbacks)
            {
                if (string.Equals(candidateExt, ext, StringComparison.OrdinalIgnoreCase)) continue;
                yield return VirtualPath.ChangeExtension(path, candidateExt);
            }
        }
    }
}
