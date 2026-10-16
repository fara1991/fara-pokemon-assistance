using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>登録済みポケモンの更新と、チャットの防御側（相手）の表示。</summary>
public class UpdatePokemonTests
{
    [Fact]
    public async Task Update_changes_evs_nature_ability_and_moves_but_keeps_the_id_and_team()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var repo = new RosterRepository(new MemoryRosterStore());
        var garchomp = data.Pokemon.First(p => p.Name == "ガブリアス");
        var entry = await repo.AddPokemonAsync("Champions", garchomp, new StatSet { Attack = 32 }, data.Natures.First(), null, new[] { 89 });
        await repo.AddToTeamAsync("Champions", 1, entry.Id, null);

        var jolly = data.Natures.First(n => n.Name == "ようき");
        var updated = await repo.UpdatePokemonAsync("Champions", entry.Id, new StatSet { Attack = 20, Speed = 32 }, jolly, null, new[] { 89, 89, 0, 200 });

        Assert.NotNull(updated);
        var read = (await repo.GetAsync()).For("Champions");
        var saved = read.Pokemon.Single();
        Assert.Equal(entry.Id, saved.Id);
        Assert.Equal(32, saved.EVs.Speed);
        Assert.Equal(20, saved.EVs.Attack);
        Assert.Equal("ようき", saved.Nature);
        Assert.Equal(new[] { 89, 200 }, saved.MoveIds);   // 重複と 0 は除く
        Assert.Single(read.Teams.Single().Slots, slot => slot.EntryId == entry.Id);   // チームに入ったまま
        Assert.Null(await repo.UpdatePokemonAsync("Champions", 999, new StatSet(), jolly, null, null));
    }

    [Fact]
    public async Task Chat_output_shows_the_opponents_item_but_not_its_hp()
    {
        var repo = new RosterRepository(new MemoryRosterStore());
        var cmd = new PokeCommand(TestData.Catalog(), repo);
        await cmd.ExecuteAsync("!pokech add -m ガブリアス 4 32 0 0 0 30 ようき");
        var reply = await cmd.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン 防:たべのこし");
        Assert.True(reply.Success, reply.Message);
        Assert.Contains("たべのこし", reply.Message.Split("→")[1].Split(':')[0]);       // 相手の持ち物（持っているとき）は出す
        Assert.DoesNotContain("HP1", reply.Message.Split("→")[1].Split(':')[0]);        // 相手の HP の数値は出さない
        // 持ち物なしの相手には、持ち物は何も出ない
        var none = await cmd.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン 防:持ち物なし");
        Assert.True(none.Success, none.Message);
        Assert.Null(none.Request!.Defender.Item);
        // 括弧の中が、持ち物なしの形（種族名(振り方 性格) だけ）になっている
        Assert.Matches(@"→ リザードン\([^)]*\)", none.Message);
        Assert.DoesNotContain(reply.Request!.Defender.Item!.Name, none.Message.Split("→")[1].Split(':')[0]);
    }
}
