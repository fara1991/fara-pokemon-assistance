#!/usr/bin/env python3
"""生成済みの learnsets.csv などを、PokeAPI の CSV（pokemon_moves / pokemon / pokemon_forms 等）と突き合わせて検証する。

sync_data.py のロジックは使わず、「各データセットのバージョングループの pokemon_moves を、そのポケモン（フォーム）自身の
ID で引いた和集合」を期待値にして、全ポケモン・全ゲームで比べる。不一致は一覧にして、あれば終了コード 1 で終わる。

  python tools/verify_learnsets.py                # POKEAPI_CACHE か tools/.cache の CSV を使う（無ければ取得）
  python tools/verify_learnsets.py --only Champions

許している差（期待値の組み立て規則）
  * Champions: そのゲーム自身のデータが無いポケモンは、datasets.json の補完用バージョングループの段（SV → 古いゲーム）の
    うち、そのポケモンのデータがある最初の段の和集合。
  * どの段にもデータが無い別フォルム（メガシンカなど）は、基本フォルムの期待値を引き継ぐ。
  * 技データが空のポケモンは不一致（アプリは空を「全技を覚える」と扱うため）。
  * 技名またはタイプが無い技（第 2〜4 世代ののろい＝タイプ「???」）は moves.csv に無いので、期待値からも外す。
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import sys
import urllib.request
from collections import defaultdict
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
import champions_learnsets  # noqa: E402
ROOT = TOOLS.parent
DATA = ROOT / "src" / "FaraPokemonBattleAssistant.Web" / "wwwroot" / "api" / "game"
POKEAPI_CSV = "https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/"
UA = "FaraPokemonBattleAssistant/1.0 (+https://github.com/fara1991/fara-pokemon-battle-assistant)"


def table(cache: Path, name: str) -> list[dict[str, str]]:
    path = cache / f"{name}.csv"
    if not path.exists():
        cache.mkdir(parents=True, exist_ok=True)
        req = urllib.request.Request(POKEAPI_CSV + f"{name}.csv", headers={"User-Agent": UA})
        with urllib.request.urlopen(req, timeout=120) as r:
            path.write_bytes(r.read())
    with path.open(encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def read(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only")
    ap.add_argument("--cache", default=os.environ.get("POKEAPI_CACHE", str(TOOLS / ".cache")))
    args = ap.parse_args()
    cache = Path(args.cache)

    by_vg: dict[str, dict[str, set[str]]] = defaultdict(lambda: defaultdict(set))
    for r in table(cache, "pokemon_moves"):
        by_vg[r["version_group_id"]][r["pokemon_id"]].add(r["move_id"])
    poke = {r["id"]: r for r in table(cache, "pokemon")}
    move_ident = {r["id"]: r["identifier"] for r in table(cache, "moves")}
    forms = defaultdict(list)
    for r in table(cache, "pokemon_forms"):
        forms[r["pokemon_id"]].append(r)
    default_of = {p["species_id"]: pid for pid, p in poke.items() if p["is_default"] == "1"}
    ptypes = defaultdict(dict)
    for r in table(cache, "pokemon_types"):
        ptypes[r["pokemon_id"]][r["slot"]] = r["type_id"]

    config = json.loads((TOOLS / "datasets.json").read_text(encoding="utf-8"))["datasets"]
    problems: list[str] = []
    for ds in config:
        key = ds["key"]
        if args.only and key != args.only:
            continue
        out = DATA / key
        vgs = [str(v) for v in ds["version_groups"]]
        valid_moves = {r["Id"] for r in read(out / "moves.csv")}
        learn = {r["PokemonId"]: set(filter(None, r["MoveIds"].split(";"))) for r in read(out / "learnsets.csv")}
        pokemon_rows = read(out / "pokemon.csv")
        listed = {r["Id"]: r for r in pokemon_rows}

        own: dict[str, set[str]] = defaultdict(set)
        for vg in vgs:
            for pid, moves in by_vg[vg].items():
                own[pid] |= moves
        fallback_tiers: list[dict[str, set[str]]] = []
        for tier in [ds.get("learnset_fallback_version_groups", []), *ds.get("learnset_fallback_older_version_groups", [])]:
            tier_moves: dict[str, set[str]] = defaultdict(set)
            for vg in (str(v) for v in tier):
                for pid, moves in by_vg[vg].items():
                    tier_moves[pid] |= moves
            fallback_tiers.append(tier_moves)

        overrides: dict[str, set[str]] = {}
        if ds.get("learnset_override_url"):
            # Showdown の champions mod の技（sync_data.py と同じ読み方）。あるポケモンはこれが期待値
            show = champions_learnsets.load_showdown(ds["learnset_override_url"], cache / "champions_learnsets.ts")
            by_ident = {r["identifier"].replace("-", ""): r["id"] for r in table(cache, "moves")}
            overrides = champions_learnsets.resolve(show, poke, [r["Id"] for r in pokemon_rows], by_ident)

        def expected(pid: str) -> tuple[set[str], str]:
            if pid in overrides:
                return overrides[pid], "showdown-champions"
            if own.get(pid):
                return own[pid], "own"
            tiers_data = fallback_tiers
            if ds.get("pokedex_id") and tiers_data:
                if tiers_data[0].get(pid):
                    return tiers_data[0][pid], "fallback-SV"
                base = default_of.get(poke[pid]["species_id"])
                base_ok = base is not None and base != pid and bool(own.get(base) or tiers_data[0].get(base))
                if not base_ok:
                    for older in tiers_data[1:]:
                        if older.get(pid):
                            return older[pid], "fallback-older"
            base = default_of.get(poke[pid]["species_id"])
            if base and base != pid:
                moves, how = expected(base)
                if moves:
                    return moves, f"inherited({how})"
            return set(), "none"

        how_count: dict[str, int] = defaultdict(int)
        bad = 0
        for pid, row in listed.items():
            exp, how = expected(pid)
            exp &= valid_moves
            got = learn.get(pid)
            how_count[how.split("(")[0]] += 1
            name = f"{row['Name']}({pid}, {poke[pid]['identifier']})"
            if got is None:
                problems.append(f"{key}: {name} は learnsets.csv に無い"); bad += 1
            elif not got:
                problems.append(f"{key}: {name} の技データが空（アプリでは全技を覚える扱いになる）"); bad += 1
            elif got != exp:
                extra = sorted(move_ident[m] for m in got - exp)[:8]
                lack = sorted(move_ident[m] for m in exp - got)[:8]
                problems.append(f"{key}: {name} [{how}] 余分 {len(got - exp)} {extra} / 不足 {len(exp - got)} {lack}"); bad += 1
            if row["SpeciesId"] != poke[pid]["species_id"]:
                problems.append(f"{key}: {name} の SpeciesId {row['SpeciesId']} が PokeAPI の {poke[pid]['species_id']} と違う"); bad += 1
        for pid in learn:
            if pid not in listed:
                problems.append(f"{key}: learnsets.csv に pokemon.csv に無い ID {pid}"); bad += 1
        # species_map（フォーム ID → 種族 ID）が PokeAPI と合っているか
        for r in read(out / "species_map.csv"):
            if poke[r["FormId"]]["species_id"] != r["SpeciesId"]:
                problems.append(f"{key}: species_map {r['FormId']} → {r['SpeciesId']} が PokeAPI と違う"); bad += 1
        dropped = sorted({move_ident[m] for pid in listed for m in expected(pid)[0] - valid_moves})
        print(f"{key}: {len(listed)} 匹 / 内訳 {dict(how_count)} / 不一致 {bad}"
              + (f" / moves.csv に無く除外された技 {dropped}" if dropped else ""))
    for p in problems:
        print("NG", p)
    print(f"不一致 合計 {len(problems)} 件")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
