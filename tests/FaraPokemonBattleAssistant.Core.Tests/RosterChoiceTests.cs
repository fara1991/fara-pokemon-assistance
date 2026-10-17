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

    [Fact]
    public async Task Registered_pokemon_can_be_switched_to_its_mega_forms()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var charizard = data.Pokemon.First(p => p.Name == "リザードン");
        var forms = RosterRepository.MegaFormsOf(data, charizard);
        Assert.Equal(new[] { "メガリザードンX", "メガリザードンY" }, forms.Select(f => f.Name).OrderBy(n => n));
        Assert.Empty(RosterRepository.MegaFormsOf(data, forms[0]));   // メガシンカ後の姿からは、さらに切り替えない

        var nature = data.Natures.First();
        var build = new PokemonBuild(charizard, data.Info.EvSystem) { EVs = new StatSet { SpAttack = 32 }, Nature = nature, Item = data.Items.First() };
        var mega = RosterRepository.ToMegaBuild(build, forms[0], data);
        Assert.Equal(forms[0], mega.Pokemon);
        Assert.Equal(32, mega.EVs.SpAttack);
        Assert.NotSame(build.EVs, mega.EVs);
        Assert.Equal(nature, mega.Nature);
        Assert.Equal(forms[0].MegaStoneId, mega.Item?.Id);
        Assert.Equal(charizard, build.Pokemon);   // 元の構成は変わらない

        // 切り替えても、ランク補正などは引き継ぐ。メガシンカ前に戻すと、持ち物・特性は登録のもの
        mega.Boosts = new StatSet { Attack = 2 };
        mega.Status = StatusCondition.Burn;
        var entry = new RegisteredPokemon { PokemonId = charizard.Id, PokemonName = charizard.Name, Nature = nature.Name };
        var back = RosterRepository.ToBaseBuild(mega, entry, data, null);
        Assert.Equal(charizard, back.Pokemon);
        Assert.Equal(2, back.Boosts.Attack);
        Assert.Equal(StatusCondition.Burn, back.Status);
        Assert.Null(back.Item);
    }

    [Fact]
    public async Task Items_and_abilities_have_descriptions()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        Assert.NotEmpty(data.Items.First(i => i.Name == "こだわりスカーフ").Description);
        Assert.NotEmpty(data.Abilities.First(a => a.Name == "いかく").Description);
    }
}
