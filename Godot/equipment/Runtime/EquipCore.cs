using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenRealm.Equipment
{
    /// <summary>装备栏位。数值与 const1.lua:730-751 的 const.ROLE_EQUIP_POS_* 一致。</summary>
    public enum EquipSlot
    {
        None = 0,
        Helm = 1,        // ROLE_EQUIP_POS_TK
        Cloth = 2,       // ROLE_EQUIP_POS_YF
        Weapon = 3,      // ROLE_EQUIP_POS_WQ
        Bracelet1 = 4,   // ROLE_EQUIP_POS_SZ1
        Ring1 = 5,       // ROLE_EQUIP_POS_JZ1
        Necklace = 6,    // ROLE_EQUIP_POS_XL
        Glove = 7,       // ROLE_EQUIP_POS_HS
        Bracelet2 = 8,   // ROLE_EQUIP_POS_SZ2
        Ring2 = 9,       // ROLE_EQUIP_POS_JZ2
        Trousers = 10,   // ROLE_EQUIP_POS_KZ
        Shoes = 11,      // ROLE_EQUIP_POS_XZ
        Artifact = 12,   // ROLE_EQUIP_POS_LQ
        Cloak = 13,      // ROLE_EQUIP_POS_PF
        Wing = 14,       // ROLE_EQUIP_POS_CB
        Gem = 15,        // ROLE_EQUIP_POS_BS
        Orb = 16,        // ROLE_EQUIP_POS_SQ_BZ
        Medal = 17,      // ROLE_EQUIP_POS_SQ_XZ
        Seal = 18,       // ROLE_EQUIP_POS_SQ_YX
        Tiger = 19,      // ROLE_EQUIP_POS_SQ_HF
        Drum = 20,       // ROLE_EQUIP_POS_SQ_ZG
        Banner = 21,     // ROLE_EQUIP_POS_SQ_ZQ
    }

    /// <summary>物品细分类型。数值与 const1.lua:287-299、876-881 的 const.ITEM_TYPE_* 一致。</summary>
    public enum EquipItemType
    {
        Weapon = 1, Cloth = 2, Helm = 3, Glove = 4, Necklace = 5, Bracelet = 6, Ring = 7,
        Trousers = 8, Shoes = 9, Cloak = 10, Wing = 11, Gem = 12, Artifact = 13,
        Orb = 151, Medal = 152, Seal = 153, Tiger = 154, Drum = 155, Banner = 156,
    }

    public enum HeroJob { Warrior = 1, Mage = 2, Taoist = 3, Archer = 4, Assassin = 5 }

    public enum HeroSex { Male = 1, Female = 2 }

    /// <summary>
    /// 空栏位的外观回退（clsUnitHero.lua:1863-1879 / clsCompModelRole.lua:218-234）：
    /// 没有装备该部件时穿"裸装"外观，而不是保留上一件。
    /// </summary>
    public static class NakedShapes
    {
        public const int Cloth = 20;
        public const int Glove = 30;
        public const int Trousers = 40;
        public const int Shoes = 50;
        public const int Cloak = 60;
    }

    /// <summary>透明占位贴图：没有强化/星级层时使用（const1.lua 的缺省值）。</summary>
    public static class TransparentTextures
    {
        public const string Strong = @"rw\rw_touming_SMINAD.blp";
        public const string Cloth = @"rw\rw_touming2_yifu_SMINAD.blp";
        public const string Trousers = @"rw\rw_touming2_kuzi_SMINAD.blp";
        public const string Shoes = @"rw\rw_touming2_xiezi_SMINAD.blp";
        public const string Glove = @"rw\rw_touming2_shoutao_SMINAD.blp";
        public const string Cloak = @"rw\rw_touming2_pifeng_SMINAD.blp";
    }

    /// <summary>
    /// 人物档案。属性/负重体系在 Lua 里来自完整的 UnitAttr/DataAttr 计数器系统，
    /// 这里只保留穿戴校验真正需要的那几项，其余交给调用方设置。
    /// </summary>
    public sealed class HeroProfile
    {
        public string Name { get; set; } = "无名侠客";
        public HeroSex Sex { get; set; } = HeroSex.Male;
        public HeroJob Job { get; set; } = HeroJob.Warrior;
        public int Level { get; set; } = 1;
        public int Rebirth { get; set; }

        /// <summary>腕力上限：武器 Heavy 与之比较（clsItemManager.lua:2094-2101）。</summary>
        public double HandHeavy { get; set; } = 200;
        /// <summary>身体负重上限：衣服/裤子/鞋/护手/披风/翅膀等共同占用。</summary>
        public double BodyHeavy { get; set; } = 500;

        /// <summary>主属性值（攻击/魔法/道术/弓术/刺术），供 NeedType 2..6 比较。</summary>
        public Dictionary<int, double> MainAttributes { get; set; } = new Dictionary<int, double>();

        /// <summary>NeedType/NeedValue 的对照值（const1.lua 需求类型：1 等级 2 攻击 3 魔法 4 道术 5 弓术 6 刺术 7 转生）。</summary>
        public double RequirementValue(int needType)
        {
            switch (needType)
            {
                case 1: return Level;
                case 7: return Rebirth;
                case 2: case 3: case 4: case 5: case 6:
                    return MainAttributes.TryGetValue(needType, out double value) ? value : 0d;
                default: return 0d;
            }
        }

        public string RequirementName(int needType) => needType switch
        {
            1 => "等级", 2 => "攻击力", 3 => "魔法力", 4 => "道术", 5 => "弓术", 6 => "刺术", 7 => "转生等级",
            _ => $"需求{needType}",
        };

        /// <summary>rw 人物本体模型：男 rw_nan_SMINAD.mdx / 女 rw_nv_SMINAD.mdx。</summary>
        public string BodyModelPath => Sex == HeroSex.Female ? @"rw\rw_nv_SMINAD.mdx" : @"rw\rw_nan_SMINAD.mdx";

        public string JobKey => Job switch
        {
            HeroJob.Mage => "FS", HeroJob.Taoist => "DS", HeroJob.Archer => "GJ", HeroJob.Assassin => "CK", _ => "ZS",
        };
    }

    /// <summary>一件装备实例。对应 Lua 的 clsItemEquip（只保留外观与校验需要的状态）。</summary>
    public sealed class EquipItem
    {
        public EquipItem(ItemDef def)
        {
            Def = def ?? throw new ArgumentNullException(nameof(def));
            Look = def.Look;
            CurrentDurability = def.MaxCj ?? 0;
            State = 0;
        }

        public ItemDef Def { get; }
        /// <summary>当前生效的外观：默认取物品的 Shape，时装系统可以覆盖它。</summary>
        public ShapeDef Look { get; set; }
        public int StrongLevel { get; set; }
        public int StarLevel { get; set; }
        public int RankLevel { get; set; }
        public int CurrentDurability { get; set; }
        /// <summary>const.EQUIP_STATE_* 位标记（1 不可见/诅咒，2 不可卸下，4 已鉴定，8 锁定）。</summary>
        public int State { get; set; }
        public EquipSlot Slot { get; set; } = EquipSlot.None;

        public int Id => Def.Id;
        public string Name => Def.Name;
        public int ItemType => Def.ItemType;
        public bool IsEquipped => Slot != EquipSlot.None;
        public bool IsNoOff => (State & 2) != 0;
        public bool IsCursed => (State & 1) != 0;
        public bool IsLocked => (State & 8) != 0;
        public bool DurabilityValid => CurrentDurability > 0;

        public string DisplayName => IsCursed ? Name + "(诅咒)" : Name;

        /// <summary>武器/翅膀等占用身体负重的部件（武器占腕力，不占身体）。</summary>
        public double BodyHeavy => ItemType == (int)EquipItemType.Weapon ? 0d : Def.Heavy ?? 0;

        /// <summary>本件装备贡献的属性计数器：装备 BaseAttr + 外观时装 Attr。</summary>
        public Dictionary<string, double> BuildCounter()
        {
            var counter = new Dictionary<string, double>();
            Accumulate(counter, Def.BaseAttr);
            if (Look != null) Accumulate(counter, Look.Attr);
            return counter;
        }

        static void Accumulate(Dictionary<string, double> target, Dictionary<string, double> source)
        {
            if (source == null) return;
            foreach (KeyValuePair<string, double> pair in source)
            {
                target.TryGetValue(pair.Key, out double current);
                target[pair.Key] = current + pair.Value;
            }
        }

        public override string ToString() => $"{Name}(#{Id})";
    }

    /// <summary>属性汇总：与 Lua 的 m_CounterEquip 对应（每个键的原始计数器）。</summary>
    public sealed class EquipStats
    {
        readonly Dictionary<string, double> _counter = new Dictionary<string, double>();

        public IReadOnlyDictionary<string, double> Counter => _counter;

        public double Get(string key) => _counter.TryGetValue(key, out double value) ? value : 0d;

        public int Count => _counter.Count;

        public void Add(string key, double value)
        {
            _counter.TryGetValue(key, out double current);
            _counter[key] = current + value;
        }

        public void Merge(IReadOnlyDictionary<string, double> other)
        {
            if (other == null) return;
            foreach (KeyValuePair<string, double> pair in other) Add(pair.Key, pair.Value);
        }

        public static EquipStats FromItems(IEnumerable<EquipItem> items)
        {
            var stats = new EquipStats();
            if (items == null) return stats;
            foreach (EquipItem item in items)
                if (item != null) stats.Merge(item.BuildCounter());
            return stats;
        }

        /// <summary>按数值降序排列，供 UI 展示。</summary>
        public IEnumerable<KeyValuePair<string, double>> Sorted()
            => _counter.Where(pair => Math.Abs(pair.Value) > 1e-9).OrderByDescending(pair => Math.Abs(pair.Value));
    }

    public enum EquipResult
    {
        Ok = 0,
        UnsupportedItem,
        WrongSlot,
        RequirementTooLow,
        WrongSex,
        WrongJob,
        HandHeavyTooLow,
        BodyHeavyFull,
        ReplacedItemLocked,
        SlotEmpty,
        UnknownItem,
    }

    /// <summary>穿戴/卸下结果的中文说明。</summary>
    public static class EquipResultText
    {
        public static string Describe(EquipResult result) => result switch
        {
            EquipResult.Ok => "可以装备",
            EquipResult.UnsupportedItem => "该物品不是可穿戴装备",
            EquipResult.WrongSlot => "不能装备在这个位置",
            EquipResult.RequirementTooLow => "需求不足",
            EquipResult.WrongSex => "性别不符",
            EquipResult.WrongJob => "职业不符",
            EquipResult.HandHeavyTooLow => "腕力不够",
            EquipResult.BodyHeavyFull => "身体负重不够",
            EquipResult.ReplacedItemLocked => "被替换的装备已锁定，无法取下",
            EquipResult.SlotEmpty => "该栏位没有装备",
            EquipResult.UnknownItem => "未知物品",
            _ => result.ToString(),
        };
    }

    /// <summary>穿戴规则的严格程度。默认值跟随 Lua 的检查项（Lua 穿戴时不校验 JobTag）。</summary>
    public sealed class EquipmentOptions
    {
        public bool EnforceJobTag { get; set; }
        public bool EnforceSex { get; set; } = true;
        public bool EnforceWeight { get; set; } = true;
        public bool EnforceRequirement { get; set; } = true;
        /// <summary>耐久为 0 的装备不可穿戴（Lua 中 IsCjValid 影响使用，穿戴检查另有入口）。</summary>
        public bool EnforceDurability { get; set; }
    }

    /// <summary>装备栏位 + 背包 + 穿戴规则。对应 Lua 的 clsUnitHero 躯体列表与 clsItemManager 校验。</summary>
    public sealed class EquipLoadout
    {
        readonly Dictionary<EquipSlot, EquipItem> _equipped = new Dictionary<EquipSlot, EquipItem>();
        readonly List<EquipItem> _bag = new List<EquipItem>();

        public EquipLoadout(EquipDatabase database, HeroProfile hero = null)
        {
            Database = database ?? throw new ArgumentNullException(nameof(database));
            Hero = hero ?? new HeroProfile();
        }

        public EquipDatabase Database { get; }
        public HeroProfile Hero { get; }
        public EquipmentOptions Options { get; } = new EquipmentOptions();

        public event Action Changed;

        public IReadOnlyDictionary<EquipSlot, EquipItem> Equipped => _equipped;
        public IReadOnlyList<EquipItem> Bag => _bag;

        public EquipItem FindEquipped(EquipSlot slot)
            => _equipped.TryGetValue(slot, out EquipItem item) ? item : null;

        public void AddToBag(EquipItem item)
        {
            if (item == null) return;
            item.Slot = EquipSlot.None;
            _bag.Add(item);
            RaiseChanged();
        }

        public bool RemoveFromBag(EquipItem item) => _bag.Remove(item);

        /// <summary>栏位类型 + 需求 + 性别 + 负重 + 被替换件是否可卸（clsItemManager.lua:2066-2131）。</summary>
        public EquipResult CanEquip(EquipItem item, EquipSlot slot, out string reason)
        {
            reason = string.Empty;
            if (item?.Def == null)
            {
                reason = "未知物品";
                return EquipResult.UnknownItem;
            }

            IReadOnlyList<int> allowed = Database.SlotsForItemType(item.ItemType);
            if (allowed.Count == 0)
            {
                reason = $"{Database.ItemTypeDisplayName(item.ItemType)} 没有对应的装备栏位";
                return EquipResult.UnsupportedItem;
            }
            if (!allowed.Contains((int)slot))
            {
                reason = $"「{item.Name}」是{Database.ItemTypeDisplayName(item.ItemType)}，不能装备在{Database.SlotDisplayName((int)slot)}栏位";
                return EquipResult.WrongSlot;
            }

            if (Options.EnforceRequirement && item.Def.NeedType.HasValue && item.Def.NeedValue.HasValue)
            {
                int needType = item.Def.NeedType.Value;
                double required = item.Def.NeedValue.Value;
                double current = Hero.RequirementValue(needType);
                if (current + 1e-9 < required)
                {
                    reason = $"需要{Hero.RequirementName(needType)} {required:0.##}（当前 {current:0.##}）";
                    return EquipResult.RequirementTooLow;
                }
            }

            if (Options.EnforceSex && item.Def.Sex.HasValue && item.Def.Sex.Value != 0 && item.Def.Sex.Value != (int)Hero.Sex)
            {
                bool femaleOnly = item.Def.Sex.Value == (int)HeroSex.Female;
                reason = femaleOnly ? "只有女性角色可以装备" : "只有男性角色可以装备";
                return EquipResult.WrongSex;
            }

            if (Options.EnforceJobTag && item.Def.JobTag.HasValue && item.Def.JobTag.Value != 0 && item.Def.JobTag.Value != (int)Hero.Job)
            {
                reason = $"只有{Database.JobName(item.Def.JobTag.Value)}可以装备";
                return EquipResult.WrongJob;
            }

            if (Options.EnforceDurability && !item.DurabilityValid)
            {
                reason = "耐久度已耗尽，需要先修理";
                return EquipResult.RequirementTooLow;
            }

            EquipItem replaced = FindEquipped(slot);
            // 换装时先扣掉被换下件的负重，再判断新件是否放得下（clsItemManager.lua:2107-2113）。
            if (replaced != null && replaced.IsNoOff)
            {
                reason = $"「{replaced.Name}」无法取下";
                return EquipResult.ReplacedItemLocked;
            }

            if (Options.EnforceWeight)
            {
                if (item.ItemType == (int)EquipItemType.Weapon)
                {
                    double heavy = item.Def.Heavy ?? 0;
                    if (heavy > Hero.HandHeavy + 1e-9)
                    {
                        reason = $"腕力不够（需要 {heavy:0.##}，当前 {Hero.HandHeavy:0.##}）";
                        return EquipResult.HandHeavyTooLow;
                    }
                }
                else
                {
                    double used = BodyHeavyUsed() - (replaced?.BodyHeavy ?? 0);
                    double total = used + item.BodyHeavy;
                    if (total > Hero.BodyHeavy + 1e-9)
                    {
                        reason = $"身体负重不够（需要 {total:0.##}，上限 {Hero.BodyHeavy:0.##}）";
                        return EquipResult.BodyHeavyFull;
                    }
                }
            }

            reason = "可以装备";
            return EquipResult.Ok;
        }

        public EquipResult TryEquip(EquipItem item, EquipSlot slot, out EquipItem replaced, out string reason)
        {
            replaced = null;
            EquipResult result = CanEquip(item, slot, out reason);
            if (result != EquipResult.Ok) return result;

            replaced = FindEquipped(slot);
            if (replaced != null)
            {
                _equipped.Remove(slot);
                replaced.Slot = EquipSlot.None;
                _bag.Add(replaced);
            }
            _bag.Remove(item);
            item.Slot = slot;
            _equipped[slot] = item;
            RaiseChanged();
            return EquipResult.Ok;
        }

        /// <summary>手镯/戒指这类双栏位：自动选择空栏，都满则替换第一格。</summary>
        public EquipResult TryEquipAuto(EquipItem item, out EquipSlot slot, out EquipItem replaced, out string reason)
        {
            replaced = null;
            slot = EquipSlot.None;
            IReadOnlyList<int> slots = Database.SlotsForItemType(item?.ItemType ?? 0);
            if (slots.Count == 0)
            {
                reason = "该物品没有对应的装备栏位";
                return EquipResult.UnsupportedItem;
            }
            EquipSlot target = EquipSlot.None;
            foreach (int candidate in slots)
            {
                var candidateSlot = (EquipSlot)candidate;
                if (FindEquipped(candidateSlot) == null) { target = candidateSlot; break; }
            }
            if (target == EquipSlot.None) target = (EquipSlot)slots[0];
            slot = target;
            return TryEquip(item, target, out replaced, out reason);
        }

        public EquipResult Unequip(EquipSlot slot, out EquipItem removed, out string reason)
        {
            removed = null;
            if (!_equipped.TryGetValue(slot, out EquipItem item))
            {
                reason = "该栏位没有装备";
                return EquipResult.SlotEmpty;
            }
            if (item.IsNoOff)
            {
                reason = $"「{item.Name}」无法取下";
                return EquipResult.ReplacedItemLocked;
            }
            _equipped.Remove(slot);
            item.Slot = EquipSlot.None;
            _bag.Add(item);
            removed = item;
            reason = "已卸下";
            RaiseChanged();
            return EquipResult.Ok;
        }

        /// <summary>已装备件的属性合计（对应 clsUnitHero:UpdateEquipAttr）。</summary>
        public EquipStats TotalStats() => EquipStats.FromItems(_equipped.Values);

        public double BodyHeavyUsed()
        {
            double used = 0;
            foreach (EquipItem item in _equipped.Values) used += item.BodyHeavy;
            return used;
        }

        public double HandHeavyUsed()
        {
            double used = 0;
            foreach (EquipItem item in _equipped.Values)
                if (item.ItemType == (int)EquipItemType.Weapon) used += item.Def.Heavy ?? 0;
            return used;
        }

        /// <summary>按栏位顺序返回已装备件，UI 用。</summary>
        public IEnumerable<KeyValuePair<EquipSlot, EquipItem>> EquippedInSlotOrder()
            => _equipped.OrderBy(pair => (int)pair.Key);

        public void RaiseChanged() => Changed?.Invoke();
    }
}
