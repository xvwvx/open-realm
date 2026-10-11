#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把《玛法岁月》Lua 配置表导出成人物装备系统可读的 JSON。

数据源（只读，来自 MPQ 反编译还原的 Lua 源码）：
  <lua>/const1.lua              常量：装备栏位、物品类型、职业/性别、强化与星级贴图档位表
  <lua>/setting/ShapeCfg.lua    外观表：StrType(WQ/YF/HS/KZ/XZ/PF/CB/FX) + Mdx/Path1/Path2/HandState
  <lua>/setting/ItemCfg.lua     物品表：Icon/Shape/BaseAttr/ItemType/MainType/...

产物（写入 Godot/equipment/Data/）：
  constants.json   栏位定义、类型映射、中文名、强化/星级贴图表
  shapes.json      外观条目（id -> 外观描述）
  items.json       装备条目（已与外观表关联，含最终资源路径）

用法：
  python Godot/tools/lua_equip_export.py [--lua-dir <script 目录>] [--out <输出目录>]
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys

DEFAULT_LUA_DIR = pathlib.Path(r"D:\rpg\output\lua_ref_src\script")
DEFAULT_OUT_DIR = pathlib.Path(__file__).resolve().parents[1] / "equipment" / "Data"

# ItemCfg.ItemType 中属于可穿戴装备的类型（const1.lua:287-299、876-881）
EQUIP_ITEM_TYPES = {
    1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,  # WQ YF TK HS XL SZ JZ KZ XZ PF CB BS LQ
}

# Lua 配置表用 PascalCase 字段名，导出时统一转成 camelCase，方便 C# 侧反序列化。
SHAPE_FIELD_MAP = {
    "StrType": "strType", "Name": "name", "Icon": "icon", "Mdx": "mdx", "ExMdx": "exMdx",
    "HandState": "handState", "Path": "path", "Path1": "path1", "Path2": "path2",
    "Attr": "attr", "NeedList": "needList", "Properties": "properties",
    "NeedMallCfgId": "needMallCfgId", "Desc": "desc", "Sound": "sound", "HitSound": "hitSound",
}

ITEM_FIELD_MAP = {
    "Name": "name", "Icon": "icon", "ItemType": "itemType", "MainType": "mainType",
    "ItemLv": "itemLv", "NeedType": "needType", "NeedValue": "needValue", "JobTag": "jobTag",
    "ShowColor": "showColor", "Shape": "shape", "BaseAttr": "baseAttr", "Price": "price",
    "Heavy": "heavy", "MaxInlay": "maxInlay", "MaxCJ": "maxCj", "Desc": "desc",
    "Sex": "sex", "TLv": "tlv", "TypeEX": "typeEx", "LogName": "logName", "NoTrade": "noTrade",
    "BindType": "bindType", "Quality": "quality", "DropTips": "dropTips", "Tlv": "tlv",
    "StrongAttrId": "strongAttrId", "StrongNeedId": "strongNeedId",
    "RankAttrId": "rankAttrId", "RankNeedId": "rankNeedId",
    "BlessNeedId": "blessNeedId", "BlessTotalNeed": "blessTotalNeed",
    "CuiLianAttrId": "cuiLianAttrId", "CuiLianNeedId": "cuiLianNeedId",
    "HLNeedId": "hlNeedId", "SQNeedId": "sqNeedId", "SQRankId": "sqRankId",
    "DropInlayGemId": "dropInlayGemId", "RecoverType": "recoverType", "SuitId": "suitId",
    "ConsecrationNeed": "consecrationNeed", "ConsecrationRuleCfgId": "consecrationRuleCfgId",
    "WXRate": "wxRate", "RandSiXiang": "randSiXiang", "Heavy2": "heavy2",
}


class LuaParseError(Exception):
    pass


class LuaTableParser:
    """极简 Lua 表字面量解析器：只接受纯数据表（number/string/bool/nil/嵌套表）。

    标识符通过 env 求值，支持 const.NAME.KEY 与 const.NAME[expr]，用于解析
    const1.lua 里互相引用的常量。
    """

    _IDENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
    _NUMBER = re.compile(r"-?(?:0[xX][0-9a-fA-F]+|(?:\d+\.\d*|\.\d+|\d+)(?:[eE][+-]?\d+)?)")

    def __init__(self, text: str, pos: int = 0, env: dict | None = None):
        self.t = text
        self.i = pos
        self.env = env if env is not None else {}

    # ---- 词法 -------------------------------------------------------------------
    def error(self, msg: str):
        raise LuaParseError("%s @%d: %r" % (msg, self.i, self.t[self.i:self.i + 60]))

    def skip(self):
        t, n = self.t, len(self.t)
        while self.i < n:
            c = t[self.i]
            if c in " \t\r\n":
                self.i += 1
                continue
            if t.startswith("--", self.i):
                long_open = re.match(r"--\[(=*)\[", t[self.i:])
                if long_open:
                    close = "]" + long_open.group(1) + "]"
                    end = t.find(close, self.i)
                    self.i = n if end < 0 else end + len(close)
                else:
                    end = t.find("\n", self.i)
                    self.i = n if end < 0 else end + 1
                continue
            break

    # ---- 语法 -------------------------------------------------------------------
    def parse_value(self):
        self.skip()
        if self.i >= len(self.t):
            self.error("值意外结束")
        c = self.t[self.i]
        if c == "{":
            return self.parse_table()
        if c in "\"'":
            return self.parse_string()
        m = self._NUMBER.match(self.t, self.i)
        if m:
            self.i = m.end()
            raw = m.group(0)
            if raw.lower().startswith(("-0x", "0x")):
                return int(raw, 16)
            return float(raw) if ("." in raw or "e" in raw.lower()) else int(raw)
        m = self._IDENT.match(self.t, self.i)
        if m:
            self.i = m.end()
            word = m.group(0)
            if word == "true":
                return True
            if word == "false":
                return False
            if word == "nil":
                return None
            return self.parse_suffix(self.env.get(word, {"__unknown__": word}))
        self.error("无法解析的值")

    def parse_suffix(self, base):
        """处理 .name 与 [expr] 访问链。"""
        while self.i < len(self.t):
            self.skip()
            if self.i < len(self.t) and self.t[self.i] == "." and not self.t.startswith("..", self.i):
                m = self._IDENT.match(self.t, self.i + 1)
                if not m:
                    break
                self.i = m.end()
                base = base.get(m.group(0)) if isinstance(base, dict) else None
                continue
            if self.i < len(self.t) and self.t[self.i] == "[":
                self.i += 1
                key = self.parse_value()
                self.skip()
                if self.i < len(self.t) and self.t[self.i] == "]":
                    self.i += 1
                else:
                    self.error("缺少 ]")
                base = base.get(key) if isinstance(base, dict) else None
                continue
            break
        return base

    def parse_string(self) -> str:
        quote = self.t[self.i]
        self.i += 1
        out = []
        escapes = {"n": "\n", "t": "\t", "r": "\r", "\\": "\\", '"': '"', "'": "'"}
        while self.i < len(self.t):
            c = self.t[self.i]
            if c == "\\":
                nxt = self.t[self.i + 1] if self.i + 1 < len(self.t) else ""
                out.append(escapes.get(nxt, nxt))
                self.i += 2
                continue
            if c == quote:
                self.i += 1
                return "".join(out)
            out.append(c)
            self.i += 1
        self.error("字符串未闭合")

    def parse_table(self):
        self.i += 1  # 进入表体
        array: list = []
        mapping: dict = {}
        while True:
            self.skip()
            if self.i >= len(self.t):
                self.error("表未闭合")
            if self.t[self.i] == "}":
                self.i += 1
                break
            if self.t.startswith("...", self.i):
                self.i += 3
                continue
            key = None
            if self.t[self.i] == "[":
                self.i += 1
                key = self.parse_value()
                self.skip()
                if self.i < len(self.t) and self.t[self.i] == "]":
                    self.i += 1
                else:
                    self.error("缺少 ]")
                self.skip()
                if self.i < len(self.t) and self.t[self.i] == "=" and not self.t.startswith("==", self.i):
                    self.i += 1
                else:
                    self.error("缺少 =")
            else:
                m = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)").match(self.t, self.i)
                if m:
                    key = m.group(1)
                    self.i = m.end()
            value = self.parse_value()
            if key is None:
                array.append(value)
            else:
                mapping[key] = value
            self.skip()
            if self.i < len(self.t) and self.t[self.i] in ",;":
                self.i += 1
        if mapping:
            for idx, item in enumerate(array, start=1):
                mapping.setdefault(idx, item)
            return mapping
        return array


def parse_lua_file(path: pathlib.Path, env: dict | None = None):
    """解析 return {...} 形式的纯数据 Lua 文件。"""
    text = path.read_text(encoding="utf-8", errors="replace")
    start = text.index("return")
    parser = LuaTableParser(text, start + len("return"), env or {})
    return parser.parse_value()


def split_subscripts(suffix: str, env: dict) -> list:
    """把 ".FX" / "[const.JOB_ZS]" 这类后缀拆成键列表。"""
    keys = []
    i = 0
    while i < len(suffix):
        if suffix[i] == ".":
            m = re.match(r"\.([A-Za-z0-9_]+)", suffix[i:])
            if not m:
                break
            keys.append(m.group(1))
            i += m.end()
        elif suffix[i] == "[":
            end = suffix.index("]", i)
            expr = suffix[i + 1:end]
            i = end + 1
            try:
                keys.append(LuaTableParser(expr, 0, env).parse_value())
            except LuaParseError:
                break
        else:
            break
    return keys


def parse_constants(path: pathlib.Path) -> dict:
    """逐条扫描 const 赋值语句，用 Lua 语法解析右侧表达式（支持互相引用）。"""
    text = path.read_text(encoding="utf-8", errors="replace")
    consts: dict = {}
    env = {"const": consts}
    pattern = re.compile(r"const\.([A-Za-z0-9_]+)((?:\.[A-Za-z0-9_]+|\[[^\]]*\])*)\s*=(?!=)")
    pos = 0
    while True:
        m = pattern.search(text, pos)
        if not m:
            break
        name, suffix = m.group(1), m.group(2)
        parser = LuaTableParser(text, m.end(), env)
        try:
            value = parser.parse_value()
        except LuaParseError:
            pos = m.end()
            continue
        pos = parser.i
        keys = split_subscripts(suffix, env)
        if not keys:
            consts[name] = value
            continue
        target = consts.get(name)
        if not isinstance(target, dict):
            target = {}
            consts[name] = target
        for key in keys[:-1]:
            child = target.get(key)
            if not isinstance(child, dict):
                child = {}
                target[key] = child
            target = child
        target[keys[-1]] = value
    return consts


def build_constants(consts: dict) -> dict:
    pos_by_value: dict[int, dict] = {}
    for name, value in consts.items():
        if not name.startswith("ROLE_EQUIP_POS_") or not isinstance(value, int):
            continue
        if name == "ROLE_EQUIP_POS_MAXPOS":
            continue
        pos_by_value[value] = {"key": name[len("ROLE_EQUIP_POS_"):].lower(), "const": name, "id": value}

    type_cn = consts.get("ITEM_TYPE_CN", {}) or {}
    type_by_value: dict[int, dict] = {}
    for name, value in consts.items():
        if not name.startswith("ITEM_TYPE_") or not isinstance(value, int) or name.startswith("ITEM_TYPE_CN"):
            continue
        type_by_value[value] = {
            "key": name[len("ITEM_TYPE_"):].lower(),
            "const": name,
            "id": value,
            "cn": type_cn.get(value),
        }

    def normalize_path_table(table) -> dict:
        out = {}
        if not isinstance(table, dict):
            return out
        for job, entries in table.items():
            if not isinstance(entries, dict):
                continue
            out[str(job)] = {str(level): path for level, path in entries.items()}
        return out

    return {
        "equipPoss": {str(k): v for k, v in sorted(pos_by_value.items())},
        "maxPos": consts.get("ROLE_EQUIP_POS_MAXPOS"),
        "itemTypes": {str(k): v for k, v in sorted(type_by_value.items())},
        "equipType2Pos": {
            str(k): (v if isinstance(v, list) else [v])
            for k, v in (consts.get("EQUIP_TYPE2POS") or {}).items()
        },
        "equipPos2Type": {str(k): v for k, v in (consts.get("EQUIP_POS2TYPE") or {}).items()},
        "shapeStrType2ItemType": consts.get("SHAPE_STRTYPE2ITEMTYPE") or {},
        "jobCn": {str(k): v for k, v in sorted((consts.get("JOB_CN") or {}).items())},
        "sexCn": {str(k): v for k, v in sorted((consts.get("SEX_CN") or {}).items())},
        "weaponStrongPath": normalize_path_table(consts.get("WEAPON_STRONG_PATH")),
        "equipStarPath": normalize_path_table(consts.get("EQUIP_STAR_PATH")),
        "equipStates": {
            "normal": consts.get("EQUIP_STATE_NORMAL"),
            "noShow": consts.get("EQUIP_STATE_NOSHOW"),
            "noOff": consts.get("EQUIP_STATE_NOOFF"),
            "jd": consts.get("EQUIP_STATE_JD"),
            "lock": consts.get("EQUIP_STATE_LOCK"),
        },
    }


def build_shapes(raw: dict) -> dict:
    shapes = {}
    for key, entry in raw.items():
        if not isinstance(key, int) or not isinstance(entry, dict):
            continue  # FXList / HairColor 等旁表
        item = {out_name: entry[src] for src, out_name in SHAPE_FIELD_MAP.items() if src in entry}
        item["id"] = key
        shapes[str(key)] = item
    return shapes


def build_items(raw: dict, equip_types: set, equip_type2pos: dict) -> dict:
    items = {}
    for key, entry in raw.items():
        if not isinstance(key, int) or not isinstance(entry, dict):
            continue  # ItemMapId / ItemNameMap 等旁表
        item_type = entry.get("ItemType")
        if item_type not in equip_types:
            continue
        item = {out_name: entry[src] for src, out_name in ITEM_FIELD_MAP.items() if src in entry}
        item["id"] = key
        item["pos"] = equip_type2pos.get(str(item_type))
        items[str(key)] = item
    return items


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="导出《玛法岁月》装备配置为 JSON")
    ap.add_argument("--lua-dir", default=str(DEFAULT_LUA_DIR), help="lua_ref_src/script 目录")
    ap.add_argument("--out", default=str(DEFAULT_OUT_DIR), help="输出目录")
    args = ap.parse_args(argv)

    lua_dir = pathlib.Path(args.lua_dir)
    out_dir = pathlib.Path(args.out)
    setting = lua_dir / "setting"
    for required in (lua_dir / "const1.lua", setting / "ShapeCfg.lua", setting / "ItemCfg.lua"):
        if not required.is_file():
            print("[导出] 缺少输入文件: %s" % required, file=sys.stderr)
            return 2

    out_dir.mkdir(parents=True, exist_ok=True)

    consts = parse_constants(lua_dir / "const1.lua")
    constants = build_constants(consts)

    equip_types = set(EQUIP_ITEM_TYPES)
    for key, value in constants["itemTypes"].items():
        if value["key"].startswith("sq_"):
            equip_types.add(int(key))

    shapes = build_shapes(parse_lua_file(setting / "ShapeCfg.lua"))
    items = build_items(parse_lua_file(setting / "ItemCfg.lua"), equip_types, constants["equipType2Pos"])

    missing_shapes = 0
    for item in items.values():
        shape_id = item.get("shape")
        look = shapes.get(str(shape_id)) if shape_id is not None else None
        if shape_id is not None and look is None:
            missing_shapes += 1
        item["look"] = look

    payloads = {"constants.json": constants, "shapes.json": shapes, "items.json": items}
    for filename, payload in payloads.items():
        target = out_dir / filename
        target.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")
        print("[导出] %s: %d 字节" % (filename, target.stat().st_size))

    print("[导出] 栏位 %d 个，物品类型 %d 个，外观 %d 条，装备 %d 条，外观缺失 %d 条"
          % (len(constants["equipPoss"]), len(constants["itemTypes"]), len(shapes), len(items), missing_shapes))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
