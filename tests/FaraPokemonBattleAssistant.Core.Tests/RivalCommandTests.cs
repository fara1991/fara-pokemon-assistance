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
        // 返信にも、防御側の持ち物（メガストーン）が出る
        Assert.Matches(@"→ メガリザードンX\([^)]*リザードナイトX[^)]*\)", dmg.Message);
        // メガシンカしない防御側は、持ち物が不明なら何も出ない（使用率の持ち物は入れない。持ち物なしとして計算する）
        var plain = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし リザードン");
        Assert.True(plain.Success, plain.Message);
        Assert.Null(plain.Request!.Defender.Item);
        Assert.Matches(@"→ リザードン\([^)]*\)", plain.Message);
        // 使用率の持ち物（その名前）も、防御側の表記に出さない
        var usageItem = (await TestData.Catalog().GetDataSetAsync(Ds)).GetUsageAsync(Models.BattleFormat.Singles).Result.TopItem(
            (await TestData.Catalog().GetDataSetAsync(Ds)).Pokemon.First(p => p.Name == "リザードン").Id);
        if (usageItem is not null) Assert.DoesNotContain(usageItem.Name, plain.Message.Split('\n')[0].Split('→')[1]);
        // 書いた持ち物は、計算にも返信にも反映する
        var held = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし リザードン 防:たべのこし");
        Assert.True(held.Success, held.Message);
        Assert.Equal("たべのこし", held.Request!.Defender.Item?.Name);
        Assert.Contains("たべのこし", held.Message.Split('\n')[0].Split('→')[1]);
        var noItem = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし リザードン 防:持ち物なし");
        Assert.Null(noItem.Request!.Defender.Item);
        Assert.DoesNotMatch(@"→ リザードン\([^)]*たべのこし", noItem.Message);
    }

    [Fact]
    public async Task Scan_uses_the_written_item_in_the_estimate_and_keeps_it_in_the_basis()
    {
        var (command, repo) = await NewAsync();
        var probe = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ H0 B10");
        Assert.True(probe.Success, probe.Message);
        var observed = (int)Math.Round((probe.Damage!.MinDamage + probe.Damage.MaxDamage) / 2.0);
        var plain = await command.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observed}");
        Assert.True(plain.Success, plain.Message);
        // 同じ B10 の相手が、ヤチェのみ（こおり技の効果抜群を半減）を持っているときの受けるダメージ
        var probeBerry = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ H0 B10 防:ヤチェのみ");
        Assert.True(probeBerry.Success, probeBerry.Message);
        var observedBerry = (int)Math.Round((probeBerry.Damage!.MinDamage + probeBerry.Damage.MaxDamage) / 2.0);
        Assert.True(observedBerry < observed);
        var (command2, repo2) = await NewAsync();
        var berry = await command2.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observedBerry} 防:ヤチェのみ");
        Assert.True(berry.Success, berry.Message);
        // 持ち物を書けば、持ち物なしのときと同じ B の範囲に推定される（持ち物が推定に届いている）
        var plainBands = (await repo.GetAsync()).For(Ds).Rivals.Single().Scans.Single().Bands.Select(x => (x.MinEv, x.MaxEv)).ToList();
        var berryBands = (await repo2.GetAsync()).For(Ds).Rivals.Single().Scans.Single().Bands.Select(x => (x.MinEv, x.MaxEv)).ToList();
        Assert.Equal(plainBands, berryBands);
        // 持ち物は保存されないので、推定の根拠に残し、dmg でも書くよう案内する
        Assert.Contains("防:ヤチェのみ", berry.Message);
        Assert.Contains("防:ヤチェのみ", (await command2.ExecuteAsync("!pokech detail -r フシギバナ")).Message);
    }

    [Fact]
    public async Task Range_line_shows_the_item_that_the_calculation_used()
    {
        var (command, _) = await NewAsync();
        var probe = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ H0 B10");
        var observed = (int)Math.Round((probe.Damage!.MinDamage + probe.Damage.MaxDamage) / 2.0);
        var plainScan = await command.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observed}");
        Assert.True(plainScan.Success, plainScan.Message);
        Assert.Contains("持ち物: 不明", plainScan.Message);
        // scan でも、書いた持ち物（防:たべのこし）は捨てずに、その持ち物として推定する
        var heldScan = await command.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observed} 防:たべのこし");
        Assert.True(heldScan.Success, heldScan.Message);
        Assert.Equal("たべのこし", heldScan.Request!.Defender.Item?.Name);
        Assert.Contains("持ち物: たべのこし", heldScan.Message);
        Assert.Contains("防:たべのこし を書いてください", heldScan.Message);
        Assert.DoesNotContain("持ち物: 不明", heldScan.Message);

        // 範囲の相手の 1 行目にも、書いた持ち物が出る。持ち物なしを書けば出ない
        var held = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ 防:たべのこし");
        Assert.True(held.Success, held.Message);
        Assert.Contains("B最小想定", held.Message);
        Assert.Equal("たべのこし", held.Request!.Defender.Item?.Name);
        Assert.Contains("たべのこし", held.Message.Split('\n')[0].Split('→')[1]);
        var none = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ 防:持ち物なし");
        Assert.Null(none.Request!.Defender.Item);
        Assert.DoesNotContain("たべのこし", none.Message.Split('\n')[0].Split('→')[1]);
    }

    [Theory]
    [InlineData("防:")]
    [InlineData("防：")]
    [InlineData("防御:")]
    [InlineData("防御：")]
    [InlineData("ぼうぎょ:")]
    [InlineData("ぼうぎょ：")]
    [InlineData("B:")]
    [InlineData("B：")]
    [InlineData("d:")]
    [InlineData("D：")]
    [InlineData("def:")]
    public async Task Defender_side_prefix_accepts_many_spellings(string prefix)
    {
        var (command, _) = await NewAsync();
        var reply = await command.ExecuteAsync($"!pokech dmg マニューラ つららおとし リザードン {prefix}たべのこし");
        Assert.True(reply.Success, reply.Message);
        Assert.Equal("たべのこし", reply.Request!.Defender.Item?.Name);
        Assert.Contains("たべのこし", reply.Message.Split('\n')[0].Split('→')[1]);
        Assert.NotEqual("たべのこし", reply.Request.Attacker.Item?.Name);
    }

    [Theory]
    [InlineData("攻:")]
    [InlineData("攻：")]
    [InlineData("攻撃:")]
    [InlineData("攻撃：")]
    [InlineData("こうげき:")]
    [InlineData("こうげき：")]
    [InlineData("A:")]
    [InlineData("C：")]
    [InlineData("atk:")]
    public async Task Attacker_side_prefix_accepts_many_spellings(string prefix)
    {
        var (command, _) = await NewAsync();
        // たべのこしは防御側に付くのが既定。攻撃側の前置きで、攻撃側に付く
        var reply = await command.ExecuteAsync($"!pokech dmg マニューラ つららおとし リザードン {prefix}たべのこし");
        Assert.True(reply.Success, reply.Message);
        Assert.Equal("たべのこし", reply.Request!.Attacker.Item?.Name);
        Assert.NotEqual("たべのこし", reply.Request.Defender.Item?.Name);
    }

    [Fact]
    public async Task Scan_faster_and_dmg_replies_put_the_input_on_the_first_line_and_the_result_after()
    {
        var (command, _) = await NewAsync();
        var probe = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ H0 B10");
        var observed = (int)Math.Round((probe.Damage!.MinDamage + probe.Damage.MaxDamage) / 2.0);

        var scan = (await command.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observed}")).Message.Split('\n');
        Assert.Contains("→ フシギバナ（", scan[0]);
        Assert.Contains($"{observed}ダメージ", scan[0]);
        Assert.StartsWith("結果 フシギバナB実数値:", scan[1]);
        // 性格が不明なときは、3 つの補正の範囲が出る
        Assert.Contains("B上昇補正", scan[2]);
        Assert.Contains("B補正無し", scan[2]);
        Assert.Contains("B下降補正", scan[2]);
        (int Lo, int Hi) StatOf(string line)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"実数値: (\d+)(?:〜(\d+))?");
            Assert.True(m.Success, line);
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value));
        }
        var unknownStat = StatOf(scan[1]);

        // 性格が分かると、その補正の範囲だけを出し、結果の行に B↑ のように出す。実数値の範囲も、その補正の分だけに絞る
        Assert.True((await command.ExecuteAsync("!pokech set -r フシギバナ ずぶとい")).Success);
        var known = (await command.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observed}")).Message.Split('\n');
        Assert.Contains("(B↑)", known[1]);
        Assert.Contains("B上昇補正", known[2]);
        Assert.DoesNotContain("B下降補正", known[2]);
        Assert.DoesNotContain("B補正無し", known[2]);
        var knownStat = StatOf(known[1]);
        Assert.True(knownStat.Lo >= unknownStat.Lo && knownStat.Hi <= unknownStat.Hi);
        Assert.True(knownStat.Hi - knownStat.Lo < unknownStat.Hi - unknownStat.Lo);

        // 割合（%）で scan したときは、HP の範囲も 3 行目の末尾に出る
        var (command2, _) = await NewAsync();
        var percent = (await command2.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {Math.Round(observed * 100.0 / probe.Damage!.DefenderHP)}%")).Message.Split('\n');
        Assert.Contains(" | H: ", percent[2]);

        // 当てはまる振り方がないダメージのときは、2 行目に理由が出る
        var notFound = (await command.ExecuteAsync($"!pokech scan マニューラ つららおとし フシギバナ {observed * 3}")).Message.Split('\n');
        Assert.StartsWith("結果 ", notFound[1]);
        Assert.Contains("見つかりません", notFound[1]);

        var faster = (await command.ExecuteAsync("!pokech faster マニューラ フシギバナ")).Message.Split('\n');
        Assert.Contains(" vs フシギバナ", faster[0]);
        Assert.StartsWith("結果 S", faster[1]);

        var dmg = (await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ")).Message.Split('\n');
        Assert.Contains("→ フシギバナ", dmg[0]);
        Assert.StartsWith("結果", dmg[1]);
    }

    [Fact]
    public async Task Registered_mega_rival_keeps_its_mega_stone_and_explicit_items_win()
    {
        var (command, _) = await NewAsync();
        // 数値が分かっている相手（登録済み）でも、メガシンカ後の姿は持ち物がメガストーン
        Assert.True((await command.ExecuteAsync("!pokech add -r メガリザードンX D32 おだやか")).Success);
        var known = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし メガリザードンX");
        Assert.True(known.Success, known.Message);
        Assert.Equal("リザードナイトX", known.Request!.Defender.Item?.Name);
        // 持ち物を書いたときは、そちらを優先する
        var none = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし メガリザードンX 防:持ち物なし");
        Assert.True(none.Success, none.Message);
        Assert.Null(none.Request!.Defender.Item);
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
