namespace FaraPokemonBattleAssistant.Core;

/// <summary>
/// 確認用サイト（pokemon-dev.fara-labs.com）で開いているときは、fara-labs.com 配下のリンクを確認用の URL に置き換える。
/// 例: https://fara-labs.com/ → https://dev.fara-labs.com/。GitHub など fara-labs.com 以外のリンクはそのまま。
/// 判定は実行時のホスト名で行う（ビルドは本番と確認用で共通）。
/// </summary>
public static class SiteLinks
{
    private const string Domain = "fara-labs.com";
    private const string DevelopHost = "pokemon-dev." + Domain;

    /// <summary>いま開いているサイトのホスト名。Web の起動時に設定する。</summary>
    public static string CurrentHost { get; set; } = "";

    public static bool IsDevelop(string host) => host.Equals(DevelopHost, StringComparison.OrdinalIgnoreCase);

    public static string Localize(string url) => Localize(url, CurrentHost);

    public static string Localize(string url, string currentHost)
    {
        if (!IsDevelop(currentHost ?? "") || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var host = uri.Host.ToLowerInvariant();
        string? devHost = null;
        if (host == Domain) devHost = "dev." + Domain;
        else if (host.EndsWith("." + Domain) && !host.StartsWith("dev.") && !host[..^(Domain.Length + 1)].EndsWith("-dev"))
            devHost = host[..^(Domain.Length + 1)] + "-dev." + Domain;
        if (devHost is null) return url;
        return new UriBuilder(uri) { Host = devHost, Port = -1 }.Uri.AbsoluteUri;
    }
}
