using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>チャットの dmg・calc・faster は、自分（攻撃側）のポケモンを登録済みのものに限る。</summary>
public class RegisteredOnlyTests
{
    private static PokeCommand NewCommand() => new(TestData.Catalog(), new RosterRepository(new MemoryRosterStore()));

    [Fact]
    public async Task Unregistered_attacker_is_rejected_by_dmg_calc_and_faster()
    {
        var cmd = NewCommand();
        var dmg = await cmd.ExecuteAsync("!pokesv dmg バシャーモ かえんほうしゃ ガブリアス");
        Assert.False(dmg.Success);
        Assert.Contains("バシャーモは登録されていません", dmg.Message);
        var calc = await cmd.ExecuteAsync("!pokesv calc バシャーモ かえんほうしゃ ガブリアス 45%");
        Assert.False(calc.Success);
        Assert.Contains("登録されていません", calc.Message);
        var faster = await cmd.ExecuteAsync("!pokesv faster バシャーモ ガブリアス");
        Assert.False(faster.Success);
        Assert.Contains("登録されていません", faster.Message);
    }

    [Fact]
    public async Task Registered_attacker_uses_its_own_ability_and_item_not_usage_defaults()
    {
        var cmd = NewCommand();
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var blaze = data.Pokemon.First(p => p.Name == "バシャーモ");
        var abilities = data.AbilitiesOf(blaze);
        Assert.True(abilities.Count > 1);
        var torch = abilities.First(a => a.Identifier == "blaze");
        // 登録の特性は、画面の登録と同じ ID で入る（ここでは直接、登録データに入れる）
        var store = new MemoryRosterStore();
        var repo = new RosterRepository(store);
        await repo.AddPokemonAsync("Gen9", blaze, new Models.StatSet { SpAttack = 252 }, data.Natures.First(), torch);
        var command = new PokeCommand(TestData.Catalog(), repo);
        var reply = await command.ExecuteAsync("!pokesv dmg バシャーモ かえんほうしゃ ガブリアス");
        Assert.True(reply.Success, reply.Message);
        Assert.Equal("blaze", reply.Request!.Attacker.Ability!.Identifier);
        Assert.Null(reply.Request.Attacker.Item);   // 持ち物は登録のとおり（使用率 1 位の持ち物は入れない）
    }

    [Fact]
    public async Task Registered_mega_pokemon_holds_its_mega_stone()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var mega = data.Pokemon.First(p => p.IsMega && p.Name.StartsWith("メガメタグロス"));
        var repo = new RosterRepository(new MemoryRosterStore());
        await repo.AddPokemonAsync("Champions", mega, new Models.StatSet { Attack = 32 }, data.Natures.First(), null);
        var command = new PokeCommand(TestData.Catalog(), repo);
        var reply = await command.ExecuteAsync("!pokech dmg メガメタグロス じしん リザードン");
        Assert.True(reply.Success, reply.Message);
        Assert.Equal(data.FindItem(mega.MegaStoneId!.Value)!.Name, reply.Request!.Attacker.Item!.Name);
        Assert.DoesNotContain("ノーマルジュエル", reply.Message);
    }
}
