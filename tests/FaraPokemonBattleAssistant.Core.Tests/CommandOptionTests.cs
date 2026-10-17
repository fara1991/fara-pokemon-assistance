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
        Assert.True((await cmd.ExecuteAsync("!pokech add --my ガブリアス 4 32 0 0 0 30 ようき")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech add -t 1 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech ls --team")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech detail -t 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech use --team 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech unuse -t")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech rm --team 1")).Success);
        Assert.True((await cmd.ExecuteAsync("!pokech rm -m 1")).Success);
    }

    [Theory]
    [InlineData("!pokech add -p ガブリアス 4 32 0 0 0 30 ようき")]
    [InlineData("!pokech ls --pokemon")]
    public async Task Unknown_option_is_reported_as_missing(string message)
    {
        // 以前の -p / --pokemon は、ほかの存在しないオプションと同じく「ありません」と返す（廃止の案内はしない）
        var r = await NewCommand().ExecuteAsync(message);
        Assert.False(r.Success);
        Assert.Contains("というオプションはありません", r.Message);
        Assert.DoesNotContain("廃止", r.Message);
    }

    [Fact]
    public async Task Set_my_accepts_a_name_or_an_id()
    {
        var cmd = NewCommand();
        Assert.True((await cmd.ExecuteAsync("!pokech add -m アシレーヌ 4 0 0 32 0 30 ひかえめ")).Success);
        var byName = await cmd.ExecuteAsync("!pokech set -m アシレーヌ B↑A↓");
        Assert.True(byName.Success, byName.Message);
        Assert.Contains("B↑A↓", byName.Message);
        Assert.True((await cmd.ExecuteAsync("!pokech set -m 1 ひかえめ")).Success);
        // 同じポケモンが複数あるときは、登録IDで指定するよう案内する
        Assert.True((await cmd.ExecuteAsync("!pokech add -m アシレーヌ 4 0 0 32 0 30 おくびょう")).Success);
        var ambiguous = await cmd.ExecuteAsync("!pokech set -m アシレーヌ B↑A↓");
        Assert.False(ambiguous.Success);
        Assert.Contains("#1 アシレーヌ(", ambiguous.Message);
        Assert.Contains("#2 アシレーヌ(", ambiguous.Message);
        Assert.Contains("set -m #1", ambiguous.Message);
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
        Assert.Contains("-m", r.Message);
        Assert.Contains("-t", r.Message);
    }

    [Fact]
    public async Task Full_width_input_is_treated_like_half_width()
    {
        var cmd = NewCommand();
        await cmd.ExecuteAsync("!pokech add -m ガブリアス 4 32 0 0 0 30 ようき");
        var half = await cmd.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン");
        var full = await cmd.ExecuteAsync("!pokech　dmg　ガブリアス　じしん　リザードン");
        var fullAll = await cmd.ExecuteAsync("！ｐｏｋｅｃｈ　ｄｍｇ  ガブリアス　　じしん＋１　リザードン");
        Assert.True(half.Success, half.Message);
        Assert.Equal(half.Message, full.Message);
        Assert.True(fullAll.Success, fullAll.Message);

        Assert.True((await cmd.ExecuteAsync("!pokech　add　－ｍ　ガブリアス　４　３２　０　０　０　３０　ようき")).Success);
    }

    [Fact]
    public void Normalize_only_touches_spaces_and_ascii_variants()
    {
        Assert.Equal("dmg -p +1 ガブ ひらがな", CommandText.Normalize("　ｄｍｇ　－ｐ　＋１　　ガブ\tひらがな　"));
    }

    [Fact]
    public async Task Add_my_accepts_a_partial_spread_and_set_my_replaces_the_spread()
    {
        var cmd = NewCommand();
        // 書いた能力だけ振る（書いていない能力は 0）
        var add = await cmd.ExecuteAsync("!pokech add -m アシレーヌ H32B32C2 ずぶとい");
        Assert.True(add.Success, add.Message);
        Assert.Contains("H32B32C2", add.Message);
        Assert.True((await cmd.ExecuteAsync("!pokech add -m フシギバナ C32 ひかえめ")).Success);
        // ポイントを書いた set -m は振り方全体の置き換え（前の C32 は残らない）
        var set = await cmd.ExecuteAsync("!pokech set -m 2 H32 D10 おだやか");
        Assert.True(set.Success, set.Message);
        Assert.Contains("H32D10", set.Message);
        Assert.DoesNotContain("C32", set.Message);
        // 性格がなければエラー
        Assert.False((await cmd.ExecuteAsync("!pokech add -m アシレーヌ H32B32")).Success);
    }
}
