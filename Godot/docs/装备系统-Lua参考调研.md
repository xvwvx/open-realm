# 玛法岁月 · 人物装备系统 Lua 参考调研

> 源码：`D:\rpg\output\lua_ref_src\script`（反编译还原）。证据格式 `文件:行号`，文件名相对 script\。
> ⚠ 该份为反编译产物：`if not X` / `next()` 类布尔条件在多处被反转（如 clsItemData.lua:162），机制结论可靠，精确极性需回原工程核对。

## 1. 装备栏位

栏位枚举：const1.lua:730-751（`ROLE_EQUIP_POS_*`，`ROLE_EQUIP_MAXPOS = ROLE_EQUIP_POS_SQ_ZQ = 21`）

| 常量 | 值 | 中文 | 类型常量 |
|---|---|---|---|
| ROLE_EQUIP_POS_TK / YF / WQ | 1 / 2 / 3 | 头盔 / 衣服 / 武器 | ITEM_TYPE_TK=3 / YF=2 / WQ=1 |
| ROLE_EQUIP_POS_SZ1 / JZ1 / XL | 4 / 5 / 6 | 手镯1 / 戒指1 / 项链 | SZ=6 / JZ=7 / XL=5 |
| ROLE_EQUIP_POS_HS / SZ2 / JZ2 | 7 / 8 / 9 | 护手 / 手镯2 / 戒指2 | HS=4 / SZ=6 / JZ=7 |
| ROLE_EQUIP_POS_KZ / XZ / LQ | 10 / 11 / 12 | 裤子 / 鞋子 / 灵器 | KZ=8 / XZ=9 / LQ=13 |
| ROLE_EQUIP_POS_PF / CB / BS | 13 / 14 / 15 | 披风 / 翅膀 / 宝石 | PF=10 / CB=11 / BS=12 |
| ROLE_EQUIP_POS_SQ_BZ…ZQ | 16…21 | 宝珠/勋章/玉玺/虎符/战鼓/战旗 | SQ_BZ=151…SQ_ZQ=156 |

映射：`EQUIP_POS2TYPE` const1.lua:752-773；`EQUIP_TYPE2POS` const1.lua:774-799（手镯/戒指映射为 `{1,2}` 两个槽位表，选空槽逻辑 clsUnitHero.lua:1681-1692）；大类 `EQUIP_BIGTYPE_*`+`EQUIP_TYPE2BIGTYPE` const1.lua:800-813。类型中文 `ITEM_TYPE_CN` const1.lua:843-899。UI 建栏位置（坐标+底图）：clsRoleEquipView.lua:589-608。坐骑装备 6 个独立栏位 const1.lua:823-842。"六格"快捷栏 `SIXITEM_MAXNUM=6` const1.lua:226（clsUnitHero.lua:1694-1704）。

## 2. ItemCfg 条目结构

ItemCfg.lua 顶层除主表还有 `ItemMapId`(1-38)、`ItemNameMap`(39-2553)、`MountItemTypeList`(2554)、`PosOffsetList`(2628)、`ShengXiaoCfgIdList`(3006)、`ShengXiaoRankMap`(3296)；主表自 ItemCfg.lua:6370 起，键=物品 CfgId。

**武器 T1（新手剑）ItemCfg.lua:6666-6692**
```lua
[1001] = {
    BaseAttr = {GongJiD = 3, GongJiU = 6},
    BlessNeedId = 1, DropInlayGemId = 10001, Heavy = 6, HitSound = 19,
    Icon = "icon\\wuqi_1_SMINAD.blp", ItemLv = 1, ItemType = 1, JobTag = 0,
    JpRate = 40, JpRuleCfgId = 12001, MainType = 1, MaxCJ = 5, MaxInlay = 2,
    Name = "新手剑", NeedType = 1, NeedValue = 1, Price = 50,
    RankAttrId = 101, RankNeedId = 1, Shape = 101, ShowColor = 1,
    Sound = 14, StrongAttrId = 1, StrongNeedId = 1
},
```

**武器 T9（星月天弓，弓/JobTag=4）ItemCfg.lua:6988-7043**
```lua
[10100] = {
    BaseAttr = {ArrowRange=100, GongJiD=16, GongJiU=40, GongShuD=28, GongShuU=84, PerZDamYinYue=3, ZhunQue=3},
    BlessNeedId=10, ConsecrationNeed=21, ConsecrationRuleCfgId=16165,
    CuiLianAttrId=67, CuiLianNeedId=4, DropInlayGemId=10001, DropTips=1,
    HLNeedId=19, Heavy=44, HitSound=19, Icon="icon\\wuqi_gj_19_SMINAD.blp",
    ItemLv=12, ItemType=1, JdRuleCfgId=12147, JobTag=4, JpRate=60, JpRuleCfgId=12147,
    MainType=1, MaxCJ=41, MaxInlay=3, Name="星月天弓", NeedType=1, NeedValue=67,
    PickUpType=210, Price=120000, RandSiXiang=30, RankAttrId=314, RankNeedId=8,
    RecoverType=326, Shape=1520, ShowColor=6, Sound=14,
    StrongAttrId=145, StrongNeedId=8, TLv=9, TypeEX="ARROW",
    WXList={{AttrRule=14101, NeedRule=13104, SkillRule=19001}}, WXRate=50, WXReRandId=1
},
```

**衣服 T1（男）ItemCfg.lua:14829-14859**（多 `Sex = 1`）；**圣器（ItemType=151，TLv=1002）ItemCfg.lua:14860-14887**（多 `SQNeedId=102, SQRankId=10021, SQDropRankRate=50`）。

字段释义：`Icon` 图标；`MainType` 大类（1=装备，const1.lua:380-394）；`ItemType` 细分类型=栏位归属；`BaseAttr` 基础属性（`U`上限/`D`下限，`Per*` 百分比）；`ItemLv` 显示等级、`RecoverType` 回收档；`Price` 售价（税率 `DEFAULT_TAX_PERGOLD=0.7` const1.lua:227）；`Heavy` 负重；`MaxCJ`（装备=耐久上限，消耗品=堆叠上限）；`MaxInlay` 镶嵌孔数；`ShowColor` 品质色索引；`NeedType/NeedValue` 需求（1等级 2攻击力 3魔法力 4道术 5弓术 6刺术 7转生等级，判定 clsItemManager.lua:2066-2106）；`JobTag` 职业标记（1战2法3道4弓5刺，仅神鼎提交校验 clsMouseManager.lua:2287、clsItemManager.lua:1616，穿装备不校验职业）；`Shape`→setting.SHAPE_CFG 索引（外观模型，ItemCfg 内**无** Mdx 字段）；`Sex` 性别限制；`TLv` T级（T1..T16；100=特殊，>1000 为 Z 系列，clsCommonItemTips.lua:457-465）；`TypeEX`（ARROW/DAGGER，影响普攻技能 clsUnitHero.lua:1133-1146）；子系统外键 `StrongNeedId/StrongAttrId`、`BlessNeedId`、`CuiLianAttrId/CuiLianNeedId`、`HLNeedId`、`RankAttrId/RankNeedId`、`SQNeedId/SQRankId`、`JpRuleCfgId`（极品）、`JdRuleCfgId`（鉴定）、`DzRuleCfgId`（打造）、`ConsecrationRuleCfgId/ConsecrationNeed`（开光）、`WXRate/WXList/WXReRandId`（词缀）、`DropInlayGemId`（掉落宝石池）、`RandSiXiang`（四象概率）、`SQDropRankRate`（圣化掉落率）；标记 `IsSM`（神秘装备，属性放大 clsItemEquip.lua:681-691）、`NoTrade/NoFix/NoSave/Persistent/HideFrom/FromTips`。

## 3. 运行时对象与穿脱流程

类层次：`clsItemData`（clsItemData.lua:1）← `clsItemEquip`（clsItemEquip.lua:1）等。持久字段 `BindPlayerId/DorpProtectId/DorpProtectTime` clsItemData.lua:2-4，`WxTitle` clsItemEquip.lua:2。

**clsItemData 字段**（Ctor clsItemData.lua:7-19）：m_CfgId/m_Cfg/m_CurCj（耐久或数量）/m_FromCfgId/m_FromPlaceId/m_FromPlayerName/m_FromTime/m_AntiKey/m_NoAutoPickIdMap。关键方法：GetCj/SetCj（上限 Cfg.MaxCJ）clsItemData.lua:161-182；GetHeavy clsItemData.lua:184；ResetBindPlayerId clsItemData.lua:56-68；GetShowColor clsItemData.lua:232-262。

**clsItemEquip 字段**（Ctor clsItemEquip.lua:4-34）：m_CounterBaseAttr/ExAttr/JdAttr/DzAttr/KGAttr/ZFYAttr、m_CounterSQDropRankAttrStep/SQNewDropRankAttrStep/SQRankAttrStep、m_RankAttrList、m_WxDataList/m_WxActiveList、m_CjHp、m_EquipState、m_CurInlayNum/m_InlayList、m_StrongLv、m_RankLv/m_RankClearNum、m_Quality、m_ChangeShape、m_BlessLv、m_HLExp、m_CuiLianLv、m_TypeSiXiang、m_XLSiXiang、m_SQRankLv、m_WXRandNum。OnCreate 时 `m_CounterBaseAttr = Cfg.BaseAttr`（clsItemEquip.lua:299-302）。`IsCjValid()=Cj>0` clsItemEquip.lua:667-669。

**穿戴流程**（客户端权威 + 存档同步）：
1. 槽位校验 `CheckBag2Equip`：`EQUIP_POS2TYPE[目标槽] == 物品ItemType`，否则"不能装备在这个位置"（clsMouseManager.lua:991-1007）。
2. 需求校验 `CheckEquipCanEquip`（clsItemManager.lua:2066-2124）：NeedType/NeedValue 比对；武器比腕力 `GetTotalHandHeavy() < Cfg.Heavy`→"腕力不够"；衣服/裤子比性别；非武器比身体负重 `GetUsedBodyHeavy()-替换件Heavy+Cfg.Heavy > GetTotalBodyHeavy()`；被换下件 `IsNoOff` 则拒绝（`CheckEquipCanOff` clsItemManager.lua:2126-2131）。
3. 执行 `ModNetCallBack_MSG_BAG2EQUIP`（netcallback.lua:308-332）：SetItemInBag/SetItemInBody → 存档 ForCaller_BagItem/BodyItem → 被换下件 SetWxActiveList({}) → `UpdateEquipLook(新装备, EQUIP_POS2TYPE[槽])` → RecountBagHeavy → RecountBodyHeavy → `UpdateEquip()` → 派发 EventBagItemChange/EventEquipItemChange。
4. 卸下 `ModNetCallBack_MSG_EQUIP2BAG`（netcallback.lua:334-358），流程对称；强制卸下 `_RemoveBodyItem`（clsDialogManager.lua:1454-1465）；升级替换 clsDialogManager.lua:1302-1323。
5. 属性汇总 `UpdateEquip`（clsUnitHero.lua:1445-1454）：UpdateWxActiveState → UpdateSuitActiveState → UpdateEquipQuality → UpdateEquipSiXiangGM → UpdateEquipSQQualityPower → UpdateEquipBlessQuality → UpdateEquipAttr → UpdateEquipSkill。
6. `UpdateEquipAttr`（clsUnitHero.lua:839-856）：遍历 m_BodyItemList，累加 `pEquip:GetCounter()`+`GetSiXiangCounter(Id)`+`GetHunQiCounter(Id)`；宝石槽（ROLE_EQUIP_POS_BS）另并 `g_MoXueObj` 血魔之心；结果经 `self:AddCounter(Counter, true)` 挂到 `m_CounterEquip`。
7. `GetCounter`（clsItemEquip.lua:304-340）汇总：BaseAttr+ExAttr+ZFYAttr+JdAttr+DzAttr+KGAttr+Strong+Bless+CuiLian+SQRankBase+SQDropRank+SQRank+已激活词缀Attr+镶嵌宝石BaseAttr+m_RankAttrList。
8. 生效：计数器注册于 clsDataAttr.lua:2-721，读取经 clsUnitAttr.lua:3-55 三类聚合——`RegisterAttrFunc`（直取、可封顶，如 `PerBaoJi` 上限 100 clsUnitAttr.lua:88）、`RegisterPerAddAttrFunc`（`floor(Base*(1+Per*0.01))`）、`RegisterPerAddAttrFunc2`（基础值+AllAttackD/U 再乘 PerGongJi+PerAllAttack）。战斗取值 `GetFightGongJi` clsUnitAttr.lua:818-829（XingYun 决定上下限，另有 PerRandZhanWu/PerDamZhanWu 随机加成）。
9. 负重/背包：`RecountBodyHeavy`（clsUnitHero.lua:1672-1679，武器不计身体负重）、`RecountBagHeavy`（clsUnitHero.lua:1577）、`CheckBagIsFull`（clsItemManager.lua:1219-1280）、`AddItemInBag`（clsItemManager.lua:1017-1160，含绑定/保护期/自动使用/堆叠合并）。

## 4. 模型拼接

SHAPE_CFG 条目含 `Mdx / Path / Path1 / Path2 / StrType / HandState / Properties / Attr / NeedMallCfgId`（ShapeCfg.lua:2-8 发型、6176-6187 武器、7319-7333 弓）。两处实现规则一致：场景侧 `clsUnitHero:_ChangeShape` clsUnitHero.lua:1775-1861；UI 侧 `clsCompModelRole:_ChangeShape` clsCompModelRole.lua:125-216。

- 裤子 KZ：`SetUnitTexture(string.format("rw\\%s.blp", Sex==SEX_BOY and Cfg.Path1 or Cfg.Path2), 31)`，强化贴图槽 36、星级贴图槽 44（clsUnitHero.lua:1781-1787）
- 鞋子 XZ：33 / 38 / 43（1788-1794）；护手 HS：32 / 37 / 45（1795-1801）；衣服 YF：34 / 39 / 46（1802-1808）；披风 PF：`Cfg.Path`→35 / 40 / 47（1809-1815）
- 翅膀 CB：`AddEffect(string.format("cb\\%s.mdx", Cfg.Mdx), "chest")`（1816-1824）
- 武器 WQ：`string.format("wq\\%s.mdx", Cfg.Mdx)`，`HandState` 定挂点：1=hand left；2=左右各一；3=左手 + 第二件用 `Cfg.ExMdx`；其他=hand right（1825-1847）；随后 `SetEffectTexture` 强化槽 41、星级槽 42（1848-1857），并 `UpdateHeroAnimationProperties` 切动作属性（1859、1918-1971）
- 强化/星级贴图：`const.WEAPON_STRONG_PATH[JobType][EffLv]`（const1.lua:1083-1193）、`const.EQUIP_STAR_PATH[JobType][StarLv]`（const1.lua:1194-1304），缺省用 `"rw\\rw_touming_SMINAD.blp"` / `"rw\\rw_touming2_*.blp"` 透明贴图
- 空栏回退 `_OffEquipLook`（clsUnitHero.lua:1863-1879 / clsCompModelRole.lua:218-234）：YF→Shape 20、HS→30、KZ→40、XZ→50、PF→60，即"裸衣/裸护手/裸裤/裸鞋/裸披风"（ShapeCfg.lua:9626-9633、10817-10824、14201-14207）
- **时装/外观**：物品 `Shape` 为默认外观，实例 `m_ChangeShape` 覆盖之（`GetShowShape` clsItemEquip.lua:213-222）。`UpdateEquipLook`（clsUnitHero.lua:1706-1723）取 GetShowShape→SHAPE_CFG；若带 `NeedMallCfgId`（商城时装）且 `g_ShapeObj` 未解锁则回退 0（不显示）。解锁/切换 `IsShapeUnlock`（clsShapeObj.lua:13-25）、`CheckShapeUse`（clsShapeObj.lua:36-76，需先装备同类型装备且 `Properties` 一致）、`GetShapeCounter`（clsShapeObj.lua:187-198，已解锁时装自身也提供属性）。发型/发色/称号走 `shape\\` 目录：`AddEffect(string.format("shape\\%s.mdx", Cfg.Mdx), "head")`（clsUnitHero.lua:1743-1765）。披风/翅膀另有 clsCloakData/clsWingData（clsUnitHero.lua:1725-1741）。

## 5. 属性字段编码（AttrCfg）

- `AttrList`（AttrCfg.lua:2-5766）：`{VarName, Name, ExName, CfgName, JR, Price, SX, ShowPriority, UDType}`。`Price` 影响神秘装备定价（clsItemEquip.lua:686-688）；`ShowPriority` 决定提示排序（clsItemEquip.lua:736、786-799）；`SX=1` 表示不吃四象加成（clsItemEquip.lua:551、579）。
- `AttrMap`（AttrCfg.lua:5767-10869）：EnName→`{CfgId(数值ID), Name, ExName, JR, Price, ShowPriority, UDType}`，如 `AllAttackD={CfgId=109}`（5780-5788）、`ArrowRange={CfgId=134}`（5798-5804）、`AtkAddHp={CfgId=70}`（5811-5817）。
- `AttrQuality`（AttrCfg.lua:10870+）：EnName→`{Q2V={品→值}, V2Q={值→品}}`，如 AllAttackD（10879-10910）。品质换算 `CountQuality` 取各极品属性 V2Q 最大值（clsItemEquip.lua:588-599）。
- 属性键全量登记 clsDataAttr.lua:2-721（约 700 个 `RegisterCounterAttr`），聚合方式 clsUnitAttr.lua:57-777。
- 叠加：加法合并（`MergeCounter` clsItemManager.lua:3262-3267）；百分比经 `Per*` 二次乘算（clsUnitAttr.lua:22-55）。
- 条件生效：四象 `GetSiXiangCounter`（clsItemEquip.lua:535-558，青龙/白虎/朱雀/玄武 Per 加成基础属性）；魂器 `GetHunQiCounter`（clsItemEquip.lua:560-586，按 TLv 分档 1-4/5-7/8-10/11-13/14-16）；套装 `_UpdateSuitEffectState`（clsUnitHero.lua:993-1060）；词缀激活 `UpdateWxActiveState`（clsUnitHero.lua:910-991）。

## 6. 强化 / 宝石 / 鉴定等子系统

统一模式：`clsEquip*Obj` 单例（Id=玩家），`CheckXxx(BagPos)` 校验 + `XxxEquip(BagPos)` 执行；成功后一律 `SetLock(true)` + `ResetAntiKey()`（部分 `ResetBindPlayerId` 绑定）。

- **强化** clsEquipStrongObj.lua:8-66：需 `Cfg.StrongNeedId` 且 `const.STRONG_MASK[ItemType]`；公式 `Rate = STRONG_CFG.StrongNeed[Id][Lv+1].Rate + GetTotalPerEquipStrong()`，`Rate >= math.random(1,100)` 成功（clsEquipStrongObj.lua:36-43）；属性 `StrongAttr[StrongAttrId][Lv]`（clsItemEquip.lua:431-438）；默认满级 `DefaultStrongMaxLv=20`（StrongCfg.lua:9423）。样例 StrongCfg.lua:66792-66850（10万金+强化石6005，成功率 100/80/60/50/30/25/20）。
- **祝福** clsEquipBlessObj.lua:30-80：需 `BlessNeedId`，另需"祝福碎片"≥`BlessTotalNeed`；`BlessNeed[Id][Lv+1].Rate >= math.random(1,100)`；属性复用 `StrongAttr[StrongAttrId][BlessLv]`（clsItemEquip.lua:469-476）。配置 StrongCfg.lua:2-。
- **淬炼** clsEquipCuiLianObj.lua:8-41：需 `CuiLianNeedId`；`CuiLianNeed[Id][Lv+1].Rate >= math.random(1,100)`；属性 `CuiLianAttr[CuiLianAttrId][Lv]`（clsItemEquip.lua:450-457）；默认满级 `DefaultCuiLianMaxLv=14`（StrongCfg.lua:9422）。
- **进阶** clsEquipRankObj.lua:8-92：需 `RankNeedId`+`RANK_MASK`；上限 `RankMaxLv=4`（StrongCfg.lua:47415）；每阶从 `RankAttr[RankAttrId][RankLv]` 逐条 `random(1,100) <= Rate + GetTotalPerEquipRankAttr()` 则取 `random(Min,Max)`（clsEquipRankObj.lua:41-56）；按阶存 m_RankAttrList；清阶上限 `3+养生等级`（clsItemEquip.lua:182-196）。
- **圣化/精炼（SQ）** clsEquipSQRankObj.lua:8-97：需 `SQNeedId`；成功率 `GetRankRate + GetTotalPerSQStrongRate`；每 3 级触发一次 `_SQRankAttr` 随机增属性（clsEquipSQRankObj.lua:52-72）；属性 = `Step × RankSQCfg[SQRankId].AttrData[EnName].Step`（clsItemEquip.lua:486-533）；精炼基础属性 = `ceil(BaseAttr * SQRankLv * 0.1)`（clsItemEquip.lua:440-448）；洗练/掉阶 clsEquipSQRankObj.lua:157-300，消耗 XiLianSQNeed（StrongCfg.lua:70175）。
- **开光（KG）** clsEquipKGObj.lua:8-41：需 `ConsecrationRuleCfgId`，消耗 ConsecrationNeed，随机生成 KG 属性（`EquipRandomKGAttr` clsItemManager.lua:402-433）。
- **幻灵（HL）** clsEquipHLObj.lua:8-116：需 `HLNeedId`；`math.random(1,100) <= HLNeed[Id][Lv+1].Rate` 则 `AddHLExp(PerExp)`（clsEquipHLObj.lua:40-45）；等级由累计经验换算 `GetHLLv`（clsItemEquip.lua:142-152）；可卸载返还 ReturnItem。
- **词缀洗练** clsEquipWXRandObj.lua:8-35：需 `WXReRandId/WXRate`；次数上限 `GetWXRandMaxNum`（默认 3，有"超神礼包"则 9999999，clsItemEquip.lua:60-66）；消耗 WXRandNeed（StrongCfg.lua:70141）。
- **镶嵌（宝石）**：孔数由 `DropInlayGemId` 池随机 `EquipRandomInlayNum`（clsItemManager.lua:227-246）；镶嵌物 ItemType=40（`ITEM_TYPE_INLAYGEM`="镶嵌" const1.lua:875）；属性直接并入 GetCounter（clsItemEquip.lua:328-333、`GetInlayCounter` 625-634）；孔位图标 `INLAY_BS_ICON` const1.lua:918+；宝石槽位 BS 另挂"血魔之心" `g_MoXueObj`（clsUnitHero.lua:850-852、clsCommonItemTips.lua:568-583）。
- **鉴定（Jd）**：无独立界面，由鉴定符类右键道具触发，`Param.FuncType==1` 按 DropId 生成新装备并调 `EquipRandomJdAttr`（netcallback.lua:1329-1354；另一入口 clsItemManager.lua:2616-2635）；属性存 m_CounterJdAttr（clsItemEquip.lua:390-400）。
- **打造（Dz）**：`Cfg.DzRuleCfgId` / `EquipRandomDzAttr`（clsItemManager.lua:388-400），属性 m_CounterDzAttr。
- **未找到**：独立"宝石升级/精炼"数值公式（镶嵌仅孔位+属性并入）；独立鉴定界面。极品属性生成公式见 `CreateRandomJpAttr`（clsItemManager.lua:481-576，按 AttrQuality 抽条数、AttrType 抽属性、AttrValue 加权，并受 PerDropPower2..13 影响）。

## 7. 品质 / 堆叠 / 绑定 / 耐久 / 限制

- **品质**：`EQUIP_QUALITY_CN` 0-20（const1.lua:507-528）、带色版 `EQUIP_QUALITYCOLOR_CN`（529-550）、品质底图 `EQUIP_QUALITYBLP`（551-572）；阶数 `EQUIP_RANK_CN` 0-7 + 颜色（617-643）。装备实际品质 = 极品属性换算（clsItemEquip.lua:588-599），显示于提示类型行（clsCommonItemTips.lua:466-476）。
- **颜色**：物品名色取 `Cfg.ShowColor`（`GetShowColor` clsItemData.lua:232-262；tips clsCommonItemTips.lua:404）；掉落品质光效 `ITEM_COLOR_EFFECT`（const1.lua:1036-1050，clsItemWar.lua:40-45）。
- **堆叠**：消耗/材料类 `MaxCJ` 即堆叠上限，`Cj` 即数量（clsItemData.lua:174-182；地面名 "xN" clsItemOther.lua:34-40；合并 `MergeCoverTypeItem` netcallback.lua:259-271、AddItemInBag clsItemManager.lua:1084-1160）。
- **耐久**：装备的 `Cj` 是耐久，`IsCjValid=Cj>0`；修复 `AddCj`/FixBodyItem（clsItemEquip.lua:653-656、clsUnitHero.lua:1539-1555）；显示"持久:x/y"（clsCommonItemTips.lua:691-694）；`DelCjHp` 每累计 1000 点扣 1（clsItemEquip.lua:671-679）；扣耐久入口 DelBodyEquipCj/DelBodyWeaponCjAttack 等（clsItemManager.lua:2961-3063）。
- **绑定**：`BindPlayerId`+`ResetBindPlayerId`（clsItemData.lua:2、56-68）；拾取校验 AddItemInBag（clsItemManager.lua:1023-1032，"这件物品不属于你"/保护期）；提示"所属/当前"（clsCommonItemTips.lua:488-498）；交易限制 `NoTrade/IsTrade`。
- **锁**：`EQUIP_STATE_LOCK=8`，`IsLock`=基础锁或强制锁（镶嵌/幻灵）clsItemEquip.lua:269-297；强化等成功后自动 SetLock(true)。
- **诅咒/不可卸**：`EQUIP_STATE_NOSHOW=1`（诅咒，名字加"(诅咒)" clsItemEquip.lua:720-722）、`EQUIP_STATE_NOOFF=2`（无法取下）；生成时 20% 概率加 NOOFF（clsItemManager.lua:733-739）；净化用神秘水 `_OnUseShenMiShui`（clsItemManager.lua:2807-2825）。
- **等级/职业限制**：等级/主属性/转生 = CheckEquipCanEquip（clsItemManager.lua:2066-2106）+ 提示变色（clsCommonItemTips.lua:412-455）；职业 = `JobTag`，**仅神鼎提交时校验**（clsMouseManager.lua:2287），穿戴时未校验；性别 = `Cfg.Sex`，仅衣服/裤子（clsItemManager.lua:2111-2113）。
- **死亡掉落**：`DropBodyRandomEquip` 每槽 10%（clsItemManager.lua:3104-3127）、`DropBodyZZEquip` 每槽 50% 且非 NOOFF（3079-3102）。
- **地面存在时间**：装备 180s / 其他 120s / 药品 30s（clsItemWar.lua:4-6）。
- **存档字段**：clsSaveManager2.lua:1104-1170（State/Cj/ExAttr/ZFYAttr/JdAttr/DzAttr/KGAttr/StrongLv/CuiLianLv/BlessLv/WxTitle/WxDataList/InlayNum/InlayList/Shape/RankLv/RankList/RankCNum/HLExp/SX/SXXL/WXXL/SQDAttr/SQRLv/SQRAttr），读取 1196-1280。

## 8. 反编译伪影提示

- `if not X then` / `if not next(T) then` 布尔极性在多处被反转，例如 clsItemData.lua:162、clsItemEquip.lua:319、clsItemManager.lua:3263（`pairs({})` 应为 `pairs(CounterB)`）。
- 若干随机生成调用被反编译为 `#pEquip`（clsItemManager.lua:366、381、395、424、456、474、759），实际应为 `CreateRandomJpAttr`（clsItemManager.lua:481-576）一类函数。
- `const.STRONG_MASK`/`const.RANK_MASK` 在 const1.lua:674-675 定义为空表且全脚本无赋值；按 clsEquipStrongObj.lua:16、clsEquipRankObj.lua:19 的判断，强化/进阶会被判为"该装备类型暂时无法强化/进阶"——疑为反编译丢失或刻意关闭，需以原工程确认。
