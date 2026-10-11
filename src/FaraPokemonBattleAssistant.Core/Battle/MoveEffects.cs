using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>
/// 技の追加効果・反動・持ち物に関する判定（全世代・今のゲームのダメージ計算で共通）。
/// 判定は公開されているダメージ計算ツール（@smogon/calc）に合わせ、tests の move_lists.json（tools/calc_vectors/move_lists.js で作成）と照合している。
/// </summary>
public static class MoveEffects
{
    private const int MoveIdRapidSpin = 229;
    public const int MoveIdWeatherBall = 311;
    public const int MoveIdTerrainPulse = 805;
    public const int MoveIdFoulPlay = 492;
    public const int MoveIdBodyPress = 776;
    public const int MoveIdBrine = 362;
    public const int MoveIdVenoshock = 474;
    public const int MoveIdHex = 506;
    public const int MoveIdInfernalParade = 844;
    public const int MoveIdBarbBarrage = 839;
    public const int MoveIdMeteorBeam = 800;
    public const int MoveIdElectroShot = 905;
    public const int MoveIdRagingBull = 873;
    public const int MoveIdBrickBreak = 280;
    public const int MoveIdPsychicFangs = 706;
    public const int MoveIdCollisionCourse = 878;
    public const int MoveIdElectroDrift = 879;
    public const int MoveIdTripleKick = 167;
    public const int MoveIdTripleAxel = 813;

    /// <summary>連続技の回数が決まっている技（ID → 回数）。</summary>
    private static readonly Dictionary<int, int> FixedHits = new()
    {
        [24] = 2, [41] = 2, [155] = 2, [458] = 2, [530] = 2, [544] = 2, [742] = 2, [751] = 2, [814] = 2, [888] = 2, [911] = 2,
        [167] = 3, [813] = 3, [818] = 3, [865] = 3,
        [860] = 10,
    };

    /// <summary>2〜5 回当たる連続技。</summary>
    private static readonly HashSet<int> TwoToFiveHits = new() { 3, 4, 31, 42, 131, 140, 154, 198, 292, 331, 333, 350, 541, 594, 799 };

    /// <summary>2〜5 回当たる技か（回数は <see cref="HitCount"/> で決める）。</summary>
    public static bool HitsTwoToFive(Move move) => TwoToFiveHits.Contains(move.Id);

    /// <summary>
    /// 連続技の回数（連続技でなければ 1）。2〜5 回の技は 3 回（スキルリンクは 5 回）とする（@smogon/calc の既定と同じ）。
    /// トリプルキック・トリプルアクセルは 3 回（1 回ごとに威力が上がる。<see cref="PowerForHit"/>）、ネズミざんは 10 回。
    /// </summary>
    public static int HitCount(Move move, string? attackerAbility)
    {
        if (FixedHits.TryGetValue(move.Id, out var hits)) return hits;
        if (TwoToFiveHits.Contains(move.Id)) return attackerAbility == "skill-link" ? 5 : 3;
        return 1;
    }

    /// <summary>連続技の <paramref name="hit"/> 回目の威力（トリプルキックは 10・20・30、トリプルアクセルは 20・40・60）。</summary>
    public static int PowerForHit(Move move, int hit) => move.Id switch
    {
        MoveIdTripleKick => 10 * hit,
        MoveIdTripleAxel => 20 * hit,
        _ => move.Power,
    };

    /// <summary>必ず急所に当たる技か（やまあらし・こおりのいぶき（第 5 世代から）、あんこくきょうだ・すいりゅうれんだ（第 8 世代から）、トリックフラワー）。</summary>
    public static bool AlwaysCrits(Move move, int generation) => move.Id switch
    {
        480 or 524 => generation >= 5,
        729 => generation == 7,
        817 or 818 => generation >= 8,
        870 => generation >= 9,
        _ => false,
    };

    /// <summary>相手の防御・特防のランク変化を無視する技（せいなるつるぎ・DDラリアット・なしくずし）。</summary>
    public static bool IgnoresDefenseBoosts(Move move) => move.Id is 498 or 533 or 663;

    /// <summary>壁（リフレクター・ひかりのかべ・オーロラベール）を壊してから攻撃する技（かわらわり・サイコファング・レイジングブル）。</summary>
    public static bool BreaksScreens(Move move, int generation) =>
        move.Id == MoveIdBrickBreak || move.Id == MoveIdPsychicFangs && generation >= 7 || move.Id == MoveIdRagingBull;

    /// <summary>ウェザーボールの、天候でのタイプ（天候なしなら null）。</summary>
    public static string? WeatherBallType(Weather weather) => weather switch
    {
        Weather.Sun => "Fire",
        Weather.Rain => "Water",
        Weather.Sand => "Rock",
        Weather.Snow => "Ice",
        _ => null,
    };

    /// <summary>だいちのはどうの、フィールドでのタイプ（フィールドなしなら null）。</summary>
    public static string? TerrainPulseType(Terrain terrain) => terrain switch
    {
        Terrain.Electric => "Electric",
        Terrain.Grassy => "Grass",
        Terrain.Psychic => "Psychic",
        Terrain.Misty => "Fairy",
        _ => null,
    };

    /// <summary>レイジングブルのタイプ（パルデアのケンタロスはすがたで変わる）。</summary>
    public static string RagingBullType(Pokemon attacker, string moveType) => attacker.Id switch
    {
        10250 => "Fighting",
        10251 => "Fire",
        10252 => "Water",
        _ => moveType,
    };

    /// <summary>相手の状態異常で威力が 2 倍になる技（たたりめ・たたりびは状態異常のとき、どくばりセンボンはどく・もうどくのとき）。</summary>
    public static bool DoublesAgainstStatus(Move move, StatusCondition defenderStatus) => move.Id switch
    {
        MoveIdHex or MoveIdInfernalParade => defenderStatus != StatusCondition.None,
        MoveIdBarbBarrage => defenderStatus is StatusCondition.Poison or StatusCondition.BadlyPoisoned,
        _ => false,
    };

    /// <summary>溜めるときに特攻が 1 段階上がる技（メテオビーム・エレクトロビーム）。</summary>
    public static bool RaisesSpAttackFirst(Move move) => move.Id is MoveIdMeteorBeam or MoveIdElectroShot;

    /// <summary>タイプ・威力・分類だけを変えた技の複製。</summary>
    public static Move With(Move move, string? type = null, int? power = null, MoveCategory? category = null) => new()
    {
        Id = move.Id,
        Name = move.Name,
        Type = type ?? move.Type,
        Power = power ?? move.Power,
        Accuracy = move.Accuracy,
        PP = move.PP,
        Category = category ?? move.Category,
        Target = move.Target,
        Priority = move.Priority,
        Flags = move.Flags,
        EffectChance = move.EffectChance,
        Description = move.Description,
    };

    /// <summary>
    /// はたきおとすの威力が上がるか。相手が持ち物を持っていて、それが はたきおとせる 持ち物のときだけ 1.5 倍
    /// （はたきおとせない持ち物: Z クリスタル、自分のメガストーン、アルセウスのプレート、シルヴァディのメモリ、ゲノセクトのカセット、
    /// ゲンシカイキの珠、ギラティナ・ディアルガ・パルキアの専用の持ち物、くちたけん・くちたたて、オーガポンのめん、
    /// こだいかっせい・クォークチャージのポケモンのブーストエナジー）。
    /// </summary>
    public static bool KnockOffBoosted(Item? item, PokemonBuild defender)
    {
        if (item is null) return false;
        var species = defender.Pokemon.SpeciesId;
        if (item.IsMegaStone && item.MegaSpeciesId == species) return false;
        if (ZCrystals.Contains(item.Id)) return false;
        return item.Id switch
        {
            >= 275 and <= 290 or 684 or 1662 or 1664 => species != 493, // プレート（アルセウス）
            >= 902 and <= 918 => species != 773,                         // メモリ（シルヴァディ）
            >= 563 and <= 566 => species != 649,                         // カセット（ゲノセクト）
            467 => species != 383,                                       // べにいろのたま（グラードン）
            468 => species != 382,                                       // あいいろのたま（カイオーガ）
            442 or 1661 => species != 487,                               // はっきんだま・だいはっきんだま（ギラティナ）
            1659 => species != 483,                                      // だいこんごうだま（ディアルガ）
            1660 => species != 484,                                      // だいしらたま（パルキア）
            1161 => species != 888,                                      // くちたけん（ザシアン）
            1162 => species != 889,                                      // くちたたて（ザマゼンタ）
            2106 or 2107 or 2108 => species != 1017,                     // めん（オーガポン）
            ItemIdBoosterEnergy => defender.Ability?.Identifier is not ("protosynthesis" or "quark-drive"),
            _ => true,
        };
    }

    private const int ItemIdBoosterEnergy = 1696;

    /// <summary>Z クリスタル（持たせたポケモンからは はたきおとせない）。</summary>
    private static readonly HashSet<int> ZCrystals = new()
    {
        817, 818, 819, 820, 821, 822, 823, 824, 825, 826, 827, 828, 829, 830, 831, 832, 833, 834, 835,
        839, 840, 841, 842, 843, 844, 845, 846, 847, 877, 967, 968, 969, 970, 971, 972, 993,
    };

    /// <summary>
    /// ちからずくで威力が上がる技か。基本は「追加効果がある技」（技データの追加効果の確率）だが、
    /// 自分の能力が下がる技（インファイト・りゅうせいぐんなど）・しめつけ系などは追加効果ではないので上がらない。
    /// 逆に確率の無い追加効果の技（第 9 世代の新しい技など）もある。こうそくスピンは第 8 世代から素早さが上がる追加効果がある。
    /// </summary>
    public static bool SheerForceBoosts(Move move, int generation)
    {
        if (move.Id == MoveIdRapidSpin) return generation >= 8;
        return move.EffectChance > 0 ? !SheerForceExcluded.Contains(move.Id) : SheerForceIncluded.Contains(move.Id);
    }

    private static readonly HashSet<int> SheerForceExcluded = new()
    {
        // 第 3〜7 世代
        20, 35, 83, 128, 130, 250, 276, 315, 328, 354, 359, 370, 434, 437, 463, 479, 524, 611, 614, 620, 621, 665, 691, 705,
        // 第 5 世代以降の技のうち、追加効果ではないもの（自分の能力が下がる・威力が変わる・ダイマックス技など）
        557, 744, 745, 746, 751, 754, 755, 779, 793, 794, 795, 796, 797, 798, 799, 800, 802, 803, 804, 808, 812, 813, 814,
        817, 818, 819,
    };

    private static readonly HashSet<int> SheerForceIncluded = new()
    {
        662, 664, 677,
        // 第 9 世代の技（追加効果の確率が技データに無いもの）
        827, 828, 830, 831, 832, 836, 839, 840, 841, 843, 844, 845, 846, 847, 848, 853, 855, 856, 864, 866, 871, 872,
        884, 885, 886, 902, 903, 905, 914, 917, 918, 919,
    };

    /// <summary>
    /// きれあじで威力が上がる「切る」技か（技データに無いため ID で持つ。@smogon/calc の slicing フラグと同じ）。
    /// </summary>
    public static bool IsSlicing(Move move) => SlicingMoves.Contains(move.Id);

    private static readonly HashSet<int> SlicingMoves = new()
    {
        15, 75, 163, 210, 314, 332, 348, 400, 403, 404, 427, 440, 533, 534, 548, 669, 781, 830, 845, 860, 869, 875, 891, 895, 910, 911,
    };

    /// <summary>かぜのりで無効になる風の技か（@smogon/calc の wind フラグと同じ）。</summary>
    public static bool IsWind(Move move) => WindMoves.Contains(move.Id);

    private static readonly HashSet<int> WindMoves = new()
    {
        16, 18, 59, 177, 196, 201, 239, 257, 314, 366, 542, 572, 584, 831, 846, 847, 848,
    };

    /// <summary>ジュエル（その タイプの技の威力を 1 回だけ上げる）なら、そのタイプ。</summary>
    public static string? GemType(Item? item) => item?.Id switch
    {
        591 => "Fire", 592 => "Water", 593 => "Electric", 594 => "Grass", 595 => "Ice", 596 => "Fighting", 597 => "Poison",
        598 => "Ground", 599 => "Flying", 600 => "Psychic", 601 => "Bug", 602 => "Rock", 603 => "Ghost", 604 => "Dark",
        605 => "Steel", 668 => "Dragon", 669 => "Normal", 731 => "Fairy",
        _ => null,
    };

    /// <summary>すてみで威力が上がる反動技か（はずすと自分がダメージを受ける技を含む。技データの Recoil フラグ）。</summary>
    public static bool IsRecoil(Move move) => move.HasFlag("Recoil");
}
