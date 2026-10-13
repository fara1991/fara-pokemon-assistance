using System.Text.Json;
using FaraPokemonBattleAssistant.Core.Battle;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using Xunit;

namespace FaraPokemonBattleAssistant.Core.Tests;

/// <summary>
/// 今のゲーム（剣盾・SV・チャンピオンズ）のダメージ計算で直した不具合の回帰テスト。
/// 数値の細かい一致は CalcVectorTests（@smogon/calc との比較）が見る。ここでは直した内容そのものを確かめる。
/// </summary>
public class CurrentGamesFixTests
{
    private sealed record MoveList(int[] Known, int[] SheerForce, int[] Recoil);

    private static Dictionary<string, MoveList> LoadMoveLists()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FaraPokemonBattleAssistant.sln")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "tests", "FaraPokemonBattleAssistant.Core.Tests", "Data", "move_lists.json");
        return JsonSerializer.Deserialize<Dictionary<string, MoveList>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static Task<PokemonDataSet> Load(string key) => TestData.Catalog().GetDataSetAsync(key);

    private static Ability Ability(PokemonDataSet data, string identifier) => data.Abilities.Single(a => a.Identifier == identifier);

    private static PokemonBuild Build(PokemonDataSet data, int pokemonId, string? ability = null, Item? item = null)
    {
        var b = new PokemonBuild(data.FindPokemon(pokemonId)!, data.Info.EvSystem) { Level = 50, Item = item };
        if (ability is not null) b.Ability = Ability(data, ability);
        return b;
    }

    private static DamageResult Calc(PokemonDataSet data, PokemonBuild attacker, PokemonBuild defender, int moveId, bool critical = false, Weather weather = Weather.None)
    {
        var request = new DamageRequest { Attacker = attacker, Defender = defender, Move = data.FindMove(moveId)!, IsCritical = critical, Weather = weather };
        return new DamageCalculator(data.TypeChart, data.Info.Generation).Calculate(request);
    }

    private static bool Has(DamageResult r, string text) => r.Modifiers.Any(m => m.Contains(text));

    /// <summary>ちからずく・すてみの対象の技を、公開されているダメージ計算ツール（@smogon/calc）と全部の技で照合する（tools/calc_vectors/move_lists.js）。</summary>
    [Theory]
    [InlineData("Gen3")]
    [InlineData("Gen4")]
    [InlineData("Gen5")]
    [InlineData("Gen6")]
    [InlineData("Gen7")]
    [InlineData("Gen8")]
    [InlineData("Gen9")]
    [InlineData("Champions")]
    public async Task Sheer_force_and_recoil_moves_match_public_damage_calculator(string key)
    {
        var data = await Load(key);
        var list = LoadMoveLists()[key];
        var sheer = list.SheerForce.ToHashSet();
        var recoil = list.Recoil.ToHashSet();
        var wrongSheer = new List<string>();
        var wrongRecoil = new List<string>();
        foreach (var id in list.Known)
        {
            var move = data.FindMove(id)!;
            if (!move.IsDamaging) continue;
            if (data.Info.Generation >= 5 && MoveEffects.SheerForceBoosts(move, data.Info.Generation) != sheer.Contains(id))
                wrongSheer.Add($"{move.Name}({id})");
            if (data.Info.Generation >= 4 && MoveEffects.IsRecoil(move) != recoil.Contains(id))
                wrongRecoil.Add($"{move.Name}({id})");
        }
        Assert.True(wrongSheer.Count == 0, $"{key} ちからずく: " + string.Join(", ", wrongSheer));
        Assert.True(wrongRecoil.Count == 0, $"{key} すてみ: " + string.Join(", ", wrongRecoil));
    }

    [Theory]
    [InlineData("Gen8")]
    [InlineData("Gen9")]
    [InlineData("Champions")]
    public async Task Sheer_force_does_not_boost_moves_without_secondary_effect(string key)
    {
        var data = await Load(key);
        const int closeCombat = 370, dracoMeteor = 434, flamethrower = 53;
        var attacker = Build(data, 445, "sheer-force"); // ガブリアス
        var target = Build(data, 6);                    // リザードン
        Assert.False(Has(Calc(data, attacker, target, closeCombat), "ちからずく"));
        Assert.False(Has(Calc(data, attacker, target, dracoMeteor), "ちからずく"));
        Assert.True(Has(Calc(data, attacker, target, flamethrower), "ちからずく"));
    }

    [Fact]
    public async Task Dynamax_moves_get_no_move_specific_boosts()
    {
        // ダイマックスすると技がダイマックス技に置き換わるので、ちからずく・すてみ・はたきおとす・ソーラービームの補正は掛からない
        var data = await Load("Gen8");
        var leftovers = data.Items.First(i => i.Name == "たべのこし");
        var target = Build(data, 6, item: leftovers);
        var sheer = Build(data, 445, "sheer-force");
        var reckless = Build(data, 445, "reckless");
        var plain = Build(data, 445);
        foreach (var a in new[] { sheer, reckless, plain }) a.IsDynamax = true;
        Assert.False(Has(Calc(data, sheer, target, 53), "ちからずく"));
        Assert.False(Has(Calc(data, reckless, target, 413), "すてみ"));
        Assert.False(Has(Calc(data, plain, target, 282), "はたきおとす"));
        Assert.False(Has(Calc(data, plain, target, 76, weather: Weather.Rain), "ソーラービーム"));
        // ダイマックスしていなければ掛かる
        plain.IsDynamax = false;
        Assert.True(Has(Calc(data, plain, target, 282), "はたきおとす"));
        Assert.True(Has(Calc(data, plain, target, 76, weather: Weather.Rain), "あめ") || Has(Calc(data, plain, target, 76, weather: Weather.Rain), "雨"));
    }

    [Theory]
    [InlineData("Gen8")]
    [InlineData("Gen9")]
    public async Task Reckless_boosts_only_recoil_moves(string key)
    {
        var data = await Load(key);
        const int braveBird = 413, aerialAce = 332, highJumpKick = 136;
        var attacker = Build(data, 445, "reckless"); // ガブリアス
        var target = Build(data, 6);
        var boosted = Calc(data, attacker, target, braveBird);
        Assert.True(Has(boosted, "すてみ"));
        Assert.True(Has(Calc(data, attacker, target, highJumpKick), "すてみ"));
        Assert.False(Has(Calc(data, attacker, target, aerialAce), "すてみ"));
        // 特性が無ければ 1.2 倍にならない
        var plain = Calc(data, Build(data, 445, "intimidate"), target, braveBird);
        Assert.True(boosted.Rolls.Max() > plain.Rolls.Max());
    }

    [Theory]
    [InlineData("Gen8")]
    [InlineData("Gen9")]
    [InlineData("Champions")]
    public async Task Knock_off_is_boosted_only_when_the_target_holds_an_item(string key)
    {
        var data = await Load(key);
        const int knockOff = 282;
        var leftovers = data.Items.First(i => i.Name == "たべのこし");
        var attacker = Build(data, 445);
        var withItem = Calc(data, attacker, Build(data, 6, item: leftovers), knockOff);
        var without = Calc(data, attacker, Build(data, 6), knockOff);
        Assert.True(Has(withItem, "はたきおとす"));
        Assert.False(Has(without, "はたきおとす"));
        Assert.True(withItem.Rolls.Max() > without.Rolls.Max());
    }

    [Fact]
    public async Task Knock_off_is_not_boosted_against_own_mega_stone()
    {
        var data = await Load("Champions");
        var stone = data.Items.First(i => i.IsMegaStone && data.Pokemon.Any(p => p.SpeciesId == i.MegaSpeciesId && p.IsMega));
        var mega = data.Pokemon.First(p => p.SpeciesId == stone.MegaSpeciesId && p.IsMega);
        var other = data.Items.First(i => i.IsMegaStone && i.MegaSpeciesId != stone.MegaSpeciesId);
        var attacker = Build(data, 445);
        Assert.False(Has(Calc(data, attacker, new PokemonBuild(mega, data.Info.EvSystem) { Level = 50, Item = stone }, 282), "はたきおとす"));
        Assert.True(Has(Calc(data, attacker, new PokemonBuild(mega, data.Info.EvSystem) { Level = 50, Item = other }, 282), "はたきおとす"));
    }

    [Fact]
    public async Task Sniper_and_burn_are_applied_in_the_public_calculators_order()
    {
        // スナイパーの 1.5 倍は急所の 1.5 倍とは別（最終補正）、やけどは相性を掛けたあとに切り捨てて半分にする。
        // 期待値は @smogon/calc（gen9）で出したもの（ムクホークのブレイブバード急所 → ガブリアス、Lv50・無振り・性格補正なし）。
        var data = await Load("Gen9");
        const int braveBird = 413;
        var defender = Build(data, 445);
        int[] Rolls(string ability, StatusCondition status)
        {
            var attacker = Build(data, 398, ability);
            attacker.Status = status;
            return Calc(data, attacker, defender, braveBird, critical: true).Rolls;
        }
        Assert.Equal(new[] { 189, 190, 193, 195, 198, 199, 202, 204, 207, 208, 211, 213, 216, 217, 220, 222 }, Rolls("sniper", StatusCondition.None));
        Assert.Equal(new[] { 126, 127, 129, 130, 132, 133, 135, 136, 138, 139, 141, 142, 144, 145, 147, 148 }, Rolls("intimidate", StatusCondition.None));
        Assert.Equal(new[] { 94, 94, 96, 97, 99, 99, 100, 102, 103, 103, 105, 106, 108, 108, 109, 111 }, Rolls("sniper", StatusCondition.Burn));
        Assert.Equal(new[] { 63, 63, 64, 65, 66, 66, 67, 68, 69, 69, 70, 71, 72, 72, 73, 74 }, Rolls("intimidate", StatusCondition.Burn));
    }

    [Fact]
    public async Task Snow_boosts_ice_type_defense_only_in_generation_9()
    {
        // ソード・シールドの「あられ」にはこおりタイプの防御上昇が無い。第 9 世代の「雪」だけ 1.5 倍。
        foreach (var (key, boosted) in new[] { ("Gen8", false), ("Gen9", true), ("Champions", true) })
        {
            var data = await Load(key);
            var attacker = Build(data, 445);
            var iceDefender = Build(data, data.Pokemon.First(p => p.HasType("Ice") && !p.IsProvisional).Id);
            var clear = Calc(data, attacker, iceDefender, 89);
            var snow = Calc(data, attacker, iceDefender, 89, weather: Weather.Snow);
            Assert.Equal(boosted, Has(snow, "こおりタイプのB1.5倍"));
            Assert.Equal(boosted, snow.Rolls.Max() < clear.Rolls.Max());
            if (!boosted) Assert.Equal(clear.Rolls, snow.Rolls);
        }
    }

    [Fact]
    public async Task Evolution_stone_flag_depends_on_the_game()
    {
        // 後の世代で進化先が増えたポケモンは、その進化先がいないゲームでは進化前扱いにならない
        var swsh = await Load("Gen8");
        var sv = await Load("Gen9");
        var gen7 = await Load("Gen7");
        // ノコッチ（→ノココッチは第 9 世代）、キリキザン（→ドドゲザンは第 9 世代）、ジュラルドン（→ブリジュラスは第 9 世代）
        foreach (var id in new[] { 206, 625, 884 })
        {
            Assert.False(swsh.FindPokemon(id)!.NotFullyEvolved, swsh.FindPokemon(id)!.Name);
            Assert.True(sv.FindPokemon(id)!.NotFullyEvolved, sv.FindPokemon(id)!.Name);
        }
        // オコリザル（→コノヨザルは第 9 世代）は、第 9 世代では進化前、コノヨザルは最終進化
        Assert.True(sv.FindPokemon(57)!.NotFullyEvolved);
        Assert.False(sv.FindPokemon(979)!.NotFullyEvolved);
        Assert.False(gen7.FindPokemon(57)!.NotFullyEvolved);
        // 変わらないもの
        Assert.True(swsh.FindPokemon(4)!.NotFullyEvolved);   // ヒトカゲ
        Assert.False(swsh.FindPokemon(6)!.NotFullyEvolved);  // リザードン
    }
}
