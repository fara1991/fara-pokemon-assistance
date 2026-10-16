namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 「対戦画面で表示」に出す内容。ページ（ダメージ計算機・素早さ比較）が結果を計算するたびに作り、BattleStage に渡す。
/// 自分側（Mine）は背景のフィールドの左下、相手側（Foe）は右上に出る。値だけの record なので、同じ内容なら等しい。
/// </summary>
public abstract record BattleScene;

/// <summary>
/// まだ出す結果が無いとき（ポケモンを選んでいない・まだ推定していない）の対戦画面。フィールドと背景のポケモンだけを出し、下の枠に案内を出す。
/// 「対戦画面で表示」をオンにしたら、結果が無くても対戦画面にするためのもの。
/// </summary>
public sealed record WaitingScene(string Hint) : BattleScene;

/// <summary>ダメージ計算機の結果（選んでいる技 1 つ分）。攻撃側が自分側、防御側が相手側。</summary>
public sealed record DamageScene(
    string AttackerName,
    int AttackerHp,
    int AttackerMaxHp,
    string DefenderName,
    int DefenderMaxHp,
    int DefenderStartHp,
    int MinDamage,
    int MaxDamage,
    bool Immune,
    string MoveName,
    string MoveType,
    string RangeText,
    string KnockOut,
    bool KnockOutGuaranteed,
    string Effectiveness,
    string? CritRangeText,
    string? CritKnockOut,
    bool CritKnockOutGuaranteed) : BattleScene;

/// <summary>自分のポケモンで選んでいる技 1 つ。</summary>
public sealed record MoveSlot(string Name, string Type, int Power);

/// <summary>自分のポケモンの入力中の内容。登録するポケモンが自分側（左下）、選んだ技（最大 4 つ）が右下に出る。相手側は背景に立っている相手。</summary>
public sealed record RegisterScene(string MineName, int MaxHp, IReadOnlyList<MoveSlot> Moves) : BattleScene
{
    // 技の一覧は参照ではなく中身で比べる（同じ内容なら描き直さない）
    public bool Equals(RegisterScene? other) =>
        other is not null && MineName == other.MineName && MaxHp == other.MaxHp && Moves.SequenceEqual(other.Moves);

    public override int GetHashCode() => HashCode.Combine(MineName, MaxHp, Moves.Count);
}

/// <summary>推定の結果 1 行（性格補正ごと）。<paramref name="Mod"/> は "up" / "none" / "down"。</summary>
public sealed record EstimateBandLine(string Label, string Mod, string Text, string StatText);

/// <summary>
/// 「相手の振り方を推定」の結果。攻撃側が自分側（左下）、相手が右上（ポイント・性格は不明）で、技と受けたダメージ・推定した実数値とポイントの範囲を出す。
/// 推定を実行した時点の内容（入力を変えても、もう一度推定するまで変わらない）。
/// </summary>
public sealed record EstimateScene(
    string AttackerName,
    int AttackerHp,
    int AttackerMaxHp,
    string DefenderName,
    string MoveName,
    string MoveType,
    string Observed,
    string StatLetter,
    string? StatRange,
    IReadOnlyList<EstimateBandLine> Bands,
    string? HpText) : BattleScene
{
    // 行の一覧は参照ではなく中身で比べる（同じ内容なら描き直さない）
    public bool Equals(EstimateScene? other) =>
        other is not null && AttackerName == other.AttackerName && AttackerHp == other.AttackerHp && AttackerMaxHp == other.AttackerMaxHp
        && DefenderName == other.DefenderName && MoveName == other.MoveName && MoveType == other.MoveType && Observed == other.Observed
        && StatLetter == other.StatLetter && StatRange == other.StatRange && HpText == other.HpText && Bands.SequenceEqual(other.Bands);

    public override int GetHashCode() => HashCode.Combine(AttackerName, DefenderName, MoveName, Observed, StatRange, Bands.Count);
}

/// <summary>素早さ比較の結果。ポケモン1が自分側、ポケモン2が相手側。</summary>
public sealed record SpeedScene(
    string MineName,
    int MineSpeed,
    string FoeName,
    int FoeSpeed,
    int Axis,
    bool MineFirst,
    bool FoeFirst,
    string Verdict) : BattleScene;
