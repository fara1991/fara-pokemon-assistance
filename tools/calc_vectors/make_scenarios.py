#!/usr/bin/env python3
"""昔の世代のダメージ計算を確かめるための条件（シナリオ）を作る。

  python tools/calc_vectors/make_scenarios.py --cache <PokeAPI CSV のキャッシュ> --gens 5 6 7 > scenarios.json

サイトのデータ（wwwroot/data/GenN）から、ポケモン・技・持ち物・特性・場の状態を固定の乱数で選ぶ。
公開されているダメージ計算ツール（@smogon/calc）で使う英語の識別子も一緒に書き出し、
expected.js がそのツールで期待値を付ける。C# 側のテスト（LegacyCalcVectorTests）が同じ条件で計算して比べる。
"""
from __future__ import annotations

import argparse
import csv
import json
import random
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "src" / "FaraPokemonAssistance.Web" / "wwwroot" / "data"

NATURES_EN = ["Hardy", "Lonely", "Brave", "Adamant", "Naughty", "Bold", "Docile", "Relaxed", "Impish", "Lax",
              "Timid", "Hasty", "Serious", "Jolly", "Naive", "Modest", "Mild", "Quiet", "Bashful", "Rash",
              "Calm", "Gentle", "Sassy", "Careful", "Quirky"]

# この計算機が効果を扱う特性（ほかの特性は「効果なし」として比べる。ダメージに関わるのに未対応のものは使わない）
SUPPORTED_ABILITIES = {
    "mold-breaker", "teravolt", "turboblaze", "pixilate", "aerilate", "refrigerate", "galvanize", "normalize",
    "technician", "sheer-force", "iron-fist", "strong-jaw", "mega-launcher", "tough-claws", "sand-force",
    "steelworker", "water-bubble", "heatproof", "dry-skin", "fluffy", "thick-fat", "huge-power", "pure-power",
    "hustle", "solar-power", "guts", "blaze", "overgrow", "torrent", "swarm", "defeatist", "fur-coat",
    "grass-pelt", "marvel-scale", "multiscale", "shadow-shield", "filter", "solid-rock", "prism-armor",
    "tinted-lens", "neuroforce", "sniper", "infiltrator", "unaware", "scrappy", "levitate", "water-absorb",
    "volt-absorb", "motor-drive", "lightning-rod", "storm-drain", "sap-sipper", "flash-fire", "wonder-guard",
    "soundproof", "bulletproof", "adaptability", "battle-armor", "shell-armor", "air-lock", "cloud-nine",
}
# ダメージに関わるが、この計算機では扱わない特性（条件に入れない）
UNSUPPORTED_ABILITIES = {
    "reckless", "download", "analytic", "rivalry", "flare-boost", "toxic-boost", "flower-gift", "plus", "minus",
    "slow-start", "parental-bond", "fairy-aura", "dark-aura", "aura-break", "forecast", "merciless",
    "liquid-voice", "stakeout", "protean", "libero", "multitype", "rks-system", "gale-wings", "triage",
    "dazzling", "queenly-majesty", "klutz", "simple", "contrary", "disguise", "schooling", "shields-down",
    "stance-change", "power-construct", "zen-mode", "imposter", "illusion", "skill-link", "intimidate",
    "neutralizing-gas", "battle-bond",
}


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def hp_stat(base: int, ev: int, level: int) -> int:
    if base == 1:
        return 1  # ヌケニン
    return (2 * base + 31 + ev // 4) * level // 100 + level + 10


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True, help="PokeAPI CSV のキャッシュ（tools/sync_data.py の --cache と同じ）")
    ap.add_argument("--gens", nargs="+", type=int, required=True)
    ap.add_argument("--count", type=int, default=500)
    ap.add_argument("--seed", type=int, default=20261011)
    ap.add_argument("--focus", type=int, default=4, help="効果を扱う特性ごとに追加する条件の数（攻撃側・防御側それぞれ）")
    args = ap.parse_args()
    cache = Path(args.cache)
    poke_ident = {r["id"]: r["identifier"] for r in read_csv(cache / "pokemon.csv")}
    move_ident = {r["id"]: r["identifier"] for r in read_csv(cache / "moves.csv")}
    item_ident = {r["id"]: r["identifier"] for r in read_csv(cache / "items.csv")}
    abil_ident = {r["id"]: r["identifier"] for r in read_csv(cache / "abilities.csv")}

    rng = random.Random(args.seed)
    scenarios = []
    for gen in args.gens:
        d = DATA / f"Gen{gen}"
        pokemon = [r for r in read_csv(d / "pokemon.csv") if r["Provisional"] != "1"]
        moves = {r["Id"]: r for r in read_csv(d / "moves.csv")}
        items = read_csv(d / "items.csv")
        learn = {r["PokemonId"]: r["MoveIds"].split(";") for r in read_csv(d / "learnsets.csv")}
        ev_presets = [
            [0, 0, 0, 0, 0, 0], [4, 252, 0, 0, 0, 252], [4, 0, 0, 252, 0, 252], [252, 0, 252, 0, 4, 0],
            [252, 0, 4, 0, 252, 0], [252, 252, 0, 0, 4, 0], [252, 0, 0, 252, 4, 0],
        ]

        def side(p: dict[str, str], force_ability: str | None = None) -> dict:
            level = 50 if rng.random() < 0.8 else 100
            evs = rng.choice(ev_presets)
            abil_ids = [a for a in p["Abilities"].split(";") if a]
            abil_ids = [a for a in abil_ids if abil_ident.get(a) not in UNSUPPORTED_ABILITIES]
            # 効果を扱う特性か、ダメージに関係しない特性（特性が無い・未対応だけのときは、どちらにも効果の無い「にげあし」）
            ability = rng.choice(abil_ids) if abil_ids else None
            if force_ability:
                ability = force_ability
            item = None
            if items and rng.random() < 0.6:
                item = rng.choice(items)
            boosts = [0, 0, 0, 0, 0]
            if rng.random() < 0.3:
                boosts = [rng.randint(-2, 2) for _ in range(5)]
            status = rng.choices(["", "brn", "par", "psn"], [80, 10, 5, 5])[0]
            maxhp = hp_stat(int(p["HP"]), evs[0], level)
            hp = None
            if rng.random() < 0.25:
                hp = rng.randint(1, maxhp)
            return {
                "id": int(p["Id"]), "smogon": poke_ident[p["Id"]], "level": level, "nature": rng.randrange(25),
                "natureSmogon": None, "evs": evs, "ability": int(ability) if ability else None,
                "abilitySmogon": abil_ident[ability] if ability else "run-away",
                "item": int(item["Id"]) if item else None, "itemSmogon": item_ident.get(item["Id"]) if item else None,
                "boosts": boosts, "status": status, "hp": hp,
            }

        # 効果を扱う特性ごとに、その特性のポケモンを攻撃側・防御側に入れた条件も作る（数の少ない特性も確かめる）
        focus: list[tuple[str, str]] = []
        for ident in sorted(SUPPORTED_ABILITIES):
            aid = next((k for k, v in abil_ident.items() if v == ident), None)
            holders = [p for p in pokemon if aid and aid in p["Abilities"].split(";")]
            if holders:
                focus += [(aid, "attacker"), (aid, "defender")] * args.focus
        made = 0
        while made < args.count + len(focus):
            force = focus[made - args.count] if made >= args.count else None
            a = rng.choice(pokemon)
            if force and force[1] == "attacker":
                a = rng.choice([p for p in pokemon if force[0] in p["Abilities"].split(";")])
            dmg_moves = [m for m in learn.get(a["Id"], []) if m in moves and moves[m]["Category"] != "Status" and int(moves[m]["Power"]) > 0]
            if not dmg_moves:
                continue
            m = moves[rng.choice(dmg_moves)]
            s = {
                "gen": gen, "key": f"Gen{gen}",
                "attacker": side(a, force[0] if force and force[1] == "attacker" else None),
                "defender": side(rng.choice([p for p in pokemon if force[0] in p["Abilities"].split(";")]) if force and force[1] == "defender" else rng.choice(pokemon),
                                 force[0] if force and force[1] == "defender" else None),
                "move": {"id": int(m["Id"]), "smogon": move_ident[m["Id"]]},
                "crit": rng.random() < 0.15,
                "z": gen == 7 and rng.random() < 0.15,
                "weather": rng.choices(["", "Sun", "Rain", "Sand", "Hail"], [60, 10, 10, 10, 10])[0],
                "terrain": rng.choices(["", "Electric", "Grassy", "Psychic", "Misty"], [80, 5, 5, 5, 5])[0] if gen >= 6 else "",
                "reflect": rng.random() < 0.1, "lightScreen": rng.random() < 0.1,
                "doubles": rng.random() < 0.25,
            }
            if s["z"]:
                # Z 技を使うときの持ち物は Z クリスタル（ほかの持ち物の効果は無い）
                s["attacker"]["item"] = None
                s["attacker"]["itemSmogon"] = None
            if gen == 6 and s["terrain"] == "Psychic":
                s["terrain"] = ""
            for sd in (s["attacker"], s["defender"]):
                sd["natureSmogon"] = NATURES_EN[sd["nature"]]
            scenarios.append(s)
            made += 1
    json.dump(scenarios, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
