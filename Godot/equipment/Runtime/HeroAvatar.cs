using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Assets;
using MdxPreview.Core.Mdx;
using MdxPreview.Runtime;

namespace OpenRealm.Equipment
{
    /// <summary>
    /// 人物节点：加载 rw\rw_{nan,nv}_SMINAD.mdx 本体，按当前穿戴把装备外观应用到身上。
    ///
    /// 两件事构成"人物装备外观"：
    ///   1. 部件贴图槽（31-47）—— 用 MdxAssetLibrary 的替换贴图覆盖重定向，然后重建材质；
    ///   2. 附着点挂件 —— 武器挂 hand left/right、翅膀挂 chest、发型挂 head，
    ///      由 MdxModelNode.AttachToAttachment 每帧跟随骨骼。
    /// </summary>
    public partial class HeroAvatar : Node3D
    {
        MdxAssetLibrary _assets;
        EquipDatabase _database;
        HeroProfile _hero;
        EquipLoadout _loadout;
        MdxModelNode _body;
        readonly List<MdxModelNode> _attachedModels = new List<MdxModelNode>();
        readonly List<Node3D> _attachedHolders = new List<Node3D>();
        EquipLook _look = new EquipLook();
        int? _hairShapeId;
        readonly Dictionary<EquipSlot, ShapeDef> _shapeOverrides = new Dictionary<EquipSlot, ShapeDef>();

        public MdxModelNode Body => _body;
        public EquipLook Look => _look;
        public IReadOnlyList<MdxModelNode> AttachedModels => _attachedModels;
        /// <summary>每次外观重建后触发（UI 刷新用）。</summary>
        public event Action LookChanged;

        public void Initialize(MdxAssetLibrary assets, EquipDatabase database, HeroProfile hero, EquipLoadout loadout)
        {
            if (_loadout != null) _loadout.Changed -= OnLoadoutChanged;
            _assets = assets;
            _database = database;
            _hero = hero ?? new HeroProfile();
            _loadout = loadout;
            if (_loadout != null) _loadout.Changed += OnLoadoutChanged;
            RefreshLook();
        }

        public override void _ExitTree()
        {
            if (_loadout != null) _loadout.Changed -= OnLoadoutChanged;
        }

        void OnLoadoutChanged() => RefreshLook();

        /// <summary>设置发型外观（ShapeCfg 中 StrType=FX 的条目），0 或 null 表示不挂发型。</summary>
        public void SetHair(int? shapeId)
        {
            if (_hairShapeId == shapeId) return;
            _hairShapeId = shapeId;
            RefreshLook();
        }

        /// <summary>用时装外观覆盖某个栏位（对应 Lua 的 m_ChangeShape）。</summary>
        public void SetShapeOverride(EquipSlot slot, ShapeDef shape)
        {
            if (shape == null) _shapeOverrides.Remove(slot);
            else _shapeOverrides[slot] = shape;
            RefreshLook();
        }

        /// <summary>重新解析外观并应用（换装、发型变化、时装配切换都走这里）。</summary>
        public void RefreshLook()
        {
            if (_assets == null || _database == null) return;
            _look = EquipLookBuilder.Build(_database, _hero, _loadout?.Equipped, _hairShapeId, _shapeOverrides);
            ApplyTextureSlots(_look);
            EnsureBody(_look.BodyModelPath);
            ApplyAttachments(_look);
            LookChanged?.Invoke();
        }

        // ---- 贴图槽 -----------------------------------------------------------------------

        void ApplyTextureSlots(EquipLook look)
        {
            _assets.ClearReplaceableOverrides();
            foreach (KeyValuePair<uint, string> pair in look.TextureSlots)
                SetSlotTexture(pair.Key, pair.Value, look);

            // 武器强化(41)/星级(42)层：Lua 用 SetEffectTexture 作用在武器模型上，
            // 人物本体的 TEXS 不含 41/42，因此共用同一张覆盖表不会冲突。
            foreach (EquipAttachment attachment in look.Attachments)
            {
                if (!string.IsNullOrEmpty(attachment.StrongTexture)) SetSlotTexture(41, attachment.StrongTexture, look);
                if (!string.IsNullOrEmpty(attachment.StarTexture)) SetSlotTexture(42, attachment.StarTexture, look);
            }
        }

        void SetSlotTexture(uint slot, string path, EquipLook look)
        {
            if (string.IsNullOrEmpty(path)) return;
            // 缺资源要显式记录：LoadTexture 会回退到占位贴图，静默使用会掩盖素材缺口。
            if (!_assets.Exists(path))
            {
                look.Notes.Add($"贴图缺失：{path}");
                return;
            }
            Texture2D texture = _assets.LoadTexture(path);
            if (texture == null || _assets.FailedAssets.ContainsKey(VirtualPath.Normalize(path)))
            {
                look.Notes.Add($"贴图无法解码：{path}");
                return;
            }
            _assets.SetReplaceableOverride(slot, texture);
        }

        // ---- 本体模型 ---------------------------------------------------------------------

        void EnsureBody(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_body != null && string.Equals(_body.SourcePath, path, StringComparison.OrdinalIgnoreCase))
            {
                // 同一模型：贴图槽已更新，重建材质即可（对应 WC3 的 SetUnitTexture）。
                _body.RebuildMaterials();
                return;
            }

            ClearBody();
            MdxModel model = _assets.LoadModel(path);
            if (model == null)
            {
                GD.PushError($"[装备] 无法加载人物模型 {path}；请确认 BLP/模型搜索路径包含 D:\\rpg\\output\\files。");
                return;
            }
            _body = new MdxModelNode { Name = "HeroBody" };
            AddChild(_body);
            _body.Build(_assets, model, path);
            _body.SetSequenceByName("Stand");
        }

        void ClearBody()
        {
            ClearAttachments();
            if (_body != null && GodotObject.IsInstanceValid(_body))
            {
                _body.QueueFree();
                _body = null;
            }
        }

        // ---- 挂件 -------------------------------------------------------------------------

        void ApplyAttachments(EquipLook look)
        {
            ClearAttachments();
            if (_body == null) return;

            foreach (EquipAttachment attachment in look.Attachments)
            {
                MdxModel model = _assets.LoadModel(attachment.ModelPath);
                if (model == null)
                {
                    look.Notes.Add($"挂件模型缺失：{attachment.ModelPath}");
                    GD.PushWarning($"[装备] 挂件模型缺失 {attachment.ModelPath}（挂点 {attachment.Attachment}）");
                    continue;
                }

                var holder = new Node3D { Name = "Attach_" + attachment.Attachment.Replace(" ", "_") };
                var node = new MdxModelNode { Name = "Model" };
                holder.AddChild(node);
                node.Build(_assets, model, attachment.ModelPath);
                node.SetSequenceByName("Stand");

                if (!_body.AttachToAttachment(holder, attachment.Attachment))
                {
                    look.Notes.Add($"人物模型没有附着点 {attachment.Attachment}（{attachment.ModelPath}）");
                    holder.QueueFree();
                    continue;
                }

                _attachedHolders.Add(holder);
                _attachedModels.Add(node);
            }
        }

        void ClearAttachments()
        {
            if (_body != null && GodotObject.IsInstanceValid(_body)) _body.DetachAllAttachmentChildren();
            foreach (Node3D holder in _attachedHolders)
                if (GodotObject.IsInstanceValid(holder)) holder.QueueFree();
            _attachedHolders.Clear();
            _attachedModels.Clear();
        }

        // ---- 动画 / 查询 ------------------------------------------------------------------

        /// <summary>按名字播放序列（Stand / Walk / Attack …），本体与挂件一起切。</summary>
        public void PlaySequence(string sequenceName)
        {
            _body?.SetSequenceByName(sequenceName);
            foreach (MdxModelNode node in _attachedModels) node.SetSequenceByName(sequenceName);
        }

        public void SetPlaying(bool playing)
        {
            _body?.SetPlaying(playing);
            foreach (MdxModelNode node in _attachedModels) node.SetPlaying(playing);
        }

        /// <summary>本体与所有挂件的世界包围盒（相机取景用）。</summary>
        public Aabb WorldBounds()
        {
            if (_body == null) return new Aabb(Vector3.Zero, Vector3.One);
            Aabb bounds = _body.WorldBounds();
            foreach (MdxModelNode node in _attachedModels) bounds = bounds.Merge(node.WorldBounds());
            return bounds;
        }

        public string DescribeLook() => _look.Describe();
    }
}
