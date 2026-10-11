using System;
using System.Collections.Generic;

namespace MdxPreview.Core.Sheet
{
    /// <summary>
    /// Pure data logic for the MDX event-object consumers, ported from
    /// games/warcraft-3/renderer/r_game.c. No Godot types, so the plain-console
    /// probes can verify it without an editor run.
    /// </summary>
    public static class MdxEventData
    {
        // ---- event family / row key ------------------------------------------------------

        /// <summary>Event families with a runtime consumer (R_W3EventFamily).</summary>
        public static readonly string[] Families = { "SND", "SPN", "SPL", "FPT", "UBR" };

        /// <summary>
        /// R_W3EventFamily: the first three name characters select the family.
        /// <paramref name="declaredType"/> is the loader's already-resolved prefix and
        /// wins when it names a known family.
        /// </summary>
        public static string FamilyPrefix(string nodeName, string declaredType)
        {
            if (!string.IsNullOrEmpty(declaredType))
            {
                string declared = declaredType.Trim().ToUpperInvariant();
                foreach (string family in Families)
                    if (declared == family) return family;
            }
            string raw = RawPrefix(nodeName);
            foreach (string family in Families)
                if (raw == family) return family;
            return null;
        }

        /// <summary>First three characters of the event node name, upper-cased.</summary>
        public static string RawPrefix(string nodeName)
        {
            string name = nodeName ?? string.Empty;
            return name.Length >= 3 ? name.Substring(0, 3).ToUpperInvariant() : name.ToUpperInvariant();
        }

        /// <summary>
        /// MDLX_EventObjectId: the row key is everything after the three-character
        /// family prefix and the fourth separator character, with surrounding
        /// whitespace trimmed. The reference requires a name longer than four
        /// characters, so `SPNx` has no key while `SPNx A` and `SPNxSomeSpawn` do.
        /// </summary>
        public static bool EventObjectId(string nodeName, string family, out string id)
        {
            id = null;
            if (string.IsNullOrEmpty(family)) return false;

            string name = nodeName ?? string.Empty;
            if (name.Length <= 4) return false;
            if (!name.StartsWith(family, StringComparison.OrdinalIgnoreCase)) return false;
            id = name.Substring(4).Trim();
            return id.Length > 0;
        }

        // ---- SND path --------------------------------------------------------------------

        /// <summary>R_W3SoundVariantCount / R_W3SoundPath: comma-separated variants.</summary>
        public static List<string> SplitVariants(string files)
        {
            var variants = new List<string>();
            if (string.IsNullOrWhiteSpace(files)) return variants;
            foreach (string part in files.Split(','))
                if (!string.IsNullOrWhiteSpace(part)) variants.Add(part.Trim());
            return variants;
        }

        /// <summary>
        /// R_W3SoundPath: `DirectoryBase` joined with the picked variant. The
        /// reference adds a separator only when the base does not already end in
        /// one, and it does not touch the extension -- `AnimSounds.slk` entries such
        /// as `step.wav` carry their own.
        /// </summary>
        public static string CombineSoundPath(string directory, string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            if (string.IsNullOrWhiteSpace(directory)) return file.Trim();
            string trimmed = directory.Trim();
            char last = trimmed[trimmed.Length - 1];
            string separator = last == '\\' || last == '/' ? string.Empty : "\\";
            return trimmed + separator + file.Trim();
        }

        /// <summary>R_W3PresentationPick: deterministic variant hash.</summary>
        public static int PresentationPick(uint entity, uint key, int time, int count)
        {
            if (count <= 0) return 0;
            uint x = entity * 0x9e3779b9u ^ key * 0x85ebca6bu ^ (uint)time;
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            return (int)(x % (uint)count);
        }

        // ---- splat atlas / colour --------------------------------------------------------

        /// <summary>
        /// R_W3SplatAtlasFrame: normalized phase progress inside the authored
        /// inclusive frame range, honouring a reversed (start &gt; end) range.
        /// </summary>
        public static int AtlasFrame(int start, int end, float progress)
        {
            int lo = Math.Min(start, end), hi = Math.Max(start, end);
            long count = hi - lo + 1;
            if (count <= 1) return start;
            progress = Math.Clamp(progress, 0f, 1f);
            long step = Math.Min(count - 1, (long)Math.Floor((double)progress * count));
            return start <= end ? (int)(start + step) : (int)(start - step);
        }

        /// <summary>
        /// R_W3SplatAtlasUV: one clamped atlas frame -> its UV rectangle. Cells are
        /// numbered left-to-right, top-to-bottom from zero, and V grows downward from
        /// the atlas top (the same convention the particle sprites use).
        /// </summary>
        public static void AtlasUv(int rows, int columns, int frame, out float minU, out float minV, out float maxU, out float maxV)
        {
            if (rows < 1) rows = 1;
            if (columns < 1) columns = 1;
            long total = (long)rows * columns;
            long index = Math.Clamp(frame, 0, total - 1);
            int column = (int)(index % columns);
            int atlasRow = (int)(index / columns);
            float invColumns = 1f / columns;
            float invRows = 1f / rows;
            minU = column * invColumns;
            minV = atlasRow * invRows;
            maxU = (column + 1) * invColumns;
            maxV = (atlasRow + 1) * invRows;
        }

        /// <summary>
        /// R_W3SplatChannel: authored channels are 0..255, but a value at or below
        /// 1.0 is treated as an already normalized fraction.
        /// </summary>
        public static float SplatChannel(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            if (value <= 1f) value *= 255f;
            return Math.Clamp(value, 0f, 255f) / 255f;
        }

        /// <summary>R_W3SplatHasSound: a NULL/-/_ placeholder is not a sound.</summary>
        public static bool HasSound(string sound)
        {
            if (string.IsNullOrWhiteSpace(sound)) return false;
            return !sound.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                   && sound != "-" && sound != "_";
        }

        /// <summary>
        /// R_W3WarnUnsupportedSplatFields: repeat counts, a non-default blend mode,
        /// water placement and a row sound are parsed but not applied by the preview.
        /// </summary>
        public static bool HasUnsupportedSplatFields(SlkRow row)
        {
            if (row == null) return false;
            string blend = row.GetStringOrNull("BlendMode");
            bool unsupportedBlend = !string.IsNullOrWhiteSpace(blend) && blend != "0";
            return row.GetInt("LifespanRepeat", 0) != 0
                   || row.GetInt("UVDecayRepeat", 0) != 0
                   || row.GetInt("Water", 0) != 0
                   || HasSound(row.GetStringOrNull("Sound"))
                   || unsupportedBlend;
        }

        /// <summary>R_W3WarnUnsupportedUberSplatFields: non-default BlendMode/Sound.</summary>
        public static bool HasUnsupportedUberSplatFields(SlkRow row)
        {
            if (row == null) return false;
            string blend = row.GetStringOrNull("BlendMode");
            bool unsupportedBlend = !string.IsNullOrWhiteSpace(blend) && blend != "0";
            return unsupportedBlend || HasSound(row.GetStringOrNull("Sound"));
        }
    }
}
