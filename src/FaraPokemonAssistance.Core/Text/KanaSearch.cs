using System.Text;

namespace FaraPokemonAssistance.Core.Text;

/// <summary>
/// 名前の部分一致検索。カタカナ・ひらがなを区別せず、ローマ字（ヘボン式・訓令式どちらも）でも探せる。
/// 例: 「ie」「いえ」「イエ」はどれもイエッサンに一致する。
/// </summary>
public static class KanaSearch
{
    /// <summary><paramref name="text"/> が <paramref name="query"/> を含むか。何度も調べるときは <see cref="Create"/> を使う。</summary>
    public static bool Matches(string text, string query) => Create(query)(text);

    /// <summary>
    /// 検索語から一致判定を作る（ローマ字の変換は 1 回だけ）。
    /// 検索語そのままでの一致に加えて、ローマ字をひらがなにした文字列でも探す。
    /// 末尾が打ちかけのローマ字（「iek」の k など）なら、その続きになりうるかなのどれかが続く名前に一致させる。
    /// 長音（ー）は無くても一致する（「rizadon」→ リザードン）。
    /// </summary>
    public static Func<string, bool> Create(string query)
    {
        var q = Normalize(query ?? "").Trim();
        if (q.Length == 0) return _ => true;
        if (!q.Any(IsAsciiLetter)) return text => Normalize(text).Contains(q, StringComparison.Ordinal);

        var (kana, rest) = RomajiToHiragana(q);
        var nexts = rest.Length == 0 ? new[] { "" } : NextKana(rest);
        var patterns = nexts.Select(n => kana + n).Where(p => p.Length > 0).Distinct().ToArray();
        var loose = patterns.Select(p => p.Replace("ー", "")).Where(p => p.Length > 0).Distinct().ToArray();
        return text =>
        {
            var t = Normalize(text);
            if (t.Contains(q, StringComparison.Ordinal)) return true;
            if (patterns.Any(p => t.Contains(p, StringComparison.Ordinal))) return true;
            var tLoose = t.Replace("ー", "");
            return loose.Any(p => tLoose.Contains(p, StringComparison.Ordinal));
        };
    }

    /// <summary>比較用に、カタカナをひらがなに、全角英数を半角に、英字を小文字にする。</summary>
    public static string Normalize(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            var c = ch;
            if (c >= 'ァ' && c <= 'ヶ') c = (char)(c - 0x60);           // カタカナ → ひらがな（ヴ → ゔ も含む）
            else if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);      // 全角英数記号 → 半角
            if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// ローマ字をひらがなにする。英字以外はそのまま（「-」は長音「ー」）。
    /// 末尾の、まだかなにならない英字（「k」「sh」「ky」など）は <c>Pending</c> に残す。
    /// </summary>
    public static (string Kana, string Pending) RomajiToHiragana(string input)
    {
        var s = Normalize(input);
        var sb = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (!IsAsciiLetter(c))
            {
                sb.Append(c == '-' ? 'ー' : c);
                i++;
                continue;
            }

            if (c == 'n')
            {
                if (i + 1 >= s.Length) return (sb.ToString(), "n");
                var next = s[i + 1];
                if (next == '\'')
                {
                    sb.Append('ん');
                    i += 2;
                    continue;
                }
                if (next == 'n')
                {
                    // 「nn」は「ん」。ただし後ろが母音・y なら最初の n だけを「ん」にする（konnichiha → こんにちは）
                    var after = i + 2 < s.Length ? s[i + 2] : '\0';
                    sb.Append('ん');
                    i += IsVowel(after) || after == 'y' ? 1 : 2;
                    continue;
                }
                if (!IsVowel(next) && next != 'y')
                {
                    sb.Append('ん');
                    i++;
                    continue;
                }
            }

            // 子音の重ね（kk, tt, ss ...）と「tch」は「っ」
            if (i + 1 < s.Length && IsConsonant(c) && c != 'n' && (s[i + 1] == c || (c == 't' && s[i + 1] == 'c')))
            {
                sb.Append('っ');
                i++;
                continue;
            }

            var matched = false;
            for (var len = Math.Min(MaxKeyLength, s.Length - i); len >= 1; len--)
            {
                if (Table.TryGetValue(s.Substring(i, len), out var kana))
                {
                    sb.Append(kana);
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (matched) continue;

            // 末尾が打ちかけ（どれかのつづりの先頭）なら残す
            var tail = s[i..];
            if (tail.All(IsAsciiLetter) && Table.Keys.Any(k => k.StartsWith(tail, StringComparison.Ordinal)))
                return (sb.ToString(), tail);

            sb.Append(c);
            i++;
        }
        return (sb.ToString(), "");
    }

    /// <summary>打ちかけのローマ字の続きになりうるかな（「k」→ か・き・く・け・こ・きゃ…、「n」→ ん・な…）。</summary>
    private static string[] NextKana(string rest)
    {
        var list = Table.Where(kv => kv.Key.StartsWith(rest, StringComparison.Ordinal)).Select(kv => kv.Value).ToList();
        if (rest == "n") list.Add("ん");
        // 子音 1 文字（「ies」の s）は、続けて同じ子音を打てば「っ」になる
        if (rest.Length == 1 && IsConsonant(rest[0]) && rest[0] != 'n') list.Add("っ");
        return list.Distinct().ToArray();
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsVowel(char c) => c is 'a' or 'i' or 'u' or 'e' or 'o';
    private static bool IsConsonant(char c) => IsAsciiLetter(c) && !IsVowel(c);

    private static readonly Dictionary<string, string> Table = BuildTable();
    private static readonly int MaxKeyLength = Table.Keys.Max(k => k.Length);

    private static Dictionary<string, string> BuildTable()
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal);
        void Row(string consonant, string kana)
        {
            var vowels = new[] { "a", "i", "u", "e", "o" };
            var chars = kana.Split(' ');
            for (var v = 0; v < 5; v++)
                if (chars[v] != "_") t[consonant + vowels[v]] = chars[v];
        }

        Row("", "あ い う え お");
        Row("k", "か き く け こ");
        Row("s", "さ し す せ そ");
        Row("t", "た ち つ て と");
        Row("n", "な に ぬ ね の");
        Row("h", "は ひ ふ へ ほ");
        Row("m", "ま み む め も");
        Row("y", "や _ ゆ いぇ よ");
        Row("r", "ら り る れ ろ");
        Row("l", "ぁ ぃ ぅ ぇ ぉ");
        Row("x", "ぁ ぃ ぅ ぇ ぉ");
        Row("w", "わ うぃ う うぇ うぉ");
        Row("g", "が ぎ ぐ げ ご");
        Row("z", "ざ じ ず ぜ ぞ");
        Row("d", "だ ぢ づ で ど");
        Row("b", "ば び ぶ べ ぼ");
        Row("p", "ぱ ぴ ぷ ぺ ぽ");
        Row("f", "ふぁ ふぃ ふ ふぇ ふぉ");
        Row("v", "ゔぁ ゔぃ ゔ ゔぇ ゔぉ");
        Row("j", "じゃ じ じゅ じぇ じょ");
        Row("c", "か し く せ こ");

        // 拗音
        foreach (var (head, i) in new[] { ("k", "き"), ("s", "し"), ("t", "ち"), ("n", "に"), ("h", "ひ"), ("m", "み"), ("r", "り"),
                     ("g", "ぎ"), ("z", "じ"), ("d", "ぢ"), ("b", "び"), ("p", "ぴ"), ("j", "じ"), ("c", "ち") })
        {
            t[head + "ya"] = i + "ゃ";
            t[head + "yi"] = i + "ぃ";
            t[head + "yu"] = i + "ゅ";
            t[head + "ye"] = i + "ぇ";
            t[head + "yo"] = i + "ょ";
        }
        foreach (var (head, i) in new[] { ("sh", "し"), ("ch", "ち") })
        {
            t[head + "a"] = i + "ゃ";
            t[head + "i"] = i;
            t[head + "u"] = i + "ゅ";
            t[head + "e"] = i + "ぇ";
            t[head + "o"] = i + "ょ";
        }
        t["tsu"] = "つ";
        t["tsa"] = "つぁ";
        t["tsi"] = "つぃ";
        t["tse"] = "つぇ";
        t["tso"] = "つぉ";
        t["thi"] = "てぃ";
        t["thu"] = "てゅ";
        t["dhi"] = "でぃ";
        t["dhu"] = "でゅ";
        t["twu"] = "とぅ";
        t["dwu"] = "どぅ";
        t["hu"] = "ふ";
        t["fu"] = "ふ";
        t["wo"] = "うぉ"; // ポケモンや技の名前では「を」より「ウォ」が多い
        t["wa"] = "わ";
        t["xtu"] = "っ";
        t["ltu"] = "っ";
        t["xtsu"] = "っ";
        t["ltsu"] = "っ";
        t["xya"] = "ゃ";
        t["xyu"] = "ゅ";
        t["xyo"] = "ょ";
        t["lya"] = "ゃ";
        t["lyu"] = "ゅ";
        t["lyo"] = "ょ";
        t["xwa"] = "ゎ";
        t["nn"] = "ん";
        return t;
    }
}
