using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>同じポケモンを別の育成で複数登録しているときの、表示と指定のテスト。</summary>
public class DuplicateRegisteredTests
{
    private const string Ds = "Champions";

    private static async Task<(PokeCommand Command, RosterRepository Repo)> NewAsync()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var repo = new RosterRepository(new MemoryRosterStore());
        // 同じイエッサン(♂)を、別の育成で 2 体
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "イエッサン(♂)"), new Models.StatSet { SpAttack = 32 }, data.Natures.First(n => n.Name == "ひかえめ"), null);
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "イエッサン(♂)"), new Models.StatSet { SpAttack = 20, Speed = 12 }, data.Natures.First(n => n.Name == "おくびょう"), null);
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "マニューラ"), new Models.StatSet { Attack = 32 }, data.Natures.First(), null);
        return (new PokeCommand(TestData.Catalog(), repo), repo);
    }

    [Fact]
    public async Task Duplicates_are_listed_separately_and_the_add_reply_mentions_it()
    {
        var (command, _) = await NewAsync();
        var list = await command.ExecuteAsync("!pokech ls -m イエッサン♂");
        Assert.Contains("#1 ", list.Message);
        Assert.Contains("#2 ", list.Message);
        var add = await command.ExecuteAsync("!pokech add -m イエッサン♂ 0 0 0 32 0 32 ひかえめ");
        Assert.True(add.Success, add.Message);
        Assert.Contains("3 体になりました", add.Message);
        Assert.Contains("#登録ID", add.Message);
    }

    [Theory]
    [InlineData("dmg")]
    [InlineData("faster")]
    public async Task Name_only_with_duplicates_warns_and_lists_them_with_ids(string sub)
    {
        var (command, _) = await NewAsync();
        var text = sub == "dmg" ? "!pokech dmg イエッサン♂ ワイドフォース リザードン" : "!pokech faster イエッサン♂ リザードン";
        var reply = await command.ExecuteAsync(text);
        Assert.False(reply.Success);
        var lines = reply.Message.Split('\n');
        Assert.Contains("育成ポケモンが 2 体います", lines[0]);
        Assert.Contains($"{sub} #1", lines[0]);
        Assert.StartsWith("#1 イエッサン(♂)(", lines[1]);
        Assert.StartsWith("#2 イエッサン(♂)(", lines[2]);
    }

    [Fact]
    public async Task Entry_id_picks_the_exact_pokemon_for_dmg_scan_and_faster()
    {
        var (command, _) = await NewAsync();
        var first = await command.ExecuteAsync("!pokech dmg #1 ワイドフォース リザードン");
        var second = await command.ExecuteAsync("!pokech dmg #2 ワイドフォース リザードン");
        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.Equal(32, first.Request!.Attacker.EVs.SpAttack);
        Assert.Equal(20, second.Request!.Attacker.EVs.SpAttack);
        Assert.Equal("おくびょう", second.Request.Attacker.Nature?.Name);
        // # を付けない数字でも指定できる
        Assert.Equal(20, (await command.ExecuteAsync("!pokech dmg 2 ワイドフォース リザードン")).Request!.Attacker.EVs.SpAttack);

        var fast = await command.ExecuteAsync("!pokech faster #2 リザードン");
        Assert.True(fast.Success, fast.Message);
        Assert.Contains("イエッサン(♂)(", fast.Message.Split('\n')[0]);
        Assert.Contains("S", fast.Message.Split('\n')[1]);

        var probe = await command.ExecuteAsync("!pokech dmg #1 ワイドフォース フシギバナ H0 D10");
        var observed = (int)Math.Round((probe.Damage!.MinDamage + probe.Damage.MaxDamage) / 2.0);
        var scan = await command.ExecuteAsync($"!pokech scan #1 ワイドフォース フシギバナ {observed}");
        Assert.True(scan.Success, scan.Message);
        Assert.StartsWith("イエッサン(♂)(", scan.Message.Split('\n')[0]);
        Assert.DoesNotContain("#1", scan.Message.Split('\n')[0]);

        // 存在しない登録 ID
        var missing = await command.ExecuteAsync("!pokech dmg #99 ワイドフォース リザードン");
        Assert.False(missing.Success);
        Assert.Contains("#99 はありません", missing.Message);
    }

    [Fact]
    public async Task The_only_member_in_the_active_team_is_used_without_a_warning()
    {
        var (command, _) = await NewAsync();
        Assert.True((await command.ExecuteAsync("!pokech add -t 1 2")).Success);
        Assert.True((await command.ExecuteAsync("!pokech use -t 1")).Success);
        var reply = await command.ExecuteAsync("!pokech dmg イエッサン♂ ワイドフォース リザードン");
        Assert.True(reply.Success, reply.Message);
        Assert.Equal(20, reply.Request!.Attacker.EVs.SpAttack);   // 使用チームにいる 2 番を使う
    }

    [Fact]
    public async Task Rm_team_accepts_an_entry_id_so_only_that_one_is_removed()
    {
        var (command, repo) = await NewAsync();
        Assert.True((await command.ExecuteAsync("!pokech add -t 1 1")).Success);
        Assert.True((await command.ExecuteAsync("!pokech add -t 1 2")).Success);
        var removed = await command.ExecuteAsync("!pokech rm -t 1 #2");
        Assert.True(removed.Success, removed.Message);
        var team = (await repo.GetAsync()).For(Ds).Teams.Single();
        Assert.Equal(new[] { 1 }, team.Slots.Select(s => s.EntryId).ToArray());
    }

    [Fact]
    public async Task Entry_id_uses_that_pokemons_own_build_in_faster_and_scan_warns_about_duplicates()
    {
        var (command, _) = await NewAsync();
        // #2 は S12（おくびょう）、#1 は S0。素早さの数字が変わる
        var first = (await command.ExecuteAsync("!pokech faster #1 リザードン")).Message;
        var second = (await command.ExecuteAsync("!pokech faster #2 リザードン")).Message;
        Assert.NotEqual(first.Split('\n')[1], second.Split('\n')[1]);

        // scan も、名前だけで同名が複数いるときは警告して、一覧を出す
        var scan = await command.ExecuteAsync("!pokech scan イエッサン♂ ワイドフォース リザードン 30%");
        Assert.False(scan.Success);
        Assert.Contains("scan #1", scan.Message.Split('\n')[0]);
        Assert.StartsWith("#2 イエッサン(♂)(", scan.Message.Split('\n')[2]);
    }

    [Fact]
    public async Task Two_duplicates_in_the_active_team_still_warn()
    {
        var (command, _) = await NewAsync();
        Assert.True((await command.ExecuteAsync("!pokech add -t 1 1")).Success);
        Assert.True((await command.ExecuteAsync("!pokech add -t 1 2")).Success);
        Assert.True((await command.ExecuteAsync("!pokech use -t 1")).Success);
        var reply = await command.ExecuteAsync("!pokech dmg イエッサン♂ ワイドフォース リザードン");
        Assert.False(reply.Success);
        Assert.Contains("育成ポケモンが 2 体います", reply.Message);
    }
}
