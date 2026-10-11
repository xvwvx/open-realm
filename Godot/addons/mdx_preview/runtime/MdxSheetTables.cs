using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Sheet;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// Lazily loaded, cached SLK tables the MDX event objects read:
    /// `Splats\SpawnData.slk` (`SPN`), `Splats\SplatData.slk` (`SPL` / `FPT`),
    /// `Splats\UberSplatData.slk` (`UBR`) and `UI\SoundInfo\AnimSounds.slk`
    /// (`SND`). The lookup mirrors R_W3LoadEventTable in
    /// games/warcraft-3/renderer/r_game.c: the row key resolves a row, and the
    /// consumers read named columns from it.
    ///
    /// Every failure degrades to a log (once per table) so a missing or custom
    /// archive never breaks previewing a model.
    /// </summary>
    public sealed class MdxSheetTables
    {
        /// <summary>Canonical archive paths, in lookup order.</summary>
        public const string SpawnDataPath = "Splats\\SpawnData.slk";
        public const string SplatDataPath = "Splats\\SplatData.slk";
        public const string UberSplatDataPath = "Splats\\UberSplatData.slk";
        public const string AnimSoundsPath = "UI\\SoundInfo\\AnimSounds.slk";
        public const string AnimLookupsPath = "UI\\SoundInfo\\AnimLookups.slk";

        readonly MdxAssetLibrary _assets;
        readonly Dictionary<string, SlkFile> _tables = new Dictionary<string, SlkFile>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public MdxSheetTables(MdxAssetLibrary assets)
        {
            _assets = assets;
        }

        /// <summary>
        /// Optional test seam: in-memory tables keyed by canonical archive path.
        /// Consulted before the asset library so a probe can exercise the consumers
        /// with hand-written SLK text.
        /// </summary>
        public Dictionary<string, SlkFile> TableOverrides { get; set; }

        /// <summary>Raised once per table that could not be read.</summary>
        public event Action<string> TableMissing;

        /// <summary>Resolved `Splats\SpawnData.slk`, or <c>null</c> when unavailable.</summary>
        public SlkFile SpawnData => Load(SpawnDataPath);

        /// <summary>Resolved `Splats\SplatData.slk`, or <c>null</c> when unavailable.</summary>
        public SlkFile SplatData => Load(SplatDataPath);

        /// <summary>Resolved `Splats\UberSplatData.slk`, or <c>null</c> when unavailable.</summary>
        public SlkFile UberSplatData => Load(UberSplatDataPath);

        /// <summary>Resolved `UI\SoundInfo\AnimSounds.slk`, or <c>null</c> when unavailable.</summary>
        public SlkFile AnimSounds => Load(AnimSoundsPath);

        /// <summary>Resolved `UI\SoundInfo\AnimLookups.slk`, or <c>null</c> when unavailable.</summary>
        public SlkFile AnimLookups => Load(AnimLookupsPath);

        /// <summary>Drops every cached table (archive scope changed).</summary>
        public void Clear()
        {
            _tables.Clear();
            _missing.Clear();
        }

        /// <summary>
        /// Reads and caches one table. The virtual path is case-insensitive and
        /// accepts either separator, matching every other asset lookup.
        /// </summary>
        public SlkFile Load(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath)) return null;
            if (_tables.TryGetValue(virtualPath, out SlkFile cached)) return cached;
            if (_missing.Contains(virtualPath)) return null;

            SlkFile table = null;
            if (TableOverrides != null && TableOverrides.TryGetValue(virtualPath, out SlkFile overridden))
                table = overridden;
            else if (_assets != null && _assets.TryReadFile(virtualPath, out byte[] data) && data != null && data.Length > 0)
                table = SlkFile.Parse(data, virtualPath);

            if (table == null || table.IsEmpty)
            {
                _missing.Add(virtualPath);
                GD.PushWarning($"[MDX Preview] 事件表不可用: {virtualPath}" +
                               (table == null ? " (读取失败)" : " (没有数据行)"));
                TableMissing?.Invoke(virtualPath);
                return null;
            }

            _tables[virtualPath] = table;
            return table;
        }

        /// <summary>
        /// R_W3AnimLookupLabel: `AnimLookups.slk` maps an MDX event key to an
        /// `AnimSounds.slk` label. The C consumer compares row names case
        /// sensitively; this lookup is case-insensitive so custom data still
        /// resolves.
        /// </summary>
        public string ResolveSoundLabel(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            SlkFile lookups = AnimLookups;
            if (lookups != null && lookups.TryGetRow(id, out SlkRow row))
            {
                string label = row.TryGetString("SoundLabel", string.Empty);
                if (!string.IsNullOrWhiteSpace(label)) return label;
            }
            return null;
        }
    }
}
