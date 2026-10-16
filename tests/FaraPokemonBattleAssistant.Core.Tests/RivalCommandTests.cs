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

        // set -r は書いた項目だけ更新する（書いていない能力のポイントも、前の値のまま残る）
        var set = await command.ExecuteAsync("!pokech set -r カイリュー H4 マルチスケイル");
        Assert.True(set.Success, set.Message);
        dragonite = (await repo.GetAsync()).For(Ds).Rivals.Single(r => r.PokemonName == "カイリュー");
        Assert.Equal(4, dragonite.HP);
        Assert.Equal(32, dragonite.SpDefense);
        Assert.Equal("おだやか", dragonite.Nature);
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
        // 7 体以上はエラーで、1 体も登録しない
        var seven = await command.ExecuteAsync("!pokech add -r カイリュー ミミッキュ ドドゲザン ガブリアス リザードン ピカチュウ ゲンガー");
        Assert.False(seven.Success, seven.Message);
        Assert.Contains("6 体まで", seven.Message);
        // 対戦中の数と合わせて 6 体を超えるときも、1 体も登録しない
        Assert.True((await command.ExecuteAsync("!pokech add -r カイリュー ミミッキュ ドドゲザン ガブリアス")).Success);
        var over = await command.ExecuteAsync("!pokech add -r リザードン ピカチュウ ゲンガー");
        Assert.False(over.Success, over.Message);
        Assert.Contains("6 体まで", over.Message);
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
        var ended = await command.ExecuteAsync("!pokech end テスト戦");
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
    public async Task Every_rival_goes_to_the_history_with_picked_moves_and_points()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -r カイリュー ミミッキュ ドドゲザン");
        await command.ExecuteAsync("!pokech set -r ミミッキュ S32 選出");
        // 技は、その相手が覚える技を書く。書いた分だけ足していく（前の技は消えない）
        var move = await command.ExecuteAsync("!pokech set -r カイリュー しんそく");
        Assert.True(move.Success, move.Message);
        Assert.True((await command.ExecuteAsync("!pokech set -r カイリュー じしん しんそく")).Success);
        var ended = await command.ExecuteAsync("!pokech end");
        Assert.True(ended.Success, ended.Message);
        Assert.Contains("3 体", ended.Message);
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Empty(roster.Rivals);

        // 選出しなかった名前だけの相手も含めて、6 体全員が履歴に残る
        var foes = roster.History.Single().Foes;
        Assert.Equal(new[] { "カイリュー", "ミミッキュ", "ドドゲザン" }, foes.Select(f => f.PokemonName).ToArray());
        Assert.Equal(new[] { false, true, false }, foes.Select(f => f.Picked).ToArray());
        Assert.Equal(32, foes[1].Speed);
        Assert.Equal(2, foes[0].MoveIds.Count);

        var detail = await command.ExecuteAsync("!pokech detail -h 1");
        Assert.Contains("しんそく", detail.Message);
        Assert.Contains("じしん", detail.Message);
        Assert.Contains("選出", detail.Message);
    }

    [Fact]
    public async Task Rival_moves_are_limited_to_four_and_unreadable_names_are_rejected()
    {
        var (command, repo) = await NewAsync();
        await command.ExecuteAsync("!pokech add -r カイリュー");
        Assert.False((await command.ExecuteAsync("!pokech set -r カイリュー しんそく じしん げきりん ばくれつパンチ はかいこうせん")).Success);
        // 5 つ書くとエラーで、1 つも覚えない
        Assert.Empty((await repo.GetAsync()).For(Ds).Rivals.Single().MoveIds);
        Assert.True((await command.ExecuteAsync("!pokech set -r カイリュー しんそく じしん げきりん りゅうのまい")).Success);
        Assert.Equal(4, (await repo.GetAsync()).For(Ds).Rivals.Single().MoveIds.Count);
        // 技として読めない名前は、エラー
        Assert.False((await command.ExecuteAsync("!pokech set -r カイリュー でたらめわざ")).Success);
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
    public async Task Scan_and_other_rival_writes_need_the_mutation_permission()
    {
        var (command, repo) = await NewAsync();
        var viewer = new PokeCommandOptions { AllowMutations = false };
        foreach (var message in new[] { "!pokech scan マニューラ つららおとし フシギバナ 55", "!pokech add -r カイリュー", "!pokech set -r カイリュー D32", "!pokech rm -r カイリュー", "!pokech clear -r", "!pokech end", "!pokech rm -h 1", "!pokech set -m 1 A1" })
        {
            var result = await command.ExecuteAsync(message, viewer);
            Assert.False(result.Success, message);
        }
        Assert.Empty((await repo.GetAsync()).For(Ds).Rivals);
        // 読むだけの ls は誰でも使える
        Assert.True((await command.ExecuteAsync("!pokech ls -r", viewer)).Success);
        Assert.True((await command.ExecuteAsync("!pokech ls -h", viewer)).Success);
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

    [Fact]
    public async Task Rival_can_be_updated_to_its_mega_form_and_picked_rivals_go_to_history()
    {
        var (command, repo) = await NewAsync();
        Assert.True((await command.ExecuteAsync("!pokech add -r リザードン D32 おだやか")).Success);
        // メガシンカしたら、同じ相手のまま姿だけ変える（ポイント・性格はそのまま）
        var mega = await command.ExecuteAsync("!pokech set -r メガリザードンY 選出");
        Assert.True(mega.Success, mega.Message);
        var rivals = (await repo.GetAsync()).For(Ds).Rivals;
        Assert.Single(rivals);
        Assert.Equal("メガリザードンY", rivals[0].PokemonName);
        Assert.Equal(32, rivals[0].SpDefense);
        Assert.Equal("おだやか", rivals[0].Nature);
        Assert.True(rivals[0].Picked);

        // 名前だけの相手も、選出していない相手も、試合終了で全員履歴に移る
        Assert.True((await command.ExecuteAsync("!pokech add -r カイリュー ミミッキュ")).Success);
        Assert.True((await command.ExecuteAsync("!pokech set -r カイリュー 選出")).Success);
        var ended = await command.ExecuteAsync("!pokech end");
        Assert.True(ended.Success, ended.Message);
        var history = (await repo.GetAsync()).For(Ds).History.Single();
        Assert.Equal(new[] { "メガリザードンY", "カイリュー", "ミミッキュ" }, history.Foes.Select(f => f.PokemonName).ToArray());
    }

    [Fact]
    public async Task Name_only_set_changes_the_form_of_a_registered_rival_but_does_nothing_otherwise()
    {
        var (command, repo) = await NewAsync();
        Assert.True((await command.ExecuteAsync("!pokech add -r リザードン D32 おだやか")).Success);
        var mega = await command.ExecuteAsync("!pokech set -r メガリザードンX");
        Assert.True(mega.Success, mega.Message);
        var rival = Assert.Single((await repo.GetAsync()).For(Ds).Rivals);
        Assert.Equal("メガリザードンX", rival.PokemonName);
        Assert.Equal(32, rival.SpDefense);
        // メガシンカ後の姿は、持ち物がメガストーンで確定している
        Assert.Contains("リザードナイトX", mega.Message);
        Assert.Contains("リザードナイトX", (await command.ExecuteAsync("!pokech detail -r メガリザードンX")).Message);

        // 姿が変わらない・登録されていない相手の名前だけでは、何も更新しない
        Assert.False((await command.ExecuteAsync("!pokech set -r メガリザードンX")).Success);
        Assert.False((await command.ExecuteAsync("!pokech set -r カイリュー")).Success);
        Assert.Single((await repo.GetAsync()).For(Ds).Rivals);
    }

    [Fact]
    public async Task Clear_rival_removes_every_rival_without_history_and_rejects_arguments()
    {
        var (command, repo) = await NewAsync();
        Assert.True((await command.ExecuteAsync("!pokech add -r カイリュー D32 おだやか")).Success);
        Assert.True((await command.ExecuteAsync("!pokech set -r カイリュー 選出")).Success);
        // 引数付きでは何も消さない（end と取り違えて全員の登録を消さないため）
        var withMemo = await command.ExecuteAsync("!pokech clear -r テスト戦");
        Assert.False(withMemo.Success);
        Assert.Single((await repo.GetAsync()).For(Ds).Rivals);

        var cleared = await command.ExecuteAsync("!pokech clear -r");
        Assert.True(cleared.Success, cleared.Message);
        var roster = (await repo.GetAsync()).For(Ds);
        Assert.Empty(roster.Rivals);
        Assert.Empty(roster.History);
    }

    [Fact]
    public async Task Mega_defender_holds_its_mega_stone_in_dmg()
    {
        var (command, _) = await NewAsync();
        var dmg = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし メガリザードンX");
        Assert.True(dmg.Success, dmg.Message);
        Assert.Equal("リザードナイトX", dmg.Request!.Defender.Item?.Name);
    }

    [Fact]
    public async Task Removed_calc_and_foe_are_unknown_commands()
    {
        var (command, _) = await NewAsync();
        foreach (var text in new[] { "!pokech calc マニューラ つららおとし フシギバナ 40", "!pokech foe ls" })
        {
            var r = await command.ExecuteAsync(text);
            Assert.False(r.Success);
            Assert.Contains("不明なサブコマンド", r.Message);
        }
    }
}
