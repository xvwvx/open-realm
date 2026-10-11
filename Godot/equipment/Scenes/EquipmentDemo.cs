using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using MdxPreview.Runtime;
using OpenRealm.Equipment;
using OpenRealm.Equipment.UI;

/// <summary>
/// 人物装备系统演示：加载游戏资源 → 建立人物与背包 → 显示装备界面。
///
/// 运行：
///   & '...\Godot_v4.7.2-stable_mono_win64_console.exe' --path '<Godot 目录>' ^
///       res://equipment/Scenes/EquipmentDemo.tscn -- --files=D:\rpg\output\files
///
/// 加 --smoke 可以让场景在若干帧后自动退出（用于无人值守的冒烟验证）。
/// </summary>
public partial class EquipmentDemo : Node
{
    MdxAssetLibrary _library;
    EquipDatabase _database;
    EquipPanel _panel;
    string _filesRoot = @"D:\rpg\output\files";
    string _shotPath;
    bool _smoke;
    bool _showHair;
    int _frames;

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--files=", StringComparison.OrdinalIgnoreCase)) _filesRoot = arg.Substring("--files=".Length);
            else if (arg == "--smoke") _smoke = true;
            else if (arg == "--hair") _showHair = true;
            else if (arg.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase)) _shotPath = arg.Substring("--shot=".Length);
        }

        _library = new MdxAssetLibrary();
        _library.SetTextureSearchPaths(new[] { _filesRoot });
        _library.RefreshDirectories();
        if (!_library.IsReady)
            GD.PushError($"[装备演示] 资源目录不可用：{_filesRoot}");

        _database = EquipDatabase.Load();

        HeroProfile hero = BuildHero();
        var loadout = new EquipLoadout(_database, hero);
        SeedBag(loadout);

        _panel = new EquipPanel { Name = "EquipPanel" };
        AddChild(_panel);
        string fontPath = Path.Combine(_filesRoot, "Fonts", "font.ttf");
        _panel.Initialize(_library, _database, hero, loadout, File.Exists(fontPath) ? fontPath : null);

        DressInitialLook();

        GD.Print($"[装备演示] 栏位 {loadout.Equipped.Count} 件已装备，背包 {loadout.Bag.Count} 件；资源根 {_filesRoot}");
    }

    public override void _Process(double delta)
    {
        if (!_smoke && _shotPath == null) return;
        if (++_frames < 90) return;

        if (_shotPath != null)
        {
            Image image = GetViewport().GetTexture().GetImage();
            if (image != null && !image.IsEmpty())
            {
                Error error = image.SavePng(_shotPath);
                GD.Print($"[装备演示] 截图 {_shotPath} ({image.GetWidth()}x{image.GetHeight()}, {error})");
            }
            else
            {
                GD.PushError("[装备演示] 截图失败：视口没有内容（headless 渲染驱动无法出图）");
            }
        }

        GD.Print($"[装备演示] 运行 {_frames} 帧结束；已装备 {_panel.Loadout.Equipped.Count} 件，挂件 {_panel.Avatar?.AttachedModels.Count ?? 0} 个");
        GetTree().Quit(0);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape) GetTree().Quit(0);
    }

    HeroProfile BuildHero()
    {
        var hero = new HeroProfile
        {
            Name = "演示角色",
            // 女性部件贴图（rw_nv_*）在本数据集里完整，男性衣服贴图 rw_nan_yifu_0..2 缺 JPEG 帧头，
            // 默认用女性角色能让演示直接看到换装贴图；性别分支本身两边都实现。
            Sex = HeroSex.Female,
            Job = HeroJob.Warrior,
            Level = 120,
            HandHeavy = 400,
            BodyHeavy = 1200,
        };
        for (int needType = 2; needType <= 6; needType++) hero.MainAttributes[needType] = 400;
        return hero;
    }

    /// <summary>放入一批有代表性的装备：武器（含不同 HandState 的时装外观）、五件套、饰品。</summary>
    void SeedBag(EquipLoadout loadout)
    {
        foreach (int id in new[] { 1001, 1002, 1003, 15001, 15002, 16001, 1101, 1102, 1201, 1301, 1401 })
            AddItem(loadout, id, strong: 0, star: 0);

        // 从外观表里挑几件时装武器（ShapeCfg 里有模型但物品表没有的条目）
        foreach (ShapeDef shape in _database.ShapesOfType("WQ").Where(s => s.Mdx != null).Take(4))
        {
            ItemDef def = _database.Items.Values.FirstOrDefault(i => i.Look != null && i.Look.Id == shape.Id);
            if (def != null) AddItem(loadout, def.Id, strong: 3, star: 2);
        }
    }

    void AddItem(EquipLoadout loadout, int itemId, int strong, int star)
    {
        ItemDef def = _database.GetItem(itemId);
        if (def == null) return;
        var item = new EquipItem(def) { StrongLevel = strong, StarLevel = star };
        loadout.AddToBag(item);
    }

    /// <summary>开局穿一套能立刻看到外观拼接的装备，并演示时装披风与发型。</summary>
    void DressInitialLook()
    {
        foreach (EquipItem item in _panel.Loadout.Bag.ToList())
        {
            if (item.ItemType == (int)EquipItemType.Weapon && _panel.Loadout.FindEquipped(EquipSlot.Weapon) == null)
                _panel.Loadout.TryEquipAuto(item, out _, out _, out _);
            else if (item.ItemType == (int)EquipItemType.Cloth && _panel.Loadout.FindEquipped(EquipSlot.Cloth) == null)
                _panel.Loadout.TryEquipAuto(item, out _, out _, out _);
        }

        EquipItem weapon = _panel.Loadout.FindEquipped(EquipSlot.Weapon);
        if (weapon != null) { weapon.StrongLevel = 5; weapon.StarLevel = 5; }

        // 披风与翅膀在物品表里没有对应物品（走商城时装），用时装配覆盖演示。
        if (_panel.Avatar != null)
        {
            _panel.Avatar.SetShapeOverride(EquipSlot.Cloak, _database.GetShape(601));
            _panel.Avatar.SetShapeOverride(EquipSlot.Wing, _database.GetShape(801));
            // 发型是额外外观（不在 21 个栏位里），模型自带发光粒子，默认关闭以免遮住人物；
            // 需要时用 --hair 打开。
            if (_showHair)
            {
                ShapeDef hair = _database.ShapesOfType("FX")
                    .FirstOrDefault(s => !string.IsNullOrEmpty(s.Mdx) && _library.Exists(@"shape\" + s.Mdx + ".mdx"));
                if (hair != null) _panel.Avatar.SetHair(hair.Id);
            }
        }
        _panel.RefreshAll();
    }
}
