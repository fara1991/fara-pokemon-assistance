using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>相手ポケモン（-r）・履歴（-h）・set・scan のコマンド。</summary>
public class RivalCommandTests
{
    private const string Ds = "Champions";

    private static async Task<(PokeCommand Command, RosterRepository Repo)> NewAsync()
    {
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var repo = new RosterRepository(new MemoryRosterStore());
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "マニューラ"), new Models.StatSet { Attack = 32 }, data.Natures.First(), null);
        await repo.AddPokemonAsync(Ds, data.Pokemon.First(p => p.Name == "イエッサン(♂)"), new Models.StatSet { SpAttack = 32 }, data.Natures.First(), null);
        return (new PokeCommand(TestData.Catalog(), repo), repo);
    }

    [Fact]
    public async Task Names_can_be_added_together_and_the_same_pokemon_stays_one()
    {
        var (command, repo) = await NewAsync();
        var added = await command.ExecuteAsync("!pokech add -r カイリュー ミミッキュ ドドゲザン");
        Assert.True(added.Success, added.Message);
        Assert.Equal(3, (await repo.GetAsync()).For(Ds).Rivals.Count);

        // 同じポケモンをもう一度 add -r すると、1 体のまま更新
        var again = await command.ExecuteAsync("!pokech add -r カイリュー D32 おだやか");
        Assert.True(again.Success, again.Message);
        var rivals = (await repo.GetAsync()).For(Ds).Rivals;
        Assert.Equal(3, rivals.Count);
        var dragonite = rivals.Single(r => r.PokemonName == "カイリュー");
        Assert.Equal(32, dragonite.SpDefense);
        Assert.Equal("おだやか", dragonite.Nature);

        // set -r は指定した項目だけ更新（書いていない項目はそのまま）
        var set = await command.ExecuteAsync("!pokech set -r カイリュー H4 マルチスケイル");
        Assert.True(set.Success, set.Message);
        dragonite = (await repo.GetAsync()).For(Ds).Rivals.Single(r => r.PokemonName == "カイリュー");
        Assert.Equal(4, dragonite.HP);
        Assert.Equal(32, dragonite.SpDefense);
        Assert.NotNull(dragonite.AbilityId);

        // 6 つの数字でも指定できる
        var six = await command.ExecuteAsync("!pokech add -r ドドゲザン 32 32 0 0 2 0 いじっぱり");
        Assert.True(six.Success, six.Message);
        var kingambit = (await repo.GetAsync()).For(Ds).Rivals.Single(r => r.PokemonName == "ドドゲザン");
        Assert.Equal((32, 32, 0, 0, 2, 0), (kingambit.HP, kingambit.Attack, kingambit.Defense, kingambit.SpAttack, kingambit.SpDefense, kingambit.Speed));

        Assert.Contains("カイリュー", (await command.ExecuteAsync("!pokech ls -r")).Message);
        Assert.True((await command.ExecuteAsync("!pokech rm -r ミミッキュ")).Success);
        Assert.Equal(2, (await repo.GetAsync()).For(Ds).Rivals.Count);
    }

    [Fact]
    public async Task At_most_six_rivals_and_invalid_input_is_explained()
    {
        var (command, _) = await NewAsync();
        var seven = await command.ExecuteAsync("!pokech add -r カイリュー ミミッキュ ドドゲザン ガブリアス リザードン ピカチュウ ゲンガー");
        Assert.True(seven.Success, seven.Message);
        Assert.Contains("6 体います", seven.Message);
        Assert.False((await command.ExecuteAsync("!pokech add -r H32")).Success);
        Assert.False((await command.ExecuteAsync("!pokech add -r カイリュー H99")).Success);
        Assert.False((await command.ExecuteAsync("!pokech set -r カイリュー")).Success);
        Assert.False((await new PokeCommand(TestData.Catalog(), new RosterRepository(new MemoryRosterStore())).ExecuteAsync("!pokech add -r カイリュー", new PokeCommandOptions { AllowMutations = false })).Success);
    }

    [Fact]
    public async Task Scan_registers_narrows_and_dmg_uses_the_range_until_the_match_ends()
    {
        var (command, repo) = await NewAsync();
        var data = await TestData.Catalog().GetDataSetAsync(Ds);
        var probe = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ H0 B10");
        var observed = (int)Math.Round((probe.Damage!.MinDamage + probe.Damage.MaxDamage) / 2.0);

        // 名前を登録していなくても、scan で自動登録する
        var scan = await command.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observed}");
        Assert.True(scan.Success, scan.Message);
        var rival = (await repo.GetAsync()).For(Ds).Rivals.Single();
        Assert.Contains("フシギバナ", rival.PokemonName);
        Assert.True(rival.IsScanned);
        var first = rival.Scans.Single();
        Assert.Equal(1, first.Count);

        // dmg は、分かっている範囲の最小・最大（B）の 2 行
        var ranged = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ");
        Assert.True(ranged.Success, ranged.Message);
        Assert.Contains("B最小想定", ranged.Message);
        Assert.Contains("B最大想定", ranged.Message);
        Assert.Equal(3, ranged.Message.Split('\n').Length);
        // 特殊技（D）には、B の推定は使わない
        Assert.DoesNotContain("想定", (await command.ExecuteAsync("!pokech dmg マニューラ れいとうビーム フシギバナ")).Message);

        // 続けて同じ相手を scan すると、前の範囲と重なる部分に狭まる
        var again = await command.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observed}");
        Assert.True(again.Success, again.Message);
        var narrowed = (await repo.GetAsync()).For(Ds).Rivals.Single().Scans.Single();
        Assert.Equal(2, narrowed.Count);
        Assert.True(narrowed.Bands.All(b => first.Bands.Any(o => o.Modifier == b.Modifier && b.MinEv >= o.MinEv && b.MaxEv <= o.MaxEv)));

        // 矛盾するダメージは反映せず、前の推定を残す
        var conflict = await command.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observed * 2}");
        Assert.False(conflict.Success, conflict.Message);
        Assert.Equal(2, (await repo.GetAsync()).For(Ds).Rivals.Single().Scans.Single().Count);

        // 試合を終えると、推定した相手は履歴へ。対戦中の相手は空に戻り、dmg は既定に戻る
        var ended = await command.ExecuteAsync("!pokech clear -r テスト戦");
        Assert.True(ended.Success, ended.Message);
        Assert.Contains("履歴 #1", ended.Message);
        Assert.Empty((await repo.GetAsync()).For(Ds).Rivals);
        Assert.Equal(2, (await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ")).Message.Split('\n').Length);
        Assert.Contains("テスト戦", (await command.ExecuteAsync("!pokech ls -h")).Message);
        Assert.Contains("フシギバナ", (await command.ExecuteAsync("!pokech ls -h フシギバナ")).Message);
        Assert.Contains("根拠", (await command.ExecuteAsync("!pokech detail -h 1")).Message);
        Assert.True((await command.ExecuteAsync("!pokech rm -h 1")).Success);
        Assert.Empty((await repo.GetAsync()).For(Ds).History);
        Assert.NotNull(data);
    }

    [Fact]
    public async Task Name_only_rivals_are_not_moved_to_the_history()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -r カイリュー ミミッキュ");
        await command.ExecuteAsync("!pokech set -r ミミッキュ S32");
        var ended = await command.ExecuteAsync("!pokech clear -r");
        Assert.True(ended.Success, ended.Message);
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Empty(roster.Rivals);
        Assert.Equal("ミミッキュ", roster.History.Single().Foes.Single().PokemonName);
        Assert.Contains("名前だけ", ended.Message);
    }

    [Fact]
    public async Task Exact_rival_spread_is_used_by_dmg_and_set_updates_my_pokemon()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -r フシギバナ H32 B20 B↑A↓");
        var dmg = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ");
        Assert.True(dmg.Success, dmg.Message);
        Assert.Equal(20, dmg.Request!.Defender.EVs.Defense);
        Assert.Equal(Models.Stat.Defense, dmg.Request.Defender.Nature!.IncreasedStat);
        Assert.Equal(2, dmg.Message.Split('\n').Length);   // 1 つに決まっていれば、想定は分けない

        // 自分のポケモンの更新（set -m）
        var set = await command.ExecuteAsync("!pokech set -m 1 A20 S10 ようき");
        Assert.True(set.Success, set.Message);
        var mine = (await repo.GetAsync()).For(Ds).Pokemon.Single(p => p.Id == 1);
        Assert.Equal(20, mine.Attack);
        Assert.Equal(10, mine.Speed);
        Assert.Equal("ようき", mine.Nature);
        Assert.False((await command.ExecuteAsync("!pokech set -m 99 A1")).Success);
    }

    [Fact]
    public async Task Old_forms_are_rejected_with_guidance()
    {
        var (command, _) = await NewAsync();
        var calc = await command.ExecuteAsync("!pokech calc マニューラ つららおとし フシギバナ 60%");
        Assert.False(calc.Success);
        Assert.Contains("scan", calc.Message);
        var foe = await command.ExecuteAsync("!pokech foe ls");
        Assert.False(foe.Success);
        Assert.Contains("-r", foe.Message);
    }
}
