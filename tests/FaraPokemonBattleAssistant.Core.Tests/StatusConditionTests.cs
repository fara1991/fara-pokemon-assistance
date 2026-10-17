using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>状態異常（やけど・まひ・どく・もうどく・ねむり・こおり）で威力や倍率が変わる技・特性の計算。</summary>
public class StatusConditionTests
{
    private const int Garchomp = 445, Charizard = 6, Blissey = 242;
    private const int Facade = 263, SmellingSalts = 265, WakeUpSlap = 358, Venoshock = 474, Hex = 506, Flamethrower = 53, Earthquake = 89;

    private static Task<PokemonDataSet> Load(string key) => TestData.Catalog().GetDataSetAsync(key);

    private static PokemonBuild Build(PokemonDataSet data, int id, string? ability = null, StatusCondition status = StatusCondition.None)
    {
        var b = new PokemonBuild(data.FindPokemon(id)!, data.Info.EvSystem) { Level = 50, Status = status };
        if (ability is not null) b.Ability = data.Abilities.Single(a => a.Identifier == ability);
        return b;
    }

    private static DamageResult Calc(PokemonDataSet data, PokemonBuild attacker, PokemonBuild defender, int moveId)
    {
        var request = new DamageRequest { Attacker = attacker, Defender = defender, Move = data.FindMove(moveId)!, IsCritical = false };
        return new DamageCalculator(data.TypeChart, data.Info.Generation).Calculate(request);
    }

    private static int Max(DamageResult r) => r.Rolls.Max();

    private static void AssertDoubled(int normal, int boosted) =>
        Assert.InRange(boosted, normal * 2 - 3, normal * 2 + 3);

    [Fact]
    public void Sleep_and_freeze_have_names_and_can_be_parsed()
    {
        Assert.Equal("ねむり", StatusNames.Japanese(StatusCondition.Sleep));
        Assert.Equal("こおり", StatusNames.Japanese(StatusCondition.Freeze));
        Assert.Equal(StatusCondition.Sleep, StatusNames.Parse("ねむり"));
        Assert.Equal(StatusCondition.Freeze, StatusNames.Parse("こおり"));
        Assert.Equal(StatusCondition.None, StatusNames.Parse("なし") ?? StatusCondition.None);
    }

    [Theory]
    [InlineData("Gen4")]
    [InlineData("Gen5")]
    [InlineData("Gen7")]
    public async Task Smelling_salts_doubles_against_paralysis_and_wake_up_slap_against_sleep(string key)
    {
        var data = await Load(key);
        var attacker = Build(data, Charizard);
        Assert.True(data.FindMove(SmellingSalts) is not null && data.FindMove(WakeUpSlap) is not null);
        var none = Max(Calc(data, attacker, Build(data, Blissey), SmellingSalts));
        AssertDoubled(none, Max(Calc(data, attacker, Build(data, Blissey, status: StatusCondition.Paralysis), SmellingSalts)));
        Assert.Equal(none, Max(Calc(data, attacker, Build(data, Blissey, status: StatusCondition.Sleep), SmellingSalts)));

        var noneWake = Max(Calc(data, attacker, Build(data, Blissey), WakeUpSlap));
        AssertDoubled(noneWake, Max(Calc(data, attacker, Build(data, Blissey, status: StatusCondition.Sleep), WakeUpSlap)));
        Assert.Equal(noneWake, Max(Calc(data, attacker, Build(data, Blissey, status: StatusCondition.Paralysis), WakeUpSlap)));
    }

    [Theory]
    [InlineData("Gen5")]
    [InlineData("Gen7")]
    [InlineData("Gen9")]
    public async Task Venoshock_doubles_against_poison_and_badly_poisoned_only(string key)
    {
        var data = await Load(key);
        var attacker = Build(data, Charizard);
        var none = Max(Calc(data, attacker, Build(data, Blissey), Venoshock));
        AssertDoubled(none, Max(Calc(data, attacker, Build(data, Blissey, status: StatusCondition.Poison), Venoshock)));
        AssertDoubled(none, Max(Calc(data, attacker, Build(data, Blissey, status: StatusCondition.BadlyPoisoned), Venoshock)));
        Assert.Equal(none, Max(Calc(data, attacker, Build(data, Blissey, status: StatusCondition.Sleep), Venoshock)));
        Assert.Equal(none, Max(Calc(data, attacker, Build(data, Blissey, status: StatusCondition.Burn), Venoshock)));
    }

    [Theory]
    [InlineData("Gen5")]
    [InlineData("Gen9")]
    public async Task Hex_doubles_against_every_status_including_sleep_and_freeze(string key)
    {
        var data = await Load(key);
        var attacker = Build(data, Charizard);
        var none = Max(Calc(data, attacker, Build(data, Blissey), Hex));
        foreach (var status in new[] { StatusCondition.Burn, StatusCondition.Poison, StatusCondition.BadlyPoisoned, StatusCondition.Paralysis, StatusCondition.Sleep, StatusCondition.Freeze })
            AssertDoubled(none, Max(Calc(data, attacker, Build(data, Blissey, status: status), Hex)));
    }

    [Fact]
    public async Task Facade_doubles_with_burn_poison_paralysis_but_not_sleep_and_burn_halving_is_gen_dependent()
    {
        // 第 6 世代以降: やけどでも半減しない。第 5 世代まで: からげんき 2 倍とやけど半減が両方掛かる
        var gen9 = await Load("Gen9");
        var normal9 = Max(Calc(gen9, Build(gen9, Garchomp), Build(gen9, Blissey), Facade));
        foreach (var status in new[] { StatusCondition.Burn, StatusCondition.Poison, StatusCondition.BadlyPoisoned, StatusCondition.Paralysis })
            AssertDoubled(normal9, Max(Calc(gen9, Build(gen9, Garchomp, status: status), Build(gen9, Blissey), Facade)));
        Assert.Equal(normal9, Max(Calc(gen9, Build(gen9, Garchomp, status: StatusCondition.Sleep), Build(gen9, Blissey), Facade)));

        var gen5 = await Load("Gen5");
        var normal5 = Max(Calc(gen5, Build(gen5, Garchomp), Build(gen5, Blissey), Facade));
        var burned5 = Max(Calc(gen5, Build(gen5, Garchomp, status: StatusCondition.Burn), Build(gen5, Blissey), Facade));
        Assert.InRange(burned5, normal5 - 2, normal5 + 2);
        AssertDoubled(normal5, Max(Calc(gen5, Build(gen5, Garchomp, status: StatusCondition.Poison), Build(gen5, Blissey), Facade)));
    }

    [Fact]
    public async Task Guts_toxic_boost_and_flare_boost_apply_with_the_right_status()
    {
        var data = await Load("Gen7");
        var defender = Build(data, Blissey);
        // こんじょう: どの状態異常でも物理 1.5 倍
        var plain = Max(Calc(data, Build(data, Garchomp, "rough-skin"), defender, Earthquake));
        foreach (var status in new[] { StatusCondition.Poison, StatusCondition.Sleep, StatusCondition.Freeze })
        {
            var guts = Max(Calc(data, Build(data, Garchomp, "guts", status), defender, Earthquake));
            Assert.InRange(guts, plain * 3 / 2 - 2, plain * 3 / 2 + 2);
        }
        // どくぼうそう: どく・もうどくで物理 1.5 倍（やけどでは変わらない）
        foreach (var status in new[] { StatusCondition.Poison, StatusCondition.BadlyPoisoned })
        {
            var toxic = Max(Calc(data, Build(data, Garchomp, "toxic-boost", status), defender, Earthquake));
            Assert.InRange(toxic, plain * 3 / 2 - 2, plain * 3 / 2 + 2);
        }
        var burnedToxic = Max(Calc(data, Build(data, Garchomp, "toxic-boost", StatusCondition.Burn), defender, Earthquake));
        Assert.True(burnedToxic < plain);
        // ねつぼうそう: やけどで特殊 1.5 倍
        var plainSp = Max(Calc(data, Build(data, Charizard, "blaze"), defender, Flamethrower));
        var flare = Max(Calc(data, Build(data, Charizard, "flare-boost", StatusCondition.Burn), defender, Flamethrower));
        Assert.InRange(flare, plainSp * 3 / 2 - 2, plainSp * 3 / 2 + 2);
        var flarePoison = Max(Calc(data, Build(data, Charizard, "flare-boost", StatusCondition.Poison), defender, Flamethrower));
        Assert.Equal(plainSp, flarePoison);
    }

    [Fact]
    public async Task Poison_heal_and_magic_guard_remove_the_poison_residual_damage_from_the_knock_out()
    {
        var data = await Load("Gen9");
        var attacker = Build(data, Garchomp);
        var rolls = new[] { 40 };
        int Hits(string? ability, StatusCondition status)
        {
            var defender = Build(data, Blissey, ability, status);
            var request = new DamageRequest { Attacker = attacker, Defender = defender, Move = data.FindMove(Earthquake)! };
            var modifiers = new List<string>();
            var options = KnockOutOptions.For(request, defender, 100, true, rolls, modifiers);
            return KnockOutCalculator.Calculate(rolls, 100, options).Hits;
        }
        Assert.Equal(2, Hits(null, StatusCondition.Poison));            // 40 → 毒 12 → 40 で倒れる
        Assert.Equal(3, Hits("magic-guard", StatusCondition.Poison));   // 定数ダメージなし: 40 × 3 = 120
        Assert.True(Hits("poison-heal", StatusCondition.Poison) > 3);   // 毎ターン 1/8 回復するので、通常より倒れにくい
        Assert.Equal(3, Hits(null, StatusCondition.None));
    }
}
