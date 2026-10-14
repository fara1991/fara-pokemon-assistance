using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Models;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>技の火力順: 同じダメージなら、遅れて当たる技（みらいよち）は後ろに並べる。</summary>
public class DelayedMoveOrderTests
{
    [Fact]
    public async Task Future_sight_comes_after_an_equal_damage_move()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var attacker = new PokemonBuild(data.FindPokemon(876)!, data.Info.EvSystem) { Nature = data.Natures.First() };   // イエッサン(♂)
        attacker.EVs = new StatSet { SpAttack = 32 };
        attacker.Ability = data.AbilitiesOf(attacker.Pokemon).First(a => a.Identifier == "psychic-surge");
        var calc = new DamageCalculator(data.TypeChart, data.Info.Generation);
        var request = new DamageRequest { Attacker = attacker, Move = data.FindMove(248)!, Defender = DamageCalculator.NeutralDefender(data.Info.EvSystem), Terrain = Terrain.Psychic };
        var futureSight = data.FindMove(248)!;
        var expandingForce = data.FindMove(797)!;

        // シングルでは、サイコフィールドのワイドフォース（威力 1.5 倍）とみらいよちは同じダメージ
        Assert.Equal(calc.Calculate(request.With(move: expandingForce)).MaxDamage, calc.Calculate(request.With(move: futureSight)).MaxDamage);

        var sorted = calc.SortByDamage(request, new[] { futureSight, expandingForce });
        Assert.Equal(new[] { expandingForce.Id, futureSight.Id }, sorted.Select(m => m.Id));
    }
}
