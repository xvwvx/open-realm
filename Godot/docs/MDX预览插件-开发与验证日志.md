# MDX 预览插件 · 完整开发与验证日志

> 交付位置：`D:\Dev\C++\open-realm\Godot\`（即需求里说的“Godot 子文件夹”）
> 插件：`addons/mdx_preview/`；目标引擎：**Godot 4.7.2 (.NET / mono)**，目标框架 `net8.0`
> 最后构建标识：**`2026-10-05 uv-orientation-fixed`**（插件启动时打印在控制台）

---

## 0. 一句话状态

插件已完成并可用：MPQ / BLP（含渐进式 JPEG）/ MDX 全量解析、骨骼动画、PRE2 粒子与 RIBB 飘带、事件对象、材质分层与替换贴图、编辑器 Dock（配置游戏路径与多 BLP 路径、动画播放、模型旋转、显示开关）、导出烘焙到 `res://Godot/`。
**本轮定位并修复了最后的纹理错位根因**（贴图 V 翻转默认值错误），并用高分辨率渲染 + C 参考实现（`mdxtool`）对照完成回归。

---

## 1. 构建与启动

```powershell
# 干净重建（推荐：编辑器/构建偶有陈旧产物）
Remove-Item D:\Dev\C++\open-realm\Godot\.godot\mono\temp\obj,D:\Dev\C++\open-realm\Godot\.godot\mono\temp\bin -Recurse -Force -ErrorAction SilentlyContinue
dotnet build D:\Dev\C++\open-realm\Godot\MdxPreview.csproj --no-incremental

# 打开工程（左侧出现 “MDX Preview” 面板）
& 'D:\Program Files\Godot_v4\Godot_v4.7.2-stable_mono_win64_console.exe' --path 'D:\Dev\C++\open-realm\Godot' --editor
```

启动后控制台应出现：

```
[MDX Preview] 面板构建完成：视口=SubViewport+独立 World3D，预览节点=True，分页=4 个。
[MDX Preview] 预览面板已挂载（父节点: @EditorDock@xxxxx），构建标识=2026-10-05 uv-orientation-fixed。
```

**构建标识是判断“是否在跑新程序集”的唯一可靠依据**（本日志作者曾多次因程序集/工具链陈旧而误判；注意 .NET 字符串字面量在 UTF-16 的 `#US` 堆里，用 ASCII 搜索会找不到）。

使用配置：

| 位置 | 内容 |
|---|---|
| 「设置」页 → 游戏路径 | 指向 `D:\Game\Warcraft3` → 点「扫描 MPQ」自动按引擎优先级填入 `War3Patch → War3xLocal → War3x → War3Local → war3.mpq` |
| 「设置」页 → BLP 搜索路径 | 可添加任意多个目录（排在 MPQ 之前查找）；打开散装 `.mdx` 时插件**自动**把"模型所在目录 + 上级目录"加为临时搜索根 |
| 「模型」页 | 虚拟路径（`units\orc\Peon\Peon.mdx`）、`res://` 或本机绝对路径 |
| 预览区 | 左键旋转 / 中右键平移 / 滚轮缩放；下方动画下拉 + 播放/暂停/循环/速度/时间轴 |
| 「显示」页 | 粒子、飘带、灯光、附着点、碰撞体、地面、**队伍色替换**、**隐藏队伍色外壳几何**、**预览光照：扁平参考(黑底)**、**背面剔除**（按文件/剔除背面/剔除正面/双面） |
| 「设置」页 → 导出 | 输出目录默认 `res://Godot`，导出 `*.tscn + textures/*.png + *.info.txt` |

---

## 2. 需求对照

| 原始需求 | 实现 | 状态 |
|---|---|---|
| 预览 MDX 内部所有功能 | `VERS/MODL/SEQS/GLBS/TEXS/MTLS(KMTF/KMTA/KMTE)/GEOS(VRTX/NRMS/UVBS/PTYP/PCNT/PVTX/GNDX/MTGC/UVAS/MATS)/GEOA/BONE/HELP/ATCH/PIVT/PREM/PRE2/RIBB/EVTS/CLID/LITE/CAMS/TXAN` 全解析；预览含几何、7 种 filter、骨骼动画、geoset 透明/染色、TXAN（含旋转专用着色器）、翻页贴图、队伍色/辉光、灯光、模型相机、附着点/碰撞/骨骼可视化 | ✅ |
| **粒子系统** | PRE2（累加器发射/纬度锥/重力/寿命/三段颜色与尺寸/头尾双段/squirt/图集 UV/优先级/替换贴图）+ PREM（精灵粒子）；PREM 子模型粒子按引擎语义不预览 | ✅ |
| **完整纹理支持** | BLP1（调色板 alpha 0/1/4/8、**4 分量 JPEG 内建解码器、渐进式 SOF2**）、BLP2（DXT1/1bit/DXT3/DXT5/BGRA）、mipmap、wrap、`.tga/.png/.jpg/.dds` | ✅ |
| 配置游戏路径读 MPQ | 多归档按优先级、listfile 名称索引、按需扇区读取 | ✅ |
| 配置多个路径读 BLP | BLP 搜索路径列表（增删/排序/拖入），并自动加入散装模型邻近目录 | ✅ |
| 预览时选择动画播放 | 序列下拉 + 播放/暂停/循环/0.1–4× 速度/时间轴拖拽（含防抖） | ✅ |
| 旋转模型 | 模型 X/Y/Z 旋转滑条 + 自动旋转 + 轨道相机 | ✅ |
| 输出到子文件夹 Godot | 导出到 `res://Godot/`（可改）：场景 + 22 个动画（30fps 烘焙）+ 贴图 PNG + info.txt | ✅ |

---

## 3. 架构

```
addons/mdx_preview/
  core/            纯 C#（无 Godot 依赖，可被独立控制台工程单测）
    Math3D/        Vec3/Quat/Mat4（列向量约定：平移在第 4 列，与 Godot Transform3D 一致）
    Mpq/           归档解析、加解密、zlib/ADPCM/Huffman/PKWARE 解压
    Blp/           BLP1/BLP2 解码 + JpegWarcraft3Decoder（基线 + 渐进式）
    Mdx/           数据模型（MdxModel）、chunk 解析（MdxLoader）、关键帧插值（MdxTrackSampler）、动画实例（MdxInstance）
    Assets/        虚拟路径、MPQ/目录/内存源、引擎式回退解析（.mdl→.mdx、.tga→.blp）
    Sheet/         SLK 解析（SpawnData/SplatData/UberSplatData/AnimSounds/AnimLookups）
  runtime/         Godot 表现层（AssetLibrary、MeshBuilder、MaterialBuilder、ModelNode、粒子、飘带、事件、贴花、预览视图、设置）
  editor/          Dock 面板（工具栏 + 3D 预览 + 动画条 + 4 个分页）
  export/          烘焙导出
```

关键约定：

- **坐标**：MDX 为 Z-up、+X 朝前；`MdxModelNode` 自身旋转 `(-90, 90, 0)` → MDX +Z→Godot +Y、+X→Godot −Z，所有子节点（粒子/飘带/事件）都在 MDX 空间工作。
- **蒙皮**：`Skeleton3D` 每个 MDX 节点一根骨骼、rest 全 Identity，每帧用 `SetBoneGlobalPoseOverride` 写入引擎风格的节点全局矩阵（等价于 MDX 矩阵调色板；已用独立探针验证 45° 折弯）。
- **材质**：MDX 每层一个独立 `MeshInstance3D`，按层序绘制；`filter=0` 进不透明队列（写深度），混合/加性层进透明队列（不写深度）——与 `r_mdx_geoset.c` 的逐层状态一致。
- **法线朝向**：WC3 资产是 D3D 时代资产（正面顺时针），Godot 按 OpenGL 逆时针判正面 → 默认 `CullMode.Front`（等价保留作者定义的正面）。

---

## 4. 修复日志（按主题，含根因与证据）

### 4.1 格式层（三个并行移植任务，均以 C 实现为唯一参考）

| 模块 | 证据 |
|---|---|
| MPQ | 4 个真实归档 **196 文件 SHA256 与 C 实现逐字节一致**；`ListFiles` 与 C 抽取的 listfile 双向 set-diff = 0；1.28 GB 全量 survey 仅 1 例"双方都失败"的坏扇区；双向 fixture（本移植写→C 读、C 写→本移植读）一致 |
| BLP | 70 个样本 0 失败；fixture 40/43 逐字节一致（3 例为 `blp2jpg.c` 丢弃 DXT1 1bit 透明的已知偏差，本移植跟随 `r_blp2.c`）；真实非 JPEG 7/7；**BLP1 JPEG 拼装 20/20、解码 20/20**；**渐进式追加 47/47 + jpegtran fixture 23/23** |
| MDX | 59 个真实模型 0 失败；与 `mdxtool --dump-all` **52358 项检查 0 不一致**；运动采样 111258 点最大偏差 1e-6；容错用例 220 项（截断/篡改/超界/尾部垃圾/v1500）全过 |

### 4.2 渐进式 JPEG（用户资产的主流编码）

用户资产树 `D:\rpg\output\files` 扫描：**15706 个 BLP1 JPEG 中 14670 个是渐进式（93.4%）**。
→ 在 core 内建渐进式解码（多 scan / 谱选择 / 逐次逼近 / EOB run），语义与 stb 的 WC3 原始 4 band 一致；用户报的 `jn_faxing_VIP26_SMINAD.blp` 与 C 参考逐字节一致。15 个资产本身损坏（帧头 `0xFFDE`、DQT 段损坏），C 参考与游戏本体同样解不出，插件会列入「解码失败」并给出原因。

### 4.3 编辑器 Dock 崩溃（用户实测）

| 症状 | 根因 | 修复 |
|---|---|---|
| 点「打开模型…」空引用崩溃（`MdxPreviewDock.BrowseForModel()`） | Godot 程序集重载会用**无参构造**重建脚本实例，插件注入的 `_assets/_settings` 丢失变 null | 字段改非空默认值 + `MdxPreviewView` 同样处理；工具栏回调包 `Safe(...)` 兜底；新增 `DockProbe` 探针在"不注入"条件下断言非空（PASS） |
| 「添加游戏路径无法扫描 MPQ / 无法添加贴图路径」 | `FileDialog(OpenDir)` 走的是 **`dir_selected`**，代码只连了 `file_selected` → 选目录无反应 | 连接 `DirSelected`；选游戏目录后**自动扫描 MPQ** |
| 骨骼名含 `:` 导致骨骼数为 0（蒙皮全丢） | `Skeleton3D.AddBone` 拒绝含 `:`/`/` 的名字并静默返回 -1 | 名字净化（`id_name`） |
| 程序集重载后树/属性访问 NPE | `MdxModelNode` 的 `Instance` 可能为空 | 属性 getter 全部空安全 |
| 偶发"改了却没生效" | `.godot/mono/temp` 里程序集/`.godot` 缓存陈旧；且（作者自己的工具）`CoreTests` 二进制过期导致误判 | 一律"清理 `obj/bin` + `--no-incremental` + UTF-16 校验标识" |

### 4.4 渲染正确性（本轮重点）

| 问题 | 根因 | 修复 |
|---|---|---|
| 转视角时部件忽隐忽现/"被剪裁" | 蒙皮网格按**绑定姿态 AABB** 剔除，骨骼把顶点移出盒外即被整块剔除 | ① 每个 geoset 层设 `ExtraCullMargin`；② 再给显式 `CustomAabb`（按模型尺寸放大） |
| 头部消失、直接看到屁股（"正反面融合"） | WC3/D3D 顺时针正面 vs Godot 逆时针正面 → 剔掉的是**作者定义的正面** | 默认绕序翻转（`TwoSided ? Disabled : Front`），并保留显示页「背面剔除」下拉按资产微调 |
| 花屏式"贴图拼贴"（粒子误判） | 一度怀疑 PREM 子模型粒子被四边形顶替（`--no-particles` 下仍存在，故排除） | PREM 子模型粒子改为按引擎语义不预览（引擎 loader 亦声明不发射） |
| **纹理映射位置不对（本轮根因）** | `MdxAssetLibrary.FlipTexturesVertically` 默认 **true**（早期基于探针的错误推断）：MDX 的 UV 以 V=0 指贴图顶部，Godot `Image` 第 0 行同样是顶部行，**不该翻转**。翻转后每张贴图采样错位一半 | 默认改为 **false** |
| 纹理"完全没显示"/发白 | 曾为"发虚"引入"去 alpha 贴图变体"，在改为每层独立节点后变成多余且把表面变白 | 移除该变体，`filter=0` 直接用原贴图（不透明队列本就忽略 alpha） |

**纹理根因的关键证据**：

1. UV 流独立校验：加载器输出与**原始字节解析**的 UVBS 逐值一致（`0.168511/0.07081 …`），且 geoset 记录游标精确停在末尾 → **模型数据无问题**。
2. 贴图解码校验：`zq\zuoqi_13_SMINAD.blp` → `BLP1 256×256 mips=9 kind=Rgba8 alphaDepth=8`，解出图集内容正确（含原作者水印）。
3. 关闭 V 翻转后（高分辨率、黑底、关粒子）：
   - `zuoqi_13_SMINAD` → **正确熊猫**（眼圈/吻部/黄眼/橙色鞍甲与绑带）
   - `zuoqi_14_SMINAD` → **正确蓝色龙**（鳞片/角/红眼/白鬃）
   - Peon → **兽人脸/獠牙/队伍色正确**（此前头部采到背包）
   - Footman → 蓝色甲胄/盾剑/面部正确
   - GreatHall → 建筑贴图正常
4. 与 C 参考（`mdxtool -o`）同帧/同距离/黑底比对：`zq13` `mean=6.22`（改进前 6.68）、Peon `mean=2.42`。

### 4.5 诊断能力（本轮新增，供后续排查）

| 开关 | 作用 |
|---|---|
| `--print-layers` | 逐层打印 `filter / 队列 / Transparency / BlendMode / DepthDrawMode / NoDepthTest / CullMode / priority` |
| `--uv-debug` | 把 UV 当颜色输出（判定 UV 是否连续） |
| `--no-txan` | 关闭 TXAN/KMTF 纹理动画 |
| `--no-flip-tex` | 关闭贴图 V 翻转（本轮根因判定的关键开关；现已与默认一致） |
| `--cull N` | 强制剔除模式（0 按文件 / 1 背面 / 2 正面 / 3 双面） |
| `--black-bg` | 扁平静态光照 + 黑底（与 `mdxtool` 预览条件对齐） |
| `--front-ortho` | 正面正交相机（注意：对平面 UI 模型以外的“正面”需人工确认朝向） |
| `--hide-shell` | 隐藏"整层都是替换贴图"的外壳几何（露出内层皮肤） |
| `--raw-team` | 关闭队伍色替换（按原文件显示遮罩） |
| `compare_mdxtool.ps1` | 与 `mdxtool --info/--dump-all` 逐项比对 chunk/序列清单 |
| `imgdiff.exe` | 与 `mdxtool -o` 的参考图做定量差异（`--diff` 可出差异图） |

---

## 5. 验证资产（`Godot/tests/reference/`）

| 文件 | 含义 |
|---|---|
| `peon_stand_ref.png` / `footman_stand_ref.png` / `greathall_stand_ref.png` / `earthquake_birth_ref.png` / `topright_panel_ref.png` | 仓库 C 工具 `mdxtool -o` 参考渲染 |
| `orbit_ref.png` / `peon_orbit_ref.png` / `panel_ref.png` / `zq13_c_ref.png` | 同帧同相机基准（本日志判定用） |
| `ok_zq13.png` / `ok_zq14.png` / `ok_peon.png` / `ok_footman.png` / `ok_hall.png` | **修复后**的插件渲染 |
| `hr_flipoff.png` / `peon_hr_flipoff.png` | 关闭翻转到正确结果的对照（与上面逐字节一致） |
| `uv_debug.png` | UV 调试视图（各部位连续渐变 → UV 正常） |
| `zq13_tex.png` | 争议贴图的解码结果（内容正确） |
| `skinprobe.png` / `skinning_probe.png` | 独立工程验证 Skeleton3D 蒙皮与离屏截图机制 |

---

## 6. 已知限制 / 未完成

1. **导出**：粒子与飘带不烘焙；UV 动画（TXAN）与翻页贴图取导出瞬间静帧；队伍色为导出时固有色；单模型 `.tscn` 体积偏大（Peon 22 序列 ≈ 28 MB 文本，可拆 `.tre` 优化，未做）。
2. **RIBB 飘带**：已实现并做过纯逻辑自检（48 边上限、`U=age/lifespan`、重力、`KRTX` 列），**移动发射器的出图验证未做**（预览默认冻结动画时条带长度为 0）。
3. **Reforged（v900+）**：按经典布局尽力解析 + 警告；专有字段（GEOS LOD、16 位 MATS 索引、LAY2 等）不解释；手头无真实 Reforged 文件。
4. **PREM 子模型粒子**：按引擎语义不预览（引擎本身声明无运行时模型发射）。
5. **资产本身损坏的 15 个 BLP**：帧头/DQT 损坏，C 参考与游戏本体同样解不出。
6. **`--front-ortho` 工具相机**沿模型 −Z 看，自制资产的"正面"朝向各异，不作为判定依据（判定请用轨道相机 + 高分辨率）。
7. 工程内若存在自行解包的资源目录（如 `Godot/files/`），会显著拖慢编辑器首次文件扫描，建议移到工程外。

---

## 7. 调试方法论教训（写给后续维护者，也写给本次会话）

1. **判据必须足够灵敏**：400px 小图 + `imgdiff` 均值在本模型上无法判别贴图错位（明暗接近的毛色把均值差压到 ~6，而错位与正确都能"PASS"）。**高分辨率目视 + 与 C 参考对照**才是可靠判据。
2. **先证工具链新鲜**：`dotnet build` 报 0 错误 ≠ 产物含新代码；`.godot/mono/temp` 可能陈旧，`CoreTests` 之类的辅助工程也可能过期（曾据此误判“渐进式不支持”）。校验方法：构建标识 + **UTF-16** 搜索程序集字符串堆。
3. **数据层与渲染层分开证**：UV/顶点/解码都可以用"独立字节解析"或"单文件解码出图"证明；证明数据层无误后，才把注意力放到渲染状态（剔除/深度/混合/采样方向）。
4. **以引擎为唯一裁判**：`mdxtool -o` 是 C 参考渲染，可给出"正确观感"的地面真值；同帧/同距离/同背景后 imgdiff 才有意义。
5. **改动要能回退**：本轮把若干实验性开关（材质优先级绘制等）撤掉、只保留有证据的修复；保留诊断开关比保留未验证的"修法"更有价值。


---

## 8. 追加记录（后续轮次）

### 8.1 构建：`dotnet build` 报 CS0579“特性重复”的绕行办法

现象：`dotnet build MdxPreview.csproj` 报

```
.godot/mono/temp/obj/Debug/MdxPreview.AssemblyInfo.cs(13,12): error CS0579: “System.Reflection.AssemblyCompanyAttribute”特性重复
.godot/mono/temp/obj/Debug/.NETCoreApp,Version=v8.0.AssemblyAttributes.cs(4,12): error CS0579: ...
```

排查结论：`dotnet msbuild MdxPreview.csproj -getItem:Compile` 显示 **`@(Compile)` 里确实包含中间目录中的这两个生成文件**，即 Godot SDK 在项目体之后把它们加进了编译项（因此在 csproj 的 `ItemGroup` 里 `Compile Remove` 无效；用 `BeforeTargets="CoreCompile"` 的 target 移除也无效，说明加入时机更晚）。

**可用绕行（已验证 0 错误、产物含新代码）**：

```powershell
Remove-Item D:\Dev\C++\open-realm\Godot\.godot\mono\temp\obj,D:\Dev\C++\open-realm\Godot\.godot\mono\temp\bin -Recurse -Force -ErrorAction SilentlyContinue
dotnet build D:\Dev\C++\open-realm\Godot\MdxPreview.csproj --no-incremental `
  -p:GenerateAssemblyInfo=false -p:GenerateTargetFrameworkAttribute=false
```

（该两属性让 SDK 不再生成会重复的特性文件；对本插件没有影响。产物位置不变：`.godot/mono/temp/bin/Debug/MdxPreview.dll`。）

### 8.2 纹理错位（本轮根因）——已修复并验证

见 §4.4：`FlipTexturesVertically` 默认由 true 改为 **false**。验证：`zuoqi_13`=正确熊猫、`zuoqi_14`=正确龙、Peon/Footman/GreatHall 全部正确；与"手动关闭翻转"的渲染逐字节一致。

### 8.3 粒子亮度（未闭环）

现象：`zuoqi_15_SMINAD.mdx`（4 个 PRE2 发射器）预览时**底部出现大片金色过亮区域**。

已确认：

1. `mdxtool -o` 渲染同一模型同一帧**完全没有粒子**（`mean=9.02` 的差异主要来自这片光晕）——说明 **C 参考渲染器并不驱动模型粒子发射**（引擎里粒子由游戏实体系统驱动），因此**不能用它作为粒子表现的基准**。
2. 已尝试把加性粒子的顶点色按 alpha 预乘（`src.rgb*alpha`，与引擎 `src.rgb*src.a + dst` 的语义一致），渲染结果**变化但光晕仍在** → 说明过亮主要来自**发射数量/贴图本身**，而非 alpha 权重。
3. 下一步排查方向（未做）：把发射率与引擎的每帧上限/池化策略对齐（引擎 `MAX_PARTICLES 10000` 共享池、粒子按 emitter 复用），核对 `Squirt` 突发语义与 `EmissionRate` 轨道的采样时刻；必要时在预览中提供"粒子亮度/密度"缩放以便对照。

当前状态：**纹理问题已修复**；粒子亮度问题已定位到"我的预览粒子发射强度与引擎不同"，但缺少可靠基准（mdxtool 不发射、游戏本体不易自动化对照），因此保留为已知限制。