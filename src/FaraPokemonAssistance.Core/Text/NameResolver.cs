using System.Text;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Text;

/// <summary>
/// 名前の正規化。カタカナ→ひらがな、全角英数→半角、記号・空白の除去を行い、
/// 「メガリザードンＸ」「めがりざーどんx」「メガ リザードン X」が同じキーになるようにする。
/// </summary>
public static class NameNormalizer
{
    private static readonly HashSet<char> Ignored = new("()（）・「」\"'.。");

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.Normalize(NormalizationForm.FormKC))
        {
            var c = raw;
            if (c >= 'ァ' && c <= 'ヶ') c = (char)(c - 0x60); // カタカナ → ひらがな
            if (char.IsWhiteSpace(c) || Ignored.Contains(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>解決結果。1 件に決まったか、候補が複数か、見つからなかったか。</summary>
public sealed class NameMatch<T>
{
    public T? Value { get; init; }
    public IReadOnlyList<T> Candidates { get; init; } = Array.Empty<T>();
    public bool IsResolved => Value is not null;
    public bool IsAmbiguous => Value is null && Candidates.Count > 1;
}

/// <summary>
/// 表記ゆれを吸収して名前から項目を探す。完全一致 → 別名 → 前方一致 → 部分一致の順。
/// </summary>
public sealed class NameResolver<T> where T : class
{
    private readonly Func<T, string> _name;
    private readonly List<(string Key, T Item)> _entries;
    private readonly Dictionary<string, T> _exact;

    public NameResolver(IEnumerable<T> items, Func<T, string> nameSelector, IEnumerable<(string Alias, string Name)>? aliases = null)
    {
        _name = nameSelector;
        var list = items.ToList();
        _entries = list.Select(i => (NameNormalizer.Normalize(nameSelector(i)), i)).ToList();
        _exact = new Dictionary<string, T>();
        foreach (var (key, item) in _entries)
            _exact.TryAdd(key, item);
        if (aliases is not null)
        {
            var byName = new Dictionary<string, T>();
            foreach (var item in list)
                byName.TryAdd(nameSelector(item), item);
            foreach (var (alias, name) in aliases)
            {
                if (byName.TryGetValue(name, out var target))
                    _exact.TryAdd(NameNormalizer.Normalize(alias), target);
            }
        }
    }

    public NameMatch<T> Resolve(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new NameMatch<T>();

        foreach (var variant in Variants(query))
        {
            var match = ResolveKey(variant);
            if (match.IsResolved || match.IsAmbiguous)
                return match;
        }
        return new NameMatch<T>();
    }

    private NameMatch<T> ResolveKey(string key)
    {
        if (_exact.TryGetValue(key, out var exact))
            return new NameMatch<T> { Value = exact, Candidates = new[] { exact } };

        var prefix = _entries.Where(e => e.Key.StartsWith(key, StringComparison.Ordinal)).Select(e => e.Item).Distinct().ToList();
        if (prefix.Count == 1)
            return new NameMatch<T> { Value = prefix[0], Candidates = prefix };
        if (prefix.Count > 1)
        {
            // 「リザードン」で「リザードン」「メガリザードンX」が両方当たる場合は短い方を優先
            var shortest = prefix.OrderBy(i => _name(i).Length).ToList();
            if (_name(shortest[0]).Length < _name(shortest[1]).Length)
                return new NameMatch<T> { Value = shortest[0], Candidates = shortest };
            return new NameMatch<T> { Candidates = shortest };
        }

        var contains = _entries.Where(e => e.Key.Contains(key, StringComparison.Ordinal)).Select(e => e.Item).Distinct().ToList();
        if (contains.Count == 1)
            return new NameMatch<T> { Value = contains[0], Candidates = contains };
        return new NameMatch<T> { Candidates = contains.OrderBy(i => _name(i).Length).ToList() };
    }

    private static IEnumerable<string> Variants(string query)
    {
        var key = NameNormalizer.Normalize(query);
        yield return key;

        // ♂♀ の表記ゆれ: 「イエッサンオス」「イエッサン(♂)」「イエッサン♂」
        var male = key.Replace("おす", "♂");
        var female = key.Replace("めす", "♀");
        if (male != key) yield return male;
        if (female != key) yield return female;

        // ♂ 指定のデータが無ければ無印（通常は♂が基本フォルム）
        if (key.Contains('♂')) yield return key.Replace("♂", "");
        // 「メガ」「キョダイ」などの接頭辞のみの入力は接頭辞をそのまま前方一致に任せる
    }
}
