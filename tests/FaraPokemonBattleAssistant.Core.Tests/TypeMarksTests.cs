using FaraPokemonBattleAssistant.Core.Battle;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

public class TypeMarksTests
{
    [Theory]
    [InlineData(0, "×")]
    [InlineData(1, "")]
    [InlineData(2, "○")]
    [InlineData(4, "◎")]
    [InlineData(0.5, "△")]
    [InlineData(0.25, "▲")]
    [InlineData(0.125, "▼")]
    // 特性で 2 の累乗でない倍率になったときは、向きだけ分かる記号 1 つ
    [InlineData(1.5, "○")]
    [InlineData(0.75, "△")]
    [InlineData(1.25, "○")]
    public void Symbol_uses_a_distinct_mark_for_each_step(double multiplier, string expected)
        => Assert.Equal(expected, TypeMarks.Symbol(multiplier));

    [Fact]
    public void Legend_lists_every_symbol_once()
    {
        Assert.Equal(new[] { "◎", "○", "△", "▲", "▼", "×" }, TypeMarks.Legend.Select(x => x.Symbol));
        foreach (var m in new[] { 4.0, 2, 0.5, 0.25, 0.125, 0 })
            Assert.Contains(TypeMarks.Legend, x => x.Symbol == TypeMarks.Symbol(m));
    }
}
