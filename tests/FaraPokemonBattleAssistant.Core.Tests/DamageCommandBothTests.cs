using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>ダメージ計算機の「チャットでの表示」: 急所なし・ありを 1 行にまとめる。</summary>
public class DamageCommandBothTests
{
    [Fact]
    public async Task FormatBoth_puts_normal_and_critical_in_one_line_with_shared_tags()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var attacker = new PokemonBuild(data.FindPokemon(445)!, data.Info.EvSystem) { Nature = data.Natures.First() };
        var defender = new PokemonBuild(data.FindPokemon(6)!, data.Info.EvSystem) { Nature = data.Natures.First() };
        var move = data.FindMove(89)!;   // じしん
        var request = new DamageRequest { Attacker = attacker, Move = move, Defender = defender, Weather = Weather.Rain };
        var calc = new DamageCalculator(data.TypeChart, data.Info.Generation);
        var normal = calc.Calculate(request);
        var crit = calc.Calculate(request.With(isCritical: true));

        var text = DamageCommand.FormatBoth(request, normal, crit, data.Info);

        Assert.Single(text.Split('\n'));
        Assert.Contains($"{normal.RangeText} {normal.KnockOut} / 急所 {crit.RangeText} {crit.KnockOut}", text);
        Assert.Contains("雨", text);
        // 急所はタグではなく「/ 急所 …」で示す（タグに重ねて出さない）
        Assert.DoesNotContain("急所 雨", text);
        Assert.Equal(1, text.Split("急所").Length - 1);
        // 従来の 1 結果の表示は変わらない
        Assert.Contains("急所", DamageCommand.Format(request.With(isCritical: true), crit, data.Info));
        Assert.DoesNotContain("急所", DamageCommand.Format(request, normal, data.Info));
    }
}
