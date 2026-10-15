using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>チャットの dmg・calc・faster は、自分（攻撃側）のポケモンを登録済みのものに限る。</summary>
public class RegisteredOnlyTests
{
    private static PokeCommand NewCommand() => new(TestData.Catalog(), new RosterRepository(new MemoryRosterStore()));

    [Fact]
    public async Task Unregistered_attacker_is_rejected_by_dmg_scan_and_faster()
    {
        var cmd = NewCommand();
        var dmg = await cmd.ExecuteAsync("!pokesv dmg バシャーモ かえんほうしゃ ガブリアス");
        Assert.False(dmg.Success);
        Assert.Contains("バシャーモは登録されていません", dmg.Message);
        var calc = await cmd.ExecuteAsync("!pokesv scan バシャーモ かえんほうしゃ ガブリアス 45%");
        Assert.False(calc.Success);
        Assert.Contains("登録されていません", calc.Message);
        var faster = await cmd.ExecuteAsync("!pokesv faster バシャーモ ガブリアス");
        Assert.False(faster.Success);
        Assert.Contains("登録されていません", faster.Message);
    }

    [Fact]
    public async Task Registered_attacker_uses_its_own_ability_and_item_not_usage_defaults()
    {
        var cmd = NewCommand();
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var blaze = data.Pokemon.First(p => p.Name == "バシャーモ");
        var abilities = data.AbilitiesOf(blaze);
        Assert.True(abilities.Count > 1);
        var torch = abilities.First(a => a.Identifier == "blaze");
        // 登録の特性は、画面の登録と同じ ID で入る（ここでは直接、登録データに入れる）
        var store = new MemoryRosterStore();
        var repo = new RosterRepository(store);
        await repo.AddPokemonAsync("Gen9", blaze, new Models.StatSet { SpAttack = 252 }, data.Natures.First(), torch);
        var command = new PokeCommand(TestData.Catalog(), repo);
        var reply = await command.ExecuteAsync("!pokesv dmg バシャーモ かえんほうしゃ ガブリアス");
        Assert.True(reply.Success, reply.Message);
        Assert.Equal("blaze", reply.Request!.Attacker.Ability!.Identifier);
        Assert.Null(reply.Request.Attacker.Item);   // 持ち物は登録のとおり（使用率 1 位の持ち物は入れない）
        // もうかは HP 1/3 以下で効くので、その結果も並べて出す
        Assert.Contains("もうか発動時(HP1/3以下)", reply.Message);
        // ほのお以外の技では、もうかは関係しないので出さない
        var other = await command.ExecuteAsync("!pokesv dmg バシャーモ ブレイズキック ガブリアス");
        Assert.True(other.Success, other.Message);
        var water = await command.ExecuteAsync("!pokesv dmg バシャーモ インファイト ガブリアス");
        Assert.True(water.Success, water.Message);
        Assert.DoesNotContain("もうか発動時", water.Message);
    }

    [Fact]
    public async Task Multi_hit_chat_reply_is_compact_and_crit_is_an_option()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var weavile = data.Pokemon.First(p => p.Name == "マニューラ");
        var repo = new RosterRepository(new MemoryRosterStore());
        await repo.AddPokemonAsync("Champions", weavile, new Models.StatSet { Attack = 32 }, data.Natures.First(), null);
        var command = new PokeCommand(TestData.Catalog(), repo);
        var plain = await command.ExecuteAsync("!pokech dmg マニューラ トリプルアクセル フシギバナ");
        Assert.True(plain.Success, plain.Message);
        var lines = plain.Message.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("結果 1回目:", lines[1]);
        Assert.Contains(" | 2回目:", lines[1]);
        Assert.Contains(" | 3回目:", lines[1]);
        Assert.DoesNotContain("急所", plain.Message);
        Assert.DoesNotContain("効果抜群", plain.Message);
        Assert.False(plain.Message.Contains('\u3000'));   // 全角スペースは使わない
        Assert.Contains("H32B32", lines[0]);           // 防御側は HP と B に全振り
        var crit = await command.ExecuteAsync("!pokech dmg マニューラ トリプルアクセル フシギバナ 急所");
        Assert.StartsWith("結果（急所） 1回目:", crit.Message.Split('\n')[1]);

        // 連続技でなければ回数は付かず、2〜5 回技は 1〜5 回目、スキルリンクは 5 回（スキルリンク）
        var single = await command.ExecuteAsync("!pokech dmg マニューラ つららおとし フシギバナ");
        Assert.True(single.Success, single.Message);
        Assert.StartsWith("結果 ", single.Message.Split('\n')[1]);
        Assert.DoesNotContain("回目", single.Message);

        // おやこあい: 1 回目・2 回目だけで、タグや急所は出さない
        var kanga = data.Pokemon.First(p => p.Name == "メガガルーラ");
        await repo.AddPokemonAsync("Champions", kanga, new Models.StatSet { Attack = 32 }, data.Natures.First(), null);
        var pb = await command.ExecuteAsync("!pokech dmg メガガルーラ ほのおのパンチ フシギバナ");
        Assert.True(pb.Success, pb.Message);
        var pbLine = pb.Message.Split('\n')[1];
        Assert.StartsWith("結果 1回目:", pbLine);
        Assert.Contains(" | 2回目:", pbLine);
        Assert.DoesNotContain("急所", pb.Message);
        Assert.DoesNotContain("[", pb.Message);
    }

    [Fact]
    public async Task Registered_mega_pokemon_holds_its_mega_stone()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var mega = data.Pokemon.First(p => p.IsMega && p.Name.StartsWith("メガメタグロス"));
        var repo = new RosterRepository(new MemoryRosterStore());
        await repo.AddPokemonAsync("Champions", mega, new Models.StatSet { Attack = 32 }, data.Natures.First(), null);
        var command = new PokeCommand(TestData.Catalog(), repo);
        var reply = await command.ExecuteAsync("!pokech dmg メガメタグロス じしん リザードン");
        Assert.True(reply.Success, reply.Message);
        Assert.Equal(data.FindItem(mega.MegaStoneId!.Value)!.Name, reply.Request!.Attacker.Item!.Name);
        Assert.DoesNotContain("ノーマルジュエル", reply.Message);
    }
}

/// <summary>攻撃側の特性が、状態（HP・状態異常・天候）でダメージを変えるときの、チャットの「発動時」の結果。</summary>
public class AttackerAbilityVariantTests
{
    private static async Task<PokeCommand> CommandWith(string dataSet, string pokemonName, string abilityIdentifier, Models.StatSet evs)
    {
        var data = await TestData.Catalog().GetDataSetAsync(dataSet);
        var pokemon = data.Pokemon.First(p => p.Name == pokemonName);
        var ability = data.AbilitiesOf(pokemon).First(a => a.Identifier == abilityIdentifier);
        var repo = new RosterRepository(new MemoryRosterStore());
        await repo.AddPokemonAsync(dataSet, pokemon, evs, data.Natures.First(), ability);
        return new PokeCommand(TestData.Catalog(), repo);
    }

    [Fact]
    public async Task Guts_and_solar_power_variants_are_listed_only_when_they_change_the_damage()
    {
        // こんじょう: ヘラクロス（Gen9）のインファイト。状態異常の結果を出す
        var guts = await CommandWith("Gen9", "ヘラクロス", "guts", new Models.StatSet { Attack = 252 });
        var g = await guts.ExecuteAsync("!pokesv dmg ヘラクロス インファイト ガブリアス");
        Assert.True(g.Success, g.Message);
        Assert.Contains("こんじょう発動時(状態異常)", g.Message);
        // すでに状態異常（やけど）を指定したら、こんじょうの結果は出さない
        var burned = await guts.ExecuteAsync("!pokesv dmg ヘラクロス インファイト ガブリアス やけど");
        Assert.DoesNotContain("こんじょう発動時", burned.Message);

        // サンパワー: リザードン（Gen9）の特殊技。晴れの結果を出す（特殊技のとき）
        var solar = await CommandWith("Gen9", "リザードン", "solar-power", new Models.StatSet { SpAttack = 252 });
        var s = await solar.ExecuteAsync("!pokesv dmg リザードン かえんほうしゃ ガブリアス");
        Assert.True(s.Success, s.Message);
        Assert.Contains("サンパワー発動時(晴れ)", s.Message);
        var physical = await solar.ExecuteAsync("!pokesv dmg リザードン ドラゴンクロー ガブリアス");
        Assert.DoesNotContain("サンパワー発動時", physical.Message);
    }
}

/// <summary>チャンピオンズの実数値（性格補正はポイントを足したあとに掛ける）で、他のダメージ計算ツールの結果と合うこと。</summary>
public class ChampionsNatureOrderTests
{
    [Fact]
    public async Task Blaziken_flare_blitz_against_scarf_charizard_matches_the_public_numbers()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Champions");
        var calc = new Battle.DamageCalculator(data.TypeChart, data.Info.Generation);
        var blaze = data.Pokemon.First(p => p.Name == "バシャーモ");
        var attacker = new Models.PokemonBuild(blaze, data.Info.EvSystem)
        {
            Nature = data.Natures.First(n => n.IncreasedStat == Models.Stat.Attack && n.DecreasedStat == Models.Stat.SpAttack),
            EVs = new Models.StatSet { HP = 2, Attack = 32, Speed = 32 },
            Ability = data.AbilitiesOf(blaze).First(a => a.Identifier == "blaze"),
        };
        var zard = data.Pokemon.First(p => p.Name == "リザードン");
        var defender = new Models.PokemonBuild(zard, data.Info.EvSystem)
        {
            Nature = data.Natures.First(n => n.IncreasedStat == Models.Stat.SpAttack && n.DecreasedStat == Models.Stat.Attack),
            EVs = new Models.StatSet { HP = 32 },
            Ability = data.AbilitiesOf(zard).First(a => a.Identifier == "solar-power"),
            Item = data.Items.First(i => i.Name == "こだわりスカーフ"),
        };
        var move = data.Moves.First(m => m.Name == "フレアドライブ");
        var request = new Models.DamageRequest { Attacker = attacker, Move = move, Defender = defender };
        Assert.Equal(189, Battle.StatCalculator.Calculate(attacker, Models.Stat.Attack));

        var normal = calc.Calculate(request);
        Assert.Equal(185, normal.DefenderHP);
        var low = attacker.Clone();
        low.HpPercent = 33;
        var pinch = calc.Calculate(request.With(attacker: low));
        Assert.Equal((97, 115), (pinch.MinDamage, pinch.MaxDamage));   // もうか発動時
    }
}
