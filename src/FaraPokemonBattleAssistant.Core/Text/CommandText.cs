using System.Text;

namespace FaraPokemonBattleAssistant.Core.Text;

/// <summary>
/// コマンド文字列の下ごしらえ。全角スペース（U+3000）を半角スペースにし、連続する空白は 1 つにまとめる。
/// 全角の英数字・記号（U+FF01〜U+FF5E。ｄｍｇ・－ｐ・＋１ など）は半角にそろえる。
/// カタカナ・ひらがなは変えない（NFKC 全体はかけない）。
/// </summary>
public static class CommandText
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastSpace = false;
        foreach (var c in text)
        {
            var ch = c;
            if (ch == '　') ch = ' ';
            else if (ch is >= '！' and <= '～') ch = (char)(ch - 0xFEE0);
            if (ch is ' ' or '\t' or '\r' or '\n')
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
