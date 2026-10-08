#!/usr/bin/env python3
"""Regenerate the CSV data set used by the web app and the Twitch bot.

Sources
  * PokeAPI's CSV dump on GitHub (species, forms, stats, moves, items, learnsets).
    The dump is the canonical data behind https://pokeapi.co/ and can be bulk
    downloaded without hitting the REST API's fair-use limits.
  * Pokémon HOME ranked-battle usage statistics (optional, per data set).

Usage
  python tools/sync_data.py                 # everything
  python tools/sync_data.py --skip-home     # only PokeAPI data
  python tools/sync_data.py --only Champions

Output goes to src/FaraPokemonAssistance.Web/wwwroot/data/<Key>/*.csv.
Hand-maintained inputs live next to this script: datasets.json, item_effects.csv.
"""
from __future__ import annotations

import argparse
import datetime
import csv
import io
import json
import os
import sys
import unicodedata
import urllib.request
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOOLS = Path(__file__).resolve().parent
OUT_ROOT = ROOT / "src" / "FaraPokemonAssistance.Web" / "wwwroot" / "data"
POKEAPI_CSV = "https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/"
SPRITE_URL = "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/{id}.png"
HOME_LIST_URL = "https://api.battle.pokemon-home.com/tt/cbd/competition/rankmatch/list"
HOME_RESOURCE_URL = "https://resource.pokemon-home.com/battledata/ranking/{resource}/{cid}/{rst}/{ts2}"
USER_AGENT = "FaraPokemonAssistance/1.0 (+https://github.com/fara1991/fara-pokemon-assistance)"

LANG_JA = "11"        # ja (kanji/kana)
LANG_JA_HRKT = "1"    # ja-hrkt fallback
TYPE_NAMES = {
    "normal": "Normal", "fighting": "Fighting", "flying": "Flying", "poison": "Poison",
    "ground": "Ground", "rock": "Rock", "bug": "Bug", "ghost": "Ghost", "steel": "Steel",
    "fire": "Fire", "water": "Water", "grass": "Grass", "electric": "Electric",
    "psychic": "Psychic", "ice": "Ice", "dragon": "Dragon", "dark": "Dark", "fairy": "Fairy",
}
MOVE_TARGETS = {
    "9": "AllOthers",        # all-other-pokemon (earthquake)
    "11": "AllFoes",         # all-opponents (expanding force, dazzling gleam)
    "14": "AllOthers",       # all-pokemon
}
DAMAGE_CLASS = {"1": "Status", "2": "Physical", "3": "Special"}
# 計算に関係する技フラグだけ残す（特性・持ち物の判定に使う）
MOVE_FLAGS_KEPT = {
    "contact": "Contact", "sound": "Sound", "punch": "Punch", "bite": "Bite",
    "ballistics": "Bullet", "pulse": "Pulse", "powder": "Powder", "recharge": "Recharge",
}
STAT_KEYS = {"1": "HP", "2": "Attack", "3": "Defense", "4": "SpAttack", "5": "SpDefense", "6": "Speed"}

# HOME nature ids follow the in-game order; keep in sync with natures.csv.
NATURES = [
    ("がんばりや", "", ""), ("さみしがり", "Attack", "Defense"), ("ゆうかん", "Attack", "Speed"),
    ("いじっぱり", "Attack", "SpAttack"), ("やんちゃ", "Attack", "SpDefense"),
    ("ずぶとい", "Defense", "Attack"), ("すなお", "", ""), ("のんき", "Defense", "Speed"),
    ("わんぱく", "Defense", "SpAttack"), ("のうてんき", "Defense", "SpDefense"),
    ("おくびょう", "Speed", "Attack"), ("せっかち", "Speed", "Defense"), ("まじめ", "", ""),
    ("ようき", "Speed", "SpAttack"), ("むじゃき", "Speed", "SpDefense"),
    ("ひかえめ", "SpAttack", "Attack"), ("おっとり", "SpAttack", "Defense"),
    ("れいせい", "SpAttack", "Speed"), ("てれや", "", ""), ("うっかりや", "SpAttack", "SpDefense"),
    ("おだやか", "SpDefense", "Attack"), ("おとなしい", "SpDefense", "Defense"),
    ("なまいき", "SpDefense", "Speed"), ("しんちょう", "SpDefense", "SpAttack"), ("きまぐれ", "", ""),
]


def log(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


def fetch(url: str, data: bytes | None = None, headers: dict | None = None) -> bytes:
    req = urllib.request.Request(url, data=data, headers={"User-Agent": USER_AGENT, **(headers or {})})
    with urllib.request.urlopen(req, timeout=120) as resp:
        return resp.read()


class PokeApi:
    """Lazy loader for PokeAPI CSV files with an on-disk cache."""

    def __init__(self, cache_dir: Path):
        self.cache_dir = cache_dir
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self._tables: dict[str, list[dict[str, str]]] = {}

    def table(self, name: str) -> list[dict[str, str]]:
        if name not in self._tables:
            path = self.cache_dir / f"{name}.csv"
            if not path.exists():
                log(f"  downloading {name}.csv")
                path.write_bytes(fetch(POKEAPI_CSV + f"{name}.csv"))
            with path.open(encoding="utf-8", newline="") as f:
                self._tables[name] = list(csv.DictReader(f))
        return self._tables[name]


def half_width(text: str) -> str:
    """Fold full-width Latin letters/digits (メガリザードンＸ → メガリザードンX)."""
    return "".join(
        unicodedata.normalize("NFKC", ch) if "Ａ" <= ch <= "ｚ" or "０" <= ch <= "９" else ch
        for ch in text
    )


def write_csv(path: Path, header: list[str], rows: list[list]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    buf = io.StringIO()
    writer = csv.writer(buf, lineterminator="\n")
    writer.writerow(header)
    writer.writerows(rows)
    path.write_text(buf.getvalue(), encoding="utf-8")
    log(f"  wrote {path.relative_to(ROOT)} ({len(rows)} rows)")


def pick_name(rows: dict[tuple[str, str], str], key: str) -> str | None:
    return rows.get((key, LANG_JA)) or rows.get((key, LANG_JA_HRKT))


# --------------------------------------------------------------------------- PokeAPI → our CSVs

class Builder:
    def __init__(self, api: PokeApi):
        self.api = api
        self.species_names = {(r["pokemon_species_id"], r["local_language_id"]): r["name"]
                              for r in api.table("pokemon_species_names")}
        self.form_names = {(r["pokemon_form_id"], r["local_language_id"]): r["form_name"]
                           for r in api.table("pokemon_form_names")}
        self.move_names = {(r["move_id"], r["local_language_id"]): r["name"] for r in api.table("move_names")}
        self.item_names = {(r["item_id"], r["local_language_id"]): r["name"] for r in api.table("item_names")}
        self.types = {r["id"]: r["identifier"] for r in api.table("types")}
        self.pokemon = {r["id"]: r for r in api.table("pokemon")}
        self.species = {r["id"]: r for r in api.table("pokemon_species")}
        self.forms_by_pokemon: dict[str, dict[str, str]] = {}
        for r in api.table("pokemon_forms"):
            # The first (lowest form_order) form of a pokemon is the one we describe.
            self.forms_by_pokemon.setdefault(r["pokemon_id"], r)
        self.stats: dict[str, dict[str, int]] = defaultdict(dict)
        for r in api.table("pokemon_stats"):
            if r["stat_id"] in STAT_KEYS:
                self.stats[r["pokemon_id"]][STAT_KEYS[r["stat_id"]]] = int(r["base_stat"])
        self.ptypes: dict[str, dict[str, str]] = defaultdict(dict)
        for r in api.table("pokemon_types"):
            self.ptypes[r["pokemon_id"]][r["slot"]] = TYPE_NAMES.get(self.types.get(r["type_id"], ""), "")
        self.moves = {r["id"]: r for r in api.table("moves")}
        self.items = {r["id"]: r for r in api.table("items")}
        self.item_by_identifier = {r["identifier"]: r["id"] for r in api.table("items")}
        # 持ち物が初めて登場した世代（その世代より前のデータセットには出さない）
        self.item_first_generation: dict[str, int] = {}
        for r in api.table("item_game_indices"):
            gen = int(r["generation_id"])
            self.item_first_generation[r["item_id"]] = min(gen, self.item_first_generation.get(r["item_id"], gen))
        self.evolves_from = {r["evolves_from_species_id"] for r in api.table("pokemon_species")
                             if r["evolves_from_species_id"]}
        # HOME item ids are the in-game item indices; PokeAPI calls them game_index.
        self.game_index_to_item: dict[int, dict[int, str]] = defaultdict(dict)
        for r in api.table("item_game_indices"):
            self.game_index_to_item[int(r["generation_id"])][int(r["game_index"])] = r["item_id"]
        with (TOOLS / "item_effects.csv").open(encoding="utf-8", newline="") as f:
            self.item_effects = {r["Identifier"]: r for r in csv.DictReader(f)}
        flag_names = {r["id"]: r["identifier"] for r in api.table("move_flags")}
        self.move_flags: dict[str, set[str]] = defaultdict(set)
        for r in api.table("move_flag_map"):
            flag = flag_names.get(r["move_flag_id"], "")
            if flag in MOVE_FLAGS_KEPT:
                self.move_flags[r["move_id"]].add(MOVE_FLAGS_KEPT[flag])
        self.ability_names = {(r["ability_id"], r["local_language_id"]): r["name"] for r in api.table("ability_names")}
        self.abilities = {r["id"]: r for r in api.table("abilities") if r["is_main_series"] == "1"}
        self.pokemon_abilities: dict[str, list[tuple[int, int, str]]] = defaultdict(list)
        for r in api.table("pokemon_abilities"):
            # 通常特性をスロット順に、隠れ特性を最後に
            self.pokemon_abilities[r["pokemon_id"]].append((int(r["is_hidden"]), int(r["slot"]), r["ability_id"]))
        self.dex_species: dict[str, set[str]] = defaultdict(set)
        for r in api.table("pokemon_dex_numbers"):
            self.dex_species[r["pokedex_id"]].add(r["species_id"])
        self.learnsets_by_vg: dict[str, dict[str, set[str]]] = defaultdict(lambda: defaultdict(set))
        for r in api.table("pokemon_moves"):
            self.learnsets_by_vg[r["version_group_id"]][r["pokemon_id"]].add(r["move_id"])
        self._flavor: dict[str, dict[int, str]] | None = None

    # -- forms -----------------------------------------------------------------------------
    EXCLUDED_FORM_WORDS = ("gmax", "totem", "starter", "cap", "cosplay", "partner", "eternamax", "-ash", "-eternal", "-belle", "-libre", "-phd", "-pop-star", "-rock-star")

    def is_battle_form(self, pid: str) -> bool:
        """対戦で意味のある別フォルムか（メガシンカ、性別差、種族値かタイプが違うフォルム）。"""
        p = self.pokemon[pid]
        if any(w in p["identifier"] for w in self.EXCLUDED_FORM_WORDS):
            return False
        form = self.forms_by_pokemon.get(pid)
        if form and (form["is_mega"] == "1" or form["form_identifier"] in ("male", "female")):
            return True
        default = next((q for q in self.pokemon.values() if q["species_id"] == p["species_id"] and q["is_default"] == "1"), None)
        if default is None:
            return False
        return self.stats.get(pid) != self.stats.get(default["id"]) or self.ptypes.get(pid) != self.ptypes.get(default["id"])

    # -- names -----------------------------------------------------------------------------
    def display_name(self, pid: str) -> str | None:
        p = self.pokemon[pid]
        species = pick_name(self.species_names, p["species_id"])
        if not species:
            return None
        form = self.forms_by_pokemon.get(pid)
        if p["is_default"] == "1" or form is None:
            return species
        fid = form["form_identifier"]
        if fid == "female":
            return f"{species}(♀)"
        if fid == "male":
            return f"{species}(♂)"
        fname = pick_name(self.form_names, form["id"])
        if not fname:
            # No Japanese form name; fall back to the English identifier.
            return f"{species}({fid})"
        fname = half_width(fname)
        if species in fname:
            return fname
        return f"{species}({fname})"

    def move_description(self, move_id: str, version_groups: list[int]) -> str:
        if self._flavor is None:
            self._flavor = defaultdict(dict)
            for r in self.api.table("move_flavor_text"):
                if r["language_id"] == LANG_JA:
                    self._flavor[r["move_id"]][int(r["version_group_id"])] = r["flavor_text"]
        texts = self._flavor.get(move_id, {})
        if not texts:
            return ""
        preferred = [vg for vg in texts if vg in version_groups]
        vg = max(preferred) if preferred else max(texts)
        return " ".join(texts[vg].split())

    # -- per data set ----------------------------------------------------------------------
    def build(self, ds: dict, out_dir: Path) -> None:
        vgs = [str(v) for v in ds["version_groups"]]
        generation = int(ds["generation"])

        learnset: dict[str, set[str]] = defaultdict(set)
        for vg in vgs:
            for pid, moves in self.learnsets_by_vg.get(vg, {}).items():
                learnset[pid] |= moves
        pokemon_ids = sorted((pid for pid in learnset if pid in self.pokemon), key=int)
        if not pokemon_ids:
            raise SystemExit(f"{ds['key']}: no pokemon found for version groups {vgs}")

        provisional: set[str] = set()
        if ds.get("pokedex_id"):
            # 図鑑を持つデータセット（チャンピオンズ）は図鑑に載っている種族をすべて収録する。
            # 技データがまだ無いポケモンは learnset_fallback_version_groups（SV）の技で補う。
            roster_species = self.dex_species.get(str(ds["pokedex_id"]), set())
            fallback: dict[str, set[str]] = defaultdict(set)
            for vg in (str(v) for v in ds.get("learnset_fallback_version_groups", [])):
                for pid, moves in self.learnsets_by_vg.get(vg, {}).items():
                    fallback[pid] |= moves
            for pid, p in self.pokemon.items():
                if p["species_id"] not in roster_species or pid in learnset:
                    continue
                if p["is_default"] != "1" and not self.is_battle_form(pid):
                    continue
                learnset[pid] = set(fallback.get(pid, ()))
            # 図鑑に無い種族は（技データがあっても）収録しない
            for pid in list(learnset):
                if self.pokemon[pid]["species_id"] not in roster_species:
                    del learnset[pid]
        elif ds.get("supplement_version_groups"):
            # 収録が追いついていないデータセットを別のバージョングループで補完し、Provisional=1 を立てる
            for vg in (str(v) for v in ds["supplement_version_groups"]):
                for pid, moves in self.learnsets_by_vg.get(vg, {}).items():
                    if pid in self.pokemon and pid not in learnset:
                        learnset[pid] = set(moves)
                        provisional.add(pid)
        pokemon_ids = sorted(learnset, key=int)
        # Pokemon forms that share a species but have no learnset of their own
        # (e.g. mega forms in some games) inherit the default form's moves.
        default_of_species = {p["species_id"]: pid for pid, p in self.pokemon.items() if p["is_default"] == "1"}
        for pid in pokemon_ids:
            if not learnset[pid]:
                base = default_of_species.get(self.pokemon[pid]["species_id"])
                if base and base in learnset:
                    learnset[pid] = set(learnset[base])

        pokemon_rows, species_rows = [], []
        mega_stones: dict[str, tuple[str, str]] = {}
        for pid in pokemon_ids:
            name = self.display_name(pid)
            st = self.stats.get(pid)
            if not name or not st or len(st) < 6:
                continue
            p = self.pokemon[pid]
            types = self.ptypes.get(pid, {})
            nfe = 1 if p["species_id"] in self.evolves_from else 0
            abilities = ";".join(aid for _, _, aid in sorted(self.pokemon_abilities.get(pid, []))
                                 if aid in self.abilities)
            stone = self.mega_stone(pid)
            if stone:
                mega_stones[pid] = stone
            pokemon_rows.append([
                pid, name, types.get("1", ""), types.get("2", ""),
                st["HP"], st["Attack"], st["Defense"], st["SpAttack"], st["SpDefense"], st["Speed"],
                SPRITE_URL.format(id=pid), p["species_id"], nfe, abilities, 1 if pid in provisional else 0,
                stone[0] if stone else "",
            ])
            if p["is_default"] != "1":
                species_rows.append([pid, p["species_id"]])
        # 性別でフォルムが分かれる種族（イエッサン等）は基本フォルムにも (♂) を付けて区別できるようにする
        female_species = {row[1][:-3] for row in pokemon_rows if row[1].endswith("(♀)")}
        for row in pokemon_rows:
            if row[1] in female_species:
                row[1] = f"{row[1]}(♂)"
        write_csv(out_dir / "pokemon.csv",
                  ["Id", "Name", "Type1", "Type2", "HP", "Attack", "Defense", "SpAttack", "SpDefense", "Speed",
                   "Icon", "SpeciesId", "NotFullyEvolved", "Abilities", "Provisional", "MegaStoneId"], pokemon_rows)
        write_csv(out_dir / "species_map.csv", ["FormId", "SpeciesId"], species_rows)

        kept = {row[0] for row in pokemon_rows}
        move_ids = sorted({m for pid in kept for m in learnset[pid]}, key=int)
        move_rows = []
        for mid in move_ids:
            m = self.moves.get(mid)
            name = pick_name(self.move_names, mid)
            if not m or not name:
                continue
            type_name = TYPE_NAMES.get(self.types.get(m["type_id"], ""), "")
            if not type_name:
                continue
            move_rows.append([
                mid, name, type_name, m["power"] or 0, m["accuracy"] or 0, m["pp"] or 0,
                DAMAGE_CLASS.get(m["damage_class_id"], "Status"),
                MOVE_TARGETS.get(m["target_id"], "Single"),
                m["priority"] or 0,
                ";".join(sorted(self.move_flags.get(mid, ()))),
                m["effect_chance"] or 0,
                self.move_description(mid, [int(v) for v in vgs]),
            ])
        write_csv(out_dir / "moves.csv",
                  ["Id", "Name", "Type", "Power", "Accuracy", "PP", "Category", "Target", "Priority", "Flags",
                   "EffectChance", "Description"],
                  move_rows)
        valid_moves = {row[0] for row in move_rows}
        write_csv(out_dir / "learnsets.csv", ["PokemonId", "MoveIds"],
                  [[pid, ";".join(sorted((m for m in learnset[pid] if m in valid_moves), key=int))]
                   for pid in pokemon_ids if pid in kept])

        self.write_items(ds, out_dir, generation, mega_stones)
        self.write_type_chart(out_dir)
        self.write_abilities(out_dir, kept)

    def write_abilities(self, out_dir: Path, pokemon_ids: set[str]) -> None:
        used = {aid for pid in pokemon_ids for _, _, aid in self.pokemon_abilities.get(pid, [])}
        rows = []
        for aid in sorted(used, key=int):
            a = self.abilities.get(aid)
            name = pick_name(self.ability_names, aid)
            if a and name:
                rows.append([aid, a["identifier"], name])
        write_csv(out_dir / "abilities.csv", ["Id", "Identifier", "Name"], rows)

    MEGA_STONE_CATEGORY = "44"

    def mega_stone(self, pid: str) -> tuple[str, str] | None:
        """メガシンカ後のフォルムなら (持ち物 ID, 名前)。データに無いメガストーンは種族名から作る。"""
        form = self.forms_by_pokemon.get(pid)
        p = self.pokemon[pid]
        if not form or form["is_mega"] != "1":
            return None
        ident = p["identifier"]  # charizard-mega-x
        base, _, suffix = ident.partition("-mega")
        suffix = suffix.strip("-")  # "x" / "y" / ""
        best: tuple[int, str] | None = None
        for iid, item in self.items.items():
            if item["category_id"] != self.MEGA_STONE_CATEGORY:
                continue
            sid = item["identifier"]
            stone_suffix = sid[-1] if sid.endswith(("-x", "-y")) else ""
            if stone_suffix != suffix:
                continue
            n = 0
            while n < min(len(base), len(sid)) and base[n] == sid[n]:
                n += 1
            if n >= 4 and (best is None or n > best[0]):
                best = (n, iid)
        if best:
            name = pick_name(self.item_names, best[1])
            if name:
                return best[1], half_width(name)
        species = pick_name(self.species_names, p["species_id"]) or ""
        stem = species.rstrip("ー")
        if stem.endswith("ナ"):
            stem = stem[:-1]
        return str(100000 + int(pid)), f"{stem}ナイト{suffix.upper()}"

    def write_items(self, ds: dict, out_dir: Path, generation: int,
                    mega_stones: dict[str, tuple[str, str]] | None = None) -> None:
        wanted: dict[str, dict] = {}
        for identifier, eff in self.item_effects.items():
            iid = self.item_by_identifier.get(identifier)
            if iid:
                wanted[iid] = eff
        # Items that show up in usage data get a neutral entry so they can be displayed.
        for fmt in ("singles", "doubles"):
            path = out_dir / f"usage_items_{fmt}.csv"
            if path.exists():
                with path.open(encoding="utf-8", newline="") as f:
                    for r in csv.DictReader(f):
                        wanted.setdefault(r["ItemId"], None)
        rows = []
        for iid in sorted(wanted, key=int):
            name = pick_name(self.item_names, iid)
            if not name:
                continue
            if self.item_first_generation.get(iid, 0) > generation:
                continue  # そのゲームには存在しない持ち物
            eff = wanted[iid] or {}
            rows.append([
                iid, name, eff.get("Category", "Other"), eff.get("Effect", ""),
                eff.get("AttackMultiplier", "1.0"), eff.get("DefenseMultiplier", "1.0"),
                eff.get("SpAttackMultiplier", "1.0"), eff.get("SpDefenseMultiplier", "1.0"),
                eff.get("DamageMultiplier", "1.0"), eff.get("TypeBoost", ""), eff.get("TypeBoostMultiplier", "1.0"),
            ])
        # メガストーン（メガシンカ後のフォルム専用。計算には影響しない）
        seen = {row[0] for row in rows}
        for pid, (iid, name) in sorted((mega_stones or {}).items(), key=lambda kv: int(kv[0])):
            if iid in seen:
                continue
            seen.add(iid)
            holder = self.display_name(pid) or ""
            rows.append([iid, name, "MegaStone", f"{holder}のメガシンカに必要", "1.0", "1.0", "1.0", "1.0", "1.0", "", "1.0"])
        write_csv(out_dir / "items.csv",
                  ["Id", "Name", "Category", "Effect", "AttackMultiplier", "DefenseMultiplier", "SpAttackMultiplier",
                   "SpDefenseMultiplier", "DamageMultiplier", "TypeBoost", "TypeBoostMultiplier"], rows)

    def write_type_chart(self, out_dir: Path) -> None:
        rows = []
        for r in self.api.table("type_efficacy"):
            a = TYPE_NAMES.get(self.types.get(r["damage_type_id"], ""))
            d = TYPE_NAMES.get(self.types.get(r["target_type_id"], ""))
            factor = int(r["damage_factor"])
            if a and d and factor != 100:
                rows.append([a, d, factor / 100])
        write_csv(out_dir / "type_effectiveness.csv", ["AttackType", "DefenseType", "Multiplier"], rows)

    def home_item_id(self, generation: int, home_id: int) -> str | None:
        for gen in (generation, 9, 8, 7, 6, 5, 4):
            iid = self.game_index_to_item.get(gen, {}).get(home_id)
            if iid:
                return iid
        return None


# --------------------------------------------------------------------------- Pokémon HOME usage

def sync_home(builder: Builder, ds: dict, out_dir: Path) -> None:
    home = ds.get("home")
    if not home:
        log(f"  {ds['key']}: no HOME source configured, keeping existing usage files")
        return
    body = json.dumps({"soft": home["soft"]}).encode()
    seasons = json.loads(fetch(HOME_LIST_URL, data=body,
                               headers={"Content-Type": "application/json", "Accept": "application/json"}))
    # list は { "41": { "<cId>": {rule, rst, cId, ts2, ...}, ... }, "40": {...} } という形。
    # 新しいシーズンから順に見て、形式ごとに「集計済み (rst == 2)」の最新シーズンを選ぶ。
    # 一番新しいシーズンは開催中 (rst == 0) で集計が無い。
    rules: dict[str, dict] = {}
    for _, season in sorted(((int(k), v) for k, v in seasons.get("list", {}).items() if k.isdigit()), reverse=True):
        entries = list(season.values()) if isinstance(season, dict) else list(season)
        for rule in entries:
            if not isinstance(rule, dict) or rule.get("rst") != 2:
                continue
            fmt = "singles" if rule.get("rule") == 0 else "doubles"
            rules.setdefault(fmt, rule)
        if len(rules) == 2:
            break
    if not rules:
        log("  HOME: no completed season found; season list summary follows (season: rule/rst/cId)")
        for num, season in sorted(((int(k), v) for k, v in seasons.get("list", {}).items() if k.isdigit()), reverse=True)[:6]:
            entries = list(season.values()) if isinstance(season, dict) else list(season)
            summary = ", ".join(f"{r.get('rule')}/{r.get('rst')}/{r.get('cId')}" for r in entries if isinstance(r, dict)) or str(season)[:300]
            log(f"    {num}: {summary}")
        top_keys = sorted(seasons.keys()) if isinstance(seasons, dict) else type(seasons).__name__
        log(f"    response top-level keys: {top_keys}")
        return
    for fmt, rule in rules.items():
        base = HOME_RESOURCE_URL.format(resource=home["resource"], cid=rule["cId"], rst=rule["rst"], ts2=rule["ts2"])
        log(f"  HOME {fmt}: {base}")
        ranking = json.loads(fetch(base + "/pokemon"))
        seen, pokemon_rows = set(), []
        for entry in ranking:
            sid = entry["id"]
            if sid not in seen:
                seen.add(sid)
                pokemon_rows.append([len(pokemon_rows) + 1, sid])
        write_csv(out_dir / f"usage_pokemon_{fmt}.csv", ["Rank", "SpeciesId"], pokemon_rows)

        move_rows, item_rows, nature_rows, ability_rows, done = [], [], [], [], set()
        for i in range(1, 7):
            try:
                detail = json.loads(fetch(f"{base}/pdetail-{i}"))
            except Exception as ex:  # noqa: BLE001 - a missing page is not fatal
                log(f"  HOME {fmt}: pdetail-{i} unavailable ({ex})")
                continue
            for sid, forms in detail.items():
                if not sid.isdigit() or sid in done:
                    continue
                done.add(sid)
                for form in forms.values():
                    temoti = form.get("temoti")
                    if not temoti:
                        break
                    for w in temoti.get("waza", []):
                        if str(w.get("id", "")).isdigit():
                            move_rows.append([sid, int(w["id"])])
                    for it in temoti.get("motimono", []):
                        if str(it.get("id", "")).isdigit():
                            iid = builder.home_item_id(int(ds["generation"]), int(it["id"]))
                            if iid:
                                item_rows.append([sid, iid])
                    for n in temoti.get("seikaku", []):
                        if str(n.get("id", "")).isdigit():
                            nature_rows.append([sid, int(n["id"])])
                    for t in temoti.get("tokusei", []):
                        if str(t.get("id", "")).isdigit():
                            ability_rows.append([sid, int(t["id"])])
                    break  # first form only
        write_csv(out_dir / f"usage_moves_{fmt}.csv", ["SpeciesId", "MoveId"], move_rows)
        write_csv(out_dir / f"usage_items_{fmt}.csv", ["SpeciesId", "ItemId"], item_rows)
        write_csv(out_dir / f"usage_natures_{fmt}.csv", ["SpeciesId", "NatureId"], nature_rows)
        write_csv(out_dir / f"usage_abilities_{fmt}.csv", ["SpeciesId", "AbilityId"], ability_rows)


# --------------------------------------------------------------------------- main

def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--skip-home", action="store_true", help="do not contact Pokémon HOME")
    parser.add_argument("--skip-pokeapi", action="store_true", help="do not regenerate PokeAPI-derived files")
    parser.add_argument("--only", help="data set key to process (default: all)")
    parser.add_argument("--cache", default=os.environ.get("POKEAPI_CACHE", str(TOOLS / ".cache")),
                        help="directory for the downloaded PokeAPI CSVs")
    args = parser.parse_args()

    config = json.loads((TOOLS / "datasets.json").read_text(encoding="utf-8"))
    datasets = [d for d in config["datasets"] if not args.only or d["key"] == args.only]
    if not datasets:
        raise SystemExit(f"unknown data set {args.only}")

    api = PokeApi(Path(args.cache))
    builder = Builder(api)

    write_csv(OUT_ROOT / "datasets.csv",
              ["Key", "Name", "Generation", "EvSystem", "CommandPrefix", "Gimmick", "UsageFallback"],
              [[d["key"], d["name"], d["generation"], d.get("ev_system", "Classic"), d.get("command_prefix", ""),
                d.get("gimmick", "None"), d.get("usage_fallback", "")]
               for d in config["datasets"]])
    # 最終更新日時（ホーム画面に表示する）
    (OUT_ROOT / "updated.txt").write_text(
        datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ") + "\n", encoding="utf-8")
    write_csv(OUT_ROOT / "natures.csv", ["Id", "Name", "IncreasedStat", "DecreasedStat"],
              [[i, n, up, down] for i, (n, up, down) in enumerate(NATURES)])

    for ds in datasets:
        out_dir = OUT_ROOT / ds["key"]
        log(f"== {ds['key']}")
        if not args.skip_home:
            try:
                sync_home(builder, ds, out_dir)
            except Exception as ex:  # noqa: BLE001 - usage data is optional
                log(f"  HOME update failed for {ds['key']}: {ex}")
        if not args.skip_pokeapi:
            builder.build(ds, out_dir)


if __name__ == "__main__":
    main()
