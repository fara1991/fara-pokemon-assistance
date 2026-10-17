using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>チャンピオンズの覚える技が、Showdown の champions データ（SV で補ったものではなく）に合っていること。</summary>
public class ChampionsLearnsetTests
{
    private const int MysticalFire = 595;   // マジカルフレイム
    private const int MagicalLeaf = 345;    // マジカルリーフ

    [Theory]
    [InlineData(876)]     // イエッサン(♂)
    [InlineData(10186)]   // イエッサン(♀)
    public async Task Indeedee_learns_mystical_fire_but_not_magical_leaf(int pokemonId)
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        Assert.Contains(data.LearnableMoves(pokemonId), m => m.Id == MysticalFire);
        Assert.DoesNotContain(data.LearnableMoves(pokemonId), m => m.Id == MagicalLeaf);
        Assert.True(data.CanLearn(pokemonId, MysticalFire));
        Assert.False(data.CanLearn(pokemonId, MagicalLeaf));
    }
}
