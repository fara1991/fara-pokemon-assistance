using FaraPokemonBattleAssistant.Core.Models;
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
}
