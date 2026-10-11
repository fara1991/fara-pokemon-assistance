using FaraPokemonAssistance.Core.Battle;
using Xunit;

namespace FaraPokemonAssistance.Core.Tests;

public class TypeMarksTests
{
    [Theory]
    [InlineData(0, "×")]
    [InlineData(1, "")]
    [InlineData(2, "○")]
    [InlineData(4, "◎")]
    [InlineData(0.5, "△")]
    [InlineData(0.25, "▲")]
    [InlineData(0.125, "▲")]
    // 特性で 2 の累乗でない倍率になったときは、向きだけ分かる記号 1 つ
    [InlineData(1.5, "○")]
    [InlineData(0.75, "△")]
    [InlineData(1.25, "○")]
    public void Symbol_uses_a_distinct_mark_for_each_step(double multiplier, string expected)
        => Assert.Equal(expected, TypeMarks.Symbol(multiplier));

    [Theory]
    [InlineData(0.125, true)]
    [InlineData(0.25, false)]
    [InlineData(0.5, false)]
    [InlineData(2, false)]
    public void IsEighth_only_for_one_eighth(double multiplier, bool expected)
        => Assert.Equal(expected, TypeMarks.IsEighth(multiplier));
}
