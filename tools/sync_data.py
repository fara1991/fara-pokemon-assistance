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

Output goes to src/FaraPokemonBattleAssistant.Web/wwwroot/api/game/<Key>/*.csv.
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
sys.path.insert(0, str(TOOLS))
import champions_learnsets  # noqa: E402  Showdown の champions mod から覚える技を読む
OUT_ROOT = ROOT / "src" / "FaraPokemonBattleAssistant.Web" / "wwwroot" / "api" / "game"
POKEAPI_CSV = "https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/"
SPRITE_URL = "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/{id}.png"
HOME_LIST_URL = "https://api.battle.pokemon-home.com/tt/cbd/competition/rankmatch/list"
HOME_RESOURCE_URL = "https://resource.pokemon-home.com/battledata/ranking/{resource}/{cid}/{rst}/{ts2}"
USER_AGENT = "FaraPokemonBattleAssistant/1.0 (+https://github.com/fara1991/fara-pokemon-battle-assistant)"

LANG_JA = "11"        # ja (kanji/kana)
LANG_JA_HRKT = "1"    # ja-hrkt fallback
LANG_EN = "9"         # 日本語名が無いときだけ使う
TYPE_NAMES = {
    "normal": "Normal", "fighting": "Fighting", "flying": "Flying", "poison": "Poison",
    "ground": "Ground", "rock": "Rock", "bug": "Bug", "ghost": "Ghost", "steel": "Steel",
    "fire": "Fire", "water": "Water", "grass": "Grass", "electric": "Electric",
    "psychic": "Psychic", "ice": "Ice", "dragon": "Dragon", "dark": "Dark", "fairy": "Fairy",
}
# タイプ「？？？」（第 2〜4 世代の のろい）。タイプ相性表には入れない
UNKNOWN_TYPE = "???"
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
# PokeAPI に技フラグが入っていない第 9 世代の技（@smogon/calc のフラグに合わせて足す。かたいツメ・てつのこぶし・もふもふなどに使う）
MOVE_FLAGS_EXTRA: dict[str, set[str]] = {
    "dire-claw": {"Contact"},
    "psyshield-bash": {"Contact"},
    "stone-axe": {"Contact"},
    "wave-crash": {"Contact"},
    "headlong-rush": {"Contact", "Punch"},
    "ceaseless-edge": {"Contact"},
    "axe-kick": {"Contact"},
    "order-up": {"Pulse"},
    "jet-punch": {"Contact", "Punch"},
    "spin-out": {"Contact"},
    "population-bomb": {"Contact"},
    "ice-spinner": {"Contact"},
    "glaive-rush": {"Contact"},
    "triple-dive": {"Contact"},
    "mortal-spin": {"Contact"},
    "kowtow-cleave": {"Contact"},
    "torch-song": {"Sound"},
    "aqua-step": {"Contact"},
    "raging-bull": {"Contact"},
    "psyblade": {"Contact"},
    "collision-course": {"Contact"},
    "electro-drift": {"Contact"},
    "pounce": {"Contact"},
    "trailblaze": {"Contact"},
    "hyper-drill": {"Contact"},
    "rage-fist": {"Contact", "Punch"},
    "bitter-blade": {"Contact"},
    "double-shock": {"Contact"},
    "comeuppance": {"Contact"},
    "syrup-bomb": {"Bullet"},
    "mighty-cleave": {"Contact"},
    "hard-press": {"Contact"},
    "alluring-voice": {"Sound"},
    "temper-flare": {"Contact"},
    "supercell-slam": {"Contact"},
    "psychic-noise": {"Sound"},
    "upper-hand": {"Contact"},
}
# 反動技（すてみの対象）。反動ダメージを受ける技（move_meta の drain が負）に加え、PokeAPI の drain が 0 の反動技と、
# はずすと自分がダメージを受ける技（飛び膝蹴り系）も含める。
RECOIL_EXTRA_MOVES = {"wave-crash", "jump-kick", "high-jump-kick", "supercell-slam", "axe-kick"}
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


def has_japanese(text: str) -> bool:
    return any("\u3040" <= c <= "\u30ff" or "\u4e00" <= c <= "\u9fff" for c in text)


def load_ability_names_ja() -> dict[str, str]:
    """PokeAPI に日本語名がまだ無い特性の補完表（identifier → 日本語名）。毎週の再生成で英語に戻さないための手入力データ。"""
    path = TOOLS / "ability_names_ja.csv"
    if not path.exists():
        return {}
    with path.open(encoding="utf-8", newline="") as f:
        return {r["Identifier"]: r["Name"] for r in csv.DictReader(f)}


ABILITY_NAMES_JA = load_ability_names_ja()


def load_ability_descriptions_ja() -> dict[str, str]:
    """PokeAPI に説明文が無い特性（新しいメガシンカの特性など）の説明文（tools/ability_descriptions_ja.csv）。"""
    path = TOOLS / "ability_descriptions_ja.csv"
    if not path.exists():
        return {}
    with path.open(encoding="utf-8", newline="") as f:
        return {r["Identifier"]: r["Description"] for r in csv.DictReader(f)}


ABILITY_DESCRIPTIONS_JA = load_ability_descriptions_ja()


def effect_sentence(effect: str) -> str:
    """item_effects.csv の短い効果の文を、説明文の文末にそろえる（「…が1.2倍」→「…が1.2倍になる。」）。"""
    if not effect:
        return ""
    if effect.endswith("倍"):
        effect += "になる"
    return effect if effect.endswith("。") else effect + "。"


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
        self.type_rows = {r["id"]: r for r in api.table("types")}
        self.vg_order = {r["id"]: int(r["order"]) for r in api.table("version_groups")}
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
        # 種族 → その進化先の種族（進化前かどうかは、そのゲームに進化先がいるかで決める）
        self.evolves_to: dict[str, set[str]] = defaultdict(set)
        for r in api.table("pokemon_species"):
            if r["evolves_from_species_id"]:
                self.evolves_to[r["evolves_from_species_id"]].add(r["id"])
        # 進化先が登場した世代（昔のゲームでは、まだ進化先がいない種族は進化前ではない）
        self.evolution_generations: dict[str, list[int]] = defaultdict(list)
        for r in api.table("pokemon_species"):
            if r["evolves_from_species_id"]:
                self.evolution_generations[r["evolves_from_species_id"]].append(int(r["generation_id"]))
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
        for r in api.table("move_meta"):
            if int(r["drain"] or 0) < 0:
                self.move_flags[r["move_id"]].add("Recoil")
        for mid, r in self.moves.items():
            if r["identifier"] in RECOIL_EXTRA_MOVES:
                self.move_flags[mid].add("Recoil")
            self.move_flags[mid].update(MOVE_FLAGS_EXTRA.get(r["identifier"], ()))
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

    def flavor_description(self, table: str, key: str, ident: str, version_groups: list[int]) -> str:
        """持ち物・特性の説明文（PokeAPI のフレーバーテキスト。そのゲームの版に一番近いもの。日本語（漢字まじり）が無ければ、かな）。"""
        cache = getattr(self, "_flavor_cache", None)
        if cache is None:
            cache = self._flavor_cache = {}
        if table not in cache:
            by_id: dict[str, dict[str, dict[int, str]]] = defaultdict(lambda: defaultdict(dict))
            for r in self.api.table(table):
                if r["language_id"] in (LANG_JA, LANG_JA_HRKT):
                    by_id[r[key]][r["language_id"]][int(r["version_group_id"])] = r["flavor_text"]
            cache[table] = by_id
        langs = cache[table].get(ident)
        if not langs:
            return ""
        for lang in (LANG_JA, LANG_JA_HRKT):
            texts = langs.get(lang)
            if texts:
                preferred = [vg for vg in texts if vg in version_groups]
                if preferred:
                    vg = max(preferred)
                else:
                    # そのゲームの版に説明文が無いときは、対象より後で一番古い版（無ければ一番新しい版）
                    later = [vg for vg in texts if vg > max(version_groups)]
                    vg = min(later) if later else max(texts)
                return " ".join(texts[vg].split())
        return ""

    # -- per data set ----------------------------------------------------------------------
    # -- 昔のゲーム（historical）: その世代の時点の値 -----------------------------------------
    # PokeAPI の *_past.csv は「その世代までの値」（generation_id はその値だった最後の世代）。
    # 対象の世代以降で一番早い世代の値を使い、無ければ今の値。move_changelog は「その版で変わる前の値」。
    def _past(self, table: str) -> dict[str, list[dict[str, str]]]:
        cache = getattr(self, "_past_cache", None)
        if cache is None:
            cache = self._past_cache = {}
        if table not in cache:
            rows: dict[str, list[dict[str, str]]] = defaultdict(list)
            for r in self.api.table(table):
                rows[r.get("pokemon_id") or r.get("move_id") or ""].append(r)
            cache[table] = rows
        return cache[table]

    def type_exists(self, type_id: str, generation: int) -> bool:
        row = self.type_rows.get(type_id)
        return bool(row) and int(row["generation_id"] or 0) <= generation and type_id in self.types and \
            self.types[type_id] in TYPE_NAMES

    def types_for(self, pid: str, generation: int) -> dict[str, str]:
        rows = [r for r in self._past("pokemon_types_past").get(pid, []) if int(r["generation_id"]) >= generation]
        if not rows:
            return dict(self.ptypes.get(pid, {}))
        g = min(int(r["generation_id"]) for r in rows)
        return {r["slot"]: TYPE_NAMES.get(self.types.get(r["type_id"], ""), "") for r in rows if int(r["generation_id"]) == g}

    def stats_for(self, pid: str, generation: int) -> dict[str, int]:
        st = dict(self.stats.get(pid, {}))
        best: dict[str, tuple[int, int]] = {}
        for r in self._past("pokemon_stats_past").get(pid, []):
            g = int(r["generation_id"])
            if g >= generation and (r["stat_id"] not in best or g < best[r["stat_id"]][0]):
                best[r["stat_id"]] = (g, int(r["base_stat"]))
        for stat_id, (_, value) in best.items():
            if stat_id in STAT_KEYS:
                st[STAT_KEYS[stat_id]] = value
        if generation == 1 and "9" in best:
            # 第 1 世代は「特殊」1 つ。特攻・特防の両方に入れる
            st["SpAttack"] = st["SpDefense"] = best["9"][1]
        return st

    def abilities_for(self, pid: str, generation: int) -> list[tuple[int, int, str]]:
        if generation <= 2:
            return []  # 特性は第 3 世代から
        slots = {slot: (hidden, slot, aid) for hidden, slot, aid in self.pokemon_abilities.get(pid, [])}
        best: dict[int, tuple[int, dict[str, str]]] = {}
        for r in self._past("pokemon_abilities_past").get(pid, []):
            g, slot = int(r["generation_id"]), int(r["slot"])
            if g >= generation and (slot not in best or g < best[slot][0]):
                best[slot] = (g, r)
        for slot, (_, r) in best.items():
            if r["ability_id"]:
                slots[slot] = (int(r["is_hidden"]), slot, r["ability_id"])
            else:
                slots.pop(slot, None)
        return sorted(v for v in slots.values()
                      if v[2] in self.abilities and int(self.abilities[v[2]]["generation_id"]) <= generation)

    def move_for(self, mid: str, max_order: int, generation: int) -> dict[str, str] | None:
        m = self.moves.get(mid)
        if not m:
            return None
        m = dict(m)
        changes = sorted((r for r in self._past("move_changelog").get(mid, [])
                          if self.vg_order.get(r["changed_in_version_group_id"], 0) > max_order),
                         key=lambda r: self.vg_order[r["changed_in_version_group_id"]])
        for field in ("type_id", "power", "pp", "accuracy", "priority", "target_id", "effect_chance"):
            for r in changes:
                if r.get(field):
                    m[field] = r[field]
                    break
        if generation <= 3 and m["damage_class_id"] != "1":
            # 第 3 世代までは技のタイプで物理・特殊が決まる
            m["damage_class_id"] = self.type_rows.get(m["type_id"], {}).get("damage_class_id") or m["damage_class_id"]
        return m

    def build(self, ds: dict, out_dir: Path) -> None:
        vgs = [str(v) for v in ds["version_groups"]]
        generation = int(ds["generation"])
        historical = bool(ds.get("historical"))
        max_order = max(self.vg_order.get(v, 0) for v in vgs)

        extra_move_ids: set[str] = set()
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
            # SV に居ないポケモン（バリヤード・ガラルのすがた等）に「全技を覚える」扱いが付かないよう、
            # 1 段目（SV）→ 2 段目以降（古いゲーム）の順に、そのポケモンのデータがある最初の段の技を使う
            # （同じ段の複数バージョングループは和集合。段をまたいで混ぜない）。
            tiers = [ds.get("learnset_fallback_version_groups", []), *ds.get("learnset_fallback_older_version_groups", [])]
            tier_data: list[dict[str, set[str]]] = []
            for tier in tiers:
                tier_moves: dict[str, set[str]] = defaultdict(set)
                for vg in (str(v) for v in tier):
                    for pid, moves in self.learnsets_by_vg.get(vg, {}).items():
                        tier_moves[pid] |= moves
                tier_data.append(tier_moves)
            primary = dict(learnset)                       # チャンピオンズ自身のデータ
            default_pid = {p["species_id"]: pid for pid, p in self.pokemon.items() if p["is_default"] == "1"}

            sv_tier = tier_data[0] if tier_data else {}
            for pid, p in self.pokemon.items():
                if p["species_id"] not in roster_species or pid in learnset:
                    continue
                if p["is_default"] != "1" and not self.is_battle_form(pid):
                    continue
                moves = set(sv_tier.get(pid, ()))
                if not moves:
                    # 別フォルム（メガシンカ等）は、基本フォルムに SV までのデータがあればそちらを引き継ぐ（後段で補う）。
                    # 基本フォルムにも無いときだけ、古いゲームの段（2 段目以降）で、データがある最初の段を使う
                    base = default_pid.get(p["species_id"])
                    if base is None or base == pid or not (base in primary or sv_tier.get(base)):
                        for older in tier_data[1:]:
                            if older.get(pid):
                                moves = set(older[pid])
                                break
                learnset[pid] = moves
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
        if ds.get("learnset_override_url"):
            # Showdown のチャンピオンズ用データがあるポケモンは、PokeAPI（SV で補ったもの）ではなくそちらの技にする
            show = champions_learnsets.load_showdown(ds["learnset_override_url"], Path(self.api.cache_dir) / "champions_learnsets.ts")
            move_id_by_identifier = {r["identifier"].replace("-", ""): mid for mid, r in self.moves.items()}
            overrides = champions_learnsets.resolve(show, self.pokemon, learnset.keys(), move_id_by_identifier)
            # 置き換える前の技（SV で補ったもの）も moves.csv には残す（誰も覚えなくなっても、技のデータは今までどおり引ける）
            extra_move_ids = {m for pid in overrides for m in learnset[pid]}
            for pid, moves in overrides.items():
                learnset[pid] = moves
            print(f"{ds['key']}: Showdown の champions データで {len(overrides)} 匹の技を置き換え")
        default_of_species = {p["species_id"]: pid for pid, p in self.pokemon.items() if p["is_default"] == "1"}
        if historical:
            # ぬしポケモン・おきがえピカチュウなど、対戦で使えないフォルムは入れない
            for pid in [pid for pid in learnset if self.pokemon[pid]["is_default"] != "1"
                        and any(w in self.pokemon[pid]["identifier"] for w in self.EXCLUDED_FORM_WORDS)]:
                del learnset[pid]
            # 昔のゲーム: そのゲームまでに登場していた別フォルム（メガシンカ・ゲンシカイキ・リージョンフォームなど）を足す
            for pid, p in self.pokemon.items():
                form = self.forms_by_pokemon.get(pid)
                if pid in learnset or p["is_default"] == "1" or not form:
                    continue
                intro = form.get("introduced_in_version_group_id") or ""
                if not intro or self.vg_order.get(intro, 10 ** 6) > max_order:
                    continue
                if default_of_species.get(p["species_id"]) in learnset and self.is_battle_form(pid):
                    learnset[pid] = set()
        pokemon_ids = sorted(learnset, key=int)
        # Pokemon forms that share a species but have no learnset of their own
        # (e.g. mega forms in some games) inherit the default form's moves.
        for pid in pokemon_ids:
            if not learnset[pid]:
                base = default_of_species.get(self.pokemon[pid]["species_id"])
                if base and base in learnset:
                    learnset[pid] = set(learnset[base])

        # そのゲームに登場する種族（後の世代で進化先が増えた種族を、進化前扱いにしないため）
        roster_species = {self.pokemon[pid]["species_id"] for pid in pokemon_ids}
        pokemon_rows, species_rows = [], []
        used_abilities: set[str] = set()
        mega_stones: dict[str, tuple[str, str]] = {}
        for pid in pokemon_ids:
            name = self.display_name(pid)
            st = self.stats_for(pid, generation) if historical else self.stats.get(pid)
            if not name or not st or len(st) < 6:
                continue
            p = self.pokemon[pid]
            types = self.types_for(pid, generation) if historical else self.ptypes.get(pid, {})
            if historical:
                nfe = 1 if any(g <= generation for g in self.evolution_generations.get(p["species_id"], [])) else 0
            else:
                nfe = 1 if self.evolves_to.get(p["species_id"], set()) & roster_species else 0
            ability_list = self.abilities_for(pid, generation) if historical else sorted(self.pokemon_abilities.get(pid, []))
            ability_ids = [aid for _, _, aid in ability_list if aid in self.abilities]
            used_abilities.update(ability_ids)
            abilities = ";".join(ability_ids)
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
        move_ids = sorted({m for pid in kept for m in learnset[pid]} | extra_move_ids, key=int)
        move_rows = []
        for mid in move_ids:
            m = self.move_for(mid, max_order, generation) if historical else self.moves.get(mid)
            name = pick_name(self.move_names, mid)
            if not m or not name:
                continue
            type_name = TYPE_NAMES.get(self.types.get(m["type_id"], ""), "")
            # 第 2〜4 世代の のろい はタイプが「？？？」（PokeAPI の unknown）。タイプなしの変化技として "???" で残す
            if not type_name and self.types.get(m["type_id"]) == "unknown":
                type_name = UNKNOWN_TYPE
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
        self.write_type_chart(out_dir, generation if historical else None)
        self.write_abilities(out_dir, used_abilities, [int(v) for v in vgs])

    def write_abilities(self, out_dir: Path, used: set[str], version_groups: list[int]) -> None:
        rows = []
        for aid in sorted(used, key=int):
            a = self.abilities.get(aid)
            # 日本語名がまだ無い特性（新しいメガシンカの特性など）は英語名で出す（空欄にしない）
            name = pick_name(self.ability_names, aid) or ABILITY_NAMES_JA.get(a["identifier"] if a else "") \
                or self.ability_names.get((aid, LANG_EN))
            if a and name:
                if not has_japanese(name):
                    log(f"  WARNING: 特性 {a['identifier']} の日本語名が無く英語のままです: {name}"
                        "（tools/ability_names_ja.csv に追加してください）")
                rows.append([aid, a["identifier"], name,
                             self.flavor_description("ability_flavor_text", "ability_id", aid, version_groups)
                             or ABILITY_DESCRIPTIONS_JA.get(a["identifier"], "")])
        write_csv(out_dir / "abilities.csv", ["Id", "Identifier", "Name", "Description"], rows)

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
        vgl = [int(v) for v in ds["version_groups"]]
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
        # datasets.json の items_file があるゲーム（チャンピオンズ）は、その一覧にある持ち物だけを出す。
        # 一覧にあって効果表（item_effects.csv）に無い持ち物は、効果なしの項目として出す
        allowed: set[str] | None = None
        if ds.get("items_file"):
            allowed = set()
            by_name: dict[str, str] = {}
            for iid in self.items:
                n = pick_name(self.item_names, iid)
                if n:
                    by_name.setdefault(unicodedata.normalize("NFKC", n), iid)
            for line in (TOOLS / ds["items_file"]).read_text(encoding="utf-8").splitlines():
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                iid = by_name.get(unicodedata.normalize("NFKC", line))
                if iid is None:
                    log(f"  WARNING: 持ち物「{line}」が元データに見つかりません（{ds['items_file']}）")
                    continue
                allowed.add(iid)
                wanted.setdefault(iid, None)
        rows = []
        for iid in sorted(wanted, key=int):
            if allowed is not None and iid not in allowed:
                continue  # そのゲームにはまだ無い持ち物
            name = pick_name(self.item_names, iid)
            if not name:
                continue
            if allowed is None and self.item_first_generation.get(iid, 0) > generation:
                continue  # そのゲームには存在しない持ち物
            eff = dict(wanted[iid] or {})
            # 第 2・第 3 世代のタイプ強化の持ち物は 1.1 倍（第 4 世代から 1.2 倍）
            if generation <= 3 and eff.get("Category") == "TypeBoost":
                eff["TypeBoostMultiplier"] = "1.1"
                eff["Effect"] = eff.get("Effect", "").replace("1.2倍", "1.1倍")
            # 世代で効果が違う持ち物（ダメージ計算 DamageCalculator / Legacy の世代ごとの扱いに合わせる）
            cat = eff.get("Category")
            if generation <= 3 and cat == "SitrusBerry":
                eff["Effect"] = "HPが1/2以下になると、HPを30回復する（1回だけ）"
            elif cat == "PinchBerry" and generation <= 6:
                eff["Effect"] = "HPが1/2以下になると、最大HPの1/8を回復する（きらいな味だと混乱する）"
            elif cat == "PinchBerry" and generation == 7:
                eff["Effect"] = "HPが1/4以下になると、最大HPの1/2を回復する（きらいな味だと混乱する）"
            elif generation <= 3 and cat == "LightBall":
                eff["Effect"] = "ピカチュウの特攻が2倍"
            rows.append([
                iid, name, eff.get("Category", "Other"), eff.get("Effect", ""),
                eff.get("AttackMultiplier", "1.0"), eff.get("DefenseMultiplier", "1.0"),
                eff.get("SpAttackMultiplier", "1.0"), eff.get("SpDefenseMultiplier", "1.0"),
                eff.get("DamageMultiplier", "1.0"), eff.get("TypeBoost", ""), eff.get("TypeBoostMultiplier", "1.0"),
                self.items[iid]["identifier"],
                # 計算に使っている効果（item_effects.csv）の文を先に。無い持ち物は、ゲーム内の説明文
                effect_sentence(eff.get("Effect", "")) or self.flavor_description("item_flavor_text", "item_id", iid, vgl),
            ])
        # メガストーン（メガシンカ後のフォルム専用。計算には影響しない）
        seen = {row[0] for row in rows}
        for pid, (iid, name) in sorted((mega_stones or {}).items(), key=lambda kv: int(kv[0])):
            if iid in seen:
                continue
            seen.add(iid)
            holder = pick_name(self.species_names, self.pokemon[pid]["species_id"]) or self.display_name(pid) or ""
            rows.append([iid, name, "MegaStone", f"{holder}のメガシンカに必要", "1.0", "1.0", "1.0", "1.0", "1.0", "", "1.0", self.items[iid]["identifier"] if iid in self.items else "",
                         (self.flavor_description("item_flavor_text", "item_id", iid, vgl) if iid in self.items else "") or f"{holder}をメガシンカさせる石"])
        write_csv(out_dir / "items.csv",
                  ["Id", "Name", "Category", "Effect", "AttackMultiplier", "DefenseMultiplier", "SpAttackMultiplier",
                   "SpDefenseMultiplier", "DamageMultiplier", "TypeBoost", "TypeBoostMultiplier", "Identifier", "Description"], rows)

    def write_type_chart(self, out_dir: Path, generation: int | None = None) -> None:
        """タイプ相性。generation を渡すと、その世代の相性（type_efficacy_past）にし、まだ無いタイプ（あく・はがね・フェアリー）を除く。"""
        factors = {(r["damage_type_id"], r["target_type_id"]): int(r["damage_factor"]) for r in self.api.table("type_efficacy")}
        if generation is not None:
            best: dict[tuple[str, str], tuple[int, int]] = {}
            for r in self.api.table("type_efficacy_past"):
                key, g = (r["damage_type_id"], r["target_type_id"]), int(r["generation_id"])
                if g >= generation and (key not in best or g < best[key][0]):
                    best[key] = (g, int(r["damage_factor"]))
            for key, (_, factor) in best.items():
                factors[key] = factor
            factors = {k: v for k, v in factors.items()
                       if self.type_exists(k[0], generation) and self.type_exists(k[1], generation)}
        rows = []
        for (atk, dfn), factor in factors.items():
            a = TYPE_NAMES.get(self.types.get(atk, ""))
            d = TYPE_NAMES.get(self.types.get(dfn, ""))
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
              ["Key", "Name", "Generation", "EvSystem", "CommandPrefix", "Gimmick", "UsageFallback", "Calc"],
              [[d["key"], d["name"], d["generation"], d.get("ev_system", "Classic"), d.get("command_prefix", ""),
                d.get("gimmick", "None"), d.get("usage_fallback", ""), d.get("calc", "Full")]
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
