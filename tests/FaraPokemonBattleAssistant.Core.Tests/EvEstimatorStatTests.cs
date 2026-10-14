using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Models;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>相手のポイントの推定: 画面に出す、性格補正ごとの防御実数値の範囲。</summary>
public class EvEstimatorStatTests
{
    [Fact]
    public async Task Bands_carry_the_defense_stat_at_their_ends()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var attacker = new PokemonBuild(data.FindPokemon(445)!, data.Info.EvSystem) { Nature = data.Natures.First() };
        var defender = new PokemonBuild(data.FindPokemon(6)!, data.Info.EvSystem) { Nature = data.Natures.First() };
        var request = new DamageRequest { Attacker = attacker, Move = data.FindMove(444)!, Defender = defender };
        var damage = new DamageCalculator(data.TypeChart, data.Info.Generation).Calculate(request).MinDamage;

        Assert.True(damage > 0);

        var estimate = new EvEstimator(data.TypeChart, data.Info.Generation).Run(request, damage, asPercent: false);

        Assert.True(estimate.HasResult);
        foreach (var band in estimate.Bands)
        {
            Assert.InRange(band.MinStat, 1, band.MaxStat);   // 振るほど、実数値は下がらない
            Assert.InRange(band.MinStat, estimate.MinDefenseStat, estimate.MaxDefenseStat);
            Assert.InRange(band.MaxStat, estimate.MinDefenseStat, estimate.MaxDefenseStat);
        }
    }
}

