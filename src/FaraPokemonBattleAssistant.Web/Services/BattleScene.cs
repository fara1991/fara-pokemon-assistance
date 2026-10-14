namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 「対戦画面で表示」に出す内容。ページ（ダメージ計算機・素早さ比較）が結果を計算するたびに作り、BattleStage に渡す。
/// 自分側（Mine）は背景のフィールドの左下、相手側（Foe）は右上に出る。値だけの record なので、同じ内容なら等しい。
/// </summary>
public abstract record BattleScene;

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
    string Effectiveness) : BattleScene;

/// <summary>ポケモン登録で選んでいる技 1 つ。</summary>
public sealed record MoveSlot(string Name, string Type);

/// <summary>ポケモン登録の入力中の内容。登録するポケモンが自分側（左下）、選んだ技（最大 4 つ）が右下に出る。相手側は背景に立っている相手。</summary>
public sealed record RegisterScene(string MineName, int MaxHp, IReadOnlyList<MoveSlot> Moves) : BattleScene
{
    // 技の一覧は参照ではなく中身で比べる（同じ内容なら描き直さない）
    public bool Equals(RegisterScene? other) =>
        other is not null && MineName == other.MineName && MaxHp == other.MaxHp && Moves.SequenceEqual(other.Moves);

    public override int GetHashCode() => HashCode.Combine(MineName, MaxHp, Moves.Count);
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
