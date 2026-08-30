#!/usr/bin/env python3
"""PoC-2 Step 3: MagicaCloth v1 のクロス設定を .prefab から抽出し、
ソルバ非依存の「クロス中間表現」を JSON で出力する。

なぜ Unity の外で書くのか
------------------------
Missing Script になったコンポーネントは、Unity の Inspector からは
`None (Mono Script)` としか見えず、シリアライズ値が読めない。
値は .prefab（YAML）には残っているため、ファイルを直接読む。
要件 F-17-13 / F-18-7、docs/adr/0003 を参照。

fileID の解決について
--------------------
- コライダと Bone Cloth の GameObject は prefab 内に実体があるため、名前で解決できる
- ただし `colliderList` は fileID 参照なので、fileID -> MonoBehaviour -> GameObject と辿る。
  **名前で引き当ててはならない**（同名オブジェクトが存在する。実際 Head コライダは 2 個ある）
- `rootList` が指す Transform は FBX 側の stripped Transform であり、
  prefab 内に名前が無い。そこで `m_CorrespondingSourceObject` の fileID を出力する。
  Unity 側は AssetDatabase.TryGetGUIDAndLocalFileIdentifier で突き合わせる。

使い方
------
    python tools/poc2_extract_v1_cloth.py <path/to/*.prefab> [-o out.json]

これは PoC の使い捨てコードである。本実装へ持ち込まないこと。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

BLOCK_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)(\s+stripped)?\s*$")
FILEID_RE = re.compile(r"fileID:\s*(-?\d+)")


class Block:
    __slots__ = ("class_id", "file_id", "stripped", "body")

    def __init__(self, class_id: str, file_id: str, stripped: bool):
        self.class_id = class_id
        self.file_id = file_id
        self.stripped = stripped
        self.body: list[str] = []

    @property
    def text(self) -> str:
        return "\n".join(self.body)


def parse_blocks(path: Path) -> list[Block]:
    blocks: list[Block] = []
    current: Block | None = None
    with path.open(encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            m = BLOCK_RE.match(line)
            if m:
                current = Block(m.group(1), m.group(2), bool(m.group(3)))
                blocks.append(current)
            elif current is not None:
                current.body.append(line)
    return blocks


def field(text: str, name: str) -> str | None:
    """`  name: value` を 1 件取る。ネストは見ない。"""
    m = re.search(rf"^\s*{re.escape(name)}:\s*(.*)$", text, re.M)
    return m.group(1).strip() if m else None


def ref(text: str, name: str) -> str | None:
    """`  name: {fileID: N}` の N を取る。"""
    m = re.search(rf"^\s*{re.escape(name)}:\s*\{{fileID:\s*(-?\d+)", text, re.M)
    return m.group(1) if m else None


def section_refs(text: str, name: str) -> list[str]:
    """`  name:` に続く `  - {fileID: N}` の並びを取る。

    インデントが浅くなる、または `- ` 以外の行が来た時点で打ち切る。
    """
    m = re.search(rf"^(\s*){re.escape(name)}:\s*$", text, re.M)
    if not m:
        return []
    indent = len(m.group(1))
    out: list[str] = []
    for line in text[m.end():].split("\n"):
        if not line.strip():
            continue
        cur_indent = len(line) - len(line.lstrip())
        stripped = line.strip()
        if stripped.startswith("- "):
            if cur_indent < indent:
                break
            fid = FILEID_RE.search(stripped)
            if fid:
                out.append(fid.group(1))
            continue
        if cur_indent <= indent:
            break
    return out


def curve(text: str, name: str) -> dict | None:
    """v1 の BezierParam（startValue / endValue / curveValue …）を取る。"""
    m = re.search(rf"^(\s*){re.escape(name)}:\s*$", text, re.M)
    if not m:
        return None
    indent = len(m.group(1))
    vals: dict[str, float] = {}
    for line in text[m.end():].split("\n"):
        if not line.strip():
            continue
        cur_indent = len(line) - len(line.lstrip())
        if cur_indent <= indent:
            break
        kv = re.match(r"\s*(\w+):\s*(-?[\d.eE+]+)\s*$", line)
        if kv:
            vals[kv.group(1)] = float(kv.group(2))
    return vals or None


def vec3(text: str, name: str) -> dict | None:
    m = re.search(
        rf"^\s*{re.escape(name)}:\s*\{{x:\s*(-?[\d.eE+]+),\s*y:\s*(-?[\d.eE+]+),\s*z:\s*(-?[\d.eE+]+)",
        text,
        re.M,
    )
    if not m:
        return None
    return {"x": float(m.group(1)), "y": float(m.group(2)), "z": float(m.group(3))}


def quat(text: str, name: str) -> dict | None:
    m = re.search(
        rf"^\s*{re.escape(name)}:\s*\{{x:\s*(-?[\d.eE+]+),\s*y:\s*(-?[\d.eE+]+),"
        rf"\s*z:\s*(-?[\d.eE+]+),\s*w:\s*(-?[\d.eE+]+)",
        text,
        re.M,
    )
    if not m:
        return None
    return {k: float(m.group(i + 1)) for i, k in enumerate("xyzw")}


def num(text: str, name: str) -> float | None:
    v = field(text, name)
    if v is None:
        return None
    try:
        return float(v)
    except ValueError:
        return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("prefab", type=Path)
    ap.add_argument("-o", "--out", type=Path)
    args = ap.parse_args()

    if not args.prefab.is_file():
        print(f"見つかりません: {args.prefab}", file=sys.stderr)
        return 1

    blocks = parse_blocks(args.prefab)

    go_name: dict[str, str] = {}          # GameObject fileID -> name
    tr: dict[str, dict] = {}              # Transform fileID -> info
    mb: dict[str, Block] = {}             # MonoBehaviour fileID -> block

    for b in blocks:
        if b.class_id == "1":
            n = field(b.text, "m_Name")
            if n is not None:
                go_name[b.file_id] = n
        elif b.class_id == "4":
            src = re.search(
                r"m_CorrespondingSourceObject:\s*\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-f]+)",
                b.text,
            )
            tr[b.file_id] = {
                "game_object": ref(b.text, "m_GameObject"),
                "father": ref(b.text, "m_Father"),
                "stripped": b.stripped,
                "source_file_id": src.group(1) if src else None,
                "source_guid": src.group(2) if src else None,
                "local_position": vec3(b.text, "m_LocalPosition"),
                "local_rotation": quat(b.text, "m_LocalRotation"),
                "local_scale": vec3(b.text, "m_LocalScale"),
            }
        elif b.class_id == "114":
            mb[b.file_id] = b

    def owner_name(mb_fid: str) -> str | None:
        blk = mb.get(mb_fid)
        if blk is None:
            return None
        return go_name.get(ref(blk.text, "m_GameObject") or "")

    def transform_of(go_fid: str) -> str | None:
        for tfid, info in tr.items():
            if info["game_object"] == go_fid:
                return tfid
        return None

    def path_of(go_fid: str) -> dict:
        """親を辿って階層パスを作る。stripped に当たったら FBX 側の fileID を返す。"""
        names: list[str] = []
        tfid = transform_of(go_fid)
        anchor = None
        depth = 0
        while tfid and depth < 32:
            info = tr.get(tfid)
            if info is None:
                break
            if info["stripped"]:
                anchor = {
                    "kind": "fbx_transform",
                    "source_file_id": info["source_file_id"],
                    "source_guid": info["source_guid"],
                }
                break
            n = go_name.get(info["game_object"] or "")
            if n:
                names.append(n)
            tfid = info["father"]
            depth += 1
        names.reverse()
        return {"names_from_anchor": names, "anchor": anchor}

    colliders = []
    chains = []
    others = []

    for fid, blk in mb.items():
        name = owner_name(fid)
        if not name:
            continue
        t = blk.text

        if "Collider" in name:
            length = num(t, "length")
            go_fid = ref(t, "m_GameObject") or ""
            self_tfid = transform_of(go_fid)
            self_tr = tr.get(self_tfid or "", {})
            entry = {
                "name": name,
                "mono_file_id": fid,
                "shape": "capsule" if length is not None else "sphere",
                # コライダ自身の Transform。親ボーンからの相対配置であり、
                # これを落とすとボーンの原点へ置かれてしまう
                "local_position": self_tr.get("local_position"),
                "local_rotation": self_tr.get("local_rotation"),
                "local_scale": self_tr.get("local_scale"),
                # コンポーネント側の中心オフセット。上記 Transform とは別物
                "center": vec3(t, "center"),
                "is_global": num(t, "isGlobal"),
                "parent": path_of(go_fid),
            }
            if length is not None:
                entry.update(
                    axis=num(t, "axis"),
                    length=length,
                    start_radius=num(t, "startRadius"),
                    end_radius=num(t, "endRadius"),
                )
            else:
                entry["radius"] = num(t, "radius")
            colliders.append(entry)

        elif "Mesh Cloth" in name or "Virtual Deformer" in name or "Spring" in name:
            # BoneCloth 以外の v1 コンポーネント。SPCR に相当物が無い可能性が高いので
            # 「見つけた」ことだけは必ず記録する。黙って落とすと変換漏れに気づけない
            others.append({"name": name, "mono_file_id": fid,
                           "kind": "mesh_cloth" if "Mesh Cloth" in name
                                   else ("virtual_deformer" if "Virtual Deformer" in name else "spring")})

        elif "Bone Cloth" in name or "BoneCloth" in name:
            root_fids = section_refs(t, "rootList")
            roots = []
            for rf in root_fids:
                info = tr.get(rf)
                if info is None:
                    roots.append({"unresolved_file_id": rf})
                elif info["stripped"]:
                    roots.append(
                        {
                            "kind": "fbx_transform",
                            "source_file_id": info["source_file_id"],
                            "source_guid": info["source_guid"],
                        }
                    )
                else:
                    roots.append(
                        {"kind": "prefab_object", "name": go_name.get(info["game_object"] or "")}
                    )

            collider_refs = section_refs(t, "colliderList")
            chains.append(
                {
                    "name": name,
                    "mono_file_id": fid,
                    "data_version": num(t, "dataVersion"),
                    "roots": roots,
                    # fileID -> MonoBehaviour -> GameObject 名。名前で引き当てない
                    "colliders": [
                        {"mono_file_id": c, "name": owner_name(c)} for c in collider_refs
                    ],
                    "params": {
                        "algorithm": num(t, "algorithm"),
                        "skinning_mode": num(t, "skinningMode"),
                        "culling_mode": num(t, "cullingMode"),
                        "user_blend_weight": num(t, "userBlendWeight"),
                        "radius": curve(t, "radius"),
                        "mass": curve(t, "mass"),
                        "gravity": curve(t, "gravity"),
                        "gravity_direction": vec3(t, "gravityDirection"),
                        "drag": curve(t, "drag"),
                        "max_velocity": curve(t, "maxVelocity"),
                        "world_move_influence": curve(t, "worldMoveInfluence"),
                        "max_move_speed": num(t, "maxMoveSpeed"),
                        "max_rotation_speed": num(t, "maxRotationSpeed"),
                    },
                }
            )

    result = {
        "source": str(args.prefab).replace("\\", "/"),
        "extractor": "poc2_extract_v1_cloth.py",
        "note": "PoC の使い捨て出力。ソルバ非依存の中間表現（要件 F-17-13）",
        "stats": {
            "game_objects": len(go_name),
            "transforms": len(tr),
            "stripped_transforms": sum(1 for i in tr.values() if i["stripped"]),
            "mono_behaviours": len(mb),
            "chains": len(chains),
            "colliders": len(colliders),
            "unconvertible": len(others),
        },
        "chains": chains,
        "colliders": colliders,
        "unconvertible": others,
    }

    text = json.dumps(result, indent=2, ensure_ascii=False)
    if args.out:
        args.out.write_text(text, encoding="utf-8")
        print(f"書き出しました: {args.out}")
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
