# Godot/ — MDX 预览工程（“Godot 子文件夹”）

本目录是 **Godot 4.7.2 (.NET/mono) 工程**，交付物全部落在这一层“Godot 子文件夹”内：

- `addons/mdx_preview/` —— 插件本体（魔兽争霸 III MDX 模型预览：MPQ / BLP / MDX 全功能、PRE2 粒子与 RIBB 飘带、
  事件对象 SPN/SPL/FPT/UBR/SND、动画播放与模型旋转、游戏路径与多 BLP 搜索路径配置、导出烘焙到 `res://Godot/`）。
  功能清单与踩坑记录见 [addons/mdx_preview/README.md](addons/mdx_preview/README.md)。
- `equipment/` —— **人物装备系统**（参考《玛法岁月》Lua：21 个栏位、穿戴规则、属性汇总、
  部件贴图槽 31-47 与武器/翅膀/发型挂点的人物外观拼接、装备界面、演示场景与 headless 自检）。
  设计与运行手册见 [docs/装备系统-设计与实现.md](docs/装备系统-设计与实现.md)，模块说明见 [equipment/README.md](equipment/README.md)。
- `tools/` —— 配置导出工具：[lua_equip_export.py](tools/lua_equip_export.py) 把 `const1.lua`/`ShapeCfg.lua`/`ItemCfg.lua`
  转成 `equipment/Data/*.json`。
- `docs/` —— 装备系统的调研底稿与设计文档（Lua 侧机制、资源侧清单、实现说明）。
- `tests/` —— 验证工程：核心层控制台自测（`AnimProbe`、`CoreTests`）、格式探针（`MpqProbe`/`BlpProbe`/`MdxProbe`/`SlkProbe`/`ParticleProbe`）、
  Godot 端到端验证（`verify/`：模型渲染、SkinningProbe、EventProbe、DockProbe、`export_probe/`）、
  与仓库 C 工具对照的脚本（`compare_mdxtool.ps1`）与参考图（`reference/`）。

## 使用

```powershell
# 1. 编译插件（首次或大改后用 --no-incremental 更稳）
dotnet build Godot/MdxPreview.csproj --no-incremental

# 2. 用 Godot 4.7.2 mono 打开本目录（project.godot），左侧出现 “MDX Preview” 面板
& 'D:\Program Files\Godot_v4\Godot_v4.7.2-stable_mono_win64_console.exe' --path 'D:\Dev\C++\open-realm\Godot' --editor

# 3. 「设置」页：游戏路径 = D:\Game\Warcraft3 → 「扫描 MPQ」（自动按引擎优先级填入 5 个归档）
#    「BLP 搜索路径」可添加任意多个目录（排在 MPQ 之前查找）
# 4. 「模型」页：路径填 units\orc\Peon\Peon.mdx 或点「打开模型…」选择本机文件
# 5. 预览区：左键拖拽旋转、中/右键平移、滚轮缩放；动画下拉可切换序列并播放/拖时间轴
# 6. 「设置」页底部：导出到 Godot 子文件夹（默认 res://Godot）
```

> 若点击按钮后仍看到**旧的行号**堆栈（例如 `MdxPreviewDock.cs:670`），说明编辑器在用旧程序集：
> 先关闭编辑器 → `dotnet build Godot/MdxPreview.csproj --no-incremental` → 再打开编辑器。

## 验证

```powershell
dotnet run --project Godot/tests/AnimProbe                       # 动画核心回归（矩阵/枢轴/插值/层级）
dotnet run --project Godot/tests/CoreTests -- mdx-dump "<mpq>:<path>"
powershell -NoProfile -ExecutionPolicy Bypass -File Godot/tests/compare_mdxtool.ps1 `
    -Models 'units\orc\Peon\Peon.mdx,units\human\Footman\Footman.mdx'   # 与 C 版 mdxtool 逐项对照
```

端到端渲染（真实 MPQ → PNG，可与 `mdxtool -o` 参考图对照）：

```powershell
& 'D:\Program Files\Godot_v4\Godot_v4.7.2-stable_mono_win64_console.exe' `
  --path 'D:\Dev\C++\open-realm\Godot' res://tests/verify/VerifyRenderer.tscn -- `
  --mpq 'D:\Game\Warcraft3\war3.mpq' --model 'units\orc\Peon\Peon.mdx' `
  --sequence 0 --frame 0.12 --size 512 --zoom 0.5 --out 'D:\tmp\peon.png'
```

证据索引、参考图与“已验证/未验证”清单见 [tests/reference/README.md](tests/reference/README.md)。

## 当前状态（交付快照）

| 项 | 状态 |
|---|---|
| 插件编译 | 0 错误（`dotnet build MdxPreview.csproj --no-incremental`） |
| 编辑器加载 | `--headless --editor` 实测：面板挂载、4 个分页构建完成、无脚本错误 |
| MPQ 读取 | 4 个真实归档 196 文件 SHA256 与 C 实现逐字节一致；1.28 GB 全量 survey 仅 1 例“双方都失败”的坏扇区 |
| BLP 解码 | 70 个样本 0 失败；BLP1 JPEG（含 4 分量）20/20 与 C 参考逐字节一致 |
| MDX 解析 | 59 个真实模型 0 失败；与 `mdxtool --dump-all` 52358 项检查 0 不一致；用户自定义模型（尾部 5 字节垃圾）可正常加载 |
| 动画求值 | 与 C 插值器 111258 个采样点最大偏差 1e-6 |
| 预览渲染 | Peon / Farm / GreatHall（含粒子）/ UI 面板均正确出图，纹理缺失为 0 |
| 导出 | 真实 Peon 导出 66/66 检查通过（场景 + 22 个动画 + 贴图 PNG + info.txt） |
| 已知未验证 | RIBB 飘带的移动发射器出图、`--headless` 下 AnimationPlayer 回写骨骼姿态、Reforged(v900+) 资产 |
