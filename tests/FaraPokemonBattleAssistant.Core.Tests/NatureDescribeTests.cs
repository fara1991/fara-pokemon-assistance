using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

public class NatureDescribeTests
{
    [Fact]
    public void Describe_uses_stat_letters()
    {
        var adamant = new Nature { Name = "いじっぱり", IncreasedStat = Stat.Attack, DecreasedStat = Stat.SpAttack };
        Assert.Equal("いじっぱり（A↑C↓）", adamant.Describe());
        var hardy = new Nature { Name = "まじめ" };
        Assert.Equal("まじめ（補正無し）", hardy.Describe());
    }

    [Fact]
    public void Summary_shows_only_stat_changes_and_labels_neutral()
    {
        var modest = new Nature { Name = "ひかえめ", IncreasedStat = Stat.SpAttack, DecreasedStat = Stat.Attack };
        Assert.Equal("C↑A↓", modest.Summary());
        Assert.Equal("補正無し", new Nature { Name = "まじめ" }.Summary());
    }

    [Fact]
    public async Task Chat_replies_show_stat_changes_instead_of_nature_name()
    {
        var store = new MemoryRosterStore();
        var cmd = new PokeCommand(TestData.Catalog(), new RosterRepository(store));
        var adamant = await cmd.ExecuteAsync("!pokech add -m ガブリアス 0 32 0 0 2 32 いじっぱり");
        Assert.Contains("A↑C↓", adamant.Message);
        Assert.DoesNotContain("いじっぱり", adamant.Message);
        var hardy = await cmd.ExecuteAsync("!pokech add -m ガブリアス 0 32 0 0 2 32 まじめ");
        Assert.Contains("補正無し", hardy.Message);
        Assert.DoesNotContain("まじめ", hardy.Message);
        Assert.DoesNotContain("↑", hardy.Message);
        var ls = await cmd.ExecuteAsync("!pokech ls -m");
        Assert.Contains("#1 ガブリアス(A↑C↓)", ls.Message);
        Assert.Contains("#2 ガブリアス(補正無し)", ls.Message);
    }
}
