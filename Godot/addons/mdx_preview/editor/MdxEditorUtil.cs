using System;
using System.Collections.Generic;
using Godot;

namespace MdxPreview.EditorUi
{
    /// <summary>
    /// 编辑器 UI 共用的小工具：把编辑器/系统的拖拽数据解析成路径列表、
    /// 判断扩展名与路径展示等。纯静态，不持有任何状态。
    /// </summary>
    internal static class MdxEditorUtil
    {
        public const string ModelFilter = "*.mdx, *.mdl ; MDX/MDL 模型";
        public const string MpqFilter = "*.mpq ; MPQ 归档";

        /// <summary>
        /// 从拖拽数据里提取文件/目录路径。兼容以下形式：
        /// 编辑器 FileSystem 的 Dictionary（files / strings / resource）、
        /// 系统文件管理器的 PackedStringArray、单个 String、Resource 对象。
        /// </summary>
        public static string[] ExtractDropPaths(Variant data)
        {
            var found = new List<string>();
            try
            {
                Collect(data, found, 0);
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 解析拖拽数据失败: {ex.Message}");
            }

            var result = new List<string>();
            foreach (string path in found)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (result.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(path);
            }
            return result.ToArray();
        }

        static void Collect(Variant value, List<string> output, int depth)
        {
            if (depth > 4) return;

            switch (value.VariantType)
            {
                case Variant.Type.Nil:
                    return;
                case Variant.Type.String:
                case Variant.Type.StringName:
                case Variant.Type.NodePath:
                    output.Add(value.AsString());
                    return;
                case Variant.Type.PackedStringArray:
                    output.AddRange(value.AsStringArray());
                    return;
                case Variant.Type.Array:
                    foreach (Variant item in value.AsGodotArray()) Collect(item, output, depth + 1);
                    return;
                case Variant.Type.Dictionary:
                {
                    Godot.Collections.Dictionary dictionary = value.AsGodotDictionary();
                    foreach (Variant key in dictionary.Keys)
                    {
                        // 只关心字符串型键，避免把任意结构体值也当成路径。
                        if (key.VariantType != Variant.Type.String) continue;
                        Collect(dictionary[key], output, depth + 1);
                    }
                    return;
                }
                case Variant.Type.Object:
                {
                    var resource = value.As<Resource>();
                    if (resource != null && !string.IsNullOrWhiteSpace(resource.ResourcePath))
                        output.Add(resource.ResourcePath);
                    return;
                }
            }
        }

        public static bool IsModelPath(string path)
            => !string.IsNullOrWhiteSpace(path)
               && (path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));

        public static bool IsMpqPath(string path)
            => !string.IsNullOrWhiteSpace(path) && path.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase);

        /// <summary>把 res:// / user:// 路径转成本机路径，其它路径原样返回。</summary>
        public static string ToDiskPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            return path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
                ? ProjectSettings.GlobalizePath(path)
                : path;
        }

        public static bool IsDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return System.IO.Directory.Exists(ToDiskPath(path)); }
            catch { return false; }
        }

        public static bool IsFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return System.IO.File.Exists(ToDiskPath(path)); }
            catch { return false; }
        }

        /// <summary>取某个文件的所在目录（用于 FileDialog 的起始目录）。</summary>
        public static string DirectoryOf(string fileOrDir)
        {
            if (string.IsNullOrWhiteSpace(fileOrDir)) return string.Empty;
            try
            {
                string disk = ToDiskPath(fileOrDir);
                if (System.IO.Directory.Exists(disk)) return disk;
                return System.IO.Path.GetDirectoryName(disk) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        /// <summary>列表里显示用的短路径（过长时省略中间）。</summary>
        public static string Shorten(string path, int max = 56)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= max) return path;
            int keep = Math.Max(4, (max - 3) / 2);
            return path.Substring(0, keep) + "..." + path.Substring(path.Length - keep);
        }
    }
}
