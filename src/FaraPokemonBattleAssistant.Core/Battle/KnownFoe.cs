using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;

namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>
/// 相手のポケモン（<see cref="RivalPokemon"/>）の分かっている振り方から作る、ダメージ計算用の防御側の構成。
/// 範囲のときは、耐久が最も低い想定（<see cref="Low"/>）と最も高い想定（<see cref="High"/>）の 2 つで計算する。
/// 分かっていない部分は、ダメージ計算の既定（HP と受ける側の防御に全振り）にする。
/// </summary>
public sealed class KnownFoe
{
    public required Pokemon Pokemon { get; init; }
    public required Stat DefenseStat { get; init; }
    /// <summary>耐久が最も低い想定の構成（ダメージが最大になる側）。</summary>
    public required PokemonBuild Low { get; init; }
    /// <summary>耐久が最も高い想定の構成（ダメージが最小になる側）。</summary>
    public required PokemonBuild High { get; init; }

    /// <summary>振り方が 1 つに決まっていない（範囲）か。</summary>
    public bool IsRange => Low.EVs.ToShortString() != High.EVs.ToShortString() || Low.Nature?.Id != High.Nature?.Id;

    /// <summary>
    /// 相手の性格が、<paramref name="stat"/> に与える補正（上がる 1.1・下がる 0.9・補正なし 1.0）。分からなければ null。
    /// 性格が決まっていれば、その性格から。決まっていなくても、その能力が上がる・下がるとだけ分かっていれば、その補正になる。
    /// </summary>
    public static double? NatureModifier(RivalPokemon rival, IReadOnlyList<Nature> natures, Stat stat)
    {
        if (rival.Nature is { } name && natures.FirstOrDefault(n => n.Name == name) is { } nature) return nature.Modifier(stat);
        if (rival.NatureUp == stat) return 1.1;
        if (rival.NatureDown == stat) return 0.9;
        return null;
    }

    /// <summary>
    /// 相手のポケモンから、<paramref name="defense"/>（その技が使う防御。B か D）で計算するときの構成を作る。
    /// 防御・HP・性格・特性のどれも分かっていなければ null（既定の構成で計算する）。<paramref name="item"/> は、持ち物が分かっているとき（メガシンカ後のメガストーン）に渡す。
    /// </summary>
    public static KnownFoe? FromRival(RivalPokemon rival, Pokemon pokemon, EvSystem system, IReadOnlyList<Nature> natures, Stat defense, Ability? ability, Item? item = null)
    {
        var exactDefense = defense == Stat.Defense ? rival.Defense : rival.SpDefense;
        var scan = rival.Scans.FirstOrDefault(s => s.Defense == defense.ToString());
        var exactNature = rival.Nature is null ? null : natures.FirstOrDefault(n => n.Name == rival.Nature);
        // 性格が決まっていなくても、防御の上がる・下がる能力が分かっていれば、その補正は分かる
        var exactModifier = NatureModifier(rival, natures, defense);
        (int Min, int Max)? hp = rival.HP is { } exactHp ? (exactHp, exactHp) : ScanHp(rival);
        if (exactDefense is null && scan is null && hp is null && exactNature is null && exactModifier is null && ability is null) return null;

        var full = EvRules.Full(system);
        // 分かっている防御のポイントの帯（性格補正ごと）。分からなければ帯なし（既定の全振り）
        var bands = new List<(double Modifier, int MinEv, int MaxEv)>();
        if (exactDefense is { } ev) bands.Add((exactModifier ?? 1.0, ev, ev));
        else if (scan is not null)
        {
            var scanBands = scan.Bands.Select(b => (b.Modifier, b.MinEv, b.MaxEv)).ToList();
            // 性格が分かっているときは、その補正の帯だけ
            if (exactModifier is { } known && scanBands.Any(b => b.Modifier == known))
                scanBands = scanBands.Where(b => b.Modifier == known).ToList();
            bands.AddRange(scanBands);
        }

        Nature? NatureFor(double modifier) =>
            exactNature ?? (modifier > 1.0 ? NatureSelection.WithIncreased(natures, null, defense)
                : modifier < 1.0 ? NatureSelection.WithDecreased(natures, null, defense)
                : NatureSelection.Neutral(natures));

        PokemonBuild Make(double modifier, int defenseEv, int hpEv)
        {
            var build = new PokemonBuild(pokemon, system) { Nature = NatureFor(modifier), Ability = ability, Item = item };
            build.EVs[Stat.HP] = hpEv;
            build.EVs[defense] = defenseEv;
            return build;
        }

        if (bands.Count == 0)
        {
            if (exactNature is null && exactModifier is null && hp is null && ability is null) return null;
            return new KnownFoe { Pokemon = pokemon, DefenseStat = defense, Low = Make(exactModifier ?? 1.0, full, hp?.Min ?? full), High = Make(exactModifier ?? 1.0, full, hp?.Max ?? full) };
        }

        // 耐久が最も低い帯（最小のポイント）と、最も高い帯（最大のポイント）を、実数値で選ぶ
        int StatAt(double modifier, int ev) => StatCalculator.Calculate(Make(modifier, ev, 0), defense);
        var lowBand = bands.OrderBy(b => StatAt(b.Modifier, b.MinEv)).First();
        var highBand = bands.OrderByDescending(b => StatAt(b.Modifier, b.MaxEv)).First();
        return new KnownFoe
        {
            Pokemon = pokemon,
            DefenseStat = defense,
            Low = Make(lowBand.Modifier, lowBand.MinEv, hp?.Min ?? full),
            High = Make(highBand.Modifier, highBand.MaxEv, hp?.Max ?? full),
        };
    }

    /// <summary>推定（scan）で分かった HP のポイントの範囲（複数の推定は重なる部分）。無ければ null。</summary>
    private static (int Min, int Max)? ScanHp(RivalPokemon rival)
    {
        int? min = null, max = null;
        foreach (var s in rival.Scans)
        {
            if (s.HpMin is not { } lo || s.HpMax is not { } hi) continue;
            min = min is null ? lo : Math.Max(min.Value, lo);
            max = max is null ? hi : Math.Min(max.Value, hi);
        }
        return min is { } a && max is { } b && a <= b ? (a, b) : null;
    }
}

/// <summary>相手のポケモンの推定（scan）の結果を反映する処理。</summary>
public static class RivalScanning
{
    /// <summary>
    /// 推定の結果 <paramref name="estimate"/> を相手に反映する。同じ防御（B か D）の前の推定があれば、重なる範囲に狭める。
    /// 前の推定と重ならない（矛盾する）ときは反映せず、false と理由を返す。
    /// </summary>
    public static (bool Applied, string? Problem) Apply(RivalPokemon rival, EvEstimator.Estimate estimate, string note, DateTimeOffset now)
    {
        if (!estimate.HasResult) return (false, "そのダメージになる振り方が見つかりません");
        var defense = estimate.DefenseStat.ToString();
        var newBands = estimate.Bands.Select(b => new RivalBand { Modifier = b.Modifier, MinEv = b.MinEv, MaxEv = b.MaxEv }).ToList();
        int? hpMin = estimate.HpEvRange?.MinEv, hpMax = estimate.HpEvRange?.MaxEv;

        var scan = rival.Scans.FirstOrDefault(s => s.Defense == defense);
        if (scan is null)
        {
            rival.Scans.Add(new RivalScan { Defense = defense, Bands = newBands, HpMin = hpMin, HpMax = hpMax, Count = 1, Notes = { note } });
        }
        else
        {
            // 前の推定と重なる部分だけを残す（性格補正ごと）
            var narrowed = new List<RivalBand>();
            foreach (var band in newBands)
            {
                var old = scan.Bands.FirstOrDefault(o => o.Modifier == band.Modifier);
                if (old is null) continue;
                var lo = Math.Max(old.MinEv, band.MinEv);
                var hi = Math.Min(old.MaxEv, band.MaxEv);
                if (lo <= hi) narrowed.Add(new RivalBand { Modifier = band.Modifier, MinEv = lo, MaxEv = hi });
            }
            if (narrowed.Count == 0) return (false, "前の推定と矛盾します（重なる振り方がありません）。前の推定を残しました");
            if (hpMin is { } a && hpMax is { } b)
            {
                var lo = scan.HpMin is { } oldMin ? Math.Max(oldMin, a) : a;
                var hi = scan.HpMax is { } oldMax ? Math.Min(oldMax, b) : b;
                if (lo > hi) return (false, "前の推定と矛盾します（HP が重なりません）。前の推定を残しました");
                scan.HpMin = lo;
                scan.HpMax = hi;
            }
            scan.Bands = narrowed;
            scan.Count++;
            scan.Notes.Add(note);
        }
        rival.Updated = now.ToString("o");
        return (true, null);
    }
}
