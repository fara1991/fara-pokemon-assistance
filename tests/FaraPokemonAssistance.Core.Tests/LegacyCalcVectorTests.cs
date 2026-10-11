using System.Text.Json;
using FaraPokemonAssistance.Core.Battle;
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
using Xunit;

namespace FaraPokemonAssistance.Core.Tests;

/// <summary>
/// 昔の世代のダメージ計算を、公開されているダメージ計算ツール（@smogon/calc）の結果と比べる。
/// 条件と期待値は tools/calc_vectors（make_scenarios.py → expected.js）で作った Data/calc_vectors.json。
/// </summary>
public class LegacyCalcVectorTests
{
    private sealed record Side(int Id, int Level, int Nature, int[] Evs, int? Ability, string AbilitySmogon, int? Item, int[] Boosts, string Status, int? Hp);
    private sealed record MoveRef(int Id, string Smogon);
    private sealed record Vector(int Gen, string Key, Side Attacker, Side Defender, MoveRef Move, bool Crit, bool Z, string Weather,
        string Terrain, bool Reflect, bool LightScreen, bool Doubles, int[] Expected, int ExpectedMaxHp);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static IReadOnlyList<Vector> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FaraPokemonAssistance.sln")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "tests", "FaraPokemonAssistance.Core.Tests", "Data", "calc_vectors.json");
        return JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(path), Options)!;
    }

    private static PokemonBuild Build(PokemonDataSet data, Side s)
    {
        var b = new PokemonBuild(data.FindPokemon(s.Id)!, EvSystem.Classic)
        {
            Level = s.Level,
            Nature = data.FindNature(s.Nature),
            EVs = new StatSet(s.Evs[0], s.Evs[1], s.Evs[2], s.Evs[3], s.Evs[4], s.Evs[5]),
            Ability = s.Ability is { } a ? data.FindAbility(a) : null,
            Item = s.Item is { } i ? data.FindItem(i) : null,
            Status = s.Status switch { "brn" => StatusCondition.Burn, "par" => StatusCondition.Paralysis, "psn" => StatusCondition.Poison, _ => StatusCondition.None },
        };
        b.Boosts[Stat.Attack] = s.Boosts[0];
        b.Boosts[Stat.Defense] = s.Boosts[1];
        b.Boosts[Stat.SpAttack] = s.Boosts[2];
        b.Boosts[Stat.SpDefense] = s.Boosts[3];
        b.Boosts[Stat.Speed] = s.Boosts[4];
        if (s.Hp is { } hp) b.CurrentHp = hp;
        return b;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Matches_public_damage_calculator(int generation)
    {
        var vectors = Load().Where(v => v.Gen == generation).ToList();
        Assert.True(vectors.Count >= 200, $"第{generation}世代の比較データが少なすぎます（{vectors.Count}）");
        var data = await TestData.Catalog().GetDataSetAsync($"Gen{generation}");
        var calc = new DamageCalculator(data.TypeChart, generation);
        var failures = new List<string>();
        foreach (var v in vectors)
        {
            var attacker = Build(data, v.Attacker);
            var defender = Build(data, v.Defender);
            var move = data.FindMove(v.Move.Id)!;
            var request = new DamageRequest
            {
                Attacker = attacker,
                Defender = defender,
                Move = move,
                IsCritical = v.Crit,
                IsZMove = v.Z,
                Format = v.Doubles ? BattleFormat.Doubles : BattleFormat.Singles,
                Weather = v.Weather switch { "Sun" => Weather.Sun, "Rain" => Weather.Rain, "Sand" => Weather.Sand, "Hail" => Weather.Snow, _ => Weather.None },
                Terrain = v.Terrain switch { "Electric" => Terrain.Electric, "Grassy" => Terrain.Grassy, "Psychic" => Terrain.Psychic, "Misty" => Terrain.Misty, _ => Terrain.None },
                Reflect = v.Reflect,
                LightScreen = v.LightScreen,
            };
            var result = calc.Calculate(request);
            if (defender.MaxHp != v.ExpectedMaxHp || !result.Rolls.SequenceEqual(v.Expected))
                failures.Add($"{attacker.Pokemon.Name}({v.Attacker.AbilitySmogon},{attacker.Item?.Name}) {move.Name}({v.Move.Smogon}{(v.Z ? ",Z" : "")}{(v.Crit ? ",crit" : "")}) → {defender.Pokemon.Name}({v.Defender.AbilitySmogon},{defender.Item?.Name}) " +
                    $"[{v.Weather},{v.Terrain},R{v.Reflect},L{v.LightScreen},D{v.Doubles}] hp {defender.MaxHp}/{v.ExpectedMaxHp}\n  expected {string.Join(",", v.Expected)}\n  actual   {string.Join(",", result.Rolls)}\n  {string.Join(" / ", result.Modifiers)}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count}/{vectors.Count} 件が一致しません\n" + string.Join("\n", failures.Take(12)));
    }
}
