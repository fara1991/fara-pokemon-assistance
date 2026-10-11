namespace FaraPokemonAssistance.Core.Battle;

/// <summary>
/// タイプ相性表に出す記号。○ 2 倍、◎ 4 倍、△ ½ 倍、▲ ¼ 倍、× 効果なし、空欄は等倍。
/// ⅛ 倍は ▲ のまま、表示側で小さく「⅛」を添える（<see cref="IsEighth"/>）。
/// 1.5 倍など 2 の累乗でない倍率（特性で変わったとき）は、向きだけ分かるよう ○ / △ の 1 つにする。
/// </summary>
public static class TypeMarks
{
    /// <summary>倍率が 2 の何段階分か（等倍と無効は 0、最大 3）。2 の累乗でない倍率は 1。</summary>
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
        var strong = Count(multiplier) >= 2;
        return multiplier > 1 ? (strong ? "◎" : "○") : (strong ? "▲" : "△");
    }

    /// <summary>⅛ 倍か（▲ に小さく ⅛ を添える）。</summary>
    public static bool IsEighth(double multiplier) => Math.Abs(multiplier - 0.125) < 1e-9;
}
