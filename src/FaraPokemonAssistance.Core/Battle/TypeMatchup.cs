using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>タイプ一致技だけを見た、ポケモン同士の有利・不利。特性・持ち物・テラスタルは考えない。</summary>
public static class TypeMatchup
{
    /// <param name="Opponent">比べる相手。</param>
    /// <param name="Offense">選んだポケモンのタイプ一致技が、相手に与える最大倍率。</param>
    /// <param name="Defense">相手のタイプ一致技を、選んだポケモンが受ける最大倍率。</param>
    public sealed record Entry(Pokemon Opponent, double Offense, double Defense);

    /// <summary>攻撃側のタイプ一致技で、防御側に与えられる最大倍率。</summary>
    public static double BestStab(TypeChart chart, Pokemon attacker, Pokemon defender) =>
        attacker.Types.Select(t => chart.Against(t, defender)).DefaultIfEmpty(1.0).Max();

    /// <summary>
    /// 選んだポケモンが有利な相手（抜群を取れて、抜群を取られない）と、
    /// 不利な相手（抜群を取られて、抜群を取れない）。候補の並び順を保ったまま、倍率の大きい順に並べる。
    /// </summary>
    public static (IReadOnlyList<Entry> Advantage, IReadOnlyList<Entry> Disadvantage) For(
        TypeChart chart, Pokemon pokemon, IEnumerable<Pokemon> opponents)
    {
        var advantage = new List<Entry>();
        var disadvantage = new List<Entry>();
        foreach (var o in opponents)
        {
            if (o.Id == pokemon.Id) continue;
            var entry = new Entry(o, BestStab(chart, pokemon, o), BestStab(chart, o, pokemon));
            if (entry.Offense > 1.0 && entry.Defense <= 1.0) advantage.Add(entry);
            else if (entry.Defense > 1.0 && entry.Offense <= 1.0) disadvantage.Add(entry);
        }
        // OrderBy は安定ソートなので、同じ倍率なら候補の並び順（使用率順など）が残る
        return (
            advantage.OrderByDescending(e => e.Offense).ThenBy(e => e.Defense).ToList(),
            disadvantage.OrderByDescending(e => e.Defense).ThenBy(e => e.Offense).ToList());
    }

    /// <param name="Pokemon">有利な相手。</param>
    /// <param name="Offense">相手のタイプ一致技が、選んだポケモンに与える最大倍率（特性込み）。</param>
    /// <param name="Defense">選んだポケモンのタイプ一致技を、相手が受ける最大倍率（特性込み）。</param>
    /// <param name="Notes">特性で倍率が変わったときの説明（例: <c>きもったま(ゴーストに当たる)</c>）。</param>
    public sealed record Counter(Pokemon Pokemon, double Offense, double Defense, IReadOnlyList<string> Notes)
    {
        public Counter(Pokemon pokemon, double offense, double defense) : this(pokemon, offense, defense, Array.Empty<string>()) { }
    }

    /// <summary>
    /// 選んだポケモンに対してタイプ相性で有利なポケモン（最大 <paramref name="count"/> 匹）。
    /// まず「タイプ一致技で抜群を取れて、選んだポケモンのタイプ一致技で抜群を取られない」相手を、
    /// 足りなければ「抜群は取れないが、選んだポケモンのタイプ一致技を半減以下で受けられる」相手を足す。
    /// それぞれの中では候補の並び順（使用率順など）を保つので、よく使われるポケモンが先に出る。
    /// 特性（<paramref name="ability"/> と <paramref name="abilityOf"/>）を渡すと、きもったま・ふゆう・あついしぼうなども反映する。
    /// </summary>
    public static IReadOnlyList<Counter> Counters(TypeChart chart, Pokemon pokemon, IEnumerable<Pokemon> candidates, int count = 10,
        Ability? ability = null, Func<Pokemon, Ability?>? abilityOf = null)
    {
        var strong = new List<Counter>();
        var resist = new List<Counter>();
        var seen = new HashSet<(int, string, string)>(); // 同じ種族・同じタイプ（メガシンカ前後など）は 1 匹だけ
        foreach (var o in candidates)
        {
            if (o.SpeciesId == pokemon.SpeciesId && o.SpeciesId != 0 || o.Id == pokemon.Id) continue;
            var oAbility = abilityOf?.Invoke(o);
            var offense = AbilityMatchup.BestStab(chart, o, oAbility, pokemon, ability);
            var defense = AbilityMatchup.BestStab(chart, pokemon, ability, o, oAbility);
            var notes = offense.Notes.Concat(defense.Notes).Distinct().ToList();
            var c = new Counter(o, offense.Multiplier, defense.Multiplier, notes);
            if (c.Offense > 1.0 && c.Defense <= 1.0) strong.Add(c);
            else if (c.Offense >= 1.0 && c.Defense < 1.0) resist.Add(c);
        }
        return strong.Concat(resist).Where(c => seen.Add((c.Pokemon.SpeciesId == 0 ? -c.Pokemon.Id : c.Pokemon.SpeciesId, c.Pokemon.Type1, c.Pokemon.Type2))).Take(count).ToList();
    }
}
