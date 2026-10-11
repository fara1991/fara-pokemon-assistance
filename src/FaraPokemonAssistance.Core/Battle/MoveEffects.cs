using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>
/// 技の追加効果・反動・持ち物に関する判定（全世代・今のゲームのダメージ計算で共通）。
/// 判定は公開されているダメージ計算ツール（@smogon/calc）に合わせ、tests の move_lists.json（tools/calc_vectors/move_lists.js で作成）と照合している。
/// </summary>
public static class MoveEffects
{
    private const int MoveIdRapidSpin = 229;

    /// <summary>はたきおとすの威力が上がるか（相手が持ち物を持っていて、それが自分のメガストーンではない）。</summary>
    public static bool KnockOffBoosted(Item? item, PokemonBuild defender) =>
        item is not null && !(item.IsMegaStone && item.MegaSpeciesId == defender.Pokemon.SpeciesId);

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
