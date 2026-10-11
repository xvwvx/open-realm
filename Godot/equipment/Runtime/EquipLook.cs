using System;
using System.Collections.Generic;

namespace OpenRealm.Equipment
{
    /// <summary>挂在人物附着点上的模型（武器 / 翅膀 / 发型）。</summary>
    public sealed class EquipAttachment
    {
        public string Attachment { get; set; } = string.Empty;
        public string ModelPath { get; set; } = string.Empty;
        /// <summary>武器强化层贴图（槽位 41），无则透明件。</summary>
        public string StrongTexture { get; set; }
        /// <summary>武器星级层贴图（槽位 42），无则透明件。</summary>
        public string StarTexture { get; set; }
        public string SourceName { get; set; }

        public override string ToString() => $"{Attachment} <- {ModelPath}";
    }

    /// <summary>一次穿戴状态解析出来的人物外观。</summary>
    public sealed class EquipLook
    {
        /// <summary>替换贴图槽位（WC3 的 SetUnitTexture id）→ 虚拟贴图路径。</summary>
        public Dictionary<uint, string> TextureSlots { get; } = new Dictionary<uint, string>();
        public List<EquipAttachment> Attachments { get; } = new List<EquipAttachment>();
        /// <summary>解析过程中的问题（缺资源/异常配置），UI 与自检会读取。</summary>
        public List<string> Notes { get; } = new List<string>();
        public string BodyModelPath { get; set; } = string.Empty;

        public int BodyTextureSlots
        {
            get
            {
                int count = 0;
                foreach (uint slot in TextureSlots.Keys) if (slot < 100) count++;
                return count;
            }
        }

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("本体 ").Append(BodyModelPath);
            foreach (KeyValuePair<uint, string> pair in TextureSlots)
                sb.Append("\n贴图槽 ").Append(pair.Key).Append(" → ").Append(pair.Value);
            foreach (EquipAttachment attachment in Attachments)
                sb.Append("\n挂点 ").Append(attachment);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 把"当前穿着的装备"翻译成人物外观，规则照抄 Lua：
    /// clsUnitHero.lua:1775-1861（场景侧）与 clsCompModelRole.lua:125-216（界面侧）。
    /// </summary>
    public static class EquipLookBuilder
    {
        sealed class PartRule
        {
            public EquipSlot Slot;
            /// <summary>主贴图槽（31 裤 / 32 护手 / 33 鞋 / 34 衣 / 35 披风）。</summary>
            public uint Base;
            /// <summary>强化发光层槽位。</summary>
            public uint Strong;
            /// <summary>星级层槽位。</summary>
            public uint Star;
            /// <summary>空栏回退的裸装外观 id。</summary>
            public int NakedShape;
            /// <summary>无星级贴图时的透明件。</summary>
            public string StarFallback;
        }

        static readonly PartRule[] Parts =
        {
            new PartRule { Slot = EquipSlot.Trousers, Base = 31, Strong = 36, Star = 44, NakedShape = NakedShapes.Trousers, StarFallback = TransparentTextures.Trousers },
            new PartRule { Slot = EquipSlot.Glove,    Base = 32, Strong = 37, Star = 45, NakedShape = NakedShapes.Glove,    StarFallback = TransparentTextures.Glove },
            new PartRule { Slot = EquipSlot.Shoes,    Base = 33, Strong = 38, Star = 43, NakedShape = NakedShapes.Shoes,    StarFallback = TransparentTextures.Shoes },
            new PartRule { Slot = EquipSlot.Cloth,    Base = 34, Strong = 39, Star = 46, NakedShape = NakedShapes.Cloth,    StarFallback = TransparentTextures.Cloth },
            new PartRule { Slot = EquipSlot.Cloak,    Base = 35, Strong = 40, Star = 47, NakedShape = NakedShapes.Cloak,    StarFallback = TransparentTextures.Cloak },
        };

        /// <summary>武器主手/副手附着点（clsUnitHero.lua:1837-1847）。</summary>
        public const string HandLeft = "hand left";
        public const string HandRight = "hand right";
        public const string Chest = "chest";
        public const string Head = "head";

        public static EquipLook Build(
            EquipDatabase database,
            HeroProfile hero,
            IReadOnlyDictionary<EquipSlot, EquipItem> equipped,
            int? hairShapeId = null,
            IReadOnlyDictionary<EquipSlot, ShapeDef> shapeOverrides = null)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            hero ??= new HeroProfile();
            var look = new EquipLook { BodyModelPath = hero.BodyModelPath };
            int jobId = (int)hero.Job;

            foreach (PartRule rule in Parts)
            {
                EquipItem item = Find(equipped, rule.Slot);
                ShapeDef shape = ResolveShape(database, item, rule.NakedShape, shapeOverrides, rule.Slot);
                if (shape == null)
                {
                    look.Notes.Add($"栏位 {rule.Slot} 找不到外观（装备 {item?.Id.ToString() ?? "-"}）");
                    continue;
                }

                string texture = ResolvePartTexture(shape, hero);
                if (string.IsNullOrEmpty(texture))
                {
                    look.Notes.Add($"外观 {shape.Id}（{shape.Name}）没有可用于{hero.Sex}的部件贴图");
                    continue;
                }
                look.TextureSlots[rule.Base] = texture;

                int strongLevel = item?.StrongLevel ?? 0;
                int starLevel = item?.StarLevel ?? 0;
                look.TextureSlots[rule.Strong] = StrongTexture(database, jobId, strongLevel) ?? TransparentTextures.Strong;
                look.TextureSlots[rule.Star] = StarTexture(database, jobId, starLevel) ?? rule.StarFallback;
            }

            AddWing(database, hero, Find(equipped, EquipSlot.Wing), shapeOverrides, look);
            AddWeapons(database, hero, Find(equipped, EquipSlot.Weapon), shapeOverrides, look);
            AddHair(database, hero, hairShapeId, look);
            return look;
        }

        static EquipItem Find(IReadOnlyDictionary<EquipSlot, EquipItem> equipped, EquipSlot slot)
            => equipped != null && equipped.TryGetValue(slot, out EquipItem item) ? item : null;

        static ShapeDef ResolveShape(
            EquipDatabase database,
            EquipItem item,
            int nakedShapeId,
            IReadOnlyDictionary<EquipSlot, ShapeDef> overrides,
            EquipSlot slot)
        {
            if (overrides != null && overrides.TryGetValue(slot, out ShapeDef overwritten) && overwritten != null)
                return overwritten;
            if (item?.Look != null) return item.Look;
            return database.GetShape(nakedShapeId);
        }

        /// <summary>男用 Path1、女用 Path2；披风只有 Path（clsUnitHero.lua:1781-1815）。</summary>
        static string ResolvePartTexture(ShapeDef shape, HeroProfile hero)
        {
            string name = null;
            if (!string.IsNullOrEmpty(shape.Path)) name = shape.Path;
            else name = hero.Sex == HeroSex.Female ? shape.Path2 : shape.Path1;
            if (string.IsNullOrEmpty(name)) name = hero.Sex == HeroSex.Female ? shape.Path1 : shape.Path2;
            return string.IsNullOrEmpty(name) ? null : @"rw\" + name + ".blp";
        }

        static void AddWing(EquipDatabase database, HeroProfile hero, EquipItem item,
            IReadOnlyDictionary<EquipSlot, ShapeDef> overrides, EquipLook look)
        {
            ShapeDef shape = ResolveShape(database, item, 0, overrides, EquipSlot.Wing);
            if (shape == null || string.IsNullOrEmpty(shape.Mdx)) return;   // 裸装：不挂翅膀
            look.Attachments.Add(new EquipAttachment
            {
                Attachment = Chest,
                ModelPath = @"cb\" + shape.Mdx + ".mdx",
                SourceName = shape.Name,
            });
        }

        static void AddWeapons(EquipDatabase database, HeroProfile hero, EquipItem item,
            IReadOnlyDictionary<EquipSlot, ShapeDef> overrides, EquipLook look)
        {
            ShapeDef shape = ResolveShape(database, item, 0, overrides, EquipSlot.Weapon);
            if (shape == null || string.IsNullOrEmpty(shape.Mdx)) return;

            int jobId = (int)hero.Job;
            string strong = StrongTexture(database, jobId, item?.StrongLevel ?? 0) ?? TransparentTextures.Strong;
            string star = StarTexture(database, jobId, item?.StarLevel ?? 0) ?? TransparentTextures.Strong;

            void Add(string attachment, string modelName)
            {
                look.Attachments.Add(new EquipAttachment
                {
                    Attachment = attachment,
                    ModelPath = @"wq\" + modelName + ".mdx",
                    StrongTexture = strong,
                    StarTexture = star,
                    SourceName = shape.Name,
                });
            }

            switch (shape.HandState ?? 0)
            {
                case 1:  // 左手
                    Add(HandLeft, shape.Mdx);
                    break;
                case 2:  // 双手各一件
                    Add(HandLeft, shape.Mdx);
                    Add(HandRight, shape.Mdx);
                    break;
                case 3:  // 主手 + ExMdx 副手（Lua 两件都挂 hand left）
                    Add(HandLeft, shape.Mdx);
                    if (!string.IsNullOrEmpty(shape.ExMdx)) Add(HandLeft, shape.ExMdx);
                    else look.Notes.Add($"外观 {shape.Id}（{shape.Name}）HandState=3 但没有 ExMdx");
                    break;
                default: // 右手
                    Add(HandRight, shape.Mdx);
                    break;
            }
        }

        static void AddHair(EquipDatabase database, HeroProfile hero, int? hairShapeId, EquipLook look)
        {
            if (!hairShapeId.HasValue || hairShapeId.Value == 0) return;
            ShapeDef shape = database.GetShape(hairShapeId.Value);
            if (shape == null || string.IsNullOrEmpty(shape.Mdx))
            {
                look.Notes.Add($"发型外观 {hairShapeId.Value} 不存在或没有模型名");
                return;
            }
            look.Attachments.Add(new EquipAttachment
            {
                Attachment = Head,
                ModelPath = @"shape\" + shape.Mdx + ".mdx",
                SourceName = shape.Name,
            });
        }

        /// <summary>强化贴图：const.WEAPON_STRONG_PATH[JobType][EffLv]（const1.lua:1083-1193）。</summary>
        public static string StrongTexture(EquipDatabase database, int jobId, int level)
        {
            if (level <= 0) return null;
            if (database.Constants.WeaponStrongPath.TryGetValue(jobId.ToString(), out Dictionary<string, string> table)
                && table.TryGetValue(level.ToString(), out string path) && !string.IsNullOrEmpty(path))
                return path;
            return null;
        }

        /// <summary>星级贴图：const.EQUIP_STAR_PATH[JobType][StarLv]（const1.lua:1194-1304）。</summary>
        public static string StarTexture(EquipDatabase database, int jobId, int level)
        {
            if (level <= 0) return null;
            if (database.Constants.EquipStarPath.TryGetValue(jobId.ToString(), out Dictionary<string, string> table)
                && table.TryGetValue(level.ToString(), out string path) && !string.IsNullOrEmpty(path))
                return path;
            return null;
        }
    }
}
