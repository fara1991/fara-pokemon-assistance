using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
using Xunit;

namespace FaraPokemonAssistance.Core.Tests;

public class PokedexTests
{
    private static async Task<(PokemonDataSet Data, Pokedex Dex)> LoadAsync(string key)
    {
        var data = await TestData.Catalog().GetDataSetAsync(key);
        return (data, new Pokedex(data));
    }

    [Fact]
    public async Task Default_order_is_national_dex_with_forms_after_the_base_species()
    {
        var (data, dex) = await LoadAsync("Champions");
        var all = dex.Query(new PokedexFilter());
        Assert.Equal(data.Pokemon.Count(p => !p.IsProvisional), all.Count);
        // 図鑑番号は小さい順
        Assert.True(all.Zip(all.Skip(1)).All(x => Pokedex.DexNumber(x.First) <= Pokedex.DexNumber(x.Second)));
        // メガリザードン X / Y はリザードンのすぐ後ろ
        var i = all.ToList().FindIndex(p => p.Name == "リザードン");
        Assert.True(i >= 0);
        Assert.StartsWith("メガリザードン", all[i + 1].Name);
        Assert.StartsWith("メガリザードン", all[i + 2].Name);
    }

    [Fact]
    public async Task Filters_combine_type_move_ability_and_name()
    {
        var (data, dex) = await LoadAsync("Gen9");
        var voltSwitch = data.Moves.First(m => m.Name == "ボルトチェンジ");
        var filter = new PokedexFilter { Types = { "Electric" }, MoveIds = { voltSwitch.Id }, Sort = PokedexSort.Speed, Descending = true };
        var list = dex.Query(filter);
        Assert.NotEmpty(list);
        Assert.All(list, p => Assert.True(p.HasType("Electric") && data.CanLearn(p.Id, voltSwitch.Id)));
        // すばやさの高い順（同じ値は図鑑順）
        Assert.True(list.Zip(list.Skip(1)).All(x => x.First.BaseStats.Speed >= x.Second.BaseStats.Speed));

        // 2 タイプは両方を持つポケモン
        var dragonGround = dex.Query(new PokedexFilter { Types = { "Dragon", "Ground" } });
        Assert.Contains(dragonGround, p => p.Name == "ガブリアス");
        Assert.All(dragonGround, p => Assert.True(p.HasType("Dragon") && p.HasType("Ground")));

        // 特性・名前（ローマ字）
        var roughSkin = data.Abilities.First(a => a.Identifier == "rough-skin");
        var named = dex.Query(new PokedexFilter { Name = "gaburi", AbilityId = roughSkin.Id });
        Assert.Contains(named, p => p.Name == "ガブリアス");
        Assert.All(named, p => Assert.Contains(roughSkin.Id, p.AbilityIds));
    }

    [Fact]
    public async Task Sort_by_total_ascending_and_generation_filter()
    {
        var (_, dex) = await LoadAsync("Gen9");
        var gen1 = dex.Query(new PokedexFilter { Generation = 1, Sort = PokedexSort.Total });
        Assert.All(gen1, p => Assert.InRange(Pokedex.DexNumber(p), 1, 151));
        Assert.True(gen1.Zip(gen1.Skip(1)).All(x => x.First.BaseStats.Total <= x.Second.BaseStats.Total));
        Assert.Equal(1, Pokedex.GenerationOf(151));
        Assert.Equal(9, Pokedex.GenerationOf(1025));
    }
}
