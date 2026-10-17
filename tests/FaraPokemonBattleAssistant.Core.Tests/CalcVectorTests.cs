using System.Text.Json;
using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>
/// 全世代と今のゲーム（剣盾・SV・チャンピオンズ）のダメージ計算を、公開されているダメージ計算ツール（@smogon/calc）の結果と比べる。
/// 条件と期待値は tools/calc_vectors（make_scenarios.py → expected.js）で作った Data/calc_vectors.json。
/// </summary>
public class CalcVectorTests
{
    private sealed record Side(int Id, int Level, int Nature, int[] Evs, int? Ability, string AbilitySmogon, int? Item, int[] Boosts, string Status, int? Hp);
    private sealed record MoveRef(int Id, string Smogon);
    private sealed record Vector(int Gen, string Key, Side Attacker, Side Defender, MoveRef Move, bool Crit, bool Z, string Weather,
        string Terrain, bool Reflect, bool LightScreen, bool Doubles, int[] Expected, int ExpectedMaxHp);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static IReadOnlyList<Vector> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FaraPokemonBattleAssistant.sln")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "tests", "FaraPokemonBattleAssistant.Core.Tests", "Data", "calc_vectors.json");
        return JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(path), Options)!;
    }

    private static PokemonBuild Build(PokemonDataSet data, Side s)
    {
        var b = new PokemonBuild(data.FindPokemon(s.Id)!, data.Info.EvSystem)
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
    [InlineData("Gen3")]
    [InlineData("Gen4")]
    [InlineData("Gen5")]
    [InlineData("Gen6")]
    [InlineData("Gen7")]
    [InlineData("Gen8")]
    [InlineData("Gen9")]
    [InlineData("Champions")]
    public async Task Matches_public_damage_calculator(string key)
    {
        var vectors = Load().Where(v => v.Key == key).ToList();
        Assert.True(vectors.Count >= 200, $"{key} の比較データが少なすぎます（{vectors.Count}）");
        var data = await TestData.Catalog().GetDataSetAsync(key);
        var calc = new DamageCalculator(data.TypeChart, data.Info.Generation);
        var failures = new List<string>();
        foreach (var v in vectors)
        {
            var attacker = Build(data, v.Attacker);
            var defender = Build(data, v.Defender);
            var move = data.FindMove(v.Move.Id)!;
            // 公開ツールは専用 Z 技（ひっさつのピカチュート等）も、技のタイプの Z 技として計算するので、比べない
            if (v.Z && ZMove.HasExclusive(move, attacker.Pokemon)) continue;
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
                    $"[{v.Weather},{v.Terrain},R{v.Reflect},L{v.LightScreen},D{v.Doubles}] hp {defender.MaxHp}/{v.ExpectedMaxHp}\n  expected {string.Join(",", v.Expected)}\n  actual   {string.Join(",", result.Rolls)}\n  A{result.AttackStat} D{result.DefenseStat} BP{result.BasePower}: {string.Join(" / ", result.Modifiers)}");
        }
        // 一致しない条件をすべてファイルに書き出す（CALC_VECTOR_REPORT にフォルダ名を入れたとき。原因調べ用）
        if (Environment.GetEnvironmentVariable("CALC_VECTOR_REPORT") is { Length: > 0 } reportDir)
            await File.WriteAllTextAsync(Path.Combine(reportDir, $"{key}.txt"), string.Join("\n", failures));
        Assert.True(failures.Count == 0, $"{key}: {failures.Count}/{vectors.Count} 件が一致しません\n" + string.Join("\n", failures.Take(12)));
    }
}
