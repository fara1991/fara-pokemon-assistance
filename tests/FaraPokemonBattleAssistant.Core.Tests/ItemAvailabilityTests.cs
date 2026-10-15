using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>そのゲームにない持ち物は選べない・指定しても使われない。</summary>
public class ItemAvailabilityTests
{
    private static readonly string[] NotInChampions =
    {
        "こだわりハチマキ", "こだわりメガネ", "とつげきチョッキ", "パワーリスト", "パワーベルト", "パワーレンズ", "パワーバンド",
        "パワーアンクル", "パワーウエイト", "ひのたまプレート", "しんかのきせき", "ブーストエナジー", "きょうせいギプス", "スピードパウダー",
    };

    [Fact]
    public async Task Champions_items_only_include_items_in_the_game()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        foreach (var name in NotInChampions)
            Assert.DoesNotContain(data.Items, i => i.Name == name);
        foreach (var name in new[] { "こだわりスカーフ", "いのちのたま", "きあいのタスキ", "たべのこし", "ゴツゴツメット", "オボンのみ", "ひかりのこな" })
            Assert.Contains(data.Items, i => i.Name == name);
        // 持ち物の一覧は tools/champions_items.txt（出典つき）。メガストーン以外は、一覧と完全に一致する
        var listed = File.ReadAllLines(Path.Combine(TestData.RepositoryRoot, "tools", "champions_items.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();
        var actual = data.Items.Where(i => !i.IsMegaStone).Select(i => i.Name).ToHashSet();
        Assert.Equal(listed.OrderBy(x => x), actual.OrderBy(x => x));
    }

    [Fact]
    public async Task Gen9_still_has_all_items()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        Assert.Contains(data.Items, i => i.Name == "こだわりハチマキ");
        Assert.Contains(data.Items, i => i.Name == "パワーリスト");
    }

    [Fact]
    public async Task Item_not_in_the_game_is_ignored_with_guidance()
    {
        var cmd = new PokeCommand(TestData.Catalog(), new RosterRepository(new MemoryRosterStore()));
        // dmg の攻撃側は登録済みのポケモンだけ使える
        await cmd.ExecuteAsync("!pokech add -p ガブリアス 4 32 0 0 0 30 ようき");
        await cmd.ExecuteAsync("!pokesv add -p ガブリアス 4 252 0 0 0 252 ようき");
        var champions = await cmd.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン こだわりハチマキ");
        Assert.True(champions.Success, champions.Message);
        Assert.Contains("ポケモンチャンピオンズにない持ち物のため無視", champions.Message);
        Assert.DoesNotContain("こだわりハチマキ)", champions.Message);

        // 打ち間違いは今までどおりの案内
        var typo = await cmd.ExecuteAsync("!pokech dmg ガブリアス じしん リザードン ほげほげ");
        Assert.Contains("「ほげほげ」は無視", typo.Message);

        // SV にはあるので、そのまま使われる
        var sv = await cmd.ExecuteAsync("!pokesv dmg ガブリアス じしん リザードン こだわりハチマキ");
        Assert.DoesNotContain("にない持ち物", sv.Message);
    }

    [Fact]
    public async Task Team_item_not_in_the_game_is_rejected()
    {
        var cmd = new PokeCommand(TestData.Catalog(), new RosterRepository(new MemoryRosterStore()));
        Assert.True((await cmd.ExecuteAsync("!pokech add -p ガブリアス 4 32 0 0 0 30 ようき")).Success);
        var r = await cmd.ExecuteAsync("!pokech add -t 1 1 こだわりハチマキ");
        Assert.False(r.Success);
        Assert.Contains("ポケモンチャンピオンズにありません", r.Message);
    }

    [Fact]
    public async Task Scarf_reference_row_follows_the_games_items()
    {
        var champions = await TestData.Catalog().GetDataSetAsync("Champions");
        var gen3 = await TestData.Catalog().GetDataSetAsync("Gen3");
        var garchomp = champions.Pokemon.First(p => p.Name == "ガブリアス");
        var withScarf = SpeedCalculator.OpponentReference(garchomp, champions.Info.EvSystem, champions.Items.FirstOrDefault(i => i.Id == 264));
        Assert.Contains(withScarf, l => l.Label == "スカーフ最速");
        // こだわりスカーフが無いゲームでは「スカーフ最速」を出さない
        Assert.Null(gen3.Items.FirstOrDefault(i => i.Id == 264));
        var without = SpeedCalculator.OpponentReference(garchomp, champions.Info.EvSystem, null);
        Assert.DoesNotContain(without, l => l.Label == "スカーフ最速");
    }
}
