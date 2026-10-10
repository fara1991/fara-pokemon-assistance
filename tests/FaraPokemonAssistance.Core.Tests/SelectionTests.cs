using FaraPokemonAssistance.Core.Battle;
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
using FaraPokemonAssistance.Core.Text;
using Xunit;

namespace FaraPokemonAssistance.Core.Tests;

public class DefaultAbilityTests
{
    private static Ability A(int id, string identifier) => new() { Id = id, Identifier = identifier, Name = identifier };

    [Fact]
    public void Field_setting_ability_beats_usage_top()
    {
        var innerFocus = A(39, "inner-focus");
        var psychicSurge = A(227, "psychic-surge");
        var abilities = new[] { innerFocus, A(28, "synchronize"), psychicSurge };
        Assert.Same(psychicSurge, AbilityEffects.ChooseDefault(abilities, innerFocus));
    }

    [Fact]
    public void Offensive_ability_beats_usage_top()
    {
        var thickFat = A(47, "thick-fat");
        var hugePower = A(37, "huge-power");
        Assert.Same(hugePower, AbilityEffects.ChooseDefault(new[] { thickFat, hugePower }, thickFat));
        var sheerForce = A(125, "sheer-force");
        Assert.Same(sheerForce, AbilityEffects.ChooseDefault(new[] { A(1, "intimidate"), sheerForce }, null));
    }

    [Fact]
    public void Notable_usage_top_and_plain_abilities_are_kept()
    {
        var technician = A(101, "technician");
        var skillLink = A(92, "skill-link");
        // 使用率 1 位がもともと火力の上がる特性ならそのまま
        Assert.Same(technician, AbilityEffects.ChooseDefault(new[] { skillLink, technician }, technician));
        // どれも当てはまらなければ使用率 1 位、それも無ければ第 1 特性
        var roughSkin = A(24, "rough-skin");
        var sandVeil = A(8, "sand-veil");
        Assert.Same(roughSkin, AbilityEffects.ChooseDefault(new[] { sandVeil, roughSkin }, roughSkin));
        Assert.Same(sandVeil, AbilityEffects.ChooseDefault(new[] { sandVeil, roughSkin }, null));
    }

    [Theory]
    [InlineData("huge-power", true)]
    [InlineData("adaptability", true)]
    [InlineData("pixilate", true)]
    [InlineData("sharpness", true)]
    [InlineData("blaze", false)]
    [InlineData("intimidate", false)]
    [InlineData("multiscale", false)]
    public void Offensive_abilities(string identifier, bool expected) =>
        Assert.Equal(expected, AbilityEffects.IsOffensive(identifier));

    [Fact]
    public async Task Indeedee_defaults_to_psychic_surge_and_expanding_force_tops_the_list()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var usage = await data.GetUsageAsync(BattleFormat.Doubles);
        var indeedee = data.Pokemon.First(p => p.Name == "イエッサン(♂)");
        var ability = usage.DefaultAbility(indeedee);
        Assert.Equal("psychic-surge", ability?.Identifier);
        Assert.Equal(Terrain.Psychic, FieldEffects.TerrainFromAbility(ability?.Identifier));

        var attacker = new PokemonBuild(indeedee) { Ability = ability, EVs = new StatSet { SpAttack = 252 } };
        var moves = data.LearnableMoves(indeedee.Id).Where(m => m.IsDamaging).ToList();
        var sorted = new FaraPokemonAssistance.Core.Battle.DamageCalculator(data.TypeChart).SortByDamage(new DamageRequest
        {
            Attacker = attacker,
            Move = moves[0],
            Defender = FaraPokemonAssistance.Core.Battle.DamageCalculator.NeutralDefender(EvSystem.Classic),
            Terrain = Terrain.Psychic,
        }, moves);
        Assert.Equal("ワイドフォース", sorted[0].Name);
    }
}

public class ExactHpTests
{
    private static Pokemon Mon() =>
        new() { Id = 1, Name = "テスト", Type1 = "Normal", BaseStats = new StatSet(80, 100, 100, 100, 100, 100), SpeciesId = 1 };

    [Fact]
    public void Every_hp_value_is_reachable_and_percent_follows()
    {
        var build = new PokemonBuild(Mon());
        var max = build.MaxHp; // H80 無振り Lv50 → 155
        for (var hp = 1; hp <= max; hp++)
        {
            build.CurrentHp = hp;
            Assert.Equal(hp, build.CurrentHp);
            Assert.InRange(build.HpPercent, 1, 100);
            Assert.Equal(hp == max, build.IsFullHp);
            Assert.Equal(hp == max, build.HpPercent == 100);
        }
    }

    [Fact]
    public void Percent_clears_exact_value_and_clone_keeps_it()
    {
        var build = new PokemonBuild(Mon());
        build.CurrentHp = build.MaxHp - 1;
        Assert.Equal(99, build.HpPercent);
        var clone = build.Clone();
        Assert.Equal(build.MaxHp - 1, clone.CurrentHp);
        Assert.Contains($"HP{build.MaxHp - 1}/{build.MaxHp}", clone.DescribeShort());

        build.HpPercent = 50;
        Assert.False(build.HasExactHp);
        Assert.Equal(build.MaxHp * 50 / 100, build.CurrentHp);
        build.CurrentHp = 9999;
        Assert.True(build.IsFullHp);
        Assert.Equal(100, build.HpPercent);
    }

    [Fact]
    public async Task Exact_hp_drives_multiscale_and_knockout_start()
    {
        var data = await TestData.Catalog().GetDataSetAsync("Gen9");
        var calc = new FaraPokemonAssistance.Core.Battle.DamageCalculator(data.TypeChart);
        var dragonite = data.Pokemon.First(p => p.Name == "カイリュー");
        var multiscale = data.AbilitiesOf(dragonite).First(a => a.Identifier == "multiscale");
        var attacker = new PokemonBuild(data.Pokemon.First(p => p.Name == "ガブリアス")) { EVs = new StatSet { Attack = 252 } };
        var move = data.Moves.First(m => m.Name == "ストーンエッジ");
        var full = new PokemonBuild(dragonite) { Ability = multiscale };
        var almost = full.Clone();
        almost.CurrentHp = almost.MaxHp - 1;

        var a = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = full });
        var b = calc.Calculate(new DamageRequest { Attacker = attacker, Move = move, Defender = almost });
        Assert.Contains(a.Modifiers, m => m.Contains("HP満タン"));
        Assert.DoesNotContain(b.Modifiers, m => m.Contains("HP満タン"));
        Assert.Contains(b.Modifiers, m => m.Contains($"防御側の残りHP {almost.MaxHp - 1}/{almost.MaxHp}"));
    }
}

public class KanaSearchTests
{
    [Theory]
    [InlineData("イエッサン(♂)", "ie")]
    [InlineData("イエッサン(♂)", "iessan")]
    [InlineData("イエッサン(♂)", "ies")]
    [InlineData("イエッサン(♂)", "いえ")]
    [InlineData("イエッサン(♂)", "イエ")]
    [InlineData("リザードン", "riza-don")]
    [InlineData("リザードン", "rizadon")]
    [InlineData("メガリザードンX", "x")]
    [InlineData("メガリザードンX", "ＲＩＺＡ")]
    [InlineData("シャンデラ", "shanndera")]
    [InlineData("シャンデラ", "syandera")]
    [InlineData("チルタリス", "chiru")]
    [InlineData("チルタリス", "tiru")]
    [InlineData("ツンベアー", "tsun")]
    [InlineData("ツンベアー", "tun")]
    [InlineData("フシギダネ", "husigi")]
    [InlineData("ジャローダ", "jaro")]
    [InlineData("ジャローダ", "zya")]
    [InlineData("キョジオーン", "kyoji")]
    [InlineData("サーフゴー", "sa-hu")]
    [InlineData("パッチルドン", "patti")]
    [InlineData("パッチルドン", "pacchi")]
    [InlineData("ドオー", "doo")]
    [InlineData("ガブリアス", "gab")]
    [InlineData("ウォッシュロトム", "wosshu")]
    public void Matches(string text, string query) => Assert.True(KanaSearch.Matches(text, query), $"{text} / {query}");

    [Theory]
    [InlineData("イエッサン(♂)", "ia")]
    [InlineData("リザードン", "rizo")]
    [InlineData("ガブリアス", "gabo")]
    [InlineData("ガブリアス", "kab")]
    public void Does_not_match(string text, string query) => Assert.False(KanaSearch.Matches(text, query), $"{text} / {query}");

    [Theory]
    [InlineData("ie", "いえ", "")]
    [InlineData("kya", "きゃ", "")]
    [InlineData("shi", "し", "")]
    [InlineData("si", "し", "")]
    [InlineData("tsu", "つ", "")]
    [InlineData("tu", "つ", "")]
    [InlineData("fu", "ふ", "")]
    [InlineData("ji", "じ", "")]
    [InlineData("zi", "じ", "")]
    [InlineData("kanna", "かんな", "")]
    [InlineData("konnichiha", "こんにちは", "")]
    [InlineData("hon", "ほ", "n")]
    [InlineData("honda", "ほんだ", "")]
    [InlineData("kitte", "きって", "")]
    [InlineData("matcha", "まっちゃ", "")]
    [InlineData("ra-men", "らーめ", "n")]
    [InlineData("ky", "", "ky")]
    public void Romaji_to_hiragana(string romaji, string kana, string rest)
    {
        var (k, r) = KanaSearch.RomajiToHiragana(romaji);
        Assert.Equal(kana, k);
        Assert.Equal(rest, r);
    }

    [Fact]
    public void Empty_query_matches_everything() => Assert.True(KanaSearch.Matches("ピカチュウ", " "));
}

public class NatureSelectionTests
{
    private static async Task<IReadOnlyList<Nature>> Natures() => (await TestData.Catalog().GetDataSetAsync("Gen9")).Natures;

    [Fact]
    public async Task One_click_picks_a_nature()
    {
        var natures = await Natures();
        var neutral = NatureSelection.Neutral(natures);
        Assert.Equal("まじめ", neutral?.Name);
        // 補正なしから「上昇: 素早さ」→ 物理なら特攻を下げる（ようき）、特殊なら攻撃を下げる（おくびょう）
        Assert.Equal("ようき", NatureSelection.WithIncreased(natures, neutral, Stat.Speed, physical: true)?.Name);
        Assert.Equal("おくびょう", NatureSelection.WithIncreased(natures, neutral, Stat.Speed, physical: false)?.Name);
        // 「下降: 攻撃」→ 特攻を上げる（ひかえめ）
        Assert.Equal("ひかえめ", NatureSelection.WithDecreased(natures, neutral, Stat.Attack)?.Name);
    }

    [Fact]
    public async Task Changing_one_side_keeps_the_other()
    {
        var natures = await Natures();
        var jolly = natures.First(n => n.Name == "ようき");
        // ようき（S↑C↓）で上昇を攻撃に → いじっぱり（A↑C↓）
        var adamant = NatureSelection.WithIncreased(natures, jolly, Stat.Attack);
        Assert.Equal("いじっぱり", adamant?.Name);
        // いじっぱりで上昇を特攻に（下降と同じ）→ 下降を攻撃に入れ替えて ひかえめ
        Assert.Equal("ひかえめ", NatureSelection.WithIncreased(natures, adamant, Stat.SpAttack)?.Name);
        // 下降を素早さに → ゆうかん（A↑S↓）
        Assert.Equal("ゆうかん", NatureSelection.WithDecreased(natures, adamant, Stat.Speed)?.Name);
        // 「なし」で補正なし
        Assert.True(NatureSelection.WithDecreased(natures, adamant, null)?.IsNeutral);
    }
}

public class SpeedConditionTests
{
    private static Pokemon Mon(int speed) =>
        new() { Id = 1, Name = "テスト", Type1 = "Grass", BaseStats = new StatSet(100, 100, 100, 100, 100, speed), SpeciesId = 1 };

    private static PokemonBuild Build(string ability, int speed = 100) =>
        new(Mon(speed)) { Ability = new Ability { Id = 1, Identifier = ability, Name = ability } };

    [Fact]
    public void Chlorophyll_doubles_in_sun_and_is_shown_as_potential_otherwise()
    {
        var build = Build("chlorophyll");
        var plain = StatCalculator.Calculate(build, Stat.Speed);
        Assert.Equal(plain * 2, SpeedCalculator.Effective(build, Weather.Sun).Speed);
        Assert.Empty(SpeedCalculator.ConditionFactors(build));
        var potential = SpeedCalculator.PotentialFactor(build);
        Assert.NotNull(potential);
        Assert.Equal(2.0, potential!.Multiplier);
        Assert.Null(SpeedCalculator.PotentialFactor(build, Weather.Sun));
    }

    [Fact]
    public void Tailwind_and_paralysis()
    {
        var build = Build("overgrow");
        var plain = StatCalculator.Calculate(build, Stat.Speed);
        Assert.Equal(plain * 2, SpeedCalculator.Effective(build, tailwind: true).Speed);
        build.Status = StatusCondition.Paralysis;
        Assert.Equal(DamageCalculatorRound(plain * 0.5), SpeedCalculator.Effective(build).Speed);
        Assert.Null(SpeedCalculator.PotentialFactor(build));
    }

    [Fact]
    public void Protosynthesis_boosts_speed_when_speed_is_highest()
    {
        var build = Build("protosynthesis", speed: 150);
        var plain = StatCalculator.Calculate(build, Stat.Speed);
        var (sun, notes) = SpeedCalculator.Effective(build, Weather.Sun);
        Assert.Equal(DamageCalculatorRound(plain * 1.5), sun);
        Assert.Contains(notes, n => n.Contains("1.5倍"));
        // 素早さが一番高くなければ上がらない
        var slow = Build("protosynthesis", speed: 50);
        Assert.Equal(StatCalculator.Calculate(slow, Stat.Speed), SpeedCalculator.Effective(slow, Weather.Sun).Speed);
    }

    private static int DamageCalculatorRound(double v) => FaraPokemonAssistance.Core.Battle.DamageCalculator.PokeRound(v);
}

public class CounterTests
{
    private static Pokemon Make(int id, string name, string t1, string t2 = "") =>
        new() { Id = id, Name = name, Type1 = t1, Type2 = t2, SpeciesId = id };

    private static readonly TypeChart Chart = TypeChart.Parse(
        "AttackType,DefenseType,Multiplier\n" +
        "Water,Fire,2\nFire,Water,0.5\nFire,Grass,2\nGrass,Fire,0.5\nWater,Grass,0.5\nGrass,Water,2\n" +
        "Ground,Fire,2\nFire,Fire,0.5\nWater,Water,0.5\nGrass,Grass,0.5\nFire,Dragon,0.5\n");

    [Fact]
    public void Counters_hit_super_effectively_and_keep_candidate_order()
    {
        var fire = Make(1, "ほのお", "Fire");
        var water1 = Make(2, "みず1", "Water");
        var ground = Make(3, "じめん", "Ground");
        var water2 = Make(4, "みず2", "Water");
        var grass = Make(5, "くさ", "Grass");
        var fire2 = Make(6, "ほのお2", "Fire");
        var dragon = Make(7, "ドラゴン", "Dragon");

        var counters = TypeMatchup.Counters(Chart, fire, new[] { fire, dragon, grass, water1, ground, fire2, water2 });

        // くさ・ほのお2 は抜群を取れないので除く。抜群を取れる相手を候補順に、そのあと半減で受けられるだけの相手（ドラゴン）
        Assert.Equal(new[] { "みず1", "じめん", "みず2", "ドラゴン" }, counters.Select(c => c.Pokemon.Name));
        Assert.Equal(2.0, counters[0].Offense);
        Assert.Equal(0.5, counters[0].Defense);
        Assert.Equal(2, TypeMatchup.Counters(Chart, fire, new[] { water1, ground, water2 }, count: 2).Count);
    }
}

public class AbilityMatchupTests
{
    private static Ability A(string identifier, string name) => new() { Id = identifier.GetHashCode(), Identifier = identifier, Name = name };

    private static Pokemon Make(int id, string name, string t1, string t2 = "") =>
        new() { Id = id, Name = name, Type1 = t1, Type2 = t2, SpeciesId = id };

    private static readonly TypeChart Chart = TypeChart.Parse(
        "AttackType,DefenseType,Multiplier\n" +
        "Fire,Grass,2\nIce,Grass,2\nFire,Poison,1\nGrass,Grass,0.5\nGrass,Poison,0.5\nGround,Poison,2\nGround,Steel,2\nFire,Steel,2\n" +
        "Fighting,Steel,2\nFighting,Ghost,0\nNormal,Ghost,0\nNormal,Steel,0.5\nGhost,Ghost,2\nGhost,Normal,0\nFire,Fire,0.5\nWater,Fire,2\n" +
        "Water,Grass,0.5\nElectric,Ground,0\n");

    [Fact]
    public void Thick_fat_halves_fire_and_ice_on_mega_venusaur()
    {
        var thickFat = A("thick-fat", "あついしぼう");
        var fire = AbilityMatchup.Against(Chart, "Fire", new[] { "Grass", "Poison" }, thickFat);
        Assert.Equal(2.0, fire.TypeMultiplier);
        Assert.Equal(1.0, fire.Multiplier);
        Assert.True(fire.ChangedByAbility);
        Assert.Contains("あついしぼう(半減)", fire.Notes);
        var grass = AbilityMatchup.Against(Chart, "Grass", new[] { "Grass", "Poison" }, thickFat);
        Assert.Equal(0.25, grass.Multiplier);
        Assert.False(grass.ChangedByAbility);

        var changed = AbilityMatchup.ChangedByDefensiveAbility(Chart, new[] { "Grass", "Poison" }, thickFat);
        Assert.Equal(new[] { "Fire", "Ice" }, changed.Select(c => c.AttackType).OrderBy(t => t));
    }

    [Theory]
    [InlineData("levitate", "Ground", 0.0)]
    [InlineData("flash-fire", "Fire", 0.0)]
    [InlineData("water-absorb", "Water", 0.0)]
    [InlineData("dry-skin", "Fire", 2.5)]
    [InlineData("fluffy", "Fire", 4.0)]
    [InlineData("filter", "Fire", 1.5)]
    [InlineData("heatproof", "Fire", 1.0)]
    [InlineData("wonder-guard", "Grass", 0.0)]
    [InlineData("wonder-guard", "Fire", 2.0)]
    public void Defensive_abilities_on_grass(string ability, string attackType, double expected) =>
        Assert.Equal(expected, AbilityMatchup.Against(Chart, attackType, new[] { "Grass" }, A(ability, ability)).Multiplier);

    [Fact]
    public void Scrappy_tinted_lens_and_mold_breaker()
    {
        var scrappy = A("scrappy", "きもったま");
        var aegislash = new[] { "Steel", "Ghost" };
        Assert.Equal(0.0, AbilityMatchup.Against(Chart, "Fighting", aegislash).Multiplier);
        var hit = AbilityMatchup.Against(Chart, "Fighting", aegislash, attackerAbility: scrappy);
        Assert.Equal(2.0, hit.Multiplier);
        Assert.Contains(hit.Notes, n => n.Contains("きもったま"));

        Assert.Equal(1.0, AbilityMatchup.Against(Chart, "Grass", new[] { "Grass" }, attackerAbility: A("tinted-lens", "いろめがね")).Multiplier);
        // かたやぶりは相手の特性（ふゆう）を無視する
        Assert.Equal(2.0, AbilityMatchup.Against(Chart, "Ground", new[] { "Steel" }, A("levitate", "ふゆう"), A("mold-breaker", "かたやぶり")).Multiplier);
    }

    [Fact]
    public void Counters_use_abilities_on_both_sides()
    {
        var aegislash = Make(681, "ギルガルド", "Steel", "Ghost");
        var fighter = Make(2, "かくとう", "Fighting");
        var counters = TypeMatchup.Counters(Chart, aegislash, new[] { fighter });
        Assert.Empty(counters);
        var scrappy = A("scrappy", "きもったま");
        counters = TypeMatchup.Counters(Chart, aegislash, new[] { fighter }, abilityOf: p => p.Id == 2 ? scrappy : null);
        var c = Assert.Single(counters);
        Assert.Equal(2.0, c.Offense);
        Assert.Contains(c.Notes, n => n.Contains("きもったま"));

        // 選んだポケモンがふゆうなら、じめん技しかない相手は有利にならない
        var steel = Make(3, "はがね", "Steel");
        var digger = Make(4, "じめん", "Ground");
        Assert.Single(TypeMatchup.Counters(Chart, steel, new[] { digger }));
        Assert.Empty(TypeMatchup.Counters(Chart, steel, new[] { digger }, ability: A("levitate", "ふゆう")));
    }

    [Fact]
    public void Speed_items()
    {
        Assert.True(SpeedCalculator.IsSpeedItem(new Item { Id = 264, Name = "こだわりスカーフ" }));
        Assert.True(SpeedCalculator.IsSpeedItem(new Item { Id = 255, Name = "くろいてっきゅう" }));
        Assert.True(SpeedCalculator.IsSpeedItem(new Item { Id = 270, Name = "パワーアンクル" }));
        Assert.False(SpeedCalculator.IsSpeedItem(new Item { Id = 278, Name = "みどりのプレート" }));

        var mon = new Pokemon { Id = 1, Name = "テスト", Type1 = "Normal", BaseStats = new StatSet(100, 100, 100, 100, 100, 100), SpeciesId = 1 };
        var plain = StatCalculator.Calculate(new PokemonBuild(mon), Stat.Speed);
        Assert.Equal(plain / 2, SpeedCalculator.Effective(new PokemonBuild(mon) { Item = new Item { Id = 255, Name = "くろいてっきゅう" } }).Speed);
        Assert.Equal(plain, SpeedCalculator.Effective(new PokemonBuild(mon) { Item = new Item { Id = 278, Name = "みどりのプレート" } }).Speed);
    }
}
