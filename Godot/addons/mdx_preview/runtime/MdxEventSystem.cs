using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using MdxPreview.Core.Math3D;
using MdxPreview.Core.Mdx;
using MdxPreview.Core.Sheet;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// MDX EVTS event objects. The playhead crossing an authored key fires the
    /// event once, exactly like MDLX_EventObjectId / MDLX_EventKeyCrossed in the
    /// engine (games/warcraft-3/renderer/mdx/r_mdx_anim.c).
    ///
    /// Consumers, following docs/games/warcraft-3/mdx-event-objects.md and
    /// R_W3DispatchModelEvents in games/warcraft-3/renderer/r_game.c:
    ///  - `SND` resolves `UI\SoundInfo\AnimLookups.slk` -> `AnimSounds.slk` and plays
    ///    the authored animation sound at the event node;
    ///  - `SPN` resolves `Splats\SpawnData.slk` (row key -> `Model` column), loads the
    ///    child model and plays its `Birth` sequence (falling back to `Stand`);
    ///  - `SPL` / `FPT` resolve `Splats\SplatData.slk` ground decals;
    ///  - `UBR` resolves `Splats\UberSplatData.slk` ground decals;
    ///  - nested events inside spawned children keep working, bounded by
    ///    <see cref="MaxSpawnDepth"/>.
    ///
    /// Placement follows MDLX_EventWorldTransform: the child snapshots the animated
    /// event node's matrix (its basis, with the translation replaced by the node's
    /// transformed authored pivot). Because this system is the `Events` node inside
    /// `MdxModelNode` -- which already carries the -90 degree X rotation that maps
    /// MDX Z-up onto Godot Y-up -- the child's local transform here is the MDX-space
    /// event matrix itself. The spawned `MdxModelNode` re-applies that same root
    /// rotation, so the two cancel and the child lands in the parent's frame with
    /// the authored event rotation, exactly once.
    /// </summary>
    public partial class MdxEventSystem : Node3D
    {
        /// <summary>WC3_EVENT_SPAWN_MAX: bounded live SPN child models.</summary>
        public const int MaxSpawnedModels = 32;

        /// <summary>
        /// Nested SPN depth cap. The reference caps presentation at four model
        /// levels; the preview is an editor tool, so two levels is the default.
        /// </summary>
        public const int MaxSpawnDepth = 2;

        /// <summary>Seconds a spawned child is kept after its Birth sequence ends.</summary>
        public const float SpawnHoldSeconds = 3f;

        /// <summary>RecentEvents keeps this many entries.</summary>
        public const int RecentEventCapacity = 32;

        protected readonly MdxAssetLibrary Assets;
        protected readonly List<string> Log = new List<string>();
        readonly List<SpawnedModel> _spawns = new List<SpawnedModel>();
        readonly List<AudioStreamPlayer3D> _sounds = new List<AudioStreamPlayer3D>();
        readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        MdxSheetTables _tables;
        MdxSplatSystem _splats;
        bool _depthInitialized;
        string _lastResolvedSound;

        public MdxEventSystem(MdxAssetLibrary assets)
        {
            Assets = assets;
            Name = "Events";
            ProcessMode = ProcessModeEnum.Always;
        }

        public bool Enabled { get; set; } = true;
        public MdxModel Model { get; private set; }
        public MdxInstance Instance { get; private set; }

        /// <summary>The model node this system belongs to (shadows <see cref="Node.Owner"/>).</summary>
        public new MdxModelNode Owner { get; private set; }

        /// <summary>最近触发的若干条事件（调试面板显示）。</summary>
        public IReadOnlyList<string> RecentEvents => Log;

        /// <summary>事件对象数据表（SpawnData / SplatData / UberSplatData / AnimSounds）。</summary>
        public MdxSheetTables Tables => _tables;

        /// <summary>地面贴花系统（SPL / FPT / UBR）。</summary>
        public MdxSplatSystem Splats => _splats;

        /// <summary>存活中的 SPN 子模型数量。</summary>
        public int ActiveSpawnCount => _spawns.Count;

        /// <summary>嵌套层级：0 为顶层模型，SPN 子模型逐层 +1。</summary>
        public int Depth { get; private set; }

        /// <summary>每个触发的事件都会以 `PREFIX: 名字` 触发一次。</summary>
        public event Action<string> EventLogged;

        /// <summary>降级/警告信息（只对每种情况报一次）。</summary>
        public event Action<string> WarningLogged;

        /// <summary>Optional diagnostic sink for the dispatch clock (probe/debug builds).</summary>
        public Action<string> Diagnostics { get; set; }

        /// <summary>
        /// Step length used to age spawned children and decals. When it is left at 0
        /// the engine's own process delta is used, so a normal run needs no setup; a
        /// probe that drives `_Process` by hand sets it explicitly.
        /// </summary>
        public float LastTickDelta { get; set; }

        /// <summary>
        /// Optional test seam: when set, spawned child models come from this loader
        /// instead of <see cref="MdxAssetLibrary.LoadModel"/>. Lets the probes exercise
        /// SPN dispatch without an archive.
        /// </summary>
        public Func<string, MdxModel> ModelLoader { get; set; }

        /// <summary>
        /// Optional test seam: in-memory child models keyed by virtual path. Consulted
        /// before <see cref="ModelLoader"/> and the asset library. Spawned children
        /// inherit the parent's overrides so nested SPN dispatch can be exercised
        /// without archive data.
        /// </summary>
        public Dictionary<string, MdxModel> ModelOverrides { get; set; }

        /// <summary>
        /// Nested-event depth a freshly built system starts at. <see cref="MdxModelNode.Build"/>
        /// creates the child's system, so a spawner sets this on the new child right
        /// after building it.
        /// </summary>
        public int InitialDepth { get; set; }

        public virtual void Build(MdxModel model, MdxInstance instance, MdxModelNode owner)
        {
            Model = model;
            Instance = instance;
            Owner = owner;
            _tables ??= new MdxSheetTables(Assets);
            EnsureSplatSystem();
            if (!_depthInitialized)
            {
                // A child model node copies its inherited depth in before Build runs.
                Depth = InitialDepth;
                _depthInitialized = true;
            }
        }

        void EnsureSplatSystem()
        {
            if (_splats != null && GodotObject.IsInstanceValid(_splats)) return;
            _splats = new MdxSplatSystem(Assets);
            _splats.WarningLogged += message => Warn($"splat:{message}", message);
            AddChild(_splats);
        }

        // ---- dispatch --------------------------------------------------------------------

        public virtual void Tick(MdxInstance instance, float previousFrame, float previousGlobalMs)
        {
            if (Diagnostics != null)
                Diagnostics($"Tick depth={Depth} enabled={Enabled} " +
                            $"model={(Model == null ? "null" : Model.Events.Count.ToString())} " +
                            $"frame {previousFrame:0.##}->{instance?.Frame:0.##} " +
                            $"global {previousGlobalMs:0.##}->{instance?.GlobalTimeMs:0.##}");

            // Spawned children and decals age on the model node's step, which is also
            // what drives event timing (the engine's process delta is 0 when a probe
            // drives _Process by hand).
            float delta = LastTickDelta > 0f ? LastTickDelta : (float)GetProcessDeltaTime();

            if (!Enabled)
            {
                // Disabled event objects keep their children alive (they are still
                // rendering) but stop firing new ones.
                TickSpawns(delta);
                TickSounds();
                return;
            }

            TickSpawns(delta);
            TickSounds();
            _splats?.Tick(delta);

            if (Model == null || Model.Events.Count == 0) return;

            List<MdxEventObject> crossed = instance.CollectCrossedEvents(previousFrame, previousGlobalMs);
            if (crossed.Count == 0) return;

            foreach (MdxEventObject evt in crossed) Dispatch(evt, instance);
        }

        /// <summary>R_W3DispatchModelEvents: one crossing per event object, routed by family.</summary>
        protected void Dispatch(MdxEventObject evt, MdxInstance instance)
        {
            if (evt?.Node == null) return;

            string prefix = FamilyPrefix(evt);
            if (prefix == null)
            {
                string raw = RawPrefix(evt);
                Warn($"unknown-family:{raw}", $"未知的事件族 '{raw}' (事件 '{evt.Node.Name}')");
                return;
            }

            if (!EventObjectId(evt, prefix, out string id))
            {
                Warn($"missing-row-key:{evt.Node.Name}", $"{prefix} 事件 '{evt.Node.Name}' 没有行键, 已跳过");
                return;
            }

            switch (prefix)
            {
                case "SND":
                    LogEvent($"SND: {id}");
                    EmitSound(evt, id, instance);
                    break;
                case "SPN":
                    LogEvent($"SPN: {id}");
                    EmitSpawn(evt, id, instance);
                    break;
                case "SPL":
                case "FPT":
                    LogEvent($"{prefix}: {id}");
                    EmitSplat(evt, id, prefix);
                    break;
                case "UBR":
                    LogEvent($"UBR: {id}");
                    EmitUberSplat(evt, id);
                    break;
            }
        }

        static readonly string[] Families = MdxEventData.Families;

        /// <summary>R_W3EventFamily: the first three name characters select the family.</summary>
        static string FamilyPrefix(MdxEventObject evt)
            => MdxEventData.FamilyPrefix(evt.Node?.Name, evt.EventType);

        static string RawPrefix(MdxEventObject evt) => MdxEventData.RawPrefix(evt.Node?.Name);

        /// <summary>
        /// MDLX_EventObjectId: the row key is everything after the three-character
        /// family prefix and the fourth separator character, with surrounding
        /// whitespace trimmed. The reference also rejects a name shorter than five
        /// characters (`name_len &lt;= 4`), which the preview keeps.
        /// </summary>
        public static bool EventObjectId(MdxEventObject evt, string family, out string id)
        {
            id = null;
            if (evt?.Node == null) return false;

            // The loader already resolved the payload; prefer it when present so a
            // separator other than a space still behaves like the reference.
            if (!string.IsNullOrEmpty(evt.EventData))
            {
                id = evt.EventData.Trim();
                return id.Length > 0;
            }
            return MdxEventData.EventObjectId(evt.Node.Name, family, out id);
        }

        // ---- SND -------------------------------------------------------------------------

        void EmitSound(MdxEventObject evt, string id, MdxInstance instance)
        {
            _tables ??= new MdxSheetTables(Assets);
            SlkFile sounds = _tables.AnimSounds;
            if (sounds == null)
            {
                Warn("anim-sounds-missing", $"SND 事件 '{id}' 无法解析: UI\\SoundInfo\\AnimSounds.slk 不可用");
                return;
            }

            string label = _tables.ResolveSoundLabel(id) ?? id;
            if (!sounds.TryGetRow(label, out SlkRow row))
            {
                Warn($"missing-sound-row:{id}", $"MDX SND 事件 '{id}' 在 AnimSounds.slk 中没有对应行 (标签 '{label}')");
                return;
            }

            List<string> variants = SplitVariants(row.TryGetString("FileNames"));
            if (variants.Count == 0)
            {
                Warn($"empty-sound-row:{id}", $"MDX SND 事件 '{id}' 的 AnimSounds 行没有声音文件");
                return;
            }

            // R_W3PresentationPick: per-instance/key/time variant selection. The
            // preview has no entity number, so the instance clock plays that role.
            float nowMs = instance?.GlobalTimeMs ?? 0f;
            int pick = PresentationPick(0, 0, Mathf.RoundToInt(nowMs), variants.Count);
            string directory = row.TryGetString("DirectoryBase");
            string path = CombineSoundPath(directory, variants[pick]);
            float volume = Mathf.Clamp(row.GetFloat("Volume", 127f) / 127f, 0f, 1f);
            Vector3 origin = EventWorldPosition(evt, instance);

            PlaySound(path, origin, volume, id);
        }

        /// <summary>R_W3SoundPath: `DirectoryBase` + the picked comma-separated variant.</summary>
        public static string CombineSoundPath(string directory, string file)
            => MdxEventData.CombineSoundPath(directory, file);

        public static List<string> SplitVariants(string files) => MdxEventData.SplitVariants(files);

        /// <summary>R_W3PresentationPick: deterministic variant hash.</summary>
        public static int PresentationPick(uint entity, uint key, int time, int count)
            => MdxEventData.PresentationPick(entity, key, time, count);

        /// <summary>Candidate archive paths for one AnimSounds entry.</summary>
        public static IEnumerable<string> SoundCandidates(string virtualPath)
        {
            string extension = System.IO.Path.GetExtension(virtualPath);
            if (!string.IsNullOrEmpty(extension))
            {
                yield return virtualPath;
                yield break;
            }
            yield return virtualPath + ".wav";
            yield return virtualPath + ".mp3";
        }

        /// <summary>
        /// Best-effort playback. `AnimSounds.slk` `FileNames` entries normally carry
        /// their own extension (`step.wav`); an extension-less path (custom data)
        /// falls back to `.wav` then `.mp3`. Anything that cannot be resolved or
        /// decoded is logged once and skipped -- never thrown.
        /// </summary>
        protected virtual void PlaySound(string virtualPath, Vector3 origin, float volume, string id)
        {
            if (Assets == null || string.IsNullOrWhiteSpace(virtualPath))
            {
                Warn($"sound-unavailable:{id}", $"SND 事件 '{id}' 无法播放: 没有解析到声音路径");
                return;
            }

            AudioStream stream = null;
            string resolved = null;
            foreach (string candidate in SoundCandidates(virtualPath))
            {
                if (!Assets.TryReadFile(candidate, out byte[] data) || data == null || data.Length == 0) continue;
                stream = BuildStream(data, candidate);
                if (stream != null)
                {
                    resolved = candidate;
                    break;
                }
            }

            if (stream == null)
            {
                Warn($"sound-missing:{id}", $"SND 事件 '{id}' 的声音无法播放 (仅记录日志): {virtualPath}");
                return;
            }

            var player = new AudioStreamPlayer3D
            {
                Name = $"snd_{id}",
                Stream = stream,
                VolumeDb = Mathf.LinearToDb(Mathf.Max(0.0001f, volume)),
                UnitSize = 64f,
                MaxDistance = 4000f,
                // The player is parented to the Events node, so a top-level node is
                // placed with a world transform instead of the model's local frame.
                TopLevel = true,
                GlobalPosition = origin,
            };
            AddChild(player);
            player.Play();
            _sounds.Add(player);
            _lastResolvedSound = resolved;
        }

        static AudioStream BuildStream(byte[] data, string path)
        {
            try
            {
                string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".mp3") return AudioStreamMP3.LoadFromBuffer(data);
                return AudioStreamWav.LoadFromBuffer(data);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Archive path of the most recently resolved sound (debug/UI).</summary>
        public string LastResolvedSound => _lastResolvedSound;

        void TickSounds()
        {
            for (int i = _sounds.Count - 1; i >= 0; i--)
            {
                AudioStreamPlayer3D player = _sounds[i];
                if (player != null && GodotObject.IsInstanceValid(player) && player.IsPlaying()) continue;
                if (player != null && GodotObject.IsInstanceValid(player)) player.QueueFree();
                _sounds.RemoveAt(i);
            }
        }

        // ---- SPN -------------------------------------------------------------------------

        void EmitSpawn(MdxEventObject evt, string id, MdxInstance instance)
        {
            if (Depth >= MaxSpawnDepth)
            {
                Warn($"spawn-depth:{id}",
                    $"MDX SPN 嵌套超过 {MaxSpawnDepth} 层, 事件 '{id}' 的子模型创建被抑制");
                return;
            }

            if (_spawns.Count >= MaxSpawnedModels)
            {
                Warn("spawn-pool-full", $"SPN 子模型已达上限 {MaxSpawnedModels}, 事件 '{id}' 被丢弃");
                return;
            }

            _tables ??= new MdxSheetTables(Assets);
            SlkFile spawnData = _tables.SpawnData;
            if (spawnData == null)
            {
                Warn("spawn-data-missing", $"SPN 事件 '{id}' 无法解析: Splats\\SpawnData.slk 不可用");
                return;
            }
            if (!spawnData.TryGetRow(id, out SlkRow row))
            {
                Warn($"missing-spawn-row:{id}", $"MDX SPN 事件 '{id}' 在 SpawnData.slk 中没有对应行");
                return;
            }

            string modelPath = row.TryGetString("Model");
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                Warn($"spawn-row-no-model:{id}", $"SpawnData 行 '{id}' 没有 Model 列, 已跳过");
                return;
            }

            MdxModel childModel = LoadModel(modelPath);
            if (childModel == null)
            {
                Warn($"spawn-model-missing:{id}", $"MDX SPN '{id}' 的子模型无法加载: {modelPath}");
                return;
            }

            if (childModel.Sequences.Count == 0)
            {
                Warn($"spawn-model-no-sequence:{id}", $"MDX SPN '{id}' 的子模型 {modelPath} 没有动画序列, 已跳过");
                return;
            }

            Transform3D local = EventLocalTransform(evt, instance);
            MdxModelNode child = CreateChildNode(childModel, modelPath, local);
            if (child == null)
            {
                Warn($"spawn-node-failed:{id}", $"MDX SPN '{id}' 的子模型节点创建失败: {modelPath}");
                return;
            }

            int birth = childModel.FindSequenceIndex("Birth");
            if (birth < 0) birth = childModel.FindSequenceIndex("Stand");
            if (birth < 0) birth = 0;
            child.SetSequence(birth);
            child.SetLoop(false);

            float lifetime = (childModel.Sequences[birth].Duration / 1000f) + SpawnHoldSeconds;
            _spawns.Add(new SpawnedModel
            {
                Node = child,
                RowKey = id,
                ModelPath = modelPath,
                Lifetime = lifetime,
            });
        }

        protected MdxModel LoadModel(string virtualPath)
        {
            if (ModelOverrides != null && !string.IsNullOrEmpty(virtualPath)
                && ModelOverrides.TryGetValue(virtualPath, out MdxModel overridden))
                return overridden;
            if (ModelLoader != null) return ModelLoader(virtualPath);
            return Assets?.LoadModel(virtualPath);
        }

        /// <summary>
        /// Builds the SPN child. It is parented to this `Events` node and given the
        /// MDX-space event matrix as its local transform, so the model node's own
        /// -90 degree X root rotation is applied exactly once (see the class remark).
        /// </summary>
        protected virtual MdxModelNode CreateChildNode(MdxModel model, string path, Transform3D localTransform)
        {
            var node = new MdxModelNode { Name = $"spn_{System.IO.Path.GetFileNameWithoutExtension(path)}" };
            node.Transform = localTransform;
            AddChild(node);
            node.Build(Assets, model, path);

            if (node.Events != null)
            {
                node.Events.InitialDepth = Depth + 1;
                node.Events.Depth = Depth + 1;
                node.Events.Enabled = Enabled;
                node.Events.LastTickDelta = LastTickDelta;
                // Nested SPN dispatch must see the same in-memory fixtures a probe
                // installed on the root system.
                node.Events.ModelOverrides = ModelOverrides;
                node.Events.ModelLoader = ModelLoader;
            }
            return node;
        }

        void TickSpawns(float deltaSeconds)
        {
            if (_spawns.Count == 0) return;
            for (int i = _spawns.Count - 1; i >= 0; i--)
            {
                SpawnedModel spawn = _spawns[i];
                spawn.Age += deltaSeconds;
                bool nodeGone = spawn.Node == null || !GodotObject.IsInstanceValid(spawn.Node);
                if (!nodeGone && spawn.Age < spawn.Lifetime) continue;

                if (!nodeGone)
                {
                    RemoveChild(spawn.Node);
                    spawn.Node.QueueFree();
                }
                _spawns.RemoveAt(i);
            }
        }

        // ---- SPL / FPT / UBR -------------------------------------------------------------

        void EmitSplat(MdxEventObject evt, string id, string family)
        {
            _tables ??= new MdxSheetTables(Assets);
            SlkFile splatData = _tables.SplatData;
            if (splatData == null)
            {
                Warn("splat-data-missing", $"{family} 事件 '{id}' 无法解析: Splats\\SplatData.slk 不可用");
                return;
            }
            if (!splatData.TryGetRow(id, out SlkRow row))
            {
                Warn($"missing-splat-row:{id}", $"MDX {family} 事件 '{id}' 在 SplatData.slk 中没有对应行");
                return;
            }

            EnsureSplatSystem();
            _splats.SpawnSplat(row, EventLocalTransform(evt, Instance), $"{family} {id}");
        }

        void EmitUberSplat(MdxEventObject evt, string id)
        {
            _tables ??= new MdxSheetTables(Assets);
            SlkFile uberData = _tables.UberSplatData;
            if (uberData == null)
            {
                Warn("uber-splat-data-missing", $"UBR 事件 '{id}' 无法解析: Splats\\UberSplatData.slk 不可用");
                return;
            }
            if (!uberData.TryGetRow(id, out SlkRow row))
            {
                Warn($"missing-uber-splat-row:{id}", $"MDX UBR 事件 '{id}' 在 UberSplatData.slk 中没有对应行");
                return;
            }

            EnsureSplatSystem();
            _splats.SpawnUberSplat(row, EventLocalTransform(evt, Instance), $"UBR {id}");
        }

        // ---- placement -------------------------------------------------------------------

        /// <summary>
        /// MDLX_EventWorldTransform for a child of this node: the animated node's
        /// basis with the translation replaced by the node's transformed pivot.
        /// The matrix is already expressed in this node's parent space (MDX model
        /// space), which is exactly what a child of the `Events` node needs.
        /// </summary>
        public Transform3D EventLocalTransform(MdxEventObject evt, MdxInstance instance)
        {
            if (evt?.Node == null || instance == null) return Transform3D.Identity;
            return MdxMeshBuilder.ToTransform(MdxEventPlacement.LocalMatrix(Model, evt, instance));
        }

        /// <summary>World-space position of an event node (delegates to the owner).</summary>
        public Vector3 EventWorldPosition(MdxEventObject evt, MdxInstance instance)
        {
            if (Owner != null) return Owner.EventWorldPosition(evt);
            Transform3D local = EventLocalTransform(evt, instance);
            return GlobalTransform * local.Origin;
        }

        // ---- lifecycle -------------------------------------------------------------------

        public virtual void Reset()
        {
            Log.Clear();
            _warned.Clear();
            foreach (SpawnedModel spawn in _spawns)
            {
                if (spawn.Node != null && GodotObject.IsInstanceValid(spawn.Node)) spawn.Node.QueueFree();
            }
            _spawns.Clear();
            foreach (AudioStreamPlayer3D player in _sounds)
            {
                if (player != null && GodotObject.IsInstanceValid(player)) player.QueueFree();
            }
            _sounds.Clear();
            _splats?.Reset();
        }

        // ---- logging ---------------------------------------------------------------------

        /// <summary>
        /// Records one triggered event as `PREFIX: 名字`, keeps the most recent
        /// <see cref="RecentEventCapacity"/> entries and forwards it to the owning
        /// model node for the UI.
        /// </summary>
        protected void LogEvent(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (Log.Count == 0 || Log[^1] != message) Log.Add(message);
            while (Log.Count > RecentEventCapacity) Log.RemoveAt(0);
            Owner?.LogEvent(message);
            EventLogged?.Invoke(message);
        }

        void Warn(string key, string message)
        {
            if (!_warned.Add(key)) return;
            GD.PushWarning($"[MDX Preview] {message}");
            WarningLogged?.Invoke(message);
        }

        // ---- debug -----------------------------------------------------------------------

        /// <summary>One line per live SPN child (UI/diagnostics).</summary>
        public IReadOnlyList<string> DescribeSpawns()
        {
            var lines = new List<string>(_spawns.Count);
            foreach (SpawnedModel spawn in _spawns)
                lines.Add($"{spawn.RowKey} -> {spawn.ModelPath} ({spawn.Age:0.00}s/{spawn.Lifetime:0.00}s)");
            return lines;
        }

        sealed class SpawnedModel
        {
            public MdxModelNode Node;
            public string RowKey;
            public string ModelPath;
            public float Age;
            public float Lifetime;
        }
    }
}
