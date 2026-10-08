using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>タイプ一致技だけを見た、ポケモン同士の有利・不利。特性・持ち物・テラスタルは考えない。</summary>
public static class TypeMatchup
{
    /// <param name="Pokemon">比べる相手。</param>
    /// <param name="Offense">このポケモンのタイプ一致技が、対象に与える最大倍率。</param>
    /// <param name="Defense">対象のタイプ一致技を、このポケモンが受ける最大倍率。</param>
    public sealed record Entry(Pokemon Pokemon, double Offense, double Defense);

    /// <summary>攻撃側のタイプ一致技で、防御側に与えられる最大倍率。</summary>
    public static double BestStab(TypeChart chart, Pokemon attacker, Pokemon defender) =>
        attacker.Types.Select(t => chart.Against(t, defender)).DefaultIfEmpty(1.0).Max();

    /// <summary>
    /// 対象に強いポケモン（抜群を取れて、抜群を取られない）と、
    /// 対象に弱いポケモン（抜群を取られて、抜群を取れない）。候補の並び順を保ったまま、倍率の差が大きい順に並べる。
    /// </summary>
    public static (IReadOnlyList<Entry> Strong, IReadOnlyList<Entry> Weak) Against(
        TypeChart chart, Pokemon target, IEnumerable<Pokemon> candidates)
    {
        var strong = new List<Entry>();
        var weak = new List<Entry>();
        foreach (var p in candidates)
        {
            if (p.Id == target.Id) continue;
            var entry = new Entry(p, BestStab(chart, p, target), BestStab(chart, target, p));
            if (entry.Offense > 1.0 && entry.Defense <= 1.0) strong.Add(entry);
            else if (entry.Defense > 1.0 && entry.Offense <= 1.0) weak.Add(entry);
        }
        // OrderBy は安定ソートなので、同じ倍率なら候補の並び順（使用率順など）が残る
        return (
            strong.OrderByDescending(e => e.Offense).ThenBy(e => e.Defense).ToList(),
            weak.OrderByDescending(e => e.Defense).ThenBy(e => e.Offense).ToList());
    }
}
