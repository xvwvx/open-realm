using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MdxPreview.Runtime;

namespace OpenRealm.Equipment.UI
{
    /// <summary>
    /// 人物装备界面：左侧人物实时预览，中间栏位，右侧属性，底部背包与说明。
    /// 全部用代码构建，避免维护庞大的 .tscn；资源（图标/模型/贴图）都从游戏目录懒加载。
    /// </summary>
    public partial class EquipPanel : Control
    {
        MdxAssetLibrary _assets;
        EquipDatabase _database;
        HeroProfile _hero;
        EquipLoadout _loadout;
        HeroAvatar _avatar;

        SubViewport _viewport;
        Camera3D _camera;
        GridContainer _slotGrid;
        GridContainer _bagGrid;
        Label _info;
        Label _heroLabel;
        VBoxContainer _statsBox;
        EquipItem _selected;
        bool _framed;
        int _frames;
        FontFile _font;

        readonly Dictionary<EquipSlot, Button> _slotButtons = new Dictionary<EquipSlot, Button>();
        static readonly EquipSlot[] SlotOrder =
        {
            EquipSlot.Helm, EquipSlot.Necklace, EquipSlot.Weapon, EquipSlot.Cloth,
            EquipSlot.Glove, EquipSlot.Bracelet1, EquipSlot.Bracelet2, EquipSlot.Ring1,
            EquipSlot.Ring2, EquipSlot.Trousers, EquipSlot.Shoes, EquipSlot.Artifact,
            EquipSlot.Cloak, EquipSlot.Wing, EquipSlot.Gem, EquipSlot.Orb,
            EquipSlot.Medal, EquipSlot.Seal, EquipSlot.Tiger, EquipSlot.Drum, EquipSlot.Banner,
        };

        public HeroProfile Hero => _hero;
        public EquipLoadout Loadout => _loadout;
        public HeroAvatar Avatar => _avatar;

        public void Initialize(MdxAssetLibrary assets, EquipDatabase database, HeroProfile hero, EquipLoadout loadout, string fontPath = null)
        {
            _assets = assets;
            _database = database;
            _hero = hero;
            _loadout = loadout;
            if (fontPath != null) LoadFont(fontPath);

            if (_loadout != null)
            {
                _loadout.Changed -= OnLoadoutChanged;
                _loadout.Changed += OnLoadoutChanged;
            }

            BuildUi();
            RefreshAll();
        }

        public override void _ExitTree()
        {
            if (_loadout != null) _loadout.Changed -= OnLoadoutChanged;
        }

        public override void _Process(double delta)
        {
            // 模型刚 Build 时骨架还没算过姿态，包围盒不准；等第一帧后再取景。
            if (_framed) return;
            if (++_frames < 3) return;                     // 等骨架姿态稳定
            if (_avatar == null || _avatar.Body == null) return;
            _framed = true;
            FrameAvatar();
        }

        void OnLoadoutChanged()
        {
            RefreshSlots();
            RefreshStats();
            RefreshBag();
        }

        void LoadFont(string path)
        {
            if (!FileAccess.FileExists(path))
            {
                GD.PushWarning($"[装备界面] 字体不存在，回退默认字体：{path}");
                return;
            }
            try
            {
                var font = new FontFile();
                Error error = font.LoadDynamicFont(path);
                if (error != Error.Ok)
                {
                    GD.PushWarning($"[装备界面] 字体加载失败：{path} ({error})");
                    return;
                }
                _font = font;
                var theme = new Theme();
                theme.DefaultFont = font;
                theme.DefaultFontSize = 14;
                Theme = theme;
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[装备界面] 字体异常：{ex.Message}");
            }
        }

        // ---- 界面构建 ---------------------------------------------------------------------

        void BuildUi()
        {
            foreach (Node child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
            _slotButtons.Clear();

            SetAnchorsPreset(LayoutPreset.FullRect);
            var background = new ColorRect { Color = new Color(0.07f, 0.08f, 0.11f, 1f) };
            background.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(background);

            var root = new HBoxContainer();
            root.SetAnchorsPreset(LayoutPreset.FullRect);
            root.AddThemeConstantOverride("separation", 12);
            AddChild(root);

            root.AddChild(BuildPreviewColumn());
            root.AddChild(BuildCenterColumn());
            root.AddChild(BuildStatsColumn());
        }

        Control BuildPreviewColumn()
        {
            var column = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
            column.AddThemeConstantOverride("separation", 8);

            _heroLabel = new Label { Text = HeroSummary() };
            column.AddChild(_heroLabel);

            _viewport = new SubViewport
            {
                Size = new Vector2I(352, 480),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                OwnWorld3D = true,
                Msaa3D = Viewport.Msaa.Msaa4X,
                TransparentBg = false,
            };
            var container = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(352, 480) };
            container.AddChild(_viewport);
            column.AddChild(container);

            _camera = new Camera3D { Position = new Vector3(0f, 90f, 320f), Current = true, Fov = 35f };
            _camera.RotationDegrees = new Vector3(-8f, 0f, 0f);
            _viewport.AddChild(_camera);

            // WC3 模型是自发光很弱的低多边形资产，靠方向光 + 强环境光才看得清贴图层次。
            _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, 30f, 0f), LightEnergy = 1.6f });
            _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-25f, -145f, 0f), LightEnergy = 0.7f });

            var environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.13f, 0.14f, 0.17f, 1f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.85f, 0.85f, 0.9f, 1f),
                AmbientLightEnergy = 1.1f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            };
            _viewport.AddChild(new WorldEnvironment { Environment = environment });

            _avatar = new HeroAvatar { Name = "PreviewAvatar" };
            _viewport.AddChild(_avatar);
            _avatar.Initialize(_assets, _database, _hero, _loadout);
            FrameAvatar();
            return column;
        }

        Control BuildCenterColumn()
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            column.AddThemeConstantOverride("separation", 8);

            column.AddChild(new Label { Text = "装备栏位（左键卸下 / 右键强化 +1 / 中键星级 +1）" });

            _slotGrid = new GridContainer { Columns = 4 };
            _slotGrid.AddThemeConstantOverride("h_separation", 6);
            _slotGrid.AddThemeConstantOverride("v_separation", 6);
            column.AddChild(_slotGrid);

            foreach (EquipSlot slot in SlotOrder)
            {
                var button = new Button
                {
                    CustomMinimumSize = new Vector2(74, 74),
                    ExpandIcon = true,
                    IconAlignment = HorizontalAlignment.Center,
                    ClipText = true,
                    Text = _database.SlotDisplayName((int)slot),
                    TooltipText = _database.SlotDisplayName((int)slot),
                };
                EquipSlot captured = slot;
                button.Pressed += () => OnSlotClicked(captured);
                button.GuiInput += @event => OnSlotInput(captured, @event);
                button.MouseEntered += () => ShowSlotInfo(captured);
                _slotButtons[slot] = button;
                _slotGrid.AddChild(button);
            }

            column.AddChild(new HSeparator());
            column.AddChild(new Label { Text = "背包（左键装备 / 右键选中查看）" });
            _bagGrid = new GridContainer { Columns = 9 };
            _bagGrid.AddThemeConstantOverride("h_separation", 4);
            _bagGrid.AddThemeConstantOverride("v_separation", 4);
            column.AddChild(_bagGrid);

            _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 120) };
            column.AddChild(_info);
            return column;
        }

        Control BuildStatsColumn()
        {
            var column = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
            column.AddThemeConstantOverride("separation", 6);
            column.AddChild(new Label { Text = "属性合计（装备 + 时装）" });
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            _statsBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            scroll.AddChild(_statsBox);
            column.AddChild(scroll);
            return column;
        }

        /// <summary>按人物包围盒自动取景（换性别/换模型后调用）。</summary>
        public void FrameAvatar()
        {
            if (_camera == null || _avatar == null || _avatar.Body == null) return;
            // 只用本体包围盒：挂件（翅膀/武器/粒子）会把范围撑大，导致人物被推小。
            Aabb bounds = _avatar.Body.WorldBounds();
            Vector3 center = bounds.GetCenter();
            float height = Mathf.Max(120f, bounds.Size.Y);
            float distance = height / Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) * 0.78f;
            _camera.Position = center + new Vector3(0f, height * 0.02f, distance);
            _camera.LookAt(center, Vector3.Up);
            GD.Print($"[装备界面] 本体包围盒 {bounds}，相机 {_camera.Position}");
        }

        // ---- 刷新 -------------------------------------------------------------------------

        public void RefreshAll()
        {
            RefreshSlots();
            RefreshBag();
            RefreshStats();
            if (_heroLabel != null) _heroLabel.Text = HeroSummary();
        }

        void RefreshSlots()
        {
            foreach (KeyValuePair<EquipSlot, Button> pair in _slotButtons)
            {
                EquipItem item = _loadout.FindEquipped(pair.Key);
                Button button = pair.Value;
                button.Text = item != null ? item.Name : _database.SlotDisplayName((int)pair.Key);
                button.Icon = item != null ? LoadIcon(item.Def.Icon) : null;
                button.TooltipText = item != null ? ItemTooltip(item) : _database.SlotDisplayName((int)pair.Key);
                button.Modulate = item != null ? QualityColor(item.Def.ShowColor) : Colors.White;
            }
        }

        void RefreshBag()
        {
            if (_bagGrid == null) return;
            foreach (Node child in _bagGrid.GetChildren()) { _bagGrid.RemoveChild(child); child.QueueFree(); }

            foreach (EquipItem item in _loadout.Bag)
            {
                var button = new Button
                {
                    CustomMinimumSize = new Vector2(52, 52),
                    Icon = LoadIcon(item.Def.Icon),
                    ExpandIcon = true,
                    TooltipText = ItemTooltip(item),
                    Modulate = QualityColor(item.Def.ShowColor),
                };
                EquipItem captured = item;
                button.Pressed += () => EquipFromBag(captured);
                button.MouseEntered += () => { _selected = captured; _info.Text = ItemTooltip(captured); };
                _bagGrid.AddChild(button);
            }
            if (_loadout.Bag.Count == 0)
                _bagGrid.AddChild(new Label { Text = "（背包为空）" });
        }

        void RefreshStats()
        {
            if (_statsBox == null) return;
            foreach (Node child in _statsBox.GetChildren()) { _statsBox.RemoveChild(child); child.QueueFree(); }

            EquipStats stats = _loadout.TotalStats();
            if (stats.Count == 0)
            {
                _statsBox.AddChild(new Label { Text = "（未装备任何物品）" });
                return;
            }
            foreach (KeyValuePair<string, double> pair in stats.Sorted())
                _statsBox.AddChild(new Label { Text = $"{AttrLabel(pair.Key)}: {pair.Value:0.##}" });
        }

        string HeroSummary()
            => $"{_hero.Name}　{_database.JobName((int)_hero.Job)}　Lv {_hero.Level}　" +
               $"{(_hero.Sex == HeroSex.Female ? "女" : "男")}　负重 {_loadout.BodyHeavyUsed():0.#}/{_hero.BodyHeavy:0.#}　" +
               $"腕力 {_loadout.HandHeavyUsed():0.#}/{_hero.HandHeavy:0.#}";

        // ---- 交互 -------------------------------------------------------------------------

        void OnSlotClicked(EquipSlot slot)
        {
            EquipItem item = _loadout.FindEquipped(slot);
            if (item == null) { _info.Text = $"{_database.SlotDisplayName((int)slot)}：空"; return; }
            _selected = item;
            EquipResult result = _loadout.Unequip(slot, out EquipItem removed, out string reason);
            _info.Text = result == EquipResult.Ok ? $"已卸下 {removed.Name}" : $"{removed?.Name}：{reason}";
        }

        void OnSlotInput(EquipSlot slot, InputEvent @event)
        {
            if (@event is not InputEventMouseButton mouse || !mouse.Pressed) return;
            EquipItem item = _loadout.FindEquipped(slot);
            if (item == null) return;
            if (mouse.ButtonIndex == MouseButton.Right)
            {
                item.StrongLevel = Math.Min(item.StrongLevel + 1, 20);
                _selected = item;
                if (_avatar != null) _avatar.RefreshLook();
                OnLoadoutChanged();
                _info.Text = $"{item.Name} 强化等级 {item.StrongLevel}";
            }
            else if (mouse.ButtonIndex == MouseButton.Middle)
            {
                item.StarLevel = Math.Min(item.StarLevel + 1, 20);
                _selected = item;
                if (_avatar != null) _avatar.RefreshLook();
                OnLoadoutChanged();
                _info.Text = $"{item.Name} 星级 {item.StarLevel}";
            }
        }

        void ShowSlotInfo(EquipSlot slot)
        {
            EquipItem item = _loadout.FindEquipped(slot);
            _selected = item;
            _info.Text = item != null
                ? ItemTooltip(item)
                : $"{_database.SlotDisplayName((int)slot)}：空栏位";
        }

        void EquipFromBag(EquipItem item)
        {
            _selected = item;
            EquipResult result = _loadout.TryEquipAuto(item, out EquipSlot slot, out EquipItem replaced, out string reason);
            _info.Text = result == EquipResult.Ok
                ? $"已装备 {item.Name} → {_database.SlotDisplayName((int)slot)}" +
                  (replaced != null ? $"（换下 {replaced.Name}）" : string.Empty)
                : $"无法装备 {item.Name}：{reason}";
        }

        // ---- 展示辅助 ---------------------------------------------------------------------

        string ItemTooltip(EquipItem item)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(item.DisplayName).Append("（").Append(_database.ItemTypeDisplayName(item.ItemType)).Append("）\n");
            sb.Append("需求：");
            sb.Append(item.Def.NeedType.HasValue
                ? $"{_hero.RequirementName(item.Def.NeedType.Value)} {item.Def.NeedValue:0.##}"
                : "无");
            sb.Append("　耐久 ").Append(item.CurrentDurability).Append('/').Append(item.Def.MaxCj ?? 0);
            if (item.StrongLevel > 0) sb.Append("　强化 ").Append(item.StrongLevel);
            if (item.StarLevel > 0) sb.Append("　星级 ").Append(item.StarLevel);
            if (item.Def.Sex.HasValue && item.Def.Sex.Value != 0)
                sb.Append("　限").Append(item.Def.Sex.Value == 1 ? "男" : "女");
            if (item.Look != null) sb.Append("\n外观：").Append(item.Look.StrType).Append(" / ").Append(item.Look.Name);
            Dictionary<string, double> counter = item.BuildCounter();
            if (counter.Count > 0)
            {
                sb.Append("\n属性：");
                foreach (KeyValuePair<string, double> pair in counter)
                    sb.Append(AttrLabel(pair.Key)).Append('+').Append(pair.Value.ToString("0.##")).Append(' ');
            }
            if (!string.IsNullOrEmpty(item.Def.Desc)) sb.Append("\n").Append(item.Def.Desc);
            return sb.ToString();
        }

        /// <summary>属性键到中文标签：命中已知表就用中文，否则原样显示（Lua 的属性表另有 700 项）。</summary>
        public static string AttrLabel(string key)
            => AttrNames.TryGetValue(key, out string name) ? name : key;

        static readonly Dictionary<string, string> AttrNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GongJiD"] = "攻击下限", ["GongJiU"] = "攻击上限", ["MoShuD"] = "魔法下限", ["MoShuU"] = "魔法上限",
            ["DaoShuD"] = "道术下限", ["DaoShuU"] = "道术上限", ["GongShuD"] = "弓术下限", ["GongShuU"] = "弓术上限",
            ["CiShuD"] = "刺术下限", ["CiShuU"] = "刺术上限", ["WuFangD"] = "物防下限", ["WuFangU"] = "物防上限",
            ["MoFangD"] = "魔防下限", ["MoFangU"] = "魔防上限", ["MaxHp"] = "生命上限", ["MaxMp"] = "魔法上限",
            ["PerMaxHp"] = "生命上限%", ["PerMaxMp"] = "魔法上限%", ["PerGongJi"] = "攻击%", ["PerWuFang"] = "物防%",
            ["PerMoFang"] = "魔防%", ["PerBaoJi"] = "暴击%", ["PerBaoShang"] = "暴伤%", ["PerCiShu"] = "刺术%",
            ["PerGongSu"] = "攻速%", ["ZhunQue"] = "准确", ["ShanBi"] = "闪避", ["XingYun"] = "幸运",
            ["ShenShengFY"] = "神圣防御", ["HitAddHp"] = "命中回复", ["AtkAddHp"] = "攻击回血",
            ["ArrowRange"] = "箭矢射程", ["FixDamBoss"] = "对Boss固伤", ["PerBossDam"] = "对Boss伤害%",
        };

        Texture2D LoadIcon(string virtualPath)
        {
            if (_assets == null || string.IsNullOrEmpty(virtualPath)) return null;
            if (!_assets.Exists(virtualPath)) return null;
            return _assets.LoadTexture(virtualPath);
        }

        /// <summary>品质色：const.EQUIP_QUALITYCOLOR_CN（0 白、1 绿、2 蓝、3 紫、4 橙 …）。</summary>
        static Color QualityColor(int? showColor) => showColor switch
        {
            0 => new Color(1f, 1f, 1f),
            1 => new Color(0.4f, 0.9f, 0.4f),
            2 => new Color(0.4f, 0.7f, 1f),
            3 => new Color(0.8f, 0.5f, 1f),
            4 => new Color(1f, 0.65f, 0.2f),
            5 => new Color(1f, 0.4f, 0.4f),
            null => new Color(1f, 1f, 1f),
            _ => new Color(1f, 0.85f, 0.4f),
        };
    }
}
