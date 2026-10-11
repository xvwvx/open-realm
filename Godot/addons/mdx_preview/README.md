# MDX Preview（Godot 4 C# 编辑器插件）

魔兽争霸 III **MDX 模型预览器**：在 Godot 编辑器里直接预览 `.mdx` 模型，读取游戏目录的 **MPQ** 归档与多路径 **BLP** 贴图，支持**全部 MDX 内部功能**（几何/材质层/骨骼动画/PRE2 粒子/RIBB 飘带/事件对象/相机/碰撞体/附着点/灯光），可切换并播放动画、旋转模型，并可把模型烘焙导出到 **Godot 子文件夹**。

插件目录：`addons/mdx_preview/`（本工程即“Godot 子文件夹”）。

---

## 1. 快速开始

1. 用 Godot 4.7.2 (.NET/mono) 打开本目录（`Godot/project.godot`）。
2. 编辑器左侧会出现 **MDX Preview** 停靠面板（插件已在 `project.godot` 的 `editor_plugins` 中启用）。
3. 在面板里配置：
   - **游戏路径**：指向魔兽争霸 III 安装目录（例如 `D:\Game\Warcraft3`），点“扫描 MPQ”会自动按引擎优先级填入
     `War3Patch.mpq → War3xLocal.mpq → War3x.mpq → War3Local.mpq → war3.mpq`。
   - **BLP 搜索路径**：可添加任意多个目录（解包出来的贴图、自制素材），查找顺序在 MPQ 之前。
4. 在“模型”一栏填入虚拟路径（例如 `units\orc\Peon\Peon.mdx`）或点“浏览”选择本机文件，然后点“打开模型”。

## 2. 功能一览

| 分类 | 支持内容 |
|---|---|
| 打开方式 | MPQ 虚拟路径（大小写不敏感、自动 `\`/`/` 归一）、`res://` 路径、本机绝对路径；`.mdl` 自动回退到 `.mdx` |
| 几何 | `GEOS` 全部子块（VRTX/NRMS/UVBS(双套 UV)/PTYP/PCNT/PVTX/GNDX/MTGC/MATS）、每序列 bounds、selectable 位 |
| 材质 | 层栈（`NextPass` 串联）、7 种 filter 模式（None/Transparent/Blend/Additive/AddAlpha/Modulate/Modulate2x）、Unshaded/TwoSided/Unfogged/NoDepthTest/NoDepthSet、`KMTA` 透明动画、`KMTF` 贴图槽动画、`KMTE` 自发光 |
| 贴图 | BLP1（调色板 + alpha 1/4/8、JPEG 内容）/ BLP2（DXT1/3/5、BGRA）、mipmap、wrap 标志、`TXAN` UV 平移/旋转/缩放、替换贴图 ID（队伍颜色/辉光）、`.tga/.png/.jpg/.dds` 松散文件 |
| 骨骼动画 | 全节点层级求值（T/R/S 轨道 × None/Linear/Hermite/Bezier，四元数 squad）、`GLBS` 全局序列、序列切换/循环/非循环、逐帧 `GEOA` alpha/颜色、billboard 与轴锁定 billboard |
| 粒子 | `PRE2` 发射器（累加器发射、纬度锥方向、速度/变化量/重力/寿命、三段颜色与 alpha、尺寸缩放、头/尾/双段渲染、squirt、`Rows×Columns` 图集 UV 动画、优先级、替换贴图）与 `PREM` |
| 飘带 | `RIBB` 边历史、寿命、重力、上下高度、`KRAL/KRCO/KRTX/KRVS`、按发射器材质绘制 |
| 事件对象 | `EVTS` 事件键穿越检测 + `SND`/`SPN`/`SPL`/`FPT`/`UBR`（经 `SpawnData.slk`/`SplatData.slk`/`UberSplatData.slk`/`AnimSounds.slk` 解析） |
| 相机/灯光/其它 | `CAMS` 模型相机预览、`LITE` 灯光（omni/direct/ambient + 动画颜色/强度/范围）、`ATCH` 附着点标记、`CLID` 碰撞体线框 |
| 预览交互 | 轨道旋转/平移/缩放、自动旋转、模型自身 XYZ 旋转、序列下拉、播放/暂停/循环/速度/时间轴拖拽、队伍颜色、显示开关 |
| 导出 | 烘焙为 Godot 场景（`Skeleton3D` + `MeshInstance3D` + `AnimationPlayer` 每序列一条动画 + 贴图 PNG + `info.txt`），默认输出到 `res://Godot/` |

> **BLP1 JPEG（type=0）**：Warcraft III 的 BLP1 JPEG 是**单个 4 分量 JPEG**（SOF `Nf=4`、全部 1x1 采样、无 restart），只支持 **baseline（SOF0/SOF1）与 progressive（SOF2，多 scan + 谱选择 + 逐次逼近）** 两种；
> 第 4 个分量就是 alpha，分量按原始 `B,G,R,A` 存放（`renderer/r_blp1.c` 为此给 stb_image 定义
> `STBI_WARCRAFT3_BLP_JPEG_RGBA_BANDS`，见 `renderer/stb/stb_image.h:3957`）。
> Godot 的 `Image.LoadJpgFromBuffer` **无法解这类文件**（libjpeg-turbo 判为 CMYK/YCCK 直接拒绝，实测 `ERR_PARSE_ERROR`），
> 因此 core 层自带 `Blp/JpegWarcraft3Decoder.cs`（baseline + progressive 统一入口）：按 `r_blp1.c` + patched stb 的语义（不做 YCbCr 变换，
> `R=band2, G=band1, B=band0, A=band3`，8bit 线性 alpha、不预乘）直接产出 `Kind=Rgba8` / `Rgba`，
> 与 C 参考**逐字节一致**（37/37 真实样本，含 17 张 progressive；另有 22 个 jpegtran 生成的 progressive fixture 全部一致）。`JpegData`/`JpegBandCount`/`JpegHasAlphaBand` 保留作诊断；
> 3 分量（普通 YCbCr）JPEG 仍走 `Kind=Jpeg` 交给 Godot。不支持的形态（lossless/arithmetic SOF、restart、非 1x1 采样、12bit、DNL）抛 `BlpException` 并给出原因。
> 复现脚本见 `tests/BlpProbe/ref/`（`compare-jpeg.ps1`、`run-godot-jpeg-probe.ps1`）。
## 3. 目录结构

```
addons/mdx_preview/
  plugin.cfg                 插件描述
  MdxPreviewPlugin.cs        EditorPlugin：挂载停靠面板
  core/                      纯 C# 格式层（不依赖 Godot，可被单元测试工程直接编译）
    Math3D/Numerics.cs       Vec3/Quat/Mat4（**列向量约定：平移在第 4 列**，与 Godot Transform3D 一致）
    Mpq/MpqArchive.cs        MPQ 读取（哈希/块表、加密、zlib/ADPCM/Huffman/PKWARE 解压）
    Blp/BlpImage.cs          BLP1/BLP2 解码入口 + 解码结果契约
    Blp/Blp1Decoder.cs       BLP1（共享头 JPEG / 调色板 alpha 0/1/4/8，r_blp1.c）
    Blp/Blp2Decoder.cs       BLP2（encoding 1/2/3、DXT1/3/5，r_blp2.c）
    Blp/DxtDecoder.cs        S3TC 块解码（DXT1 含 1bit alpha / DXT3 / DXT5）
    Blp/JpegWarcraft3Decoder.cs  WC3 4 分量 JPEG 解码：baseline + progressive（r_blp1.c + patched stb 语义）
    Blp/BlpIo.cs             小端读取、mip 边界校验、调色板与 alpha 展开
    Mdx/MdxModel.cs          MDX 数据模型（chunk 结构）
    Mdx/MdxLoader.cs         MDX chunk 解析
    Mdx/MdxTrackSampler.cs   关键帧插值（r_mdx_interpolation.c 的移植）
    Mdx/MdxInstance.cs       动画实例：节点层级、序列、GEOA/TXAN 采样、附着点、事件键
    Assets/FileSources.cs    虚拟路径 + MPQ/目录/内存源 + 引擎式回退解析
    Sheet/SlkFile.cs         SLK 表格解析（事件对象数据）
  runtime/                   Godot 表现层
    MdxAssetLibrary.cs       MPQ/BLP 加载与缓存、替换贴图、队伍颜色
    MdxMeshBuilder.cs        geoset → ArrayMesh（骨骼索引/权重按 r_mdx_buffer.c 规则）
    MdxMaterialBuilder.cs    材质层 → StandardMaterial3D（filter/cull/unshaded/depth）
    MdxModelNode.cs          模型节点：骨架、几何、材质、灯光、gizmo、每帧驱动
    MdxParticleSystem.cs     PRE2/PREM 粒子
    MdxRibbonSystem.cs       RIBB 飘带
    MdxEventSystem.cs        EVTS 事件对象
    MdxSplatSystem.cs        地面贴花（SPL/FPT/UBR）
    MdxPreviewView.cs        预览场景（环境/地面/灯光/轨道相机/模型）
    MdxPreviewSettings.cs    配置持久化（res://addons/mdx_preview/settings.cfg）
  editor/                    停靠面板 UI
  export/                    烘焙导出
```

**坐标约定**：MDX 是 Z-up。`MdxModelNode` 自身旋转 `-90°X`，因此其所有子节点（粒子/飘带/事件）都在 MDX 坐标系里工作；只有跨出该节点的相机与包围盒计算需要转换。

## 4. 与 open-realm C 实现的关系

本插件的格式层是 `D:\Dev\C++\open-realm` 中 C 实现的**纯 C# 移植**，逐项对照以下文件，便于互相验证：

| C# | C 参考 |
|---|---|
| `core/Mpq/MpqArchive.cs` | `common/mpq.c` |
| `core/Blp/BlpImage.cs` | `renderer/r_blp1.c`, `renderer/r_blp2.c` |
| `core/Mdx/MdxLoader.cs` | `games/warcraft-3/renderer/mdx/r_mdx_load.c` |
| `core/Mdx/MdxTrackSampler.cs` | `.../r_mdx_interpolation.c` |
| `core/Mdx/MdxInstance.cs` | `.../r_mdx_anim.c` |
| `runtime/MdxMeshBuilder.cs` | `.../r_mdx_buffer.c` |
| `runtime/MdxParticleSystem.cs` | `.../r_mdx_particles.c` + `renderer/r_particles.c` |
| `runtime/MdxRibbonSystem.cs` | `.../r_mdx_ribbons.c` + `renderer/r_trail.c` |
| `runtime/MdxEventSystem.cs` | `.../r_mdx_anim.c` 的事件部分 + `docs/games/warcraft-3/mdx-event-objects.md` |
| `runtime/MdxMaterialBuilder.cs` | `.../r_mdx_geoset.c` 的层混合/标志处理 |

## 5. 验证

`tests/` 下是自检工程（不参与插件运行时）：

```
dotnet build MdxPreview.csproj                         # 插件本体
dotnet run --project tests/AnimProbe                    # 动画核心数学/层级/采样回归测试
dotnet run --project tests/CoreTests -- mdx-dump "<mpq>:<path>"   # 格式层命令行探针
dotnet run --project tests/BlpProbe -- selfcheck                   # BLP 解码器自检（合成用例 + 负例）
dotnet run --project tests/BlpProbe -- errtests                    # 错误处理负例
dotnet run --project tests/BlpProbe -- batch <list.tsv> <outDir> png   # 批量解码 + PNG（对照见 tests/BlpProbe/ref）
```

端到端渲染验证（与仓库自带 `mdxtool.exe` 参考渲染对照）：

```
& 'D:\Program Files\Godot_v4\Godot_v4.7.2-stable_mono_win64_console.exe' \
  --path '<本目录>' res://tests/verify/VerifyRenderer.tscn -- \
  --mpq 'D:\Game\Warcraft3\war3.mpq' --model 'units\orc\Peon\Peon.mdx' \
  --sequence 0 --frame 0.35 --out 'D:\tmp\peon_godot.png' --size 1024

& 'D:\Dev\C++\open-realm\build-cmake\bin\mdxtool.exe' \
  -mpq 'D:\Game\Warcraft3\war3.mpq' -model 'units\orc\Peon\Peon.mdx' \
  --anim Stand --frame 1000 -o 'D:\tmp\peon_reference.png'
```

参考数据示例（`mdxtool --info`，Peon.mdx）：22 个序列、TEXS=3、GEOS=1、GEOA=1、BONE=2、HELP=1、ATCH=2、PIVT=55、CLID=1。

## 6. 已知限制与注意事项（实测踩坑记录）

### 贴图方向（本轮根因，已修正）
- `FlipTexturesVertically` 默认 **false**：MDX 的 UV 以 V=0 指贴图顶部，Godot 的 `Image` 第 0 行同样是顶部行，因此**不能翻转**；设为 true 会让每张贴图采样错位一半（Peon 头部采到背包、自制皮肤错乱、熊猫脸镜像分裂）。
- 判别方法：用**高分辨率**渲染与 `mdxtool -o` 对照（400px 小图的 imgdiff 均值不足以判别：明暗接近的毛色会掩盖错位）。

### 运行时
- **脚本类必须有可无参构造**：Godot 在“程序集重载”（改完 C# 重新构建后启动编辑器）时会用无参构造重建挂在节点上的 C# 实例，缺失会报 `MissingMemberException` + `InvalidCastException`。
  `MdxPreviewView`/`MdxParticleSystem`/`MdxRibbonSystem`/`MdxEventSystem`/`MdxSplatSystem` 都提供无参构造 + `Initialize(...)`。
- **骨骼名不能含 `:` 或 `/`**：`Skeleton3D.AddBone` 会静默返回 -1，骨骼数为 0、蒙皮失效。插件会把 MDX 节点名里的这些字符替换为 `_`。
- **`MeshInstance3D.Skeleton` 要用相对 NodePath**：节点入树前 `GetPath()` 不可靠，插件入树后用 `GetPathTo(skeleton)`。
- **目录选择回调是 `DirSelected`**：`FileDialog(FileMode.OpenDir)` 通过 `dir_selected` 返回路径；只连 `FileSelected` 会表现为“选了目录没反应”（游戏路径/BLP 路径/导出目录都受影响，已修）。
- **离屏截图不要 `Image.FlipY()`**：Godot 4.7 的 `SubViewport.GetTexture().GetImage()` 已是自上而下，再翻转会让整个预览上下颠倒（已与 `mdxtool -o` 参考图比对确认）。
- **偶发需要 `dotnet build --no-incremental`**：Godot.NET.Sdk 的输出在 `.godot/mono/temp/bin/`，个别增量构建会产出缺少 `editor/`、`tests/` 类型的不完整程序集，表现为脚本类找不到。验证脚本会检查程序集是否含预期类型。

### 导出（MdxExporter）限制
- 粒子（PREM/PRE2）与飘带（RIBB）不参与烘焙；UV 动画（TXAN）与翻页贴图（KMTF）取导出瞬间的静帧。
- 队伍颜色/辉光是导出时的固有色，已写入 PNG；之后改队伍颜色不会影响已导出的资源。
- 每个模型一个 `.tscn`（Peon 22 序列 / 55 骨骼约 28 MB 文本）。若要更小体积，可把各 `Animation` 拆成独立 `.tres`（当前未做）。

### 工程内其它目录
- `tests/skinprobe` 是**独立 Godot 工程**（用于验证蒙皮/截图机制），编辑器会提示 “Detected another project.godot”，无害。
- 若工程内存在自行解包的资源目录（例如 `files/`），会显著拉长编辑器首次文件扫描时间，建议移到工程外。
