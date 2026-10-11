using FaraPokemonAssistance.Core.Battle;
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
using FaraPokemonAssistance.Core.Text;
using Xunit;

namespace FaraPokemonAssistance.Core.Tests;

/// <summary>昔の世代のデータ（その世代の時点のタイプ・種族値・特性・技・相性）。</summary>
public class HistoricalDataTests
{
    private static Task<PokemonDataSet> Load(string key) => TestData.Catalog().GetDataSetAsync(key);

    [Fact]
    public async Task Game_list_starts_from_generation_1_and_old_games_are_reference_only()
    {
        var sets = await TestData.Catalog().GetDataSetsAsync();
        Assert.Equal("Gen1", sets[0].Key);
        Assert.Equal(Enumerable.Range(1, 9), sets.Where(s => s.Key.StartsWith("Gen")).Select(s => s.Generation));
        // 計算は今のゲームだけ（昔の世代は順に対応していく）
        Assert.False(sets.Single(s => s.Key == "Gen1").CalcSupported);
        Assert.False(sets.Single(s => s.Key == "Gen2").CalcSupported);
        Assert.True(sets.Single(s => s.Key == "Gen9").CalcSupported);
        Assert.True(sets.Single(s => s.Key == "Champions").CalcSupported);
    }

    [Fact]
    public async Task Generation_1_types_stats_and_chart()
    {
        var data = await Load("Gen1");
        Assert.Equal(151, data.Pokemon.Count);
        Assert.Empty(data.Abilities);
        // あく・はがね・フェアリーは無い
        Assert.Equal(15, data.TypeChart.Types.Count);
        Assert.DoesNotContain("Fairy", data.TypeChart.Types);
        Assert.DoesNotContain("Steel", data.TypeChart.Types);
        // 第 1 世代の相性: ゴースト→エスパー 0、むし⇔どく 2 倍、こおり→ほのお 等倍
        Assert.Equal(0.0, data.TypeChart.Against("Ghost", "Psychic"));
        Assert.Equal(2.0, data.TypeChart.Against("Bug", "Poison"));
        Assert.Equal(2.0, data.TypeChart.Against("Poison", "Bug"));
        Assert.Equal(1.0, data.TypeChart.Against("Ice", "Fire"));
        // ピッピはノーマル、コイルはでんき単タイプ、フーディンの「とくしゅ」135
        var clefairy = data.Pokemon.Single(p => p.Id == 35);
        Assert.Equal("Normal", clefairy.Type1);
        Assert.Equal("", data.Pokemon.Single(p => p.Id == 81).Type2);
        var alakazam = data.Pokemon.Single(p => p.Id == 65);
        Assert.Equal(135, alakazam.BaseStats.SpAttack);
        Assert.Equal(135, alakazam.BaseStats.SpDefense);
        // かみつくはノーマル技
        Assert.Equal("Normal", data.Moves.Single(m => m.Id == 44).Type);
    }

    [Fact]
    public async Task Generations_2_to_5_have_old_steel_and_no_fairy()
    {
        foreach (var key in new[] { "Gen2", "Gen3", "Gen4", "Gen5" })
        {
            var data = await Load(key);
            Assert.Equal(17, data.TypeChart.Types.Count);
            Assert.Equal(0.5, data.TypeChart.Against("Ghost", "Steel"));
            Assert.Equal(0.5, data.TypeChart.Against("Dark", "Steel"));
            Assert.Equal("Normal", data.Pokemon.Single(p => p.Id == 35).Type1); // ピッピ
        }
        var gen6 = await Load("Gen6");
        Assert.Equal(18, gen6.TypeChart.Types.Count);
        Assert.Equal(1.0, gen6.TypeChart.Against("Ghost", "Steel"));
        Assert.Equal("Fairy", gen6.Pokemon.Single(p => p.Id == 35).Type1);
    }

    [Fact]
    public async Task Physical_special_split_and_abilities_by_generation()
    {
        // 第 3 世代は技のタイプで物理・特殊が決まる（あく技は特殊）。第 4 世代から技ごと
        Assert.Equal(MoveCategory.Special, (await Load("Gen3")).Moves.Single(m => m.Id == 44).Category);
        Assert.Equal(MoveCategory.Physical, (await Load("Gen4")).Moves.Single(m => m.Id == 44).Category);
        // 隠れ特性は第 5 世代から（フシギダネのようりょくそ）
        Assert.DoesNotContain(34, (await Load("Gen4")).Pokemon.Single(p => p.Id == 1).AbilityIds);
        Assert.Contains(34, (await Load("Gen5")).Pokemon.Single(p => p.Id == 1).AbilityIds);
        // 第 6〜7 世代にはメガシンカ（メガストーン付き）
        var gen6 = await Load("Gen6");
        var megaCharizardX = gen6.Pokemon.Single(p => p.Id == 10034);
        Assert.True(megaCharizardX.IsMega);
        Assert.NotNull(gen6.FindItem(megaCharizardX.MegaStoneId!.Value));
        Assert.DoesNotContain((await Load("Gen5")).Pokemon, p => p.IsMega);
        // 第 7 世代にはアローラのすがた
        Assert.Contains((await Load("Gen7")).Pokemon, p => p.Id == 10091);
    }

    [Fact]
    public async Task Reference_only_games_refuse_calculation_commands()
    {
        var poke = new PokeCommand(TestData.Catalog(), new FaraPokemonAssistance.Core.Roster.RosterRepository(new FaraPokemonAssistance.Core.Roster.MemoryRosterStore()));
        var result = await poke.ExecuteAsync("!pokerg dmg ケンタロス のしかかり スターミー");
        Assert.False(result.Success);
        Assert.Contains("対応していません", result.Message);
        var dmg = await new DamageCommand(TestData.Catalog()).ExecuteAsync("ケンタロス のしかかり スターミー", new DamageCommandOptions { DataSetKey = "Gen1" });
        Assert.False(dmg.Success);
        Assert.Contains("対応していません", dmg.Message);
    }

    [Fact]
    public async Task Champions_mega_lucario_z_has_an_ability()
    {
        var data = await Load("Champions");
        var megaLucarioZ = data.Pokemon.Single(p => p.Name == "メガルカリオZ");
        Assert.NotEmpty(data.AbilitiesOf(megaLucarioZ));
    }
}

/// <summary>昔の世代の計算で、ダメージ計算ツールとの比較以外に確かめること（素早さ・確定数・Z 技・チャット）。</summary>
public class LegacyMechanicsTests
{
    private static Task<PokemonDataSet> Load(string key) => TestData.Catalog().GetDataSetAsync(key);

    [Fact]
    public void Paralysis_quarters_speed_until_generation_6()
    {
        var build = new PokemonBuild(new Pokemon { Id = 1, Name = "テスト", Type1 = "Normal", BaseStats = new StatSet(100, 100, 100, 100, 100, 100), SpeciesId = 1 })
        {
            Status = StatusCondition.Paralysis,
        };
        var plain = StatCalculator.Calculate(build, Stat.Speed);
        Assert.Equal(plain / 4, FaraPokemonAssistance.Core.Battle.SpeedCalculator.Effective(build, generation: 6).Speed);
        Assert.Equal(plain / 2, FaraPokemonAssistance.Core.Battle.SpeedCalculator.Effective(build, generation: 7).Speed);
        Assert.Equal(plain / 2, FaraPokemonAssistance.Core.Battle.SpeedCalculator.Effective(build).Speed);
    }

    [Fact]
    public async Task Generation_5_has_no_terrain_and_double_critical_hits()
    {
        var command = new DamageCommand(TestData.Catalog());
        var normal = await command.ExecuteAsync("ガブリアス げきりん ボーマンダ", new DamageCommandOptions { DataSetKey = "Gen5" });
        var crit = await command.ExecuteAsync("ガブリアス げきりん ボーマンダ 急所", new DamageCommandOptions { DataSetKey = "Gen5" });
        Assert.True(normal.Success, normal.Message);
        Assert.True(crit.Success, crit.Message);
        // 第 5 世代の急所は 2 倍
        Assert.InRange(crit.Damage!.MaxDamage / (double)normal.Damage!.MaxDamage, 1.95, 2.05);
    }

    [Fact]
    public async Task Generations_3_and_4_calculate_with_their_own_rules()
    {
        var command = new DamageCommand(TestData.Catalog());
        // 第 3 世代: あく技は特殊（かみくだくはヘラクロスの低い特攻で計算）
        var gen3 = await command.ExecuteAsync("バンギラス かみくだく フーディン", new DamageCommandOptions { DataSetKey = "Gen3" });
        var gen4 = await command.ExecuteAsync("バンギラス かみくだく フーディン", new DamageCommandOptions { DataSetKey = "Gen4" });
        Assert.True(gen3.Success, gen3.Message);
        Assert.True(gen4.Success, gen4.Message);
        Assert.Equal(MoveCategory.Special, gen3.Request!.Move.Category);
        Assert.Equal(MoveCategory.Physical, gen4.Request!.Move.Category);
        // 第 3 世代のオボンのみは 30 回復
        var sitrus = await command.ExecuteAsync("ガルーラ のしかかり カビゴン 防:オボンのみ", new DamageCommandOptions { DataSetKey = "Gen3" });
        Assert.True(sitrus.Success, sitrus.Message);
        Assert.Contains(sitrus.Damage!.Modifiers, m => m.Contains("30回復"));
    }

    [Fact]
    public void Burn_damage_and_pinch_berries_follow_the_generation()
    {
        Assert.Equal(20, StatusNames.ResidualDamage(StatusCondition.Burn, 160, 1, generation: 6));
        Assert.Equal(10, StatusNames.ResidualDamage(StatusCondition.Burn, 160, 1, generation: 7));
        Assert.Equal(10, StatusNames.ResidualDamage(StatusCondition.Burn, 160, 1));
    }

    [Fact]
    public void Z_move_power_table_and_exceptions()
    {
        Assert.Equal(175, FaraPokemonAssistance.Core.Battle.ZMove.Power(new Move { Id = 57, Power = 90 }));   // なみのり
        Assert.Equal(200, FaraPokemonAssistance.Core.Battle.ZMove.Power(new Move { Id = 63, Power = 150 }));  // はかいこうせん
        Assert.Equal(100, FaraPokemonAssistance.Core.Battle.ZMove.Power(new Move { Id = 33, Power = 40 }));   // たいあたり
        Assert.Equal(170, FaraPokemonAssistance.Core.Battle.ZMove.Power(new Move { Id = 560, Power = 100 })); // フライングプレス
        Assert.Equal(220, FaraPokemonAssistance.Core.Battle.ZMove.Power(new Move { Id = 557, Power = 180 })); // Vジェネレート
    }

    [Fact]
    public async Task Generation_6_and_7_are_calculated_and_chat_supports_z_moves()
    {
        var sets = await TestData.Catalog().GetDataSetsAsync();
        Assert.True(sets.Single(s => s.Key == "Gen6").CalcSupported);
        Assert.True(sets.Single(s => s.Key == "Gen5").CalcSupported);
        Assert.True(sets.Single(s => s.Key == "Gen3").CalcSupported);
        Assert.True(sets.Single(s => s.Key == "Gen4").CalcSupported);
        Assert.False(sets.Single(s => s.Key == "Gen5").HasTerrain);
        Assert.True(sets.Single(s => s.Key == "Gen7").HasZMove);
        var command = new DamageCommand(TestData.Catalog());
        var normal = await command.ExecuteAsync("ガブリアス じしん ギルガルド", new DamageCommandOptions { DataSetKey = "Gen7" });
        var z = await command.ExecuteAsync("ガブリアス じしん ギルガルド Z", new DamageCommandOptions { DataSetKey = "Gen7" });
        Assert.True(normal.Success, normal.Message);
        Assert.True(z.Success, z.Message);
        Assert.Contains("Z技", z.Message);
        Assert.True(z.Damage!.MaxDamage > normal.Damage!.MaxDamage);
        // 第 6 世代はあられ（こおりタイプの防御は上がらない）
        var gen6 = await command.ExecuteAsync("ガブリアス じしん マニューラ あられ", new DamageCommandOptions { DataSetKey = "Gen6" });
        Assert.True(gen6.Success, gen6.Message);
        Assert.Contains("あられ", gen6.Message);
        // Z 技は第 7 世代だけ
        var gen6z = await command.ExecuteAsync("ガブリアス じしん マニューラ Z", new DamageCommandOptions { DataSetKey = "Gen6" });
        Assert.Contains("Z技は第7世代だけ", gen6z.Message);
    }
}

/// <summary>第 2〜4 世代の のろい（タイプ「？？？」）。</summary>
public class CurseTypelessTests
{
    [Theory]
    [InlineData("Gen2")]
    [InlineData("Gen3")]
    [InlineData("Gen4")]
    public async Task Curse_is_a_typeless_status_move_outside_the_type_chart(string key)
    {
        var data = await TestData.Catalog().GetDataSetAsync(key);
        var curse = data.Moves.Single(m => m.Id == 174);
        Assert.Equal(TypeNames.Unknown, curse.Type);
        Assert.Equal(MoveCategory.Status, curse.Category);
        Assert.False(curse.IsDamaging);
        Assert.Equal("？？？", TypeNames.ToJapanese(curse.Type));
        // タイプ相性表・タイプの一覧には入らない
        Assert.DoesNotContain(TypeNames.Unknown, data.TypeChart.Types);
        Assert.DoesNotContain(TypeNames.Unknown, TypeNames.All);
        // カビゴンは覚える
        var snorlax = data.Pokemon.First(p => p.SpeciesId == 143);
        Assert.Contains(data.LearnableMoves(snorlax.Id), m => m.Id == 174);
    }

    [Fact]
    public async Task Curse_deals_no_damage_and_is_ghost_from_gen5()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen3");
        var calc = new FaraPokemonAssistance.Core.Battle.DamageCalculator(data.TypeChart, 3);
        var snorlax = new PokemonBuild(data.Pokemon.First(p => p.SpeciesId == 143));
        var r = calc.Calculate(new DamageRequest { Attacker = snorlax, Move = data.Moves.Single(m => m.Id == 174), Defender = snorlax });
        Assert.Equal(0, r.MaxDamage);
        var gen5 = await TestData.Catalog().GetDataSetAsync("Gen5");
        Assert.Equal("Ghost", gen5.Moves.Single(m => m.Id == 174).Type);
    }
}
