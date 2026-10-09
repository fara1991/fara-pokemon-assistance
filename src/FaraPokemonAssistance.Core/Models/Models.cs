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

/// <summary>状態異常。</summary>
public enum StatusCondition
{
    None,
    Burn,
    Poison,
    BadlyPoisoned,
    Paralysis,
}

public static class StatusNames
{
    public static string Japanese(StatusCondition s) => s switch
    {
        StatusCondition.Burn => "やけど",
        StatusCondition.Poison => "どく",
        StatusCondition.BadlyPoisoned => "もうどく",
        StatusCondition.Paralysis => "まひ",
        _ => "なし",
    };

    public static StatusCondition? Parse(string normalized) => normalized switch
    {
        "やけど" or "burn" or "brn" => StatusCondition.Burn,
        "どく" or "poison" or "psn" => StatusCondition.Poison,
        "もうどく" or "toxic" or "tox" => StatusCondition.BadlyPoisoned,
        "まひ" or "まひ状態" or "paralysis" or "para" or "par" => StatusCondition.Paralysis,
        _ => null,
    };

    /// <summary>ターン終了時の定数ダメージ（最大 HP と経過ターン数から）。</summary>
    public static int ResidualDamage(StatusCondition s, int maxHp, int turn) => s switch
    {
        StatusCondition.Burn => Math.Max(1, maxHp / 16),
        StatusCondition.Poison => Math.Max(1, maxHp / 8),
        StatusCondition.BadlyPoisoned => Math.Max(1, maxHp * Math.Min(turn, 15) / 16),
        _ => 0,
    };
}

/// <summary>努力値の方式。</summary>
public enum EvSystem
{
    /// <summary>従来方式: 各 0〜252、合計 510。Lv50 なら 4 で +1、以降 8 ごとに +1。</summary>
    Classic,
    /// <summary>ポケモンチャンピオンズ: 各 0〜32、合計 66。1 ポイントごとに実数値 +1。</summary>
    Points,
}

public static class EvRules
{
    public static int MaxPerStat(EvSystem system) => system == EvSystem.Points ? 32 : 252;
    public static int MaxTotal(EvSystem system) => system == EvSystem.Points ? 66 : 510;
    public static int Step(EvSystem system) => system == EvSystem.Points ? 1 : 4;
    public static string Label(EvSystem system) => system == EvSystem.Points ? "ポイント" : "努力値";

    /// <summary>範囲・合計を検証し、問題があればメッセージを返す。</summary>
    public static string? Validate(EvSystem system, StatSet evs)
    {
        var max = MaxPerStat(system);
        foreach (var stat in Enum.GetValues<Stat>())
        {
            var v = evs[stat];
            if (v < 0 || v > max)
                return $"{StatNames.Japanese(stat)}の{Label(system)}は 0〜{max} で指定してください（{v}）";
        }
        if (evs.Total > MaxTotal(system))
            return $"{Label(system)}の合計は {MaxTotal(system)} 以下にしてください（{evs.Total}）";
        return null;
    }

    /// <summary>「最大まで振る」値（従来 252 / ポイント 32）。</summary>
    public static int Full(EvSystem system) => MaxPerStat(system);
}

public enum Weather
{
    None,
    Sun,
    Rain,
    Sand,
    Snow,
}

public enum Terrain
{
    None,
    Electric,
    Grassy,
    Psychic,
    Misty,
}

public static class FieldNames
{
    public static string Japanese(Weather w) => w switch
    {
        Weather.Sun => "晴れ", Weather.Rain => "雨", Weather.Sand => "砂嵐", Weather.Snow => "雪", _ => "なし",
    };

    public static string Japanese(Terrain t) => t switch
    {
        Terrain.Electric => "エレキフィールド", Terrain.Grassy => "グラスフィールド",
        Terrain.Psychic => "サイコフィールド", Terrain.Misty => "ミストフィールド", _ => "なし",
    };

    /// <summary>登場時に天候を変える特性（あめふらし等）なら、その天候。それ以外は <c>null</c>。</summary>
    public static Weather? WeatherSetBy(Ability? ability) => ability?.Identifier switch
    {
        "drizzle" or "primordial-sea" => Weather.Rain,
        "drought" or "desolate-land" or "orichalcum-pulse" => Weather.Sun,
        "sand-stream" => Weather.Sand,
        "snow-warning" => Weather.Snow,
        _ => null,
    };

    /// <summary>登場時にフィールドを張る特性（サイコメイカー等）なら、そのフィールド。それ以外は <c>null</c>。</summary>
    public static Terrain? TerrainSetBy(Ability? ability) => ability?.Identifier switch
    {
        "electric-surge" or "hadron-engine" => Terrain.Electric,
        "grassy-surge" => Terrain.Grassy,
        "psychic-surge" => Terrain.Psychic,
        "misty-surge" => Terrain.Misty,
        _ => null,
    };
}

public sealed class Ability
{
    public int Id { get; init; }
    /// <summary>元データの英語識別子（例: <c>huge-power</c>）。計算ロジックはこれで判定する。</summary>
    public string Identifier { get; init; } = "";
    public string Name { get; init; } = "";

    public override string ToString() => Name;
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
    /// <summary>持ちうる特性の ID（通常特性の順、隠れ特性が最後）。</summary>
    public IReadOnlyList<int> AbilityIds { get; init; } = Array.Empty<int>();
    /// <summary>このデータセットに正式収録されておらず、他のデータで補完したポケモンなら true。</summary>
    public bool IsProvisional { get; init; }
    /// <summary>メガシンカ後のフォルムなら、必要なメガストーンの持ち物 ID。</summary>
    public int? MegaStoneId { get; init; }

    public bool IsMega => MegaStoneId is not null;

    public bool HasType(string type) =>
        !string.IsNullOrEmpty(type) && (Type1 == type || Type2 == type);

    public IEnumerable<string> Types
    {
        get
        {
            if (!string.IsNullOrEmpty(Type1)) yield return Type1;
            if (!string.IsNullOrEmpty(Type2)) yield return Type2;
        }
    }

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
    /// <summary>技フラグ（Contact, Sound, Punch, Bite, Bullet, Pulse, Powder, Recharge）。</summary>
    public IReadOnlySet<string> Flags { get; init; } = new HashSet<string>();
    /// <summary>追加効果の発生率（%）。ちからずくの判定に使う。</summary>
    public int EffectChance { get; init; }
    public string Description { get; init; } = "";

    public bool IsDamaging => Category != MoveCategory.Status && Power > 0;
    /// <summary>いのちがけ・ナイトヘッド・カウンターなど、威力ではなく固定値でダメージを与える技。</summary>
    public bool IsFixedDamage => Category != MoveCategory.Status && Power == 0;
    /// <summary>ダメージ計算で参照する防御側の能力（サイコショック系は特殊技でも防御）。</summary>
    public Stat DefenseStatUsed => Category == MoveCategory.Physical || UsesPhysicalDefense ? Stat.Defense : Stat.SpDefense;
    /// <summary>ダメージ計算で参照する攻撃側の能力。</summary>
    public Stat AttackStatUsed => Category == MoveCategory.Physical ? Stat.Attack : Stat.SpAttack;
    /// <summary>特殊技だが相手の「防御」で計算する技（サイコショック・サイコブレイク・しんぴのつるぎ）。</summary>
    public bool UsesPhysicalDefense => Category == MoveCategory.Special && Id is 473 or 540 or 548;
    public bool HasFlag(string flag) => Flags.Contains(flag);
    public bool IsContact => HasFlag("Contact");
    public bool IsSound => HasFlag("Sound");

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
    /// <summary>メガストーン（メガシンカ後のフォルムは固定）。</summary>
    MegaStone,
    /// <summary>半減きのみ（TypeBoost のタイプの効果抜群技を 1 回だけ半減。ホズのみはノーマル技）。</summary>
    ResistBerry,
    /// <summary>オボンのみ（HP 1/2 以下で 1/4 回復）。</summary>
    SitrusBerry,
    /// <summary>オレンのみ（HP 1/2 以下で 10 回復）。</summary>
    OranBerry,
    /// <summary>フィラのみ等（HP 1/4 以下で 1/3 回復）。</summary>
    PinchBerry,
    /// <summary>たべのこし（毎ターン 1/16 回復）。</summary>
    Leftovers,
    /// <summary>くろいヘドロ（どくタイプは 1/16 回復、それ以外は 1/8 ダメージ）。</summary>
    BlackSludge,
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
    public bool IsDefensive => Category is ItemCategory.AssaultVest or ItemCategory.Eviolite
        or ItemCategory.ResistBerry or ItemCategory.SitrusBerry or ItemCategory.OranBerry or ItemCategory.PinchBerry
        or ItemCategory.Leftovers or ItemCategory.BlackSludge
        || DefenseMultiplier != 1.0 || SpDefenseMultiplier != 1.0;
    /// <summary>攻撃側のダメージに関係する持ち物か（計算機の持ち物一覧の絞り込みに使う）。</summary>
    public bool IsOffensive => Category is not (ItemCategory.ResistBerry) && (Category is ItemCategory.LifeOrb or ItemCategory.ExpertBelt or ItemCategory.TypeBoost or ItemCategory.LightBall or ItemCategory.ThickClub
        || AttackMultiplier != 1.0 || SpAttackMultiplier != 1.0 || DamageMultiplier != 1.0 || !string.IsNullOrEmpty(TypeBoost));
    public bool IsMegaStone => Category == ItemCategory.MegaStone;

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

public enum BattleGimmick
{
    None,
    Dynamax,
    Terastal,
}

/// <summary>データセット（世代・ゲーム）の定義。<c>data/datasets.csv</c> に対応。</summary>
public sealed class DataSetInfo
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public int Generation { get; init; }
    public EvSystem EvSystem { get; init; } = EvSystem.Classic;
    /// <summary>チャットコマンドの接頭辞（例: pokech）。</summary>
    public string CommandPrefix { get; init; } = "";
    /// <summary>そのゲームのバトルギミック（テラスタル / ダイマックス / なし）。</summary>
    public BattleGimmick Gimmick { get; init; } = BattleGimmick.None;
    /// <summary>このデータセットに使用率データが無いとき、代わりに使うデータセットのキー（例: Champions → Gen9）。</summary>
    public string UsageFallback { get; init; } = "";

    public bool HasTerastal => Gimmick == BattleGimmick.Terastal;
    public bool HasDynamax => Gimmick == BattleGimmick.Dynamax;

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
