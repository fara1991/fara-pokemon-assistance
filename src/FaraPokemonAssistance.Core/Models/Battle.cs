using FaraPokemonAssistance.Core.Battle;

namespace FaraPokemonAssistance.Core.Models;

/// <summary>対戦で使う 1 体の構成（レベル・個体値・努力値・性格・持ち物・ランク）。</summary>
public sealed class PokemonBuild
{
    public Pokemon Pokemon { get; }
    public int Level { get; set; } = 50;
    /// <summary>努力値の方式（データセットに従う）。</summary>
    public EvSystem EvSystem { get; set; } = EvSystem.Classic;
    public StatSet IVs { get; set; } = StatSet.Filled(31);
    public StatSet EVs { get; set; } = new();
    public Nature? Nature { get; set; }
    public Item? Item { get; set; }
    public Ability? Ability { get; set; }
    /// <summary>テラスタル中のタイプ。null ならテラスタルしていない。</summary>
    public string? TeraType { get; set; }
    /// <summary>ランク補正（-6〜+6）。HP は使わない。</summary>
    public StatSet Boosts { get; set; } = new();
    /// <summary>状態異常。</summary>
    public StatusCondition Status { get; set; } = StatusCondition.None;
    /// <summary>ダイマックス中（剣盾）。HP 2 倍、技はダイマックス技の威力になる。</summary>
    public bool IsDynamax { get; set; }
    /// <summary>残り HP の割合（1〜100）。もうか等のピンチ特性、マルチスケイル、確定数の開始 HP に使う。</summary>
    public int HpPercent { get; set; } = 100;

    /// <summary>最大 HP（ダイマックス中は 2 倍）。</summary>
    public int MaxHp => StatCalculator.Calculate(this, Stat.HP) * (IsDynamax ? 2 : 1);

    /// <summary>今の HP（<see cref="HpPercent"/> から求める。最低 1）。</summary>
    public int CurrentHp => HpPercent >= 100 ? MaxHp : Math.Max(1, MaxHp * Math.Clamp(HpPercent, 1, 100) / 100);

    public PokemonBuild(Pokemon pokemon)
    {
        Pokemon = pokemon;
    }

    public PokemonBuild(Pokemon pokemon, EvSystem evSystem) : this(pokemon)
    {
        EvSystem = evSystem;
    }

    public bool IsTerastallized => !string.IsNullOrEmpty(TeraType);

    /// <summary>防御側として受けるタイプ（テラスタル中はテラスタイプのみ）。</summary>
    public IReadOnlyList<string> DefensiveTypes =>
        IsTerastallized ? new[] { TeraType! } : Pokemon.Types.ToList();

    public bool HasAbility(string identifier) =>
        Ability is not null && Ability.Identifier == identifier;

    public PokemonBuild Clone() => new(Pokemon, EvSystem)
    {
        Level = Level,
        IVs = IVs.Clone(),
        EVs = EVs.Clone(),
        Nature = Nature,
        Item = Item,
        Ability = Ability,
        TeraType = TeraType,
        Boosts = Boosts.Clone(),
        Status = Status,
        IsDynamax = IsDynamax,
        HpPercent = HpPercent,
    };

    /// <summary>チャット向けの短い説明（例: <c>C252 ひかえめ こだわりメガネ サイコメイカー</c>）。</summary>
    public string DescribeShort()
    {
        var parts = new List<string> { EVs.ToShortString() };
        if (Nature is not null) parts.Add(Nature.Name);
        if (Item is not null) parts.Add(Item.Name);
        if (Ability is not null) parts.Add(Ability.Name);
        if (IsTerastallized) parts.Add($"テラス{TypeNames.ToJapanese(TeraType!)}");
        if (Status != StatusCondition.None) parts.Add(StatusNames.Japanese(Status));
        if (IsDynamax) parts.Add("ダイマックス");
        if (HpPercent < 100) parts.Add($"HP{HpPercent}%");
        var boosts = new List<string>();
        foreach (var stat in new[] { Stat.Attack, Stat.Defense, Stat.SpAttack, Stat.SpDefense, Stat.Speed })
        {
            var b = Boosts[stat];
            if (b != 0) boosts.Add($"{StatNames.Letter(stat)}{(b > 0 ? "+" : "")}{b}");
        }
        if (boosts.Count > 0) parts.Add(string.Join(",", boosts));
        if (Level != 50) parts.Add($"Lv{Level}");
        return string.Join(" ", parts);
    }
}

public sealed class DamageRequest
{
    public required PokemonBuild Attacker { get; init; }
    public required Move Move { get; init; }
    public required PokemonBuild Defender { get; init; }
    public BattleFormat Format { get; init; } = BattleFormat.Singles;
    public bool IsCritical { get; init; }
    public Weather Weather { get; init; } = Weather.None;
    public Terrain Terrain { get; init; } = Terrain.None;
    /// <summary>防御側の場にオーロラベールがある（物理・特殊の両方に効く）。チャットの「壁」もこれ。</summary>
    public bool Screen { get; init; }
    /// <summary>防御側の場にリフレクターがある（物理技を半減）。</summary>
    public bool Reflect { get; init; }
    /// <summary>防御側の場にひかりのかべがある（特殊技を半減）。</summary>
    public bool LightScreen { get; init; }

    /// <summary>その分類の技に壁が効くか。</summary>
    public bool HasScreenFor(bool physical) => Screen || (physical ? Reflect : LightScreen);

    /// <summary>技や防御側だけ差し替えた複製（場の条件はそのまま）。</summary>
    public DamageRequest With(Move? move = null, PokemonBuild? defender = null, PokemonBuild? attacker = null) => new()
    {
        Attacker = attacker ?? Attacker,
        Move = move ?? Move,
        Defender = defender ?? Defender,
        Format = Format,
        IsCritical = IsCritical,
        Weather = Weather,
        Terrain = Terrain,
        Screen = Screen,
        Reflect = Reflect,
        LightScreen = LightScreen,
    };
}

/// <summary>確定数。<see cref="Hits"/> 発で倒せる確率が <see cref="Probability"/>。</summary>
public readonly record struct KnockOut(int Hits, double Probability)
{
    public bool IsGuaranteed => Probability >= 0.9999;

    public override string ToString()
    {
        if (Hits <= 0) return "ダメージなし";
        if (IsGuaranteed) return $"確定{Hits}発";
        return $"乱数{Hits}発({Probability * 100:0.#}%)";
    }
}

public sealed class DamageResult
{
    public required int[] Rolls { get; init; }
    public int MinDamage => Rolls[0];
    public int MaxDamage => Rolls[^1];
    public required int DefenderHP { get; init; }
    public double MinPercent => Math.Round(MinDamage * 100.0 / DefenderHP, 1);
    public double MaxPercent => Math.Round(MaxDamage * 100.0 / DefenderHP, 1);
    public required double TypeEffectiveness { get; init; }
    public required bool IsStab { get; init; }
    public required int AttackStat { get; init; }
    public required int DefenseStat { get; init; }
    public required int BasePower { get; init; }
    /// <summary>実際に判定に使われた技タイプ（スキン系特性で変わる）。</summary>
    public string MoveType { get; init; } = "";
    public required KnockOut KnockOut { get; init; }
    public required IReadOnlyList<string> Modifiers { get; init; }

    public string EffectivenessText => TypeEffectiveness switch
    {
        0.0 => "効果なし",
        < 1.0 => $"いまひとつ({TypeEffectiveness:0.##}倍)",
        > 1.0 => $"効果抜群({TypeEffectiveness:0.##}倍)",
        _ => "等倍",
    };

    public string RangeText => $"{MinPercent:0.0}〜{MaxPercent:0.0}% ({MinDamage}〜{MaxDamage})";
}
