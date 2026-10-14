"""ポケモンチャンピオンズの覚える技を、Pokémon Showdown の champions mod（data/mods/champions/learnsets.ts）から読む。

PokeAPI にはチャンピオンズ自身の技データが一部のポケモンにしか無く、残りは SV で補っているため、
SV にあってチャンピオンズに無い技（例: イエッサンのマジカルリーフ）が出たり、その逆（マジカルフレイム）が抜けたりする。
そこで、Showdown のチャンピオンズ用データがあるポケモンは、その技で置き換える（sync_data.py と verify_learnsets.py が共有する）。
"""
from __future__ import annotations

import re
import urllib.request
from pathlib import Path

USER_AGENT = "FaraPokemonBattleAssistant/1.0 (+https://github.com/fara1991/fara-pokemon-battle-assistant)"
# 別フォルムが基本フォルムの技を引き継ぐとき、地域のすがたは引き継がない（リージョンフォームは覚える技が違う）
REGIONAL_WORDS = ("-alola", "-galar", "-hisui", "-paldea")


def load_showdown(url: str, cache_file: Path) -> dict[str, set[str]]:
    """Showdown の ID（例: indeedee・indeedeef）→ 覚える技の ID（例: mysticalfire）の集合。取得した内容は cache_file に置く。"""
    if cache_file.exists():
        text = cache_file.read_text(encoding="utf-8")
    else:
        req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(req, timeout=120) as r:
            text = r.read().decode("utf-8")
        cache_file.parent.mkdir(parents=True, exist_ok=True)
        cache_file.write_text(text, encoding="utf-8")
    result: dict[str, set[str]] = {}
    for m in re.finditer(r"\n\t([a-z0-9]+): \{\n\t\tlearnset: \{(.*?)\n\t\t\},", text, re.S):
        result[m.group(1)] = set(re.findall(r"\t\t\t([a-z0-9]+): \[", m.group(2)))
    if not result:
        raise SystemExit("Showdown の champions learnsets.ts を読めませんでした（形式が変わった可能性があります）")
    return result


def _candidates(identifier: str, is_default: bool) -> list[str]:
    """PokeAPI の identifier（例: indeedee-female）に対応する Showdown の ID の候補。"""
    out = [identifier.replace("-", "")]
    if identifier.endswith("-male"):
        out.append(identifier[:-5].replace("-", ""))
    if identifier.endswith("-female"):
        out.append(identifier[:-7].replace("-", "") + "f")
    if is_default and "-" in identifier:
        out.append(identifier.split("-")[0])   # アーマーガア・ギルガルド（シールドフォルム）などの基本フォルム
    return out


def resolve(show: dict[str, set[str]], pokemon: dict[str, dict], pids, move_id_by_identifier: dict[str, str]) -> dict[str, set[str]]:
    """
    pids のうち、Showdown のデータで置き換えるポケモンの ID → 技の ID の集合（PokeAPI の技 ID）。
    * identifier から Showdown の ID が決まるポケモンは、その技。
    * 決まらない別フォルム（メガシンカなど）は、同じ種族の基本フォルムが決まっていればその技を引き継ぐ（地域のすがたは除く）。
    * どちらでもないポケモン（ガラルのバリヤード等）は含めない（呼び出し側が今までの技を使う）。
    """
    pids = list(pids)
    default_of = {p["species_id"]: pid for pid, p in pokemon.items() if p["is_default"] == "1"}
    direct: dict[str, str] = {}
    for pid in pids:
        p = pokemon[pid]
        for c in _candidates(p["identifier"], p["is_default"] == "1"):
            if c in show:
                direct[pid] = c
                break

    def moves_of(key: str) -> set[str]:
        return {move_id_by_identifier[m] for m in show[key] if m in move_id_by_identifier}

    result = {pid: moves_of(key) for pid, key in direct.items()}
    for pid in pids:
        if pid in result:
            continue
        p = pokemon[pid]
        if any(w in p["identifier"] for w in REGIONAL_WORDS):
            continue
        base = default_of.get(p["species_id"])
        if base and base != pid and base in direct:
            result[pid] = moves_of(direct[base])
    return result
