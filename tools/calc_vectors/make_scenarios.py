#!/usr/bin/env python3
"""昔の世代のダメージ計算を確かめるための条件（シナリオ）を作る。

  python tools/calc_vectors/make_scenarios.py --cache <PokeAPI CSV のキャッシュ> --gens 3 4 5 6 7 8 9 Champions > scenarios.json

サイトのデータ（wwwroot/data/GenN、Champions）から、ポケモン・技・持ち物・特性・場の状態を固定の乱数で選ぶ。
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
    "hustle", "solar-power", "guts", "reckless", "blaze", "overgrow", "torrent", "swarm", "defeatist", "fur-coat",
    "grass-pelt", "marvel-scale", "multiscale", "shadow-shield", "filter", "solid-rock", "prism-armor",
    "tinted-lens", "neuroforce", "sniper", "infiltrator", "unaware", "scrappy", "levitate", "water-absorb",
    "volt-absorb", "motor-drive", "lightning-rod", "storm-drain", "sap-sipper", "flash-fire", "wonder-guard",
    "soundproof", "bulletproof", "adaptability", "battle-armor", "shell-armor", "air-lock", "cloud-nine",
}
# ダメージに関わるが、この計算機では扱わない特性（条件に入れない）
UNSUPPORTED_ABILITIES = {
    "download", "analytic", "rivalry", "flare-boost", "toxic-boost", "flower-gift", "plus", "minus",
    "slow-start", "parental-bond", "fairy-aura", "dark-aura", "aura-break", "forecast", "merciless",
    "liquid-voice", "stakeout", "protean", "libero", "multitype", "rks-system", "gale-wings", "triage",
    "dazzling", "queenly-majesty", "klutz", "simple", "contrary", "disguise", "schooling", "shields-down",
    "stance-change", "power-construct", "zen-mode", "imposter", "illusion", "skill-link", "intimidate",
    "neutralizing-gas", "battle-bond",
}

# 剣盾・SV・チャンピオンズのダメージ計算（DamageCalculator 本体）がまだ扱っていない特性・技（条件から除く）。
# 第 3〜7 世代の計算（LegacyDamageCalculator）は扱っているので、今のゲームの条件だけに適用する。
MODERN_UNSUPPORTED_ABILITIES = {
    "fire-mane", "eelevate", "scrappy", "protosynthesis", "quark-drive", "air-lock", "cloud-nine", "shell-armor", "battle-armor", "defeatist", "marvel-scale", "fluffy", "aura-guard",
    "sword-of-ruin", "tablets-of-ruin", "vessel-of-ruin", "beads-of-ruin", "tera-shell",
}
# 本体と扱いが違う持ち物: ものしりメガネ・ちからのハチマキの補正値（本体は 4506、ツールは 4505）、フィールドのタネ（ツールは防御が上がる）
MODERN_UNSUPPORTED_ITEMS = {"muscle-band", "wise-glasses", "electric-seed", "grassy-seed", "misty-seed", "psychic-seed"}
# 壁の扱いが違う技（サイコショック系は特殊技なのに物理の壁が効く・かわらわり系は壁を壊さない）。条件から壁を外す
MODERN_NO_SCREEN_MOVES = {"psyshock", "psystrike", "secret-sword", "brick-break", "psychic-fangs", "raging-bull"}


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
    ap.add_argument("--gens", nargs="+", required=True, help="世代の数字（3〜9）か Champions")
    ap.add_argument("--count", type=int, default=500)
    ap.add_argument("--seed", type=int, default=20261011)
    ap.add_argument("--targeted", type=int, default=30, help="技・特性の組み合わせを絞った条件の数（はたきおとす・すてみ・ちからずく・スナイパー・やけど・雪）")
    ap.add_argument("--focus", type=int, default=4, help="効果を扱う特性ごとに追加する条件の数（攻撃側・防御側それぞれ）")
    args = ap.parse_args()
    cache = Path(args.cache)
    poke_ident = {r["id"]: r["identifier"] for r in read_csv(cache / "pokemon.csv")}
    move_ident = {r["id"]: r["identifier"] for r in read_csv(cache / "moves.csv")}
    item_ident = {r["id"]: r["identifier"] for r in read_csv(cache / "items.csv")}
    abil_ident = {r["id"]: r["identifier"] for r in read_csv(cache / "abilities.csv")}

    rng = random.Random(args.seed)
    scenarios = []
    for key in args.gens:
        champions = key == "Champions"
        modern = champions or int(key) >= 8
        gen = 9 if champions else int(key)
        data_key = "Champions" if champions else f"Gen{gen}"
        d = DATA / data_key
        unsupported = UNSUPPORTED_ABILITIES | (MODERN_UNSUPPORTED_ABILITIES if modern else set())
        pokemon = [r for r in read_csv(d / "pokemon.csv") if r["Provisional"] != "1"]
        moves = {r["Id"]: r for r in read_csv(d / "moves.csv")}
        items = read_csv(d / "items.csv")
        learn = {r["PokemonId"]: r["MoveIds"].split(";") for r in read_csv(d / "learnsets.csv")}
        ev_presets = [
            [0, 0, 0, 0, 0, 0], [4, 252, 0, 0, 0, 252], [4, 0, 0, 252, 0, 252], [252, 0, 252, 0, 4, 0],
            [252, 0, 4, 0, 252, 0], [252, 252, 0, 0, 4, 0], [252, 0, 0, 252, 4, 0],
        ]
        if champions:
            # チャンピオンズの努力値はポイント（1 ポイントで実数値 +1、上限 32）。
            # 性格補正を足す前に掛けるため、比べるのは補正なしの性格・Lv50 だけ（ツールには 8 倍の努力値を渡す）
            ev_presets = [
                [0, 0, 0, 0, 0, 0], [1, 32, 0, 0, 0, 32], [1, 0, 0, 32, 0, 32], [32, 0, 32, 0, 1, 0],
                [32, 0, 1, 0, 32, 0], [32, 32, 0, 0, 1, 0], [32, 0, 0, 32, 1, 0],
            ]

        def side(p: dict[str, str], force_ability: str | None = None) -> dict:
            level = 50 if champions or rng.random() < 0.8 else 100
            evs = rng.choice(ev_presets)
            abil_ids = [a for a in p["Abilities"].split(";") if a]
            abil_ids = [a for a in abil_ids if abil_ident.get(a) not in unsupported]
            # 効果を扱う特性か、ダメージに関係しない特性（特性が無い・未対応だけのときは、どちらにも効果の無い「にげあし」）
            ability = rng.choice(abil_ids) if abil_ids else None
            if force_ability:
                ability = force_ability
            item = None
            if items and rng.random() < 0.6:
                item = rng.choice(items)
                # ツールに同じ持ち物が無い（チャンピオンズのメガストーンなど）・本体と扱いが違う持ち物は使わない
                if item_ident.get(item["Id"]) is None or modern and item_ident.get(item["Id"]) in MODERN_UNSUPPORTED_ITEMS:
                    item = None
            boosts = [0, 0, 0, 0, 0]
            if rng.random() < 0.3:
                boosts = [rng.randint(-2, 2) for _ in range(5)]
            status = rng.choices(["", "brn", "par", "psn"], [80, 10, 5, 5])[0]
            maxhp = hp_stat(int(p["HP"]), 0 if champions else evs[0], level) + (evs[0] if champions else 0)
            hp = None
            if rng.random() < 0.25:
                hp = rng.randint(1, maxhp)
            return {
                "id": int(p["Id"]), "smogon": poke_ident[p["Id"]], "level": level, "nature": rng.choice([0, 6, 12, 18, 24]) if champions else rng.randrange(25),
                "natureSmogon": None, "evs": evs, "ability": int(ability) if ability else None,
                "abilitySmogon": abil_ident[ability] if ability else "run-away",
                "item": int(item["Id"]) if item else None, "itemSmogon": item_ident.get(item["Id"]) if item else None,
                "boosts": boosts, "status": status, "hp": hp,
            }

        # 効果を扱う特性ごとに、その特性のポケモンを攻撃側・防御側に入れた条件も作る（数の少ない特性も確かめる）
        focus: list[tuple[str, str]] = []
        for ident in sorted(SUPPORTED_ABILITIES - (MODERN_UNSUPPORTED_ABILITIES if modern else set())):
            aid = next((k for k, v in abil_ident.items() if v == ident), None)
            holders = [p for p in pokemon if aid and aid in p["Abilities"].split(";")]
            if holders:
                focus += [(aid, "attacker"), (aid, "defender")] * args.focus
        def make(a: dict, d: dict, m: dict, force_a: str | None = None, force_d: str | None = None, **over) -> dict:
            sc = {
                "gen": gen, "key": data_key,
                "attacker": side(a, force_a),
                "defender": side(d, force_d),
                "move": {"id": int(m["Id"]), "smogon": move_ident[m["Id"]]},
                "crit": rng.random() < 0.15,
                "z": gen == 7 and rng.random() < 0.15,
                "weather": rng.choices(["", "Sun", "Rain", "Sand", "Hail"], [60, 10, 10, 10, 10])[0],  # Hail は第 9 世代では雪
                "terrain": rng.choices(["", "Electric", "Grassy", "Psychic", "Misty"], [80, 5, 5, 5, 5])[0] if gen >= 6 else "",
                "reflect": rng.random() < 0.1, "lightScreen": rng.random() < 0.1,
                "doubles": rng.random() < 0.25,
            }
            sc.update(over)
            if modern:
                mid = move_ident[m["Id"]]
                if mid in MODERN_NO_SCREEN_MOVES:
                    sc["reflect"] = sc["lightScreen"] = False
                if int(m["Priority"]) > 0 and sc["terrain"] == "Psychic":
                    sc["terrain"] = ""
                # かたやぶりは相手の特性を無視するが、シャドーシールドは無視できない（本体は無視してしまう）
                if sc["attacker"]["abilitySmogon"] in ("mold-breaker", "teravolt", "turboblaze") and sc["defender"]["abilitySmogon"] == "shadow-shield":
                    sc["attacker"]["ability"] = None
                    sc["attacker"]["abilitySmogon"] = "run-away"
            if sc["z"]:
                # Z 技を使うときの持ち物は Z クリスタル（ほかの持ち物の効果は無い）
                sc["attacker"]["item"] = None
                sc["attacker"]["itemSmogon"] = None
            if gen == 6 and sc["terrain"] == "Psychic":
                sc["terrain"] = ""
            for sd in (sc["attacker"], sc["defender"]):
                sd["natureSmogon"] = NATURES_EN[sd["nature"]]
            return sc

        def damaging(a: dict) -> list[str]:
            return [m for m in learn.get(a["Id"], []) if m in moves and moves[m]["Category"] != "Status" and int(moves[m]["Power"]) > 0]

        made = 0
        while made < args.count + len(focus):
            force = focus[made - args.count] if made >= args.count else None
            a = rng.choice(pokemon)
            if force and force[1] == "attacker":
                a = rng.choice([p for p in pokemon if force[0] in p["Abilities"].split(";")])
            dmg_moves = damaging(a)
            if not dmg_moves:
                continue
            m = moves[rng.choice(dmg_moves)]
            d = rng.choice([p for p in pokemon if force[0] in p["Abilities"].split(";")]) if force and force[1] == "defender" else rng.choice(pokemon)
            scenarios.append(make(a, d, m, force[0] if force and force[1] == "attacker" else None, force[0] if force and force[1] == "defender" else None))
            made += 1

        # 技・特性の組み合わせを絞った条件（ランダムでは当たりにくいもの）
        def ability_id(ident: str) -> str | None:
            return next((k for k, v in abil_ident.items() if v == ident), None)

        def holders_of(ident: str) -> list[dict]:
            aid = ability_id(ident)
            return [p for p in pokemon if aid and aid in p["Abilities"].split(";")]

        def targeted(count: int, pick) -> None:
            n = 0
            while n < count:
                made_one = pick()
                if made_one:
                    scenarios.append(made_one)
                    n += 1

        n = args.targeted
        # はたきおとす（相手の持ち物の有無・自分のメガストーン）
        knock = next((m for m in moves.values() if move_ident[m["Id"]] == "knock-off"), None)
        if knock:
            def pick_knock():
                d = rng.choice(pokemon)
                s_ = make(rng.choice(pokemon), d, knock)
                if rng.random() < 0.8 and s_["defender"]["item"] is None and items:
                    it = rng.choice([i for i in items if item_ident.get(i["Id"])])
                    s_["defender"]["item"], s_["defender"]["itemSmogon"] = int(it["Id"]), item_ident.get(it["Id"])
                return s_
            targeted(n, pick_knock)
        # すてみ（反動技・はずすと自分がダメージを受ける技）
        recoil_moves = [m for m in moves.values() if "Recoil" in m["Flags"].split(";") and int(m["Power"]) > 0]
        reckless = holders_of("reckless")
        if recoil_moves and reckless:
            targeted(n, lambda: make(rng.choice(reckless), rng.choice(pokemon), rng.choice(recoil_moves), ability_id("reckless")))
        # ちからずく（追加効果がある技・ない技）
        sheer = holders_of("sheer-force")
        if sheer and gen >= 5:
            all_dmg = [m for m in moves.values() if m["Category"] != "Status" and int(m["Power"]) > 0]
            targeted(n, lambda: make(rng.choice(sheer), rng.choice(pokemon), rng.choice(all_dmg), ability_id("sheer-force")))
        # スナイパー・やけど・急所
        sniper = holders_of("sniper")
        if sniper:
            def pick_sniper():
                a = rng.choice(sniper)
                phys = [m for m in damaging(a) if moves[m]["Category"] == "Physical"] or damaging(a)
                if not phys:
                    return None
                sc = make(a, rng.choice(pokemon), moves[rng.choice(phys)], ability_id("sniper"), crit=True)
                if rng.random() < 0.5:
                    sc["attacker"]["status"] = "brn"  # スナイパーとやけどを同時に掛ける
                return sc
            targeted(n, pick_sniper)
            def pick_burn():
                a = rng.choice(pokemon)
                phys = [m for m in damaging(a) if moves[m]["Category"] == "Physical"]
                if not phys:
                    return None
                sc = make(a, rng.choice(pokemon), moves[rng.choice(phys)], crit=rng.random() < 0.5)
                sc["attacker"]["status"] = "brn"
                return sc
            targeted(n, pick_burn)
        # 雪・あられ（こおりタイプの防御）
        ice = [p for p in pokemon if "Ice" in (p["Type1"], p["Type2"])]
        def pick_snow():
            a = rng.choice(pokemon)
            phys = [m for m in damaging(a) if moves[m]["Category"] == "Physical"]
            if not phys:
                return None
            return make(a, rng.choice(ice), moves[rng.choice(phys)], weather="Hail", crit=False)
        if ice:
            targeted(n, pick_snow)
    json.dump(scenarios, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
