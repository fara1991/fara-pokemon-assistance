namespace FaraPokemonBattleAssistant.Core;

/// <summary>
/// サイトのページの URL（先頭の / なし）と、以前の URL から新しい URL への対応表。
/// 古い URL は Web 側の <c>LegacyUrlRedirect</c> が新しい URL へ転送する（ブックマーク・既存のリンク用）。
/// API のパス（/api/roster）とデータのパス（/data/...）はページの URL ではないので、ここには入れない。
/// </summary>
public static class SitePaths
{
    public const string DamageCalculator = "damage-calculator";
    public const string SpeedComparison = "speed-comparison";
    public const string DataSync = "data-sync";
    public const string MyPokemon = "my-pokemon";
    public const string MyTeams = "my-teams";
    public const string TypeChart = "type-chart";
    public const string PokemonList = "pokemon-list";
    public const string BattleTerms = "battle-terms";
    public const string ChatCommands = "chat-commands";
    public const string AboutAuthor = "about-author";

    /// <summary>以前の URL → 新しい URL（どちらも先頭の / なし、小文字）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Legacy = new Dictionary<string, string>
    {
        ["speed"] = SpeedComparison,
        ["sync"] = DataSync,
        ["pokemon"] = MyPokemon,
        ["roster"] = MyPokemon,
        ["team"] = MyTeams,
        ["types"] = TypeChart,
        ["pokedex"] = PokemonList,
        ["glossary"] = BattleTerms,
        ["command"] = ChatCommands,
        ["about"] = AboutAuthor,
    };

    /// <summary>
    /// 以前の URL なら転送先を返す。パスは / の有無・大文字小文字を問わない（例: <c>/Speed/</c>）。
    /// クエリ文字列（?...）とハッシュ（#...）はそのまま引き継ぐ。以前の URL でなければ <c>null</c>。
    /// </summary>
    public static string? Redirect(string pathAndQuery)
    {
        if (pathAndQuery is null) return null;
        var end = pathAndQuery.IndexOfAny(new[] { '?', '#' });
        var path = end < 0 ? pathAndQuery : pathAndQuery[..end];
        var suffix = end < 0 ? "" : pathAndQuery[end..];
        var key = path.Trim('/').ToLowerInvariant();
        return Legacy.TryGetValue(key, out var target) ? target + suffix : null;
    }
}
