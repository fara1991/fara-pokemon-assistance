using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>登録ポケモンに技（最大 4 つ）を持たせて保存できること。</summary>
public class RosterMovesTests
{
    [Fact]
    public async Task Moves_are_saved_with_the_registration_and_read_back()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var store = new MemoryRosterStore();
        var repo = new RosterRepository(store);
        var moves = data.LearnableMoves(445).Take(4).Select(m => m.Id).ToList();
        var entry = await repo.AddPokemonAsync("Gen9", data.FindPokemon(445)!, new StatSet(), data.Natures.First(), null, moves);
        Assert.Equal(moves, entry.MoveIds);

        // 保存した JSON から読み直しても、技が残っている
        var reloaded = new RosterRepository(store);
        var read = (await reloaded.GetAsync()).For("Gen9").Pokemon.Single();
        Assert.Equal(moves, read.MoveIds);
    }

    [Fact]
    public async Task Duplicate_and_extra_moves_are_dropped()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var repo = new RosterRepository(new MemoryRosterStore());
        var ids = data.LearnableMoves(445).Take(6).Select(m => m.Id).ToList();
        var entry = await repo.AddPokemonAsync("Gen9", data.FindPokemon(445)!, new StatSet(), data.Natures.First(), null,
            new[] { ids[0], ids[0], 0, ids[1], ids[2], ids[3], ids[4], ids[5] });
        Assert.Equal(ids.Take(4), entry.MoveIds);
    }

    [Fact]
    public async Task Registration_without_moves_still_works()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var repo = new RosterRepository(new MemoryRosterStore());
        var entry = await repo.AddPokemonAsync("Gen9", data.FindPokemon(445)!, new StatSet(), data.Natures.First(), null);
        Assert.Empty(entry.MoveIds);
    }
}
