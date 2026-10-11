using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace MdxPreview.Export
{
    /// <summary>
    /// Path plumbing for the exporter: it accepts <c>res://</c>, <c>user://</c> and
    /// plain absolute/relative file-system folders and always answers with both
    /// forms (the resource path for Godot APIs, the global path for
    /// <see cref="System.IO"/> and <c>Image.SavePng</c>).
    ///
    /// The plugin never writes outside the folder it was handed, so a caller that
    /// passes <c>res://Godot</c> keeps every artifact inside the project.
    /// </summary>
    public static class MdxExportPaths
    {
        /// <summary>Set of characters Godot's own path handling expects to be escaped.</summary>
        static readonly char[] IllegalFileNameChars =
            { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0', '\r', '\n', '\t' };

        public static bool IsResPath(string path)
            => !string.IsNullOrEmpty(path)
               && (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("user://", StringComparison.OrdinalIgnoreCase));

        /// <summary>True for a Windows drive path or a UNC share.</summary>
        public static bool IsAbsoluteFileSystemPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        }

        /// <summary>Resource path when the folder lives inside res:// or user://.</summary>
        public static string ToResourcePath(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return string.Empty;
            if (IsResPath(folder)) return NormalizeResourcePath(folder);
            if (IsAbsoluteFileSystemPath(folder) || Path.IsPathRooted(folder))
                return ProjectSettings.LocalizePath(NormalizeFileSystemPath(folder));
            // A bare relative folder is interpreted relative to the project root.
            return NormalizeResourcePath("res://" + folder.TrimStart('.', '/', '\\'));
        }

        /// <summary>Global (native) path for the same folder, usable by System.IO.</summary>
        public static string ToGlobalPath(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return string.Empty;
            if (IsResPath(folder)) return ProjectSettings.GlobalizePath(NormalizeResourcePath(folder));
            return NormalizeFileSystemPath(folder);
        }

        public static string NormalizeResourcePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string result = path.Replace('\\', '/');
            bool rooted = result.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                          || result.StartsWith("user://", StringComparison.OrdinalIgnoreCase);
            while (result.Length > "res://".Length && result.EndsWith("/", StringComparison.Ordinal))
                result = result.Substring(0, result.Length - 1);
            return rooted ? result : result;
        }

        public static string NormalizeFileSystemPath(string path)
            => string.IsNullOrEmpty(path) ? path : path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        /// <summary>Joins a resource folder with a file name ("res://a" + "b.png").</summary>
        public static string CombineResource(string folder, string fileName)
            => $"{NormalizeResourcePath(folder).TrimEnd('/')}/{fileName}";

        public static string CombineGlobal(string folder, string fileName)
            => Path.Combine(ToGlobalPath(folder), fileName);

        /// <summary>
        /// Creates the folder (and every parent) wherever it lives. <c>res://</c>
        /// paths only exist on disk while the project is not exported, which is the
        /// situation the plugin's export runs in.
        /// </summary>
        public static Error MakeDirectory(string folder, out string globalPath)
        {
            globalPath = ToGlobalPath(folder);
            if (string.IsNullOrEmpty(globalPath)) return Error.InvalidParameter;

            Error error = IsResPath(folder)
                ? DirAccess.MakeDirRecursiveAbsolute(NormalizeResourcePath(folder))
                : DirAccess.MakeDirRecursiveAbsolute(globalPath);
            if (error != Error.Ok && error != Error.AlreadyExists) return error;

            // res:// also has to exist on disk for Image.SavePng and ResourceSaver.
            try { Directory.CreateDirectory(globalPath); }
            catch (Exception) { return Error.CantCreate; }
            return Directory.Exists(globalPath) ? Error.Ok : Error.CantCreate;
        }

        /// <summary>
        /// Turns an arbitrary model/texture name into something usable both as a
        /// Godot node name and as a file name: illegal characters become '_' and
        /// runs of underscores are collapsed.
        /// </summary>
        public static string Sanitize(string name, string fallback = "model")
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            var buffer = new System.Text.StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                bool illegal = Array.IndexOf(IllegalFileNameChars, c) >= 0 || char.IsControl(c);
                char mapped = illegal ? '_' : c;
                if (mapped == '_' && buffer.Length > 0 && buffer[buffer.Length - 1] == '_') continue;
                buffer.Append(mapped);
            }
            string result = buffer.ToString().Trim('_', ' ', '.');
            if (result.Length == 0) return fallback;
            // Windows reserved device names would make file creation fail.
            if (IsReservedDeviceName(result)) result = "_" + result;
            return result;
        }

        static bool IsReservedDeviceName(string name)
        {
            string upper = name.ToUpperInvariant();
            switch (upper)
            {
                case "CON": case "PRN": case "AUX": case "NUL":
                case "COM1": case "COM2": case "COM3": case "COM4": case "COM5":
                case "COM6": case "COM7": case "COM8": case "COM9":
                case "LPT1": case "LPT2": case "LPT3": case "LPT4": case "LPT5":
                case "LPT6": case "LPT7": case "LPT8": case "LPT9":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// File name for an exported texture: the original virtual path with its
        /// separators and illegal characters flattened, so
        /// <c>units\orc\Peon\Peon.blp</c> becomes <c>units_orc_Peon_Peon.png</c>.
        /// </summary>
        public static string TextureFileName(string virtualPath, uint replaceableId)
        {
            if (replaceableId != 0 && string.IsNullOrWhiteSpace(virtualPath))
                return replaceableId == 1 ? "Replaceable_TeamColor.png"
                     : replaceableId == 2 ? "Replaceable_TeamGlow.png"
                     : $"Replaceable_{replaceableId}.png";

            string normalized = (virtualPath ?? string.Empty).Replace('\\', '/').Trim('/');
            if (normalized.Length == 0) return $"texture_{replaceableId}.png";

            string directory = string.Empty;
            string file = normalized;
            int slash = normalized.LastIndexOf('/');
            if (slash >= 0)
            {
                directory = normalized.Substring(0, slash);
                file = normalized.Substring(slash + 1);
            }

            string extension = Path.GetExtension(file);
            string stem = extension.Length > 0 ? file.Substring(0, file.Length - extension.Length) : file;
            string prefix = Sanitize(directory.Replace('/', '_'), string.Empty);
            string body = Sanitize(stem, "texture");
            string result = prefix.Length > 0 ? $"{prefix}_{body}" : body;
            if (replaceableId != 0) result = $"{result}_r{replaceableId}";
            return Sanitize(result, "texture") + ".png";
        }

        /// <summary>Stable short hash, used only to disambiguate colliding file names.</summary>
        public static string ShortHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (char c in text ?? string.Empty)
                {
                    hash ^= c;
                    hash *= 16777619u;
                }
                return hash.ToString("x8");
            }
        }

        /// <summary>Adds a unique suffix when <paramref name="fileName"/> is taken.</summary>
        public static string UniqueFileName(string fileName, Dictionary<string, string> used, string key)
        {
            string candidate = fileName;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int counter = 1;
            while (used.TryGetValue(candidate, out string owner) && !string.Equals(owner, key, StringComparison.Ordinal))
            {
                candidate = $"{stem}_{counter}{extension}";
                counter++;
                if (counter > 4096)
                {
                    candidate = $"{stem}_{ShortHash(key)}{extension}";
                    break;
                }
            }
            used[candidate] = key;
            return candidate;
        }

        /// <summary>Byte length of a file, or -1 when it does not exist.</summary>
        public static long FileSize(string globalPath)
        {
            try { return File.Exists(globalPath) ? new FileInfo(globalPath).Length : -1L; }
            catch (Exception) { return -1L; }
        }
    }
}
