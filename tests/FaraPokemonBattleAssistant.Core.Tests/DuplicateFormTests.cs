using FaraPokemonBattleAssistant.Core.Data;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>見た目だけが違うフォルム（メガニャオニクスの ♂・♀）は 1 体にまとめ、まとめた方の Id でも探せること。</summary>
public class DuplicateFormTests
{
    [Fact]
    public async Task MegaMeowsticGenderFormsAreMergedIntoOne()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var megas = data.Pokemon.Where(p => p.Name == "メガニャオニクス").ToList();
        Assert.Single(megas);
        // ♀ の方（10326）で探しても、残した ♂ の方（10314）が返る
        Assert.Equal(10314, data.FindPokemon(10326)?.Id);
        Assert.Equal(10314, data.FindPokemon(10314)?.Id);
    }

    [Fact]
    public async Task NoBattleIdenticalDuplicatesRemainInAnyDataSet()
    {
        var catalog = TestData.Catalog();
        foreach (var ds in await catalog.GetDataSetsAsync())
        {
            var data = await catalog.GetDataSetAsync(ds.Key);
            var dup = data.Pokemon.GroupBy(p => (p.SpeciesId, p.Name)).Where(g => g.Count() > 1)
                .Where(g => g.Select(p => (p.Type1, p.Type2, p.BaseStats.HP, p.BaseStats.Attack, p.BaseStats.Defense, p.BaseStats.SpAttack, p.BaseStats.SpDefense, p.BaseStats.Speed, string.Join(";", p.AbilityIds), p.MegaStoneId)).Distinct().Count() == 1)
                .Select(g => g.Key.Name).ToList();
            Assert.True(dup.Count == 0, $"{ds.Key}: {string.Join("、", dup)}");
        }
    }
}
