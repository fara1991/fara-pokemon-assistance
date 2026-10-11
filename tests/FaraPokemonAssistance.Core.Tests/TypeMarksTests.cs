using FaraPokemonAssistance.Core.Battle;
using Xunit;

namespace FaraPokemonAssistance.Core.Tests;

public class TypeMarksTests
{
    [Theory]
    [InlineData(0, "×")]
    [InlineData(1, "")]
    [InlineData(2, "◎")]
    [InlineData(4, "◎◎")]
    [InlineData(0.5, "△")]
    [InlineData(0.25, "△△")]
    [InlineData(0.125, "△△△")]
    // 特性で 2 の累乗でない倍率になったときは、向きだけ分かる記号 1 つ
    [InlineData(1.5, "◎")]
    [InlineData(0.75, "△")]
    [InlineData(1.25, "◎")]
    public void Symbol_stacks_marks_for_each_doubling(double multiplier, string expected)
        => Assert.Equal(expected, TypeMarks.Symbol(multiplier));
}
