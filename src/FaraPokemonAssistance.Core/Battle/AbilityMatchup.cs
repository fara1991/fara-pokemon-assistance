using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>
/// 特性を含めたタイプ相性。タイプ相性表と「有利なポケモン」に使う。
/// ダメージ計算機（<see cref="DamageCalculator"/>）が扱う特性のうち、技のタイプだけで決まるものを反映する
/// （ぼうおん・ぼうだんのように技ごとに決まるものは含めない）。
/// </summary>
public static class AbilityMatchup
{
    /// <param name="TypeMultiplier">タイプだけで見た倍率。</param>
    /// <param name="Multiplier">特性を含めた倍率。</param>
    /// <param name="Notes">特性で倍率が変わったときの説明（例: <c>あついしぼう(半減)</c>）。</param>
    public sealed record Result(double TypeMultiplier, double Multiplier, IReadOnlyList<string> Notes)
    {
        /// <summary>特性で倍率が変わったか。</summary>
        public bool ChangedByAbility => Notes.Count > 0;
    }

    /// <summary>
    /// <paramref name="attackType"/> の技が、<paramref name="defenderTypes"/> のポケモンに当たったときの倍率。
    /// 攻撃側の特性（きもったま・しんがん・いろめがね・かたやぶり系）と防御側の特性（あついしぼう・ふゆう・ちょすい・フィルターなど）を反映する。
    /// </summary>
    public static Result Against(TypeChart chart, string attackType, IReadOnlyList<string> defenderTypes,
        Ability? defenderAbility = null, Ability? attackerAbility = null)
    {
        var notes = new List<string>();
        var atk = attackerAbility?.Identifier ?? "";
        var typeOnly = 1.0;
        var multiplier = 1.0;
        foreach (var t in defenderTypes)
        {
            var m = chart.Against(attackType, t);
            typeOnly *= m;
            // きもったま・しんがん: ノーマル・かくとう技がゴーストに当たる
            if (m == 0 && t == "Ghost" && attackType is "Normal" or "Fighting" && atk is "scrappy" or "minds-eye")
            {
                m = 1;
                notes.Add($"{attackerAbility!.Name}(ゴーストに当たる)");
            }
            multiplier *= m;
        }

        // かたやぶり系は防御側の特性を無視する
        var def = AbilityEffects.IgnoresTargetAbility(atk) ? "" : defenderAbility?.Identifier ?? "";
        var defName = defenderAbility?.Name ?? "";
        if (multiplier > 0 && def.Length > 0)
        {
            var after = DefensiveAbility(def, attackType, multiplier, out var what);
            if (after != multiplier)
            {
                multiplier = after;
                notes.Add($"{defName}({what})");
            }
        }

        if (atk == "tinted-lens" && multiplier > 0 && multiplier < 1)
        {
            multiplier *= 2;
            notes.Add($"{attackerAbility!.Name}(いまひとつが2倍)");
        }
        return new Result(typeOnly, multiplier, notes);
    }

    /// <summary>防御側の特性で変わった後の倍率。<paramref name="what"/> に変わり方（「無効」「半減」など）を入れる。</summary>
    private static double DefensiveAbility(string ability, string attackType, double multiplier, out string what)
    {
        what = "";
        switch (ability)
        {
            case "levitate" or "earth-eater" when attackType == "Ground":
            case "water-absorb" or "storm-drain" or "dry-skin" when attackType == "Water":
            case "flash-fire" or "well-baked-body" when attackType == "Fire":
            case "volt-absorb" or "lightning-rod" or "motor-drive" when attackType == "Electric":
            case "sap-sipper" when attackType == "Grass":
                what = "無効";
                return 0;
            case "wonder-guard" when multiplier <= 1:
                what = "抜群以外は無効";
                return 0;
            case "thick-fat" when attackType is "Fire" or "Ice":
            case "heatproof" or "water-bubble" when attackType == "Fire":
            case "purifying-salt" when attackType == "Ghost":
                what = "半減";
                return multiplier * 0.5;
            case "dry-skin" when attackType == "Fire":
                what = "1.25倍";
                return multiplier * 1.25;
            case "fluffy" when attackType == "Fire":
                what = "2倍・接触技は半減";
                return multiplier * 2;
            case "filter" or "solid-rock" or "prism-armor" when multiplier > 1:
                what = "抜群が0.75倍";
                return multiplier * 0.75;
            default:
                return multiplier;
        }
    }

    /// <summary>
    /// 攻撃側のタイプ一致技の候補タイプ。ノーマルタイプのポケモンがスキン系特性（フェアリースキン等）を持つときは、
    /// ノーマル技がそのタイプになるので置き換える。
    /// </summary>
    public static IReadOnlyList<string> StabTypes(Pokemon attacker, Ability? attackerAbility)
    {
        var types = attacker.Types.ToList();
        if (AbilityEffects.SkinType(attackerAbility?.Identifier ?? "") is { } skin && types.Contains("Normal"))
        {
            types.Remove("Normal");
            if (!types.Contains(skin)) types.Add(skin);
        }
        return types;
    }

    /// <summary>攻撃側のタイプ一致技で、防御側に与えられる最大倍率（両方の特性を含める）。一番よいタイプの結果を返す。</summary>
    public static Result BestStab(TypeChart chart, Pokemon attacker, Ability? attackerAbility, Pokemon defender, Ability? defenderAbility)
    {
        Result? best = null;
        foreach (var t in StabTypes(attacker, attackerAbility))
        {
            var r = Against(chart, t, defender.Types.ToList(), defenderAbility, attackerAbility);
            if (best is null || r.Multiplier > best.Multiplier) best = r;
        }
        return best ?? new Result(1, 1, Array.Empty<string>());
    }

    /// <summary>
    /// 防御側の特性で倍率が変わる攻撃タイプの一覧（タイプ相性表のパネル用）。
    /// 例: あついしぼう → ほのお・こおり がタイプだけの倍率から半分になる。
    /// </summary>
    public static IReadOnlyList<(string AttackType, Result Result)> ChangedByDefensiveAbility(TypeChart chart, IReadOnlyList<string> defenderTypes, Ability? ability)
    {
        if (ability is null) return Array.Empty<(string, Result)>();
        return TypeNames.All
            .Select(t => (AttackType: t, Result: Against(chart, t, defenderTypes, ability)))
            .Where(x => x.Result.ChangedByAbility)
            .ToList();
    }
}
