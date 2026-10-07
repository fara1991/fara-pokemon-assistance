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
        self.evolves_from = {r["evolves_from_species_id"] for r in api.table("pokemon_species")
                             if r["evolves_from_species_id"]}
        # HOME item ids are the in-game item indices; PokeAPI calls them game_index.
        self.game_index_to_item: dict[int, dict[int, str]] = defaultdict(dict)
        for r in api.table("item_game_indices"):
            self.game_index_to_item[int(r["generation_id"])][int(r["game_index"])] = r["item_id"]
        with (TOOLS / "item_effects.csv").open(encoding="utf-8", newline="") as f:
            self.item_effects = {r["Identifier"]: r for r in csv.DictReader(f)}
        self.learnsets_by_vg: dict[str, dict[str, set[str]]] = defaultdict(lambda: defaultdict(set))
        for r in api.table("pokemon_moves"):
            self.learnsets_by_vg[r["version_group_id"]][r["pokemon_id"]].add(r["move_id"])
        self._flavor: dict[str, dict[int, str]] | None = None

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

        # Pokemon forms that share a species but have no learnset of their own
        # (e.g. mega forms in some games) inherit the default form's moves.
        default_of_species = {p["species_id"]: pid for pid, p in self.pokemon.items() if p["is_default"] == "1"}
        for pid in pokemon_ids:
            if not learnset[pid]:
                base = default_of_species.get(self.pokemon[pid]["species_id"])
                if base and base in learnset:
                    learnset[pid] = set(learnset[base])

        pokemon_rows, species_rows = [], []
        for pid in pokemon_ids:
            name = self.display_name(pid)
            st = self.stats.get(pid)
            if not name or not st or len(st) < 6:
                continue
            p = self.pokemon[pid]
            types = self.ptypes.get(pid, {})
            nfe = 1 if p["species_id"] in self.evolves_from else 0
            pokemon_rows.append([
                pid, name, types.get("1", ""), types.get("2", ""),
                st["HP"], st["Attack"], st["Defense"], st["SpAttack"], st["SpDefense"], st["Speed"],
                SPRITE_URL.format(id=pid), p["species_id"], nfe,
            ])
            if p["is_default"] != "1":
                species_rows.append([pid, p["species_id"]])
        write_csv(out_dir / "pokemon.csv",
                  ["Id", "Name", "Type1", "Type2", "HP", "Attack", "Defense", "SpAttack", "SpDefense", "Speed",
                   "Icon", "SpeciesId", "NotFullyEvolved"], pokemon_rows)
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
                self.move_description(mid, [int(v) for v in vgs]),
            ])
        write_csv(out_dir / "moves.csv",
                  ["Id", "Name", "Type", "Power", "Accuracy", "PP", "Category", "Target", "Priority", "Description"],
                  move_rows)
        valid_moves = {row[0] for row in move_rows}
        write_csv(out_dir / "learnsets.csv", ["PokemonId", "MoveIds"],
                  [[pid, ";".join(sorted((m for m in learnset[pid] if m in valid_moves), key=int))]
                   for pid in pokemon_ids if pid in kept])

        self.write_items(ds, out_dir, generation)
        self.write_type_chart(out_dir)

    def write_items(self, ds: dict, out_dir: Path, generation: int) -> None:
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
            eff = wanted[iid] or {}
            rows.append([
                iid, name, eff.get("Category", "Other"), eff.get("Effect", ""),
                eff.get("AttackMultiplier", "1.0"), eff.get("DefenseMultiplier", "1.0"),
                eff.get("SpAttackMultiplier", "1.0"), eff.get("SpDefenseMultiplier", "1.0"),
                eff.get("DamageMultiplier", "1.0"), eff.get("TypeBoost", ""), eff.get("TypeBoostMultiplier", "1.0"),
            ])
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
    latest = max(((int(k), v) for k, v in seasons.get("list", {}).items() if k.isdigit()), default=None)
    if latest is None:
        log("  HOME: season list empty")
        return
    rules = [r for r in latest[1].get("rule", []) if r.get("rst") == 2]
    if not rules:
        log("  HOME: no completed season in the latest entry")
        return
    for rule in rules:
        fmt = "singles" if rule["rule"] == 0 else "doubles"
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

        move_rows, item_rows, nature_rows, done = [], [], [], set()
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
                    break  # first form only
        write_csv(out_dir / f"usage_moves_{fmt}.csv", ["SpeciesId", "MoveId"], move_rows)
        write_csv(out_dir / f"usage_items_{fmt}.csv", ["SpeciesId", "ItemId"], item_rows)
        write_csv(out_dir / f"usage_natures_{fmt}.csv", ["SpeciesId", "NatureId"], nature_rows)


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

    write_csv(OUT_ROOT / "datasets.csv", ["Key", "Name", "Generation"],
              [[d["key"], d["name"], d["generation"]] for d in config["datasets"]])
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
