using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>計算機・素早さ比較の「登録から選ぶ」（チームごとのグループ＋チームに入っていない登録）。</summary>
public class RosterChoiceTests
{
    [Fact]
    public async Task Choices_are_grouped_by_team_with_team_items_and_the_rest_last()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var repo = new RosterRepository(new MemoryRosterStore());
        var nature = data.Natures.First();
        var garchomp = await repo.AddPokemonAsync("Gen9", data.FindPokemon(445)!, new StatSet { Attack = 252, Speed = 252 }, nature, null);
        var charizard = await repo.AddPokemonAsync("Gen9", data.FindPokemon(6)!, new StatSet { SpAttack = 252 }, nature, null);
        var dragonite = await repo.AddPokemonAsync("Gen9", data.FindPokemon(149)!, new StatSet { HP = 252 }, nature, null);
        var scarf = data.Items.First(i => i.Id == 264);
        await repo.AddToTeamAsync("Gen9", 2, garchomp.Id, scarf);
        await repo.AddToTeamAsync("Gen9", 1, charizard.Id, null);
        await repo.UseTeamAsync("Gen9", 2);

        var groups = await repo.ChoiceGroupsAsync("Gen9", data);
        Assert.Equal(new[] { "チーム1", "チーム2（使用中）", "チームに入っていない登録" }, groups.Select(g => g.Label));
        var team2 = groups[1].Choices.Single();
        Assert.Equal(garchomp.Id, team2.Entry.Id);
        Assert.Equal(scarf, team2.Item);   // チームで持たせた持ち物
        Assert.Contains("こだわりスカーフ", team2.Label);
        Assert.Equal(dragonite.Id, groups[2].Choices.Single().Entry.Id);
        Assert.Null(groups[2].Choices.Single().Item);
        // 別のゲームの登録は混ざらない
        Assert.Empty(await repo.ChoiceGroupsAsync("Gen8", data));
    }

    [Fact]
    public async Task Changing_a_picked_build_does_not_change_the_registration()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var repo = new RosterRepository(new MemoryRosterStore());
        var entry = await repo.AddPokemonAsync("Gen9", data.FindPokemon(445)!, new StatSet { Attack = 252, Speed = 252 }, data.Natures.First(), null);
        var choice = (await repo.ChoiceGroupsAsync("Gen9", data)).Single().Choices.Single();
        var build = RosterRepository.ToBuild(choice.Entry, data, choice.Item)!;
        Assert.Equal(252, build.EVs.Attack);
        build.EVs = new StatSet { HP = 252 };
        build.Nature = data.Natures.Last();
        build.Item = data.Items.First();
        var stored = (await repo.GetAsync()).For("Gen9").Pokemon.Single();
        Assert.Equal(252, stored.Attack);
        Assert.Equal(0, stored.HP);
        Assert.Equal(entry.Nature, stored.Nature);
        Assert.Null(stored.ItemId);
    }
}
