using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace OpenRealm.Equipment
{
    // ---------------------------------------------------------------------------------------
    // 配置表模型：字段与 Godot/tools/lua_equip_export.py 的产物一一对应（camelCase JSON）。
    // ---------------------------------------------------------------------------------------

    /// <summary>const1.lua 中的装备相关常量（栏位、类型、职业、强化/星级贴图表）。</summary>
    public sealed class EquipConstants
    {
        public Dictionary<string, EquipSlotDef> EquipPoss { get; set; } = new Dictionary<string, EquipSlotDef>();
        public int? MaxPos { get; set; }
        public Dictionary<string, EquipItemTypeDef> ItemTypes { get; set; } = new Dictionary<string, EquipItemTypeDef>();
        public Dictionary<string, List<int>> EquipType2Pos { get; set; } = new Dictionary<string, List<int>>();
        public Dictionary<string, int> EquipPos2Type { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> ShapeStrType2ItemType { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, string> JobCn { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> SexCn { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, string>> WeaponStrongPath { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, Dictionary<string, string>> EquipStarPath { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public EquipStateDefs EquipStates { get; set; } = new EquipStateDefs();
    }

    public sealed class EquipSlotDef
    {
        public string Key { get; set; } = string.Empty;
        public string Const { get; set; } = string.Empty;
        public int Id { get; set; }
    }

    public sealed class EquipItemTypeDef
    {
        public string Key { get; set; } = string.Empty;
        public string Const { get; set; } = string.Empty;
        public int Id { get; set; }
        public string Cn { get; set; } = string.Empty;
    }

    public sealed class EquipStateDefs
    {
        public int Normal { get; set; }
        public int NoShow { get; set; } = 1;
        public int NoOff { get; set; } = 2;
        public int Jd { get; set; } = 4;
        public int Lock { get; set; } = 8;
    }

    /// <summary>ShapeCfg.lua 的一条外观：StrType 决定拼哪个目录、用哪个槽位。</summary>
    public sealed class ShapeDef
    {
        public int Id { get; set; }
        public string StrType { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; }
        /// <summary>武器/翅膀/发型的裸模型名（运行时拼 wq\、cb\、shape\）。</summary>
        public string Mdx { get; set; }
        /// <summary>HandState==3 时的副手模型名。</summary>
        public string ExMdx { get; set; }
        /// <summary>武器挂点：1 左手，2 双手，3 左手+ExMdx，其他右手。</summary>
        public int? HandState { get; set; }
        /// <summary>披风贴图（不分性别）。</summary>
        public string Path { get; set; }
        /// <summary>男/女部件贴图（YF/HS/KZ/XZ）。</summary>
        public string Path1 { get; set; }
        public string Path2 { get; set; }
        /// <summary>时装自带属性（与装备 BaseAttr 是两套独立加成）。</summary>
        public Dictionary<string, double> Attr { get; set; }
        public string Properties { get; set; }
        public int? NeedMallCfgId { get; set; }
        public string Desc { get; set; }
    }

    /// <summary>ItemCfg.lua 的一条可穿戴物品。</summary>
    public sealed class ItemDef
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; }
        public int ItemType { get; set; }
        public int? MainType { get; set; }
        public int? ItemLv { get; set; }
        /// <summary>需求类型：1 等级，2 攻击，3 魔法，4 道术，5 弓术，6 刺术，7 转生。</summary>
        public int? NeedType { get; set; }
        public double? NeedValue { get; set; }
        public int? JobTag { get; set; }
        /// <summary>品质色索引（const.EQUIP_QUALITYCOLOR_CN）。</summary>
        public int? ShowColor { get; set; }
        public int? Shape { get; set; }
        public Dictionary<string, double> BaseAttr { get; set; }
        public int? Price { get; set; }
        public int? Heavy { get; set; }
        public int? MaxInlay { get; set; }
        public int? MaxCj { get; set; }
        public string Desc { get; set; }
        public int? Sex { get; set; }
        public int? Tlv { get; set; }
        public string TypeEx { get; set; }
        public List<int> Pos { get; set; }
        /// <summary>由 Shape 关联出的外观条目（导出时已内联）。</summary>
        public ShapeDef Look { get; set; }

        public int PrimarySlot => Pos != null && Pos.Count > 0 ? Pos[0] : 0;
    }

    /// <summary>装备数据库：加载 Data/*.json 并提供查询。</summary>
    public sealed class EquipDatabase
    {
        public const string DefaultDataDir = "res://equipment/Data";

        static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        readonly Dictionary<int, ShapeDef> _shapes = new Dictionary<int, ShapeDef>();
        readonly Dictionary<int, ItemDef> _items = new Dictionary<int, ItemDef>();
        readonly List<ItemDef> _itemsByShapeLook = new List<ItemDef>();

        public EquipConstants Constants { get; private set; } = new EquipConstants();
        public string SourceDir { get; private set; } = string.Empty;
        public IReadOnlyDictionary<int, ShapeDef> Shapes => _shapes;
        public IReadOnlyDictionary<int, ItemDef> Items => _items;

        public static EquipDatabase Load(string dataDir = DefaultDataDir)
        {
            var db = new EquipDatabase { SourceDir = dataDir };
            db.Constants = ReadJson<EquipConstants>(dataDir + "/constants.json") ?? new EquipConstants();
            Dictionary<string, ShapeDef> shapes = ReadJson<Dictionary<string, ShapeDef>>(dataDir + "/shapes.json");
            if (shapes != null)
                foreach (KeyValuePair<string, ShapeDef> pair in shapes)
                    if (int.TryParse(pair.Key, out int id) && pair.Value != null)
                    {
                        pair.Value.Id = id;
                        db._shapes[id] = pair.Value;
                    }
            Dictionary<string, ItemDef> items = ReadJson<Dictionary<string, ItemDef>>(dataDir + "/items.json");
            if (items != null)
                foreach (KeyValuePair<string, ItemDef> pair in items)
                    if (int.TryParse(pair.Key, out int id) && pair.Value != null)
                    {
                        pair.Value.Id = id;
                        db._items[id] = pair.Value;
                    }
            return db;
        }

        static T ReadJson<T>(string path) where T : class
        {
            if (!FileAccess.FileExists(path))
            {
                GD.PushError($"[装备] 缺少配置表 {path}；先运行 Godot/tools/lua_equip_export.py 生成。");
                return null;
            }
            string text = FileAccess.GetFileAsString(path);
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(text, Options);
            }
            catch (JsonException ex)
            {
                GD.PushError($"[装备] 解析 {path} 失败: {ex.Message}");
                return null;
            }
        }

        public ShapeDef GetShape(int id) => _shapes.TryGetValue(id, out ShapeDef shape) ? shape : null;

        public ItemDef GetItem(int id) => _items.TryGetValue(id, out ItemDef item) ? item : null;

        public IEnumerable<ItemDef> ItemsOfType(int itemType)
        {
            foreach (ItemDef item in _items.Values)
                if (item.ItemType == itemType) yield return item;
        }

        public IEnumerable<ShapeDef> ShapesOfType(string strType)
        {
            foreach (ShapeDef shape in _shapes.Values)
                if (string.Equals(shape.StrType, strType, StringComparison.OrdinalIgnoreCase)) yield return shape;
        }

        /// <summary>栏位中文名：栏位 -&gt; 物品类型 -&gt; ITEM_TYPE_CN 的中文。</summary>
        public string SlotDisplayName(int slotId)
        {
            if (!Constants.EquipPos2Type.TryGetValue(slotId.ToString(), out int itemType)) return $"栏位{slotId}";
            if (Constants.ItemTypes.TryGetValue(itemType.ToString(), out EquipItemTypeDef def) && !string.IsNullOrEmpty(def.Cn))
                return def.Cn;
            return $"类型{itemType}";
        }

        public string ItemTypeDisplayName(int itemType)
            => Constants.ItemTypes.TryGetValue(itemType.ToString(), out EquipItemTypeDef def) && !string.IsNullOrEmpty(def.Cn)
                ? def.Cn : $"类型{itemType}";

        /// <summary>某物品类型可以放进哪些栏位（手镯/戒指各有两格）。</summary>
        public IReadOnlyList<int> SlotsForItemType(int itemType)
            => Constants.EquipType2Pos.TryGetValue(itemType.ToString(), out List<int> slots) ? slots : Array.Empty<int>();

        public string JobKey(int jobId) => jobId switch
        {
            1 => "ZS", 2 => "FS", 3 => "DS", 4 => "GJ", 5 => "CK", _ => "ZS",
        };

        public string JobName(int jobId)
            => Constants.JobCn.TryGetValue(jobId.ToString(), out string name) ? name : $"职业{jobId}";
    }
}
