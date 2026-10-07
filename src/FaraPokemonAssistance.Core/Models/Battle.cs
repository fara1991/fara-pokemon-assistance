namespace FaraPokemonAssistance.Core.Models;

/// <summary>対戦で使う 1 体の構成（レベル・個体値・努力値・性格・持ち物・ランク）。</summary>
public sealed class PokemonBuild
{
    public Pokemon Pokemon { get; }
    public int Level { get; set; } = 50;
    public StatSet IVs { get; set; } = StatSet.Filled(31);
    public StatSet EVs { get; set; } = new();
    public Nature? Nature { get; set; }
    public Item? Item { get; set; }
    /// <summary>ランク補正（-6〜+6）。HP は使わない。</summary>
    public StatSet Boosts { get; set; } = new();

    public PokemonBuild(Pokemon pokemon)
    {
        Pokemon = pokemon;
    }

    public PokemonBuild Clone() => new(Pokemon)
    {
        Level = Level,
        IVs = IVs.Clone(),
        EVs = EVs.Clone(),
        Nature = Nature,
        Item = Item,
        Boosts = Boosts.Clone(),
    };

    /// <summary>チャット向けの短い説明（例: <c>C252 ひかえめ こだわりメガネ</c>）。</summary>
    public string DescribeShort()
    {
        var parts = new List<string> { EVs.ToShortString() };
        if (Nature is not null) parts.Add(Nature.Name);
        if (Item is not null) parts.Add(Item.Name);
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
