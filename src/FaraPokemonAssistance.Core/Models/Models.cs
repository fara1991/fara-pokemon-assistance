namespace FaraPokemonAssistance.Core.Models;

public enum Stat
{
    HP,
    Attack,
    Defense,
    SpAttack,
    SpDefense,
    Speed,
}

public enum MoveCategory
{
    Status,
    Physical,
    Special,
}

/// <summary>技の対象。ダブルバトルで複数対象になる技は威力 0.75 倍。</summary>
public enum MoveTarget
{
    Single,
    AllFoes,
    AllOthers,
}

public enum BattleFormat
{
    Singles,
    Doubles,
}

/// <summary>H/A/B/C/D/S の 6 値。種族値・個体値・努力値・実数値・ランク補正のいずれにも使う。</summary>
public sealed class StatSet
{
    public int HP { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public int SpAttack { get; set; }
    public int SpDefense { get; set; }
    public int Speed { get; set; }

    public StatSet() { }

    public StatSet(int hp, int attack, int defense, int spAttack, int spDefense, int speed)
    {
        HP = hp;
        Attack = attack;
        Defense = defense;
        SpAttack = spAttack;
        SpDefense = spDefense;
        Speed = speed;
    }

    public static StatSet Filled(int value) => new(value, value, value, value, value, value);

    public int this[Stat stat]
    {
        get => stat switch
        {
            Stat.HP => HP,
            Stat.Attack => Attack,
            Stat.Defense => Defense,
            Stat.SpAttack => SpAttack,
            Stat.SpDefense => SpDefense,
            Stat.Speed => Speed,
            _ => throw new ArgumentOutOfRangeException(nameof(stat)),
        };
        set
        {
            switch (stat)
            {
                case Stat.HP: HP = value; break;
                case Stat.Attack: Attack = value; break;
                case Stat.Defense: Defense = value; break;
                case Stat.SpAttack: SpAttack = value; break;
                case Stat.SpDefense: SpDefense = value; break;
                case Stat.Speed: Speed = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(stat));
            }
        }
    }

    public StatSet Clone() => new(HP, Attack, Defense, SpAttack, SpDefense, Speed);

    public int Total => HP + Attack + Defense + SpAttack + SpDefense + Speed;

    /// <summary>H252-A0-B4 … の短縮表記。0 の項目は省く。</summary>
    public string ToShortString(bool skipZero = true)
    {
        var parts = new List<string>();
        foreach (var stat in Enum.GetValues<Stat>())
        {
            var v = this[stat];
            if (skipZero && v == 0) continue;
            parts.Add($"{StatNames.Letter(stat)}{v}");
        }
        return parts.Count == 0 ? "無振り" : string.Join("", parts);
    }
}

public static class StatNames
{
    public static string Letter(Stat stat) => stat switch
    {
        Stat.HP => "H",
        Stat.Attack => "A",
        Stat.Defense => "B",
        Stat.SpAttack => "C",
        Stat.SpDefense => "D",
        Stat.Speed => "S",
        _ => "?",
    };

    public static string Japanese(Stat stat) => stat switch
    {
        Stat.HP => "HP",
        Stat.Attack => "攻撃",
        Stat.Defense => "防御",
        Stat.SpAttack => "特攻",
        Stat.SpDefense => "特防",
        Stat.Speed => "素早さ",
        _ => "?",
    };

    public static Stat? Parse(string? text) => text switch
    {
        "HP" or "H" => Stat.HP,
        "Attack" or "A" => Stat.Attack,
        "Defense" or "B" => Stat.Defense,
        "SpAttack" or "C" => Stat.SpAttack,
        "SpDefense" or "D" => Stat.SpDefense,
        "Speed" or "S" => Stat.Speed,
        _ => null,
    };
}

public sealed class Pokemon
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Type1 { get; init; } = "";
    public string Type2 { get; init; } = "";
    public StatSet BaseStats { get; init; } = new();
    public string Icon { get; init; } = "";
    /// <summary>種族 ID（フォルム違いは同じ値を持つ）。</summary>
    public int SpeciesId { get; init; }
    /// <summary>進化前（しんかのきせき対象）なら true。</summary>
    public bool NotFullyEvolved { get; init; }

    public bool HasType(string type) =>
        !string.IsNullOrEmpty(type) && (Type1 == type || Type2 == type);

    public override string ToString() => Name;
}

public sealed class Move
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public int Power { get; init; }
    public int Accuracy { get; init; }
    public int PP { get; init; }
    public MoveCategory Category { get; init; }
    public MoveTarget Target { get; init; }
    public int Priority { get; init; }
    public string Description { get; init; } = "";

    public bool IsDamaging => Category != MoveCategory.Status && Power > 0;

    public override string ToString() => Name;
}

public enum ItemCategory
{
    Other,
    Choice,
    LifeOrb,
    ExpertBelt,
    TypeBoost,
    AssaultVest,
    Eviolite,
    LightBall,
    ThickClub,
}

public sealed class Item
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public ItemCategory Category { get; init; }
    public string Effect { get; init; } = "";
    public double AttackMultiplier { get; init; } = 1.0;
    public double DefenseMultiplier { get; init; } = 1.0;
    public double SpAttackMultiplier { get; init; } = 1.0;
    public double SpDefenseMultiplier { get; init; } = 1.0;
    public double DamageMultiplier { get; init; } = 1.0;
    /// <summary>威力を上げる対象。タイプ名（"Fire"）または分類（"Physical"/"Special"）。</summary>
    public string TypeBoost { get; init; } = "";
    public double TypeBoostMultiplier { get; init; } = 1.0;

    /// <summary>防御側に持たせるのが自然な持ち物か（チャットコマンドの振り分けに使う）。</summary>
    public bool IsDefensive => Category is ItemCategory.AssaultVest or ItemCategory.Eviolite;

    public override string ToString() => Name;
}

public sealed class Nature
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public Stat? IncreasedStat { get; init; }
    public Stat? DecreasedStat { get; init; }

    public bool IsNeutral => IncreasedStat is null;

    public double Modifier(Stat stat)
    {
        if (IncreasedStat == stat) return 1.1;
        if (DecreasedStat == stat) return 0.9;
        return 1.0;
    }

    public string Describe()
    {
        if (IncreasedStat is null || DecreasedStat is null)
            return $"{Name}（補正無し）";
        return $"{Name}（{StatNames.Japanese(IncreasedStat.Value)}↑{StatNames.Japanese(DecreasedStat.Value)}↓）";
    }

    public override string ToString() => Name;
}

/// <summary>データセット（世代・ゲーム）の定義。<c>data/datasets.csv</c> に対応。</summary>
public sealed class DataSetInfo
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public int Generation { get; init; }

    public override string ToString() => Name;
}

public static class TypeNames
{
    private static readonly Dictionary<string, string> Japanese = new()
    {
        ["Normal"] = "ノーマル", ["Fire"] = "ほのお", ["Water"] = "みず", ["Electric"] = "でんき",
        ["Grass"] = "くさ", ["Ice"] = "こおり", ["Fighting"] = "かくとう", ["Poison"] = "どく",
        ["Ground"] = "じめん", ["Flying"] = "ひこう", ["Psychic"] = "エスパー", ["Bug"] = "むし",
        ["Rock"] = "いわ", ["Ghost"] = "ゴースト", ["Dragon"] = "ドラゴン", ["Dark"] = "あく",
        ["Steel"] = "はがね", ["Fairy"] = "フェアリー",
    };

    public static IReadOnlyCollection<string> All => Japanese.Keys;

    public static string ToJapanese(string type) => Japanese.GetValueOrDefault(type, type);

    public static string? FromJapanese(string text)
    {
        foreach (var (key, value) in Japanese)
        {
            if (value == text) return key;
        }
        return null;
    }
}
