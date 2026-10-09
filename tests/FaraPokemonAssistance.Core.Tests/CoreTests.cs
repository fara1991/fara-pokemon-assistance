using FaraPokemonAssistance.Core.Battle;
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
using FaraPokemonAssistance.Core.Roster;
using FaraPokemonAssistance.Core.Text;
using Xunit;

namespace FaraPokemonAssistance.Core.Tests;

public static class TestData
{
    public static string DataDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "FaraPokemonAssistance.Web", "wwwroot", "data");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("wwwroot/data not found");
        }
    }

    public static DataCatalog Catalog() => new(new FileDataSource(DataDirectory));
}

public class StatCalculatorTests
{
    [Fact]
    public void Garchomp_Lv50_Adamant_252Atk()
    {
        // ガブリアス 種族値 A130、いじっぱり A252 → 実数値 200
        Assert.Equal(200, StatCalculator.Other(130, 31, 252, 50, 1.1));
        // HP 種族値 108、H252 → 215
        Assert.Equal(215, StatCalculator.Hp(108, 31, 252, 50));
        // 下降補正は 0.9 倍（÷1.1 ではない）: 種族値 80、無振り、下降 → floor(100 * 0.9) = 90
        Assert.Equal(90, StatCalculator.Other(80, 31, 0, 50, 0.9));
    }

    [Theory]
    [InlineData(100, 1, 150)]
    [InlineData(100, 2, 200)]
    [InlineData(100, -1, 66)]
    [InlineData(100, -2, 50)]
    public void Boosts(int stat, int stage, int expected) =>
        Assert.Equal(expected, StatCalculator.ApplyBoost(stat, stage));
}

public class DamageCalculatorTests
{
    private static Pokemon MakePokemon(int id, string name, string t1, string t2, int hp, int a, int b, int c, int d, int s) =>
        new() { Id = id, Name = name, Type1 = t1, Type2 = t2, BaseStats = new StatSet(hp, a, b, c, d, s), SpeciesId = id };

    [Fact]
    public void Rolls_are_monotonic_and_percent_matches_hp()
    {
        var garchomp = MakePokemon(445, "ガブリアス", "Dragon", "Ground", 108, 130, 95, 80, 85, 102);
        var heatran = MakePokemon(485, "ヒードラン", "Fire", "Steel", 91, 90, 106, 130, 106, 77);
        var earthquake = new Move { Id = 89, Name = "じしん", Type = "Ground", Power = 100, Category = MoveCategory.Physical, Target = MoveTarget.AllOthers };
        var chart = TypeChart.Parse("AttackType,DefenseType,Multiplier\nGround,Fire,2\nGround,Steel,2\n");

        var attacker = new PokemonBuild(garchomp) { EVs = new StatSet(0, 252, 0, 0, 0, 0), Nature = new Nature { Name = "いじっぱり", IncreasedStat = Stat.Attack, DecreasedStat = Stat.SpAttack } };
        var defender = new PokemonBuild(heatran) { EVs = new StatSet(252, 0, 0, 0, 0, 0) };

        var result = new DamageCalculator(chart).Calculate(new DamageRequest { Attacker = attacker, Move = earthquake, Defender = defender });

        Assert.Equal(16, result.Rolls.Length);
        Assert.True(result.Rolls.SequenceEqual(result.Rolls.OrderBy(x => x)));
        Assert.Equal(4.0, result.TypeEffectiveness);
        Assert.True(result.IsStab);
        Assert.Equal(198, result.DefenderHP);
        Assert.True(result.MinDamage >= result.DefenderHP, $"4x STAB earthquake should OHKO: {result.RangeText}");
        Assert.Equal(1, result.KnockOut.Hits);
        Assert.True(result.KnockOut.IsGuaranteed);
    }

    [Fact]
    public void Doubles_spread_move_is_weaker()
    {
        var a = MakePokemon(1, "A", "Psychic", "", 100, 100, 100, 100, 100, 100);
        var d = MakePokemon(2, "D", "Normal", "", 100, 100, 100, 100, 100, 100);
        var move = new Move { Id = 1, Name = "M", Type = "Psychic", Power = 80, Category = MoveCategory.Special, Target = MoveTarget.AllFoes };
        var calc = new DamageCalculator(TypeChart.Empty);
        var singles = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = move, Defender = new PokemonBuild(d) });
        var doubles = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = move, Defender = new PokemonBuild(d), Format = BattleFormat.Doubles });
        Assert.True(doubles.MaxDamage < singles.MaxDamage);
    }

    [Fact]
    public void Immune_type_deals_zero()
    {
        var a = MakePokemon(1, "A", "Normal", "", 100, 100, 100, 100, 100, 100);
        var d = MakePokemon(2, "D", "Ghost", "", 100, 100, 100, 100, 100, 100);
        var move = new Move { Id = 1, Name = "M", Type = "Normal", Power = 80, Category = MoveCategory.Physical };
        var chart = TypeChart.Parse("AttackType,DefenseType,Multiplier\nNormal,Ghost,0\n");
        var result = new DamageCalculator(chart).Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = move, Defender = new PokemonBuild(d) });
        Assert.Equal(0, result.MaxDamage);
        Assert.Equal(0, result.KnockOut.Hits);
    }

    private static Ability Ab(int id, string identifier, string name) => new() { Id = id, Identifier = identifier, Name = name };

    [Fact]
    public void Huge_power_doubles_attack()
    {
        var a = MakePokemon(1, "A", "Water", "", 100, 50, 100, 100, 100, 100);
        var d = MakePokemon(2, "D", "Normal", "", 100, 100, 100, 100, 100, 100);
        var move = new Move { Id = 1, Name = "M", Type = "Normal", Power = 80, Category = MoveCategory.Physical };
        var calc = new DamageCalculator(TypeChart.Empty);
        var plain = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = move, Defender = new PokemonBuild(d) });
        var huge = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a) { Ability = Ab(37, "huge-power", "ちからもち") }, Move = move, Defender = new PokemonBuild(d) });
        Assert.Equal(plain.AttackStat * 2, huge.AttackStat);
        Assert.True(huge.MaxDamage > plain.MaxDamage * 1.8);
    }

    [Fact]
    public void Pixilate_converts_normal_move_to_fairy()
    {
        var a = MakePokemon(1, "A", "Fairy", "", 100, 100, 100, 100, 100, 100);
        var d = MakePokemon(2, "D", "Dragon", "", 100, 100, 100, 100, 100, 100);
        var move = new Move { Id = 1, Name = "ハイパーボイス", Type = "Normal", Power = 90, Category = MoveCategory.Special, Flags = new HashSet<string> { "Sound" } };
        var chart = TypeChart.Parse("AttackType,DefenseType,Multiplier\nFairy,Dragon,2\n");
        var result = new DamageCalculator(chart).Calculate(new DamageRequest
        {
            Attacker = new PokemonBuild(a) { Ability = Ab(182, "pixilate", "フェアリースキン") },
            Move = move,
            Defender = new PokemonBuild(d),
        });
        Assert.Equal("Fairy", result.MoveType);
        Assert.Equal(2.0, result.TypeEffectiveness);
        Assert.True(result.IsStab);
    }

    [Fact]
    public void Tera_changes_defensive_type_and_adds_stab()
    {
        var a = MakePokemon(1, "A", "Normal", "", 100, 100, 100, 100, 100, 100);
        var d = MakePokemon(2, "D", "Fire", "", 100, 100, 100, 100, 100, 100);
        var move = new Move { Id = 1, Name = "M", Type = "Water", Power = 80, Category = MoveCategory.Special };
        var chart = TypeChart.Parse("AttackType,DefenseType,Multiplier\nWater,Fire,2\nWater,Water,0.5\n");
        var calc = new DamageCalculator(chart);
        var plain = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = move, Defender = new PokemonBuild(d) });
        Assert.Equal(2.0, plain.TypeEffectiveness);
        Assert.False(plain.IsStab);

        var tera = calc.Calculate(new DamageRequest
        {
            Attacker = new PokemonBuild(a) { TeraType = "Water" },
            Move = move,
            Defender = new PokemonBuild(d) { TeraType = "Water" },
        });
        Assert.Equal(0.5, tera.TypeEffectiveness);
        Assert.True(tera.IsStab);
    }

    [Fact]
    public void Sun_boosts_fire_and_levitate_blocks_ground()
    {
        var a = MakePokemon(1, "A", "Fire", "", 100, 100, 100, 100, 100, 100);
        var d = MakePokemon(2, "D", "Normal", "", 100, 100, 100, 100, 100, 100);
        var fire = new Move { Id = 1, Name = "F", Type = "Fire", Power = 80, Category = MoveCategory.Special };
        var ground = new Move { Id = 2, Name = "G", Type = "Ground", Power = 80, Category = MoveCategory.Physical };
        var calc = new DamageCalculator(TypeChart.Empty);
        var normal = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = fire, Defender = new PokemonBuild(d) });
        var sun = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = fire, Defender = new PokemonBuild(d), Weather = Weather.Sun });
        Assert.True(sun.MaxDamage > normal.MaxDamage);

        var blocked = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = ground, Defender = new PokemonBuild(d) { Ability = Ab(26, "levitate", "ふゆう") } });
        Assert.Equal(0, blocked.MaxDamage);
        Assert.Contains(blocked.Modifiers, m => m.Contains("ふゆう"));
    }

    [Fact]
    public void Expanding_force_on_psychic_terrain_is_boosted_and_spreads()
    {
        var a = MakePokemon(1, "A", "Psychic", "", 100, 100, 100, 100, 100, 100);
        var d = MakePokemon(2, "D", "Normal", "", 100, 100, 100, 100, 100, 100);
        var move = new Move { Id = 797, Name = "ワイドフォース", Type = "Psychic", Power = 80, Category = MoveCategory.Special };
        var calc = new DamageCalculator(TypeChart.Empty);
        var plain = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = move, Defender = new PokemonBuild(d) });
        var terrain = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = move, Defender = new PokemonBuild(d), Terrain = Terrain.Psychic });
        Assert.True(terrain.MaxDamage > plain.MaxDamage * 1.5);
        var doubles = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(a), Move = move, Defender = new PokemonBuild(d), Terrain = Terrain.Psychic, Format = BattleFormat.Doubles });
        Assert.True(doubles.MaxDamage < terrain.MaxDamage);
        Assert.Contains(doubles.Modifiers, m => m.Contains("複数対象"));
    }

    [Fact]
    public void PokeRound_rounds_half_down()
    {
        Assert.Equal(2, DamageCalculator.PokeRound(2.5));
        Assert.Equal(3, DamageCalculator.PokeRound(2.51));
        Assert.Equal(2, DamageCalculator.PokeRound(2.49));
    }

    [Fact]
    public void KnockOut_probability_counts_rolls()
    {
        // 16 通りのうち 4 通りだけ HP 以上 → 25% で 1 発
        var rolls = Enumerable.Range(0, 16).Select(i => i < 12 ? 90 : 100).ToArray();
        var ko = KnockOutCalculator.Calculate(rolls, 100);
        Assert.Equal(1, ko.Hits);
        Assert.Equal(0.25, ko.Probability, 6);
    }
}

public class NameResolverTests
{
    [Theory]
    [InlineData("メガリザードンＸ", "めがりざーどんx")]
    [InlineData("イエッサン(♀)", "いえっさん♀")]
    [InlineData("ワイド フォース", "わいどふぉーす")]
    public void Normalize(string input, string expected) =>
        Assert.Equal(expected, NameNormalizer.Normalize(input));

    [Fact]
    public void Resolves_prefix_and_gender()
    {
        var names = new[] { "リザードン", "メガリザードンX", "メガリザードンY", "イエッサン(♂)", "イエッサン(♀)" };
        var resolver = new NameResolver<string>(names, s => s);
        Assert.Equal("リザードン", resolver.Resolve("リザードン").Value);
        Assert.Equal("メガリザードンX", resolver.Resolve("めがりざーどんｘ").Value);
        Assert.Equal("イエッサン(♂)", resolver.Resolve("イエッサン♂").Value);
        Assert.Equal("イエッサン(♂)", resolver.Resolve("イエッサン").Value);
        Assert.Equal("イエッサン(♀)", resolver.Resolve("イエッサン♀").Value);
        Assert.Equal("イエッサン(♀)", resolver.Resolve("イエッサンメス").Value);
        Assert.True(resolver.Resolve("メガリザードン").IsAmbiguous);
        Assert.False(resolver.Resolve("ピカチュウ").IsResolved);
    }
}

public class DataAndCommandTests
{
    [Fact]
    public async Task All_datasets_load()
    {
        var catalog = TestData.Catalog();
        var sets = await catalog.GetDataSetsAsync();
        Assert.Contains(sets, s => s.Key == "Gen9");
        Assert.Contains(sets, s => s.Key == "Champions");
        foreach (var info in sets)
        {
            var data = await catalog.GetDataSetAsync(info.Key);
            Assert.NotEmpty(data.Pokemon);
            Assert.NotEmpty(data.Moves);
            Assert.NotEmpty(data.Items);
            Assert.Equal(25, data.Natures.Count);
            Assert.Equal(2.0, data.TypeChart.Against("Water", "Fire"));
        }
    }

    [Fact]
    public async Task Champions_command_example()
    {
        var command = new DamageCommand(TestData.Catalog());
        // PokeAPI の champions データに収録済みのポケモンで確認する（収録は段階的に増える）
        var result = await command.ExecuteAsync("ガブ じしん メガリザX", new DamageCommandOptions { DataSetKey = "Champions" });
        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Damage);
        Assert.Equal("ガブリアス", result.Request!.Attacker.Pokemon.Name);
        Assert.Equal("メガリザードンX", result.Request.Defender.Pokemon.Name);
        Assert.True(result.Damage!.MinDamage > 0);
        Assert.True(result.Damage.MaxPercent > result.Damage.MinPercent);
        Assert.Contains("%", result.Message);
    }

    [Fact]
    public async Task Gen9_command_uses_usage_defaults_and_options()
    {
        var command = new DamageCommand(TestData.Catalog());
        var result = await command.ExecuteAsync("ガブリアス じしん ハバタクカミ 防:ずぶとい H252 B252 +2 急所 ダブル", new DamageCommandOptions { DataSetKey = "Gen9" });
        Assert.True(result.Success, result.Message);
        var req = result.Request!;
        Assert.Equal(252, req.Defender.EVs.HP);
        Assert.Equal(252, req.Defender.EVs.Defense);
        Assert.Equal("ずぶとい", req.Defender.Nature?.Name);
        Assert.Equal(2, req.Attacker.Boosts.Attack);
        Assert.True(req.IsCritical);
        Assert.Equal(BattleFormat.Doubles, req.Format);
        Assert.NotNull(req.Attacker.Nature);
    }

    [Fact]
    public async Task Champions_roster_comes_from_pokedex()
    {
        var command = new DamageCommand(TestData.Catalog());
        var result = await command.ExecuteAsync("イエッサン♂ ワイドフォース メガリザードンX サイコフィールド", new DamageCommandOptions { DataSetKey = "Champions" });
        Assert.True(result.Success, result.Message);
        Assert.Equal("イエッサン(♂)", result.Request!.Attacker.Pokemon.Name);
        Assert.Equal(Terrain.Psychic, result.Request.Terrain);
        Assert.DoesNotContain("未収録", result.Message);
        Assert.NotNull(result.Request.Attacker.Ability);

        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        Assert.DoesNotContain(data.Pokemon, p => p.IsProvisional);
        Assert.Contains(data.Pokemon, p => p.Name == "イエッサン(♀)");
        Assert.True(data.Pokemon.Count(p => p.Name.StartsWith("メガ")) >= 40);
    }

    [Fact]
    public async Task Field_tera_and_ability_tokens()
    {
        var command = new DamageCommand(TestData.Catalog());
        var gen9 = new DamageCommandOptions { DataSetKey = "Gen9" };
        var result = await command.ExecuteAsync("ガブリアス じしん ハバタクカミ 晴れ テラスじめん 防:こだいかっせい", gen9);
        Assert.True(result.Success, result.Message);
        var req = result.Request!;
        Assert.Equal(Weather.Sun, req.Weather);
        Assert.Equal("Ground", req.Attacker.TeraType);
        Assert.Equal("protosynthesis", req.Defender.Ability?.Identifier);
        Assert.True(result.Damage!.MaxDamage > 0);

        // ふゆう はどちらも持てないので「防御的な特性」として防御側に付き、じしんが無効になる
        var immune = await command.ExecuteAsync("ガブリアス じしん ハバタクカミ ふゆう", gen9);
        Assert.True(immune.Success, immune.Message);
        Assert.Equal("levitate", immune.Request!.Defender.Ability?.Identifier);
        Assert.Equal(0, immune.Damage!.MaxDamage);
    }

    [Fact]
    public async Task Dataset_word_switches_dataset()
    {
        var command = new DamageCommand(TestData.Catalog());
        var result = await command.ExecuteAsync("champions ガブリアス じしん メガリザードンX");
        Assert.True(result.Success, result.Message);
        Assert.Equal("メガリザードンX", result.Request!.Defender.Pokemon.Name);
    }

    [Fact]
    public async Task Missing_name_hints_other_dataset()
    {
        var command = new DamageCommand(TestData.Catalog());
        var result = await command.ExecuteAsync("ガブリアス じしん メガリザードンX", new DamageCommandOptions { DataSetKey = "Gen9" });
        Assert.False(result.Success);
        Assert.Contains("ポケモンチャンピオンズ", result.Message);
        Assert.Contains("champions", result.Message);
    }

    [Fact]
    public async Task Default_dataset_is_champions()
    {
        var command = new DamageCommand(TestData.Catalog());
        var result = await command.ExecuteAsync("ガブリアス じしん メガリザードンX");
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task Unknown_name_reports_error()
    {
        var command = new DamageCommand(TestData.Catalog());
        var result = await command.ExecuteAsync("ほげほげ ワイドフォース ガブリアス");
        Assert.False(result.Success);
        Assert.Contains("ほげほげ", result.Message);
    }
}


public class EvSystemTests
{
    [Fact]
    public void Points_add_one_per_point()
    {
        var garchomp = new Pokemon { Id = 445, Name = "ガブリアス", Type1 = "Dragon", Type2 = "Ground", BaseStats = new StatSet(108, 130, 95, 80, 85, 102), SpeciesId = 445 };
        var zero = new PokemonBuild(garchomp, EvSystem.Points);
        var full = new PokemonBuild(garchomp, EvSystem.Points) { EVs = new StatSet(32, 32, 0, 0, 0, 2) };
        Assert.Equal(StatCalculator.Calculate(zero, Stat.HP) + 32, StatCalculator.Calculate(full, Stat.HP));
        Assert.Equal(StatCalculator.Calculate(zero, Stat.Attack) + 32, StatCalculator.Calculate(full, Stat.Attack));
        Assert.Equal(StatCalculator.Calculate(zero, Stat.Speed) + 2, StatCalculator.Calculate(full, Stat.Speed));
        // 性格補正はポイントを足す前に掛かる
        var adamant = new PokemonBuild(garchomp, EvSystem.Points) { EVs = new StatSet(0, 32, 0, 0, 0, 0), Nature = new Nature { Name = "いじっぱり", IncreasedStat = Stat.Attack, DecreasedStat = Stat.SpAttack } };
        Assert.Equal((int)Math.Floor((2 * 130 + 31) * 50 / 100 * 1.0 + 5) , StatCalculator.Other(130, 31, 0, 50, 1.0));
        Assert.Equal((int)Math.Floor(StatCalculator.Other(130, 31, 0, 50, 1.0) * 1.1) + 32, StatCalculator.Calculate(adamant, Stat.Attack));
    }

    [Fact]
    public void Validation_limits_per_system()
    {
        Assert.Null(EvRules.Validate(EvSystem.Points, new StatSet(32, 32, 2, 0, 0, 0)));
        Assert.NotNull(EvRules.Validate(EvSystem.Points, new StatSet(33, 0, 0, 0, 0, 0)));
        Assert.NotNull(EvRules.Validate(EvSystem.Points, new StatSet(32, 32, 32, 0, 0, 0)));
        Assert.Null(EvRules.Validate(EvSystem.Classic, new StatSet(252, 252, 4, 0, 0, 0)));
        Assert.NotNull(EvRules.Validate(EvSystem.Classic, new StatSet(252, 252, 8, 0, 0, 0)));
    }

    [Fact]
    public async Task Champions_dataset_uses_points()
    {
        var sets = await TestData.Catalog().GetDataSetsAsync();
        Assert.Equal(EvSystem.Points, sets.Single(s => s.Key == "Champions").EvSystem);
        Assert.Equal("pokech", sets.Single(s => s.Key == "Champions").CommandPrefix);
        Assert.Equal("pokesv", sets.Single(s => s.Key == "Gen9").CommandPrefix);
        Assert.Equal("pokess", sets.Single(s => s.Key == "Gen8").CommandPrefix);
    }
}

public class PokeCommandTests
{
    private static PokeCommand NewCommand(out MemoryRosterStore store)
    {
        store = new MemoryRosterStore();
        return new PokeCommand(TestData.Catalog(), new RosterRepository(store));
    }

    [Fact]
    public async Task Register_list_more_remove()
    {
        var cmd = NewCommand(out var store);
        var add = await cmd.ExecuteAsync("!pokech add ガブリアス 4 32 0 0 0 30 ようき");
        Assert.True(add.Success, add.Message);
        Assert.Contains("#1", add.Message);
        Assert.NotNull(store.Json);

        var tooMany = await cmd.ExecuteAsync("!pokech add ガブリアス 32 32 32 0 0 0 ようき");
        Assert.False(tooMany.Success);

        var ls = await cmd.ExecuteAsync("!pokech ls");
        Assert.Contains("ガブリアス(ようき)", ls.Message);
        var lsFiltered = await cmd.ExecuteAsync("!pokech ls ピカチュウ");
        Assert.Contains("登録されたポケモンはありません", lsFiltered.Message);

        var more = await cmd.ExecuteAsync("!pokech more 1");
        Assert.True(more.Success, more.Message);
        Assert.Contains("実数値", more.Message);
        Assert.Contains("ようき", more.Message);

        var rm = await cmd.ExecuteAsync("!pokech rm 1");
        Assert.True(rm.Success, rm.Message);
        var lsAfter = await cmd.ExecuteAsync("!pokech ls");
        Assert.Contains("ありません", lsAfter.Message);
    }

    [Fact]
    public async Task TryExecute_returns_null_for_other_messages()
    {
        var cmd = NewCommand(out _);
        Assert.Null(await cmd.TryExecuteAsync("!bsr 1a2b"));
        Assert.Null(await cmd.TryExecuteAsync("こんにちは"));

        var help = await cmd.TryExecuteAsync("!poke cmd ls");
        Assert.NotNull(help);
        Assert.True(help.Success, help.Message);
    }

    [Fact]
    public async Task Sv_registration_uses_classic_limits()
    {
        var cmd = NewCommand(out _);
        var ok = await cmd.ExecuteAsync("!pokesv add ガブリアス 4 252 0 0 0 252 ようき");
        Assert.True(ok.Success, ok.Message);
        var bad = await cmd.ExecuteAsync("!pokesv add ガブリアス 4 253 0 0 0 252 ようき");
        Assert.False(bad.Success);
    }

    [Fact]
    public async Task Team_flow_and_team_aware_damage()
    {
        var cmd = NewCommand(out _);
        Assert.True((await cmd.ExecuteAsync("!pokech add ガブリアス 4 32 0 0 0 30 ようき")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech add リザードン 0 0 0 32 2 32 おくびょう")).Success);

        var addTeam = await cmd.ExecuteAsync("!pokech add team 1 1 こだわりスカーフ");
        Assert.True(addTeam.Success, addTeam.Message);
        Assert.Contains("こだわりスカーフ", addTeam.Message);
        Assert.True((await cmd.ExecuteAsync("!pokech add team 1 2 きあいのタスキ")).Success);

        var lsTeam = await cmd.ExecuteAsync("!pokech ls team");
        Assert.Contains("チーム1", lsTeam.Message);
        var use = await cmd.ExecuteAsync("!pokech use team 1");
        Assert.True(use.Success, use.Message);
        var moreTeam = await cmd.ExecuteAsync("!pokech more team 1");
        Assert.Contains("使用中", moreTeam.Message);
        Assert.Contains("@こだわりスカーフ", moreTeam.Message);

        var dmg = await cmd.ExecuteAsync("!pokech dmg ガブリアス ドラゴンクロー+1 リザードン");
        Assert.True(dmg.Success, dmg.Message);
        Assert.Contains("こだわりスカーフ", dmg.Message);   // 使用チームの持ち物が表示される
        Assert.Contains("急所", dmg.Message);
        Assert.Equal(1, dmg.Request!.Attacker.Boosts.Attack);
        Assert.Equal(32, dmg.Request.Attacker.EVs.Attack);  // 登録した振り
        Assert.Equal(EvSystem.Points, dmg.Request.Attacker.EvSystem);

        var rmMember = await cmd.ExecuteAsync("!pokech rm team 1 リザードン");
        Assert.True(rmMember.Success, rmMember.Message);
        var rmTeam = await cmd.ExecuteAsync("!pokech rm team 1");
        Assert.True(rmTeam.Success, rmTeam.Message);
        Assert.Contains("チームはありません", (await cmd.ExecuteAsync("!pokech ls team")).Message);
    }

    [Fact]
    public async Task Calc_estimates_defense_investment()
    {
        var cmd = NewCommand(out _);
        Assert.True((await cmd.ExecuteAsync("!pokesv add ガブリアス 4 252 0 0 0 252 ようき")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokesv add team 1 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokesv use team 1")).Success);

        // まず既知の構成でダメージを出し、その最大値を観測値として逆算すると元の振りが候補に含まれる
        var known = await cmd.ExecuteAsync("!pokesv dmg ガブリアス じしん ハバタクカミ 防:H252 防:B0 防:おくびょう 防:持ち物なし");
        Assert.True(known.Success, known.Message);
        var observed = known.Damage!.MaxDamage;

        var calc = await cmd.ExecuteAsync($"!pokesv calc ガブリアス じしん ハバタクカミ {observed}");
        Assert.True(calc.Success, calc.Message);
        Assert.Contains("防御実数値", calc.Message);
        Assert.Contains("補正なし:B0", calc.Message);
    }

    [Fact]
    public async Task Diff_compares_speed_with_reference_lines()
    {
        var cmd = NewCommand(out _);
        Assert.True((await cmd.ExecuteAsync("!pokesv add ガブリアス 4 252 0 0 0 252 ようき")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokesv add team 1 1 こだわりスカーフ")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokesv use team 1")).Success);

        var diff = await cmd.ExecuteAsync("!pokesv diff ガブリアス ハバタクカミ");
        Assert.True(diff.Success, diff.Message);
        Assert.Contains("こだわりスカーフ", diff.Message);
        Assert.Contains("最速", diff.Message);
        Assert.Contains("抜ける", diff.Message);
    }

    [Fact]
    public async Task Command_list_and_generic_prefix()
    {
        var cmd = NewCommand(out _);
        var list = await cmd.ExecuteAsync("!poke cmd ls");
        Assert.True(list.Success, list.Message);
        Assert.Contains("add team", list.Message);
        Assert.Contains("!pokech", list.Message);
        Assert.Contains("!pokesv", list.Message);
        Assert.Contains("!pokess", list.Message);
        Assert.True(list.Message.Length <= 480, list.Message.Length.ToString());

        // !poke は既定データセット（チャンピオンズ）
        var dmg = await cmd.ExecuteAsync("!poke dmg ガブリアス ドラゴンクロー リザードン");
        Assert.True(dmg.Success, dmg.Message);
        Assert.Equal(EvSystem.Points, dmg.Request!.Attacker.EvSystem);
    }

    [Fact]
    public async Task Damage_lists_ability_variants_when_they_differ()
    {
        var cmd = NewCommand(out _);
        // ドータクン: ふゆう / たいねつ / ヘヴィメタル → じしんは ふゆう で無効
        var dmg = await cmd.ExecuteAsync("!pokesv dmg ガブリアス じしん ドータクン");
        Assert.True(dmg.Success, dmg.Message);
        Assert.Contains("特性別", dmg.Message);
        Assert.Contains("ふゆう: 無効", dmg.Message);

        // 特性を指定したときは特性別を出さない
        var fixedAbility = await cmd.ExecuteAsync("!pokesv dmg ガブリアス じしん ドータクン 防:たいねつ");
        Assert.True(fixedAbility.Success, fixedAbility.Message);
        Assert.DoesNotContain("特性別", fixedAbility.Message);

        // 特性で差が無い相手には出さない（ハバタクカミは特性 1 つ）
        var single = await cmd.ExecuteAsync("!pokesv dmg ガブリアス じしん ハバタクカミ");
        Assert.DoesNotContain("特性別", single.Message);
    }

    [Fact]
    public async Task Calc_lists_ability_variants_when_they_differ()
    {
        var cmd = NewCommand(out _);
        Assert.True((await cmd.ExecuteAsync("!pokesv add ガブリアス 4 252 0 0 0 252 ようき")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokesv add team 1 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokesv use team 1")).Success);
        // ドータクンへのじしんは振り方によって概ね 114〜180。150 なら候補がある
        var calc = await cmd.ExecuteAsync("!pokesv calc ガブリアス じしん ドータクン 150");
        Assert.True(calc.Success, calc.Message);
        Assert.Contains("特性別", calc.Message);
        Assert.Contains("ふゆう: 該当なし", calc.Message);
        Assert.Contains("たいねつ:", calc.Message);
    }

    [Fact]
    public async Task Status_conditions_affect_damage_and_speed()
    {
        var cmd = NewCommand(out _);
        var plain = await cmd.ExecuteAsync("!pokesv dmg ガブリアス じしん ドータクン 防:たいねつ");
        var burned = await cmd.ExecuteAsync("!pokesv dmg ガブリアス じしん ドータクン 防:たいねつ やけど");
        Assert.True(burned.Success, burned.Message);
        Assert.True(burned.Damage!.MaxDamage < plain.Damage!.MaxDamage);
        Assert.Equal(StatusCondition.Burn, burned.Request!.Attacker.Status);
        Assert.Contains("やけど", burned.Message);

        // 防御側のどくは確定数を縮める方向（確率が下がらない）
        var poisoned = await cmd.ExecuteAsync("!pokesv dmg ガブリアス じしん ドータクン 防:たいねつ どく");
        Assert.Equal(StatusCondition.Poison, poisoned.Request!.Defender.Status);
        Assert.True(poisoned.Damage!.KnockOut.Hits <= plain.Damage.KnockOut.Hits);

        var diff = await cmd.ExecuteAsync("!pokesv diff ガブリアス ハバタクカミ まひ");
        Assert.True(diff.Success, diff.Message);
        Assert.Contains("(まひ)", diff.Message);
        var selfPara = await cmd.ExecuteAsync("!pokesv diff ガブリアス ハバタクカミ 自:まひ");
        Assert.Contains("まひ(0.5倍)", selfPara.Message);
    }

    [Fact]
    public void Residual_damage_shortens_knockout()
    {
        var rolls = Enumerable.Repeat(40, 16).ToArray(); // 100 HP: 素では確定3発
        Assert.Equal(3, KnockOutCalculator.Calculate(rolls, 100).Hits);
        // どく 1/8 = 12: 40 +12 +40 +12 = 104 → 2 ターン目の終了時に倒れる = 2 発
        Assert.Equal(2, KnockOutCalculator.Calculate(rolls, 100, StatusCondition.Poison).Hits);
        // もうどく: 40 +6 +40 +12 = 98 → 3 発目が必要
        Assert.Equal(3, KnockOutCalculator.Calculate(rolls, 100, StatusCondition.BadlyPoisoned).Hits);
        // やけど 1/16 = 6: 40 +6 +40 +6 = 92 → 3 発
        Assert.Equal(3, KnockOutCalculator.Calculate(rolls, 100, StatusCondition.Burn).Hits);
        var small = Enumerable.Repeat(30, 16).ToArray(); // 30+12+30+12+30 = 114 → どくで 3 発（素では 4 発）
        Assert.Equal(4, KnockOutCalculator.Calculate(small, 100).Hits);
        Assert.Equal(3, KnockOutCalculator.Calculate(small, 100, StatusCondition.Poison).Hits);
    }

    [Fact]
    public async Task Unknown_prefix_is_rejected()
    {
        var cmd = NewCommand(out _);
        var result = await cmd.ExecuteAsync("!dmg ガブリアス じしん ハバタクカミ");
        Assert.False(result.Success);
        Assert.Contains("!pokech", result.Message);
    }
}

public class DataSourceTests
{
    private sealed class ScriptedSource : IDataSource
    {
        public Func<string, CancellationToken, Task<string?>> Handler { get; set; } = (_, _) => Task.FromResult<string?>("ok");
        public int Calls { get; private set; }

        public Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Handler(relativePath, cancellationToken);
        }
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "fpa-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Caching_falls_back_to_stale_cache_on_timeout()
    {
        var dir = TempDir();
        var inner = new ScriptedSource();
        // 期限 0 なので毎回取り直す
        var source = new CachingDataSource(inner, dir, TimeSpan.Zero);

        inner.Handler = (_, _) => Task.FromResult<string?>("v1");
        Assert.Equal("v1", await source.ReadTextAsync("a/b.csv"));

        // HttpClient のタイムアウトは TaskCanceledException（呼び出し側の ct は未キャンセル）
        inner.Handler = (_, _) => throw new TaskCanceledException("timeout");
        Assert.Equal("v1", await source.ReadTextAsync("a/b.csv"));

        inner.Handler = (_, _) => throw new HttpRequestException("offline");
        Assert.Equal("v1", await source.ReadTextAsync("a/b.csv"));
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Caching_propagates_caller_cancellation()
    {
        var dir = TempDir();
        var inner = new ScriptedSource();
        var source = new CachingDataSource(inner, dir, TimeSpan.Zero);
        inner.Handler = (_, _) => Task.FromResult<string?>("v1");
        await source.ReadTextAsync("x.csv");

        using var cts = new CancellationTokenSource();
        inner.Handler = (_, ct) => { cts.Cancel(); throw new OperationCanceledException(ct); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadTextAsync("x.csv", cts.Token));
    }

    [Fact]
    public async Task Caching_without_cache_rethrows_timeout()
    {
        var inner = new ScriptedSource { Handler = (_, _) => throw new TaskCanceledException("timeout") };
        var source = new CachingDataSource(inner, TempDir(), TimeSpan.FromDays(1));
        await Assert.ThrowsAsync<TaskCanceledException>(() => source.ReadTextAsync("none.csv"));
    }

    [Fact]
    public async Task Catalog_retries_after_failed_load()
    {
        var inner = new ScriptedSource { Handler = (_, _) => throw new HttpRequestException("offline") };
        var catalog = new DataCatalog(inner);
        await Assert.ThrowsAsync<HttpRequestException>(() => catalog.GetDataSetsAsync());

        // 失敗した Task を保持し続けず、次の呼び出しで取り直す
        var real = new FileDataSource(TestData.DataDirectory);
        inner.Handler = (path, ct) => real.ReadTextAsync(path, ct);
        var sets = await catalog.GetDataSetsAsync();
        Assert.Contains(sets, s => s.Key == "Champions");
    }

    [Fact]
    public async Task Catalog_reloads_after_interval()
    {
        var real = new FileDataSource(TestData.DataDirectory);
        var inner = new ScriptedSource { Handler = (path, ct) => real.ReadTextAsync(path, ct) };
        var catalog = new DataCatalog(inner, TimeSpan.FromSeconds(2));
        await catalog.GetNaturesAsync();
        await catalog.GetNaturesAsync();
        Assert.Equal(1, inner.Calls);

        await Task.Delay(3000);
        await catalog.GetNaturesAsync();
        Assert.Equal(2, inner.Calls);
    }
}

public class GimmickAndFormTests
{
    [Theory]
    [InlineData(40, "Normal", 90)]
    [InlineData(80, "Fire", 130)]
    [InlineData(120, "Water", 140)]
    [InlineData(150, "Dragon", 150)]
    [InlineData(80, "Fighting", 90)]
    [InlineData(40, "Poison", 70)]
    public void Max_move_power_table(int power, string type, int expected)
    {
        var move = new Move { Id = 1, Name = "x", Type = type, Power = power, Category = MoveCategory.Physical };
        Assert.Equal(expected, FaraPokemonAssistance.Core.Battle.DamageCalculator.MaxMovePower(move));
    }

    [Fact]
    public async Task Dynamax_doubles_hp_and_uses_max_move_power()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen8");
        var garchomp = data.Pokemon.First(p => p.Name == "ガブリアス");
        var charizard = data.Pokemon.First(p => p.Name == "リザードン");
        var earthquake = data.Moves.First(m => m.Name == "ストーンエッジ"); // じしん はひこうに無効
        var attacker = new PokemonBuild(garchomp) { EVs = new StatSet { Attack = 252 } };
        var defender = new PokemonBuild(charizard) { EVs = new StatSet { HP = 252 }, IsDynamax = true };
        var calc = new FaraPokemonAssistance.Core.Battle.DamageCalculator(data.TypeChart);
        var plain = calc.Calculate(new DamageRequest { Attacker = attacker, Move = earthquake, Defender = new PokemonBuild(charizard) { EVs = new StatSet { HP = 252 } } });
        var dyna = calc.Calculate(new DamageRequest { Attacker = attacker, Move = earthquake, Defender = defender });
        Assert.Equal(plain.DefenderHP * 2, dyna.DefenderHP);

        attacker.IsDynamax = true;
        var maxMove = calc.Calculate(new DamageRequest { Attacker = attacker, Move = earthquake, Defender = defender });
        Assert.Equal(130, maxMove.BasePower);
        Assert.Contains(maxMove.Modifiers, m => m.Contains("ダイマックス技"));
    }

    [Fact]
    public async Task Dynamax_token_in_chat_command()
    {
        var command = new DamageCommand(TestData.Catalog());
        var result = await command.ExecuteAsync("ガブリアス じしん リザードン 防:ダイマ", new DamageCommandOptions { DataSetKey = "Gen8" });
        Assert.True(result.Success, result.Message);
        Assert.True(result.Request!.Defender.IsDynamax);
        Assert.False(result.Request.Attacker.IsDynamax);
        Assert.Contains("ダイマックス", result.Message);
    }

    [Fact]
    public async Task Psyshock_uses_physical_defense()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var psyshock = data.Moves.First(m => m.Name == "サイコショック");
        Assert.Equal(MoveCategory.Special, psyshock.Category);
        Assert.Equal(Stat.Defense, psyshock.DefenseStatUsed);
        Assert.Equal(Stat.SpAttack, psyshock.AttackStatUsed);

        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "イエッサン(♂)")) { EVs = new StatSet { SpAttack = 252 } };
        var defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "ハバタクカミ")) { EVs = new StatSet { HP = 252 } };
        var result = new FaraPokemonAssistance.Core.Battle.DamageCalculator(data.TypeChart).Calculate(new DamageRequest { Attacker = attacker, Move = psyshock, Defender = defender });
        Assert.Equal(StatCalculator.Calculate(defender, Stat.Defense), result.DefenseStat);
    }

    [Fact]
    public async Task Mega_forms_have_their_mega_stone()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var megaX = data.Pokemon.First(p => p.Name == "メガリザードンX");
        Assert.True(megaX.IsMega);
        var stone = data.FindItem(megaX.MegaStoneId!.Value);
        Assert.NotNull(stone);
        Assert.Equal("リザードナイトX", stone!.Name);
        Assert.True(stone.IsMegaStone);
        Assert.False(stone.IsOffensive);
        Assert.False(data.Pokemon.First(p => p.Name == "リザードン").IsMega);
        Assert.All(data.Pokemon.Where(p => p.Name.StartsWith("メガ") && p.Name != "メガニウム" && p.Name != "メガヤンマ"), p => Assert.True(p.IsMega, p.Name));
    }

    [Fact]
    public async Task Male_form_resolves_without_gender_mark()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var resolver = DamageCommand.CreatePokemonResolver(data);
        Assert.Equal("イエッサン(♂)", resolver.Resolve("イエッサン").Value?.Name);
        Assert.Equal("イエッサン(♂)", resolver.Resolve("イエッサン♂").Value?.Name);
        Assert.Equal("イエッサン(♀)", resolver.Resolve("イエッサン♀").Value?.Name);
    }

    [Fact]
    public async Task Item_sides_and_dataset_gimmicks()
    {
        var catalog = TestData.Catalog();
        var sets = await catalog.GetDataSetsAsync();
        Assert.True(sets.First(d => d.Key == "Gen8").HasDynamax);
        Assert.True(sets.First(d => d.Key == "Gen9").HasTerastal);
        Assert.Equal(BattleGimmick.None, sets.First(d => d.Key == "Champions").Gimmick);

        var data = await catalog.GetDataSetAsync("Gen9");
        Assert.True(data.Items.First(i => i.Name == "こだわりハチマキ").IsOffensive);
        Assert.True(data.Items.First(i => i.Name == "いのちのたま").IsOffensive);
        Assert.True(data.Items.First(i => i.Name == "とつげきチョッキ").IsDefensive);
        Assert.False(data.Items.First(i => i.Name == "とつげきチョッキ").IsOffensive);
    }
}

public class RecoveryAndFieldTests
{
    private static readonly int[] Thirty = Enumerable.Repeat(30, 16).ToArray();

    [Fact]
    public void Sitrus_berry_adds_a_hit()
    {
        Assert.Equal(4, KnockOutCalculator.Calculate(Thirty, 100).Hits);
        // 2 発目で 40 → 1/2 以下なので +25 = 65 → 35 → 5 → 5 発目で倒れる
        var sitrus = new KnockOutOptions { BerryThreshold = 50, BerryHeal = 25 };
        Assert.Equal(5, KnockOutCalculator.Calculate(Thirty, 100, sitrus).Hits);
    }

    [Fact]
    public void Leftovers_heal_every_turn()
    {
        Assert.Equal(6, KnockOutCalculator.Calculate(Thirty, 160).Hits);
        Assert.Equal(8, KnockOutCalculator.Calculate(Thirty, 160, new KnockOutOptions { EndOfTurnHeal = 10 }).Hits);
    }

    [Fact]
    public void First_hit_rolls_differ_from_later_hits()
    {
        var first = Enumerable.Repeat(15, 16).ToArray();
        Assert.Equal(3, KnockOutCalculator.Calculate(Thirty, 90).Hits);
        Assert.Equal(4, KnockOutCalculator.Calculate(first, 90, new KnockOutOptions { LaterRolls = Thirty }).Hits);
    }

    [Fact]
    public void Start_hp_lowers_hits()
    {
        Assert.Equal(2, KnockOutCalculator.Calculate(Thirty, 100, new KnockOutOptions { StartHp = 50 }).Hits);
    }

    private static async Task<(PokemonDataSet Data, FaraPokemonAssistance.Core.Battle.DamageCalculator Calc)> Gen9()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        return (data, new FaraPokemonAssistance.Core.Battle.DamageCalculator(data.TypeChart));
    }

    [Fact]
    public async Task Screen_halves_damage_and_crit_ignores_it()
    {
        var (data, calc) = await Gen9();
        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス")) { EVs = new StatSet { Attack = 252 } };
        var defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "ハバタクカミ")) { EVs = new StatSet { HP = 252 } };
        var move = data.Moves.First(m => m.Name == "ドラゴンクロー");
        var normal = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = defender });
        var walled = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = defender, Screen = true });
        Assert.Equal(0, normal.MaxDamage); // フェアリーに無効
        var target = new PokemonBuild(data.Pokemon.First(p => p.Name == "ドドゲザン")) { EVs = new StatSet { HP = 252 } };
        normal = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = target });
        walled = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = target, Screen = true });
        Assert.InRange(walled.MaxDamage, normal.MaxDamage / 2 - 2, normal.MaxDamage / 2 + 2);
        Assert.Contains(walled.Modifiers, m => m.Contains("リフレクター"));
        var crit = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = target, Screen = true, IsCritical = true });
        Assert.DoesNotContain(crit.Modifiers, m => m.Contains("リフレクター"));
    }

    [Fact]
    public async Task Blaze_boosts_fire_moves_at_low_hp()
    {
        var (data, calc) = await Gen9();
        var charizard = data.Pokemon.First(p => p.Name == "リザードン");
        var blaze = data.AbilitiesOf(charizard).First(a => a.Identifier == "blaze");
        var defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"));
        var move = data.Moves.First(m => m.Name == "かえんほうしゃ");
        var full = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(charizard) { Ability = blaze }, Move = move, Defender = defender });
        var pinch = calc.Calculate(new DamageRequest { Attacker = new PokemonBuild(charizard) { Ability = blaze, HpPercent = 30 }, Move = move, Defender = defender });
        Assert.True(pinch.AttackStat > full.AttackStat * 1.4, $"{pinch.AttackStat} vs {full.AttackStat}");
        Assert.Contains(pinch.Modifiers, m => m.Contains("HP1/3以下"));
    }

    [Fact]
    public async Task Resist_berry_halves_first_hit_only()
    {
        var (data, calc) = await Gen9();
        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス")) { EVs = new StatSet { Attack = 252 } };
        var move = data.Moves.First(m => m.Name == "ストーンエッジ");
        var plain = new PokemonBuild(data.Pokemon.First(p => p.Name == "リザードン")) { EVs = new StatSet { HP = 252 } };
        var berry = data.Items.First(i => i.Name == "ヨロギのみ");
        Assert.Equal(ItemCategory.ResistBerry, berry.Category);
        var withBerry = plain.Clone();
        withBerry.Item = berry;
        var a = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = plain });
        var b = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = withBerry });
        Assert.InRange(b.MaxDamage, a.MaxDamage / 2 - 2, a.MaxDamage / 2 + 2);
        Assert.True(b.KnockOut.Hits >= a.KnockOut.Hits);
    }

    [Fact]
    public async Task Sitrus_berry_noted_in_result()
    {
        var (data, calc) = await Gen9();
        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス"));
        var defender = new PokemonBuild(data.Pokemon.First(p => p.Name == "ドドゲザン")) { Item = data.Items.First(i => i.Name == "オボンのみ") };
        var result = calc.Calculate(new DamageRequest { Attacker = attacker, Move = data.Moves.First(m => m.Name == "ドラゴンクロー"), Defender = defender });
        Assert.Contains(result.Modifiers, m => m.Contains("オボンのみ"));
    }

    [Fact]
    public async Task Chat_tokens_for_screen_and_hp()
    {
        var command = new DamageCommand(TestData.Catalog());
        var result = await command.ExecuteAsync("ガブリアス じしん ドドゲザン 壁 HP50% 攻:HP30%", new DamageCommandOptions { DataSetKey = "Gen9" });
        Assert.True(result.Success, result.Message);
        Assert.True(result.Request!.Screen);
        Assert.Equal(50, result.Request.Defender.HpPercent);
        Assert.Equal(30, result.Request.Attacker.HpPercent);
        Assert.Contains("壁", result.Message);
    }
}

public class TeamCleanupTests
{
    [Fact]
    public async Task Removing_last_member_deletes_team_and_unuse_works()
    {
        var store = new MemoryRosterStore();
        var cmd = new PokeCommand(TestData.Catalog(), new RosterRepository(store));
        Assert.True((await cmd.ExecuteAsync("!pokech add ガブリアス 4 32 0 0 0 30 ようき")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech add team 1 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech use team 1")).Success);

        var unuse = await cmd.ExecuteAsync("!pokech unuse team");
        Assert.True(unuse.Success, unuse.Message);
        Assert.Contains("解除", unuse.Message);
        Assert.Contains("ありません", (await cmd.ExecuteAsync("!pokech unuse team")).Message);

        await cmd.ExecuteAsync("!pokech use team 1");
        var rm = await cmd.ExecuteAsync("!pokech rm 1");
        Assert.Contains("チーム1を削除", rm.Message);
        var teams = await cmd.ExecuteAsync("!pokech ls team");
        Assert.Contains("チームはありません", teams.Message);
    }

    [Fact]
    public async Task Removing_from_team_until_empty_deletes_team()
    {
        var store = new MemoryRosterStore();
        var cmd = new PokeCommand(TestData.Catalog(), new RosterRepository(store));
        await cmd.ExecuteAsync("!pokech add ガブリアス 4 32 0 0 0 30 ようき");
        await cmd.ExecuteAsync("!pokech add team 2 1");
        var rm = await cmd.ExecuteAsync("!pokech rm team 2 ガブリアス");
        Assert.Contains("チームを削除", rm.Message);
    }
}

public class TypeMatchupTests
{
    private static Pokemon Make(int id, string name, string t1, string t2 = "") =>
        new() { Id = id, Name = name, Type1 = t1, Type2 = t2, SpeciesId = id };

    private static readonly TypeChart Chart = TypeChart.Parse(
        "AttackType,DefenseType,Multiplier\n" +
        "Water,Fire,2\nFire,Water,0.5\nFire,Grass,2\nGrass,Fire,0.5\nWater,Grass,0.5\nGrass,Water,2\n" +
        "Ground,Fire,2\nGround,Steel,2\nFire,Steel,2\nWater,Ground,2\nGrass,Ground,2\nIce,Dragon,2\nIce,Ground,2\nIce,Fire,0.5\n");

    [Fact]
    public void Advantage_and_disadvantage_are_from_the_selected_pokemon()
    {
        var heatran = Make(1, "ヒードラン", "Fire", "Steel");
        var gastrodon = Make(2, "トリトドン", "Water", "Ground");
        var ferrothorn = Make(3, "ナットレイ", "Grass", "Steel");
        var glaceon = Make(4, "グレイシア", "Ice");

        var (advantage, disadvantage) = TypeMatchup.For(Chart, heatran, new[] { heatran, gastrodon, ferrothorn, glaceon });

        // ヒードランはナットレイにほのお技が 4 倍で刺さり、ナットレイからは抜群を取られない
        var ferro = Assert.Single(advantage);
        Assert.Equal("ナットレイ", ferro.Opponent.Name);
        Assert.Equal(4.0, ferro.Offense);
        Assert.Equal(1.0, ferro.Defense); // くさは半減、はがねは等倍
        // トリトドンにはじめん技を 4 倍で受け、こちらは抜群を取れない。グレイシアはどちらも抜群を取れない
        var gastro = Assert.Single(disadvantage);
        Assert.Equal("トリトドン", gastro.Opponent.Name);
        Assert.Equal(4.0, gastro.Defense);
        Assert.Equal(1.0, gastro.Offense);
    }

    [Fact]
    public void Equal_multipliers_keep_candidate_order()
    {
        var heatran = Make(10, "ヒードラン", "Fire");
        var a = Make(11, "A", "Grass");
        var b = Make(12, "B", "Grass");

        var (advantage, _) = TypeMatchup.For(Chart, heatran, new[] { b, a });

        Assert.Equal(new[] { "B", "A" }, advantage.Select(e => e.Opponent.Name));
    }
}

public class UsageFallbackTests
{
    [Fact]
    public async Task Champions_uses_gen9_usage_for_ordering()
    {
        var catalog = TestData.Catalog();
        var champions = await catalog.GetDataSetAsync("Champions");
        var usage = await champions.GetUsageAsync(BattleFormat.Doubles);

        Assert.Equal("Gen9", champions.Info.UsageFallback);
        Assert.False(usage.IsEmpty);
        Assert.Equal("Gen9", usage.SourceKey);
        Assert.NotEmpty(usage.MoveOrder(876)); // イエッサン(♂)
    }

    [Fact]
    public async Task Gen9_uses_its_own_usage()
    {
        var gen9 = await TestData.Catalog().GetDataSetAsync("Gen9");
        Assert.Equal("Gen9", (await gen9.GetUsageAsync(BattleFormat.Singles)).SourceKey);
    }
}

public class SortByDamageTests
{
    private static readonly TypeChart Chart = TypeChart.Parse(
        "AttackType,DefenseType,Multiplier\nNormal,Ghost,0\n");

    private static Move MakeMove(int id, string name, string type, int power, MoveCategory category) =>
        new() { Id = id, Name = name, Type = type, Power = power, Category = category, Target = MoveTarget.Single };

    [Fact]
    public void Orders_by_actual_damage_including_stats_and_terrain()
    {
        // イエッサン(♂) 相当: A65 / C105、エスパー・ノーマル
        var indeedee = new PokemonBuild(new Pokemon { Id = 876, Name = "イエッサン", Type1 = "Psychic", Type2 = "Normal", SpeciesId = 876,
            BaseStats = new StatSet(60, 65, 55, 105, 95, 95) }) { EVs = new StatSet(0, 0, 0, 252, 0, 0) };
        var lastResort = MakeMove(387, "とっておき", "Normal", 140, MoveCategory.Physical);
        var hyperVoice = MakeMove(304, "ハイパーボイス", "Normal", 90, MoveCategory.Special);
        var expandingForce = MakeMove(797, "ワイドフォース", "Psychic", 80, MoveCategory.Special);
        var calc = new DamageCalculator(Chart);
        var defender = DamageCalculator.NeutralDefender(EvSystem.Classic);
        var moves = new[] { lastResort, hyperVoice, expandingForce };

        // 威力はとっておきが最大だが、C に振っていればハイパーボイスの方がダメージが大きい
        var plain = calc.SortByDamage(new DamageRequest { Attacker = indeedee, Move = lastResort, Defender = defender }, moves);
        Assert.True(plain.ToList().IndexOf(hyperVoice) < plain.ToList().IndexOf(lastResort));

        // A に振ると逆転する（威力ではなく実際のダメージで並ぶ）
        var physical = new PokemonBuild(indeedee.Pokemon) { EVs = new StatSet(0, 252, 0, 0, 0, 0) };
        var plainPhysical = calc.SortByDamage(new DamageRequest { Attacker = physical, Move = lastResort, Defender = defender }, moves);
        Assert.Same(lastResort, plainPhysical[0]);

        // サイコフィールドではワイドフォースが最大
        var psychic = calc.SortByDamage(new DamageRequest { Attacker = indeedee, Move = lastResort, Defender = defender, Terrain = Terrain.Psychic }, moves);
        Assert.Same(expandingForce, psychic[0]);

        // ゴーストにはノーマル技が効かないので最後
        var ghost = new PokemonBuild(new Pokemon { Id = 2, Name = "ゴースト", Type1 = "Ghost", SpeciesId = 2, BaseStats = new StatSet(100, 100, 100, 100, 100, 100) });
        var vsGhost = calc.SortByDamage(new DamageRequest { Attacker = indeedee, Move = lastResort, Defender = ghost }, moves);
        Assert.Same(expandingForce, vsGhost[0]);
    }
}
