using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

public class CommandOptionTests
{
    private static PokeCommand NewCommand() => new(TestData.Catalog(), new RosterRepository(new MemoryRosterStore()));

    [Fact]
    public async Task Short_and_long_options_are_accepted()
    {
        var cmd = NewCommand();
        Assert.True((await cmd.ExecuteAsync("!pokech add --pokemon ガブリアス 4 32 0 0 0 30 ようき")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech add -t 1 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech ls --team")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech detail -t 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech use --team 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech unuse -t")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech rm --team 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech rm -p 1")).Success);
    }

    [Theory]
    [InlineData("!pokech add pokemon ガブリアス 4 32 0 0 0 30 ようき")]
    [InlineData("!pokech add team 1 1")]
    [InlineData("!pokech ls team")]
    [InlineData("!pokech use team 1")]
    [InlineData("!pokech add")]
    [InlineData("!pokech use 1")]
    public async Task Word_forms_are_rejected_with_guidance(string message)
    {
        var r = await NewCommand().ExecuteAsync(message);
        Assert.False(r.Success);
        Assert.Contains("-p", r.Message);
        Assert.Contains("-t", r.Message);
    }

    [Fact]
    public async Task Full_width_input_is_treated_like_half_width()
    {
        var cmd = NewCommand();
        var half = await cmd.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン");
        var full = await cmd.ExecuteAsync("!pokech　dmg　ガブリアス　じしん　リザードン");
        var fullAll = await cmd.ExecuteAsync("！ｐｏｋｅｃｈ　ｄｍｇ  ガブリアス　　じしん＋１　リザードン");
        Assert.True(half.Success, half.Message);
        Assert.Equal(half.Message, full.Message);
        Assert.True(fullAll.Success, fullAll.Message);

        Assert.True((await cmd.ExecuteAsync("!pokech　add　－ｐ　ガブリアス　４　３２　０　０　０　３０　ようき")).Success);
    }

    [Fact]
    public void Normalize_only_touches_spaces_and_ascii_variants()
    {
        Assert.Equal("dmg -p +1 ガブ ひらがな", CommandText.Normalize("　ｄｍｇ　－ｐ　＋１　　ガブ\tひらがな　"));
    }
}
