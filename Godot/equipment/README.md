# equipment/ —— 人物装备系统

参考《玛法岁月 16.0.10》反编译 Lua 实现的人物装备系统：栏位、穿脱规则、属性汇总、
基于 `rw`/`wq`/`cb`/`shape` 资源的外观拼接，以及一个可交互的装备界面。

完整说明（数据流、规则出处、已知缺口、扩展指引）见 [../docs/装备系统-设计与实现.md](../docs/装备系统-设计与实现.md)。

## 快速开始

```powershell
# 1) 生成配置（Lua → JSON）
python Godot/tools/lua_equip_export.py

# 2) 编译
dotnet build Godot/MdxPreview.csproj

# 3) 打开演示界面（资源目录默认 D:\rpg\output\files）
& 'D:\Program Files\Godot_v4\Godot_v4.7.2-stable_mono_win64_console.exe' `
  --path 'D:\Dev\C++\open-realm\Godot' `
  res://equipment/Scenes/EquipmentDemo.tscn -- --files=D:\rpg\output\files

# 4) headless 自检（79 项断言，退出码 0 = 全通过）
& 'D:\Program Files\Godot_v4\Godot_v4.7.2-stable_mono_win64_console.exe' `
  --headless --path 'D:\Dev\C++\open-realm\Godot' `
  res://equipment/SelfTest/EquipSelfTest.tscn -- --files=D:\rpg\output\files
```

## 最小用法

```csharp
using OpenRealm.Equipment;

EquipDatabase db = EquipDatabase.Load();                       // res://equipment/Data
var hero = new HeroProfile { Sex = HeroSex.Male, Job = HeroJob.Warrior, Level = 60 };
var loadout = new EquipLoadout(db, hero);
loadout.AddToBag(new EquipItem(db.GetItem(1001)));              // 新手剑
loadout.TryEquipAuto(loadout.Bag[0], out EquipSlot slot, out EquipItem replaced, out string reason);

EquipLook look = EquipLookBuilder.Build(db, hero, loadout.Equipped);
// look.TextureSlots: 31-35 部件 / 36-40 强化 / 43-47 星级
// look.Attachments : wq\*→hand left|right, cb\*→chest, shape\*→head

var avatar = new HeroAvatar();
AddChild(avatar);
avatar.Initialize(assets, db, hero, loadout);                   // assets 已指向 D:\rpg\output\files
```

## 目录

| 路径 | 说明 |
|---|---|
| `Data/` | 导出产物：`constants.json`（栏位/类型/职业/强化星级贴图表）、`shapes.json`（外观）、`items.json`（装备，内联 look） |
| `Runtime/EquipData.cs` | 配置模型与 `EquipDatabase` |
| `Runtime/EquipCore.cs` | 栏位枚举、`HeroProfile`、`EquipItem`、`EquipStats`、`EquipLoadout`（规则） |
| `Runtime/EquipLook.cs` | `EquipLookBuilder`：贴图槽与挂点规则 |
| `Runtime/HeroAvatar.cs` | 人物节点：本体模型 + 贴图槽 + 挂件 |
| `UI/EquipPanel.cs` | 装备界面（纯代码构建） |
| `Scenes/EquipmentDemo.*` | 演示场景（`--smoke` 自动退出） |
| `SelfTest/EquipSelfTest.*` | headless 自检 |
