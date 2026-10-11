namespace FaraPokemonAssistance.Core.Battle;

/// <summary>
/// タイプ相性表に出す記号。◎ 効果抜群、△ いまひとつ、× 効果なし、空欄は等倍。
/// 4 倍・¼ 倍のように 2 倍（½ 倍）を重ねた倍率は記号を重ねる（◎◎ / △△。⅛ 倍は △△△）。
/// 1.5 倍など 2 の累乗でない倍率（特性で変わったとき）は、向きだけ分かるよう記号 1 つにする。
/// </summary>
public static class TypeMarks
{
    /// <summary>記号を重ねる数（等倍と無効は 0）。2 の累乗の倍率は何段階かを表し、それ以外は 1。</summary>
    public static int Count(double multiplier)
    {
        if (multiplier <= 0 || multiplier == 1) return 0;
        var exp = Math.Log2(multiplier);
        var steps = Math.Abs(exp);
        var rounded = Math.Round(steps);
        var exact = Math.Abs(steps - rounded) < 1e-9;
        return (int)Math.Clamp(exact ? rounded : 1, 1, 3);
    }

    /// <summary>表のマスに出す記号（等倍は空文字）。</summary>
    public static string Symbol(double multiplier)
    {
        if (multiplier == 0) return "×";
        if (multiplier == 1) return "";
        return new string(multiplier > 1 ? '◎' : '△', Count(multiplier));
    }
}
