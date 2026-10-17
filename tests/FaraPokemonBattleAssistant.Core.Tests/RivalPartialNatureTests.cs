using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>相手の性格は、上がる能力・下がる能力の片方だけ分かるときがある。もう片方は補わず、分かっている方だけ覚える。</summary>
public class RivalPartialNatureTests
{
    private const string Ds = "Champions";

    private static async Task<(PokeCommand Command, RosterRepository Repo)> NewAsync()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var repo = new RosterRepository(new MemoryRosterStore());
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "イエッサン(♂)"), new StatSet { SpAttack = 32 }, data.Natures.First(), null);
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "マニューラ"), new StatSet { Attack = 32 }, data.Natures.First(), null);
        return (new PokeCommand(TestData.Catalog(), repo), repo);
    }

    [Fact]
    public async Task One_arrow_is_kept_as_is_and_the_other_is_not_filled_in()
    {
        var (command, repo) = await NewAsync();
        var up = await command.ExecuteAsync("!pokech add -r リザードン D↑");
        Assert.True(up.Success, up.Message);
        var rival = Assert.Single((await repo.GetAsync()).For(Ds).Rivals);
        Assert.Null(rival.Nature);
        Assert.Equal(Stat.SpDefense, rival.NatureUp);
        Assert.Null(rival.NatureDown);
        Assert.Contains("D↑", up.Message);
        Assert.DoesNotContain("↓", up.Message);

        // 下がる能力が分かったら、2 つがそろって性格が決まる（部分の情報は性格に置き換わる）
        Assert.True((await command.ExecuteAsync("!pokech set -r リザードン A↓")).Success);
        rival = Assert.Single((await repo.GetAsync()).For(Ds).Rivals);
        Assert.Equal("おだやか", rival.Nature);
        Assert.Null(rival.NatureUp);
        Assert.Null(rival.NatureDown);

        // 下がる能力だけ書いた場合も、上がる能力は補わない
        Assert.True((await command.ExecuteAsync("!pokech add -r ミミッキュ S↓")).Success);
        var mimikyu = (await repo.GetAsync()).For(Ds).Rivals.Single(r => r.PokemonName == "ミミッキュ");
        Assert.Null(mimikyu.NatureUp);
        Assert.Equal(Stat.Speed, mimikyu.NatureDown);
    }

    [Fact]
    public async Task Partial_nature_narrows_dmg_and_scan_to_the_known_modifier()
    {
        var (command, _) = await NewAsync();
        Assert.True((await command.ExecuteAsync("!pokech add -r リザードン D20 D↑")).Success);
        var dmg = await command.ExecuteAsync("!pokech dmg イエッサン♂ ワイドフォース リザードン");
        Assert.True(dmg.Success, dmg.Message);
        Assert.Equal(Stat.SpDefense, dmg.Request!.Defender.Nature?.IncreasedStat);

        var (command2, _) = await NewAsync();
        Assert.True((await command2.ExecuteAsync("!pokech add -r リザードン D↑")).Success);
        var scan = (await command2.ExecuteAsync("!pokech scan イエッサン♂ ワイドフォース リザードン 30%")).Message.Split((char)10);
        Assert.Contains("(D↑)", scan[1]);
        Assert.Contains("D上昇補正", scan[2]);
        Assert.DoesNotContain("D補正無し", scan[2]);
        Assert.DoesNotContain("D下降補正", scan[2]);
        // 下がる能力が D 以外のときは、D の補正は分からないので、絞らない
        var (command3, _) = await NewAsync();
        Assert.True((await command3.ExecuteAsync("!pokech add -r リザードン A↓")).Success);
        var other = (await command3.ExecuteAsync("!pokech scan イエッサン♂ ワイドフォース リザードン 30%")).Message.Split((char)10);
        Assert.DoesNotContain("(D", other[1]);
    }

    [Fact]
    public async Task Own_pokemon_still_complete_a_single_arrow_into_one_nature()
    {
        var (command, repo) = await NewAsync();
        var add = await command.ExecuteAsync("!pokech add -m アシレーヌ 4 0 0 32 0 30 B↑");
        Assert.True(add.Success, add.Message);
        var entry = (await repo.GetAsync()).For(Ds).Pokemon.Last();
        Assert.False(string.IsNullOrEmpty(entry.Nature));
    }
}
