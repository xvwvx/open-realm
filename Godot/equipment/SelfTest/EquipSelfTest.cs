using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MdxPreview.Core.Mdx;
using MdxPreview.Runtime;
using OpenRealm.Equipment;
using OpenRealm.Equipment.UI;

/// <summary>
/// 人物装备系统自检：headless 跑一遍数据加载、穿戴规则、外观拼接与真实资源可达性。
///
/// 运行：
///   & '...\Godot_v4.7.2-stable_mono_win64_console.exe' --headless --path '&lt;Godot 目录&gt;' ^
///       res://equipment/SelfTest/EquipSelfTest.tscn -- --files 'D:\rpg\output\files'
///
/// 退出码 0 表示全部通过。
/// </summary>
public partial class EquipSelfTest : Node
{
    int _passed;
    int _failed;
    readonly List<string> _failures = new List<string>();
    string _filesRoot = @"D:\rpg\output\files";

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--files=", StringComparison.OrdinalIgnoreCase))
                _filesRoot = arg.Substring("--files=".Length);

        GD.Print("=== 人物装备系统自检 ===");
        GD.Print($"资源根目录: {_filesRoot}");

        EquipDatabase database = null;
        try
        {
            database = EquipDatabase.Load();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"数据库加载异常: {ex}");
        }

        Check(database != null, "装备数据库加载成功");
        if (database != null)
        {
            TestDatabase(database);
            TestEquipRules(database);
            TestLookRules(database);
            TestResourceReachability(database);
            TestAvatar(database);
            TestPanel(database);
        }

        GD.Print($"=== 通过 {_passed} 项，失败 {_failed} 项 ===");
        foreach (string failure in _failures) GD.PrintErr("失败: " + failure);
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    // ---- 断言 ---------------------------------------------------------------------------

    void Check(bool condition, string what)
    {
        if (condition)
        {
            _passed++;
            GD.Print("  ok   " + what);
        }
        else
        {
            _failed++;
            _failures.Add(what);
            GD.PrintErr("  FAIL " + what);
        }
    }

    void CheckEqual(object actual, object expected, string what)
        => Check(Equals(actual?.ToString(), expected?.ToString()), $"{what}（期望 {expected}，实际 {actual}）");

    /// <summary>
    /// WC3 的附着点查找是"不区分大小写的包含"匹配：AddSpecialEffectTarget("hand left")
    /// 命中模型里的 "Hand Left Ref"。自制模型改名后仍然能用同一套挂点名。
    /// </summary>
    static bool AttachmentMatches(IEnumerable<string> names, string wanted)
        => names.Any(name => !string.IsNullOrEmpty(name) && name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);

    // ---- 1. 配置表 ----------------------------------------------------------------------

    void TestDatabase(EquipDatabase db)
    {
        GD.Print("-- 配置表 --");
        CheckEqual(db.Constants.EquipPoss.Count, 21, "装备栏位数量");
        Check(db.Shapes.Count > 800, $"外观条目数量 = {db.Shapes.Count}");
        Check(db.Items.Count > 1200, $"装备条目数量 = {db.Items.Count}");

        Check(db.SlotsForItemType(1).SequenceEqual(new[] { 3 }), "武器(1) → 栏位 3");
        Check(db.SlotsForItemType(6).SequenceEqual(new[] { 4, 8 }), "手镯(6) → 栏位 4/8");
        Check(db.SlotsForItemType(7).SequenceEqual(new[] { 5, 9 }), "戒指(7) → 栏位 5/9");
        CheckEqual(db.SlotDisplayName(3), "武器", "栏位 3 中文名");
        CheckEqual(db.SlotDisplayName(14), "翅膀", "栏位 14 中文名");

        ItemDef sword = db.GetItem(1001);
        Check(sword != null, "取到 1001 新手剑");
        if (sword != null)
        {
            CheckEqual(sword.Name, "新手剑", "1001 名字");
            CheckEqual(sword.Look?.StrType, "WQ", "1001 外观类型");
            CheckEqual(sword.Look?.Mdx, "wuqi_1_SMINAD", "1001 武器模型名");
            CheckEqual(sword.BaseAttr?["GongJiD"], 3d, "1001 基础攻击下限");
        }

        ItemDef clothes = db.GetItem(1101);
        CheckEqual(clothes?.Look?.Path1, "rw_nan_yifu_1_SMINAD", "1101 男性衣服贴图名");
        CheckEqual(clothes?.Look?.Path2, "rw_nv_yifu_1_SMINAD", "1101 女性衣服贴图名");
        CheckEqual(clothes?.Sex, 1, "1101 只限男性");

        Check(db.GetShape(NakedShapes.Cloth) != null, "裸衣外观 20 存在");
        Check(db.GetShape(NakedShapes.Cloak) != null, "裸披风外观 60 存在");
    }

    // ---- 2. 穿戴规则 --------------------------------------------------------------------

    void TestEquipRules(EquipDatabase db)
    {
        GD.Print("-- 穿戴规则 --");
        var hero = new HeroProfile { Sex = HeroSex.Male, Job = HeroJob.Warrior, Level = 60, HandHeavy = 200, BodyHeavy = 500 };
        var loadout = new EquipLoadout(db, hero);

        var sword = new EquipItem(db.GetItem(1001));
        loadout.AddToBag(sword);
        EquipResult result = loadout.TryEquipAuto(sword, out EquipSlot slot, out _, out string reason);
        CheckEqual(result, EquipResult.Ok, $"新手剑可装备（{reason}）");
        CheckEqual(slot, EquipSlot.Weapon, "新手剑进入武器栏位");
        Check(loadout.FindEquipped(EquipSlot.Weapon) == sword, "武器栏位持有该装备");
        CheckEqual(loadout.TotalStats().Get("GongJiD"), 3d, "属性汇总含 GongJiD=3");

        // 栏位不匹配
        var glove = new EquipItem(db.GetItem(1003));
        CheckEqual(loadout.CanEquip(glove, EquipSlot.Cloth, out _), EquipResult.WrongSlot, "武器不能放进衣服栏位");

        // 等级不足（10100 星月天弓 需求等级 67）
        var bow = new EquipItem(db.GetItem(10100));
        var young = new EquipLoadout(db, new HeroProfile { Sex = HeroSex.Male, Level = 1 }) ;
        CheckEqual(young.CanEquip(bow, EquipSlot.Weapon, out _), EquipResult.RequirementTooLow, "等级不足时拒绝装备");

        // 性别限制（1101 男性衣服）
        var female = new EquipLoadout(db, new HeroProfile { Sex = HeroSex.Female, Level = 60 });
        var maleCloth = new EquipItem(db.GetItem(1101));
        CheckEqual(female.CanEquip(maleCloth, EquipSlot.Cloth, out _), EquipResult.WrongSex, "女性不能穿男性衣服");

        // 卸下
        loadout.Unequip(EquipSlot.Weapon, out EquipItem removed, out _);
        Check(removed == sword && loadout.FindEquipped(EquipSlot.Weapon) == null, "卸下后栏位清空并回到背包");
        Check(loadout.Bag.Contains(sword), "卸下的装备回到背包");

        // 不可卸下（EQUIP_STATE_NOOFF = 2）
        var locked = new EquipItem(db.GetItem(1001)) { State = 2 };
        var lockedLoadout = new EquipLoadout(db, new HeroProfile { Level = 60 });
        lockedLoadout.AddToBag(locked);
        lockedLoadout.TryEquipAuto(locked, out _, out _, out _);
        CheckEqual(lockedLoadout.Unequip(EquipSlot.Weapon, out _, out _), EquipResult.ReplacedItemLocked, "NOOFF 装备无法卸下");
    }

    // ---- 3. 外观拼接 --------------------------------------------------------------------

    void TestLookRules(EquipDatabase db)
    {
        GD.Print("-- 外观拼接 --");
        var hero = new HeroProfile { Sex = HeroSex.Male, Job = HeroJob.Warrior, Level = 60 };
        var loadout = new EquipLoadout(db, hero);

        // 空装：五个部件全部回退到裸装外观
        EquipLook naked = EquipLookBuilder.Build(db, hero, loadout.Equipped);
        CheckEqual(naked.BodyModelPath, @"rw\rw_nan_SMINAD.mdx", "男性本体模型路径");
        CheckEqual(naked.TextureSlots.GetValueOrDefault(31u), @"rw\rw_nan_kuzi_0_SMINAD.blp", "空装 → 裸裤贴图槽 31");
        CheckEqual(naked.TextureSlots.GetValueOrDefault(34u), @"rw\rw_nan_yifu_0_SMINAD.blp", "空装 → 裸衣贴图槽 34");
        // 裸披风外观（Shape 60）本身就是透明件 rw\rw_touming_SMINAD.blp
        CheckEqual(naked.TextureSlots.GetValueOrDefault(35u), @"rw\rw_touming_SMINAD.blp", "空装 → 裸披风贴图槽 35");
        CheckEqual(naked.TextureSlots.GetValueOrDefault(36u), TransparentTextures.Strong, "未强化 → 透明强化层 36");
        CheckEqual(naked.Attachments.Count, 0, "空装没有挂件");

        // 女角色：同一外观取 Path2
        var female = new HeroProfile { Sex = HeroSex.Female, Job = HeroJob.Warrior, Level = 60 };
        EquipLook femaleLook = EquipLookBuilder.Build(db, female, new EquipLoadout(db, female).Equipped);
        CheckEqual(femaleLook.BodyModelPath, @"rw\rw_nv_SMINAD.mdx", "女性本体模型路径");
        CheckEqual(femaleLook.TextureSlots.GetValueOrDefault(34u), @"rw\rw_nv_yifu_0_SMINAD.blp", "女性 → 女装贴图");

        // 穿上衣服与武器
        var clothItem = new EquipItem(db.GetItem(1101)) { StrongLevel = 3, StarLevel = 2 };
        loadout.AddToBag(clothItem);
        loadout.TryEquipAuto(clothItem, out _, out _, out _);
        var sword = new EquipItem(db.GetItem(1001)) { StrongLevel = 3, StarLevel = 2 };
        loadout.AddToBag(sword);
        loadout.TryEquipAuto(sword, out _, out _, out _);

        EquipLook dressed = EquipLookBuilder.Build(db, hero, loadout.Equipped);
        CheckEqual(dressed.TextureSlots.GetValueOrDefault(34u), @"rw\rw_nan_yifu_1_SMINAD.blp", "穿衣服后槽 34 换成装备贴图");
        CheckEqual(dressed.TextureSlots.GetValueOrDefault(39u), @"jn\wuqi_qh_zs3_SMINAD.blp", "强化 3 级 → 衣服强化层 39");
        CheckEqual(dressed.TextureSlots.GetValueOrDefault(46u), @"jn\wuqi_sx_zs2_SMINAD.blp", "星级 2 → 衣服星级层 46");

        EquipAttachment weapon = dressed.Attachments.FirstOrDefault(a => a.ModelPath.StartsWith(@"wq\", StringComparison.OrdinalIgnoreCase));
        Check(weapon != null, "武器挂件已生成");
        if (weapon != null)
        {
            CheckEqual(weapon.ModelPath, @"wq\wuqi_1_SMINAD.mdx", "武器模型路径");
            CheckEqual(weapon.Attachment, EquipLookBuilder.HandRight, "HandState=0 → 右手挂点");
            CheckEqual(weapon.StrongTexture, @"jn\wuqi_qh_zs3_SMINAD.blp", "武器强化贴图 41");
            CheckEqual(weapon.StarTexture, @"jn\wuqi_sx_zs2_SMINAD.blp", "武器星级贴图 42");
        }

        // HandState 1/2/3 决定武器挂点（clsUnitHero.lua:1837-1847）
        TestHandStates(db, hero);

        // 翅膀 / 发型走 ShapeCfg，不经物品表
        var overrides = new Dictionary<EquipSlot, ShapeDef> { [EquipSlot.Wing] = db.GetShape(801) };
        EquipLook winged = EquipLookBuilder.Build(db, hero, loadout.Equipped, hairShapeId: 1, shapeOverrides: overrides);
        EquipAttachment wing = winged.Attachments.FirstOrDefault(a => a.ModelPath.StartsWith(@"cb\", StringComparison.OrdinalIgnoreCase));
        Check(wing != null && wing.Attachment == EquipLookBuilder.Chest, "翅膀挂到 chest");
        CheckEqual(wing?.ModelPath, @"cb\cb_1_SMINAD.mdx", "翅膀模型路径");
        EquipAttachment hair = winged.Attachments.FirstOrDefault(a => a.ModelPath.StartsWith(@"shape\", StringComparison.OrdinalIgnoreCase));
        Check(hair != null && hair.Attachment == EquipLookBuilder.Head, "发型挂到 head");
    }

    /// <summary>
    /// 武器挂点规则（clsUnitHero.lua:1837-1847）：
    /// 1 → hand left；2 → 左右手各一件；3 → hand left 主手 + hand left ExMdx 副手；其他 → hand right。
    /// 这些外观多数只存在于商城 ShapeCfg（没有 ItemCfg 物品），因此直接用时装配覆盖来驱动。
    /// </summary>
    void TestHandStates(EquipDatabase db, HeroProfile hero)
    {
        var loadout = new EquipLoadout(db, hero);
        foreach (int handState in new[] { 1, 2, 3 })
        {
            ShapeDef shape = db.ShapesOfType("WQ").FirstOrDefault(s => s.HandState == handState);
            if (shape == null)
            {
                Check(false, $"缺少 HandState={handState} 的武器外观");
                continue;
            }

            var overrides = new Dictionary<EquipSlot, ShapeDef> { [EquipSlot.Weapon] = shape };
            EquipLook look = EquipLookBuilder.Build(db, hero, loadout.Equipped, null, overrides);
            List<EquipAttachment> weapons = look.Attachments
                .Where(a => a.ModelPath.StartsWith(@"wq\", StringComparison.OrdinalIgnoreCase)).ToList();

            int expected = handState == 1 ? 1 : 2;
            CheckEqual(weapons.Count, expected, $"HandState={handState}（{shape.Name}）生成 {expected} 件武器挂件");
            if (weapons.Count != expected) continue;

            if (handState == 1)
            {
                CheckEqual(weapons[0].Attachment, EquipLookBuilder.HandLeft, "HandState=1 → 左手");
            }
            else if (handState == 2)
            {
                Check(weapons.Any(w => w.Attachment == EquipLookBuilder.HandLeft)
                   && weapons.Any(w => w.Attachment == EquipLookBuilder.HandRight), "HandState=2 → 左右手各一件");
            }
            else
            {
                Check(weapons.All(w => w.Attachment == EquipLookBuilder.HandLeft),
                      "HandState=3 → 主手与 ExMdx 副手都挂 hand left（照抄 Lua）");
                CheckEqual(weapons[1].ModelPath, @"wq\" + shape.ExMdx + ".mdx", "HandState=3 副手使用 ExMdx");
            }
        }
    }

    // ---- 4. 真实资源可达性 --------------------------------------------------------------

    void TestResourceReachability(EquipDatabase db)
    {
        GD.Print("-- 资源可达性 --");
        using var library = new MdxAssetLibrary();
        library.SetTextureSearchPaths(new[] { _filesRoot });
        library.RefreshDirectories();
        Check(library.IsReady, "资源搜索路径已就绪");
        Check(library.Exists(@"rw\rw_nan_SMINAD.mdx"), "人物本体模型存在");
        Check(library.Exists(@"wq\wuqi_1_SMINAD.mdx"), "新手剑模型存在");

        var hero = new HeroProfile { Sex = HeroSex.Male, Job = HeroJob.Warrior, Level = 60 };
        var loadout = new EquipLoadout(db, hero);
        foreach (ItemDef def in new[] { db.GetItem(1101), db.GetItem(1001), db.GetItem(15001) })
        {
            if (def == null) continue;
            var item = new EquipItem(def) { StrongLevel = 5, StarLevel = 5 };
            loadout.AddToBag(item);
            loadout.TryEquipAuto(item, out _, out _, out _);
        }

        var overrides = new Dictionary<EquipSlot, ShapeDef> { [EquipSlot.Wing] = db.GetShape(801) };
        // 发型目录里有已知缺口（ShapeCfg 引用的 faxing_nan_1..6 大多不存在），挑一个真实存在的。
        ShapeDef hairShape = db.ShapesOfType("FX")
            .FirstOrDefault(s => !string.IsNullOrEmpty(s.Mdx) && library.Exists(@"shape\" + s.Mdx + ".mdx"));
        Check(hairShape != null, "存在可用的发型外观");
        EquipLook look = EquipLookBuilder.Build(db, hero, loadout.Equipped, hairShape?.Id, overrides);

        int missing = 0;
        foreach (KeyValuePair<uint, string> pair in look.TextureSlots)
            if (!library.Exists(pair.Value))
            {
                missing++;
                GD.PrintErr($"    贴图槽 {pair.Key} 路径不存在: {pair.Value}");
            }
        CheckEqual(missing, 0, $"{look.TextureSlots.Count} 条贴图槽路径全部可达");

        int missingModels = 0;
        foreach (EquipAttachment attachment in look.Attachments)
            if (!library.Exists(attachment.ModelPath))
            {
                missingModels++;
                GD.PrintErr($"    挂件模型不存在: {attachment.ModelPath}（{attachment.Attachment}）");
            }
        CheckEqual(missingModels, 0, $"{look.Attachments.Count} 个挂件模型全部可达");
    }

    // ---- 5. 装备界面 --------------------------------------------------------------------

    void TestPanel(EquipDatabase db)
    {
        GD.Print("-- 装备界面 --");
        using var library = new MdxAssetLibrary();
        library.SetTextureSearchPaths(new[] { _filesRoot });
        library.RefreshDirectories();

        var hero = new HeroProfile { Name = "UI 测试", Sex = HeroSex.Male, Job = HeroJob.Warrior, Level = 60 };
        var loadout = new EquipLoadout(db, hero);
        var sword = new EquipItem(db.GetItem(1001));
        loadout.AddToBag(sword);

        var panel = new EquipPanel { Name = "TestPanel" };
        AddChild(panel);
        panel.Initialize(library, db, hero, loadout);

        Check(panel.GetChildCount() > 0, "装备面板建立节点树");
        Check(panel.Avatar != null, "面板内建人物预览");
        Check(panel.Avatar.Body != null, "预览人物加载了本体模型");
        CheckEqual(panel.Avatar.Look.TextureSlots.Count, 15, "预览外观槽位数量");

        loadout.TryEquipAuto(sword, out _, out _, out _);
        CheckEqual(panel.Avatar.AttachedModels.Count, 1, "面板内换装后预览出现武器挂件");
        CheckEqual(EquipPanel.AttrLabel("GongJiD"), "攻击下限", "属性中文名映射");
        CheckEqual(EquipPanel.AttrLabel("UnknownAttrKey"), "UnknownAttrKey", "未知属性键原样显示");

        panel.QueueFree();
    }

    // ---- 6. 端到端人物 ------------------------------------------------------------------

    void TestAvatar(EquipDatabase db)
    {
        GD.Print("-- 人物节点 --");
        using var library = new MdxAssetLibrary();
        library.SetTextureSearchPaths(new[] { _filesRoot });
        library.RefreshDirectories();

        MdxModel bodyModel = library.LoadModel(@"rw\rw_nan_SMINAD.mdx");
        Check(bodyModel != null, "加载人物本体模型");
        if (bodyModel == null) return;

        Check(bodyModel.Sequences.Any(s => string.Equals(s.Name, "Stand", StringComparison.OrdinalIgnoreCase)), "本体含 Stand 序列");

        var instance = new MdxInstance(bodyModel);
        instance.SetSequence(Math.Max(0, bodyModel.FindSequenceIndex("Stand")));
        instance.Evaluate();
        List<MdxAttachmentPose> poses = instance.CollectAttachmentPoses();
        var names = new HashSet<string>(poses.Select(p => p.Attachment.Node.Name), StringComparer.OrdinalIgnoreCase);
        foreach (string name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) GD.Print("    附着点: " + name);
        Check(AttachmentMatches(names, EquipLookBuilder.HandLeft), $"本体有 {EquipLookBuilder.HandLeft} 附着点");
        Check(AttachmentMatches(names, EquipLookBuilder.HandRight), $"本体有 {EquipLookBuilder.HandRight} 附着点");
        Check(AttachmentMatches(names, EquipLookBuilder.Chest), $"本体有 {EquipLookBuilder.Chest} 附着点");
        Check(AttachmentMatches(names, EquipLookBuilder.Head), $"本体有 {EquipLookBuilder.Head} 附着点");

        var hero = new HeroProfile { Sex = HeroSex.Male, Job = HeroJob.Warrior, Level = 60 };
        var loadout = new EquipLoadout(db, hero);
        var avatar = new HeroAvatar { Name = "SelfTestAvatar" };
        AddChild(avatar);
        avatar.Initialize(library, db, hero, loadout);

        Check(avatar.Body != null, "HeroAvatar 建立本体节点");
        CheckEqual(avatar.Look.TextureSlots.Count, 15, "空装时 15 个贴图槽（5 部件 × 3 层）");

        var sword = new EquipItem(db.GetItem(1001));
        loadout.AddToBag(sword);
        loadout.TryEquipAuto(sword, out _, out _, out _);
        CheckEqual(avatar.AttachedModels.Count, 1, "装备武器后出现 1 个挂件模型");

        var cloth = new EquipItem(db.GetItem(1101));
        loadout.AddToBag(cloth);
        loadout.TryEquipAuto(cloth, out _, out _, out _);
        Check(avatar.Body != null, "换装后本体仍有效");
        CheckEqual(avatar.Look.TextureSlots.GetValueOrDefault(34u), @"rw\rw_nan_yifu_1_SMINAD.blp", "换装后衣服贴图槽已更新");

        if (avatar.Look.Notes.Count > 0)
            foreach (string note in avatar.Look.Notes) GD.Print("    外观提示: " + note);

        avatar.QueueFree();
    }
}
