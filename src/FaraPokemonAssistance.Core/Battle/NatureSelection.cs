using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>「上がる能力」「下がる能力」のボタンから性格を決める。</summary>
public static class NatureSelection
{
    /// <summary>性格で上下する能力（HP 以外）。ボタンの並び順。</summary>
    public static IReadOnlyList<Stat> Stats { get; } = new[] { Stat.Attack, Stat.Defense, Stat.SpAttack, Stat.SpDefense, Stat.Speed };

    /// <summary>
    /// 上がる能力を選び直した性格。null なら補正なし。
    /// 下がる能力は今のまま。今が補正なし、または同じ能力なら、使わない方の攻撃能力（物理なら特攻、特殊なら攻撃）を下げる。
    /// </summary>
    public static Nature? WithIncreased(IReadOnlyList<Nature> natures, Nature? current, Stat? up, bool physical = true)
    {
        if (up is null) return Neutral(natures);
        var down = current?.DecreasedStat;
        if (down is null || down == up)
            down = up == Stat.Attack ? Stat.SpAttack : up == Stat.SpAttack ? Stat.Attack : physical ? Stat.SpAttack : Stat.Attack;
        return Find(natures, up.Value, down.Value) ?? current;
    }

    /// <summary>
    /// 下がる能力を選び直した性格。null なら補正なし。
    /// 上がる能力は今のまま。今が補正なし、または同じ能力なら、使う方の攻撃能力（物理なら攻撃、特殊なら特攻）を上げる。
    /// </summary>
    public static Nature? WithDecreased(IReadOnlyList<Nature> natures, Nature? current, Stat? down, bool physical = true)
    {
        if (down is null) return Neutral(natures);
        var up = current?.IncreasedStat;
        if (up is null || up == down)
            up = down == Stat.Attack ? Stat.SpAttack : down == Stat.SpAttack ? Stat.Attack : physical ? Stat.Attack : Stat.SpAttack;
        return Find(natures, up.Value, down.Value) ?? current;
    }

    /// <summary>補正なしの性格（まじめ。無ければ最初の補正なし）。</summary>
    public static Nature? Neutral(IReadOnlyList<Nature> natures) =>
        natures.FirstOrDefault(n => n.Name == "まじめ") ?? natures.FirstOrDefault(n => n.IsNeutral);

    private static Nature? Find(IReadOnlyList<Nature> natures, Stat up, Stat down) =>
        natures.FirstOrDefault(n => n.IncreasedStat == up && n.DecreasedStat == down);
}
