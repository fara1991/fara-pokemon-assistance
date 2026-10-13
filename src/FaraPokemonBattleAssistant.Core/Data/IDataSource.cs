namespace FaraPokemonBattleAssistant.Core.Data;

/// <summary>
/// データファイル（CSV）の取得元。ローカルフォルダ、HTTP（公開サイト）などを差し替えられる。
/// </summary>
public interface IDataSource
{
    /// <summary>
    /// データルートからの相対パス（例: <c>Gen9/pokemon.csv</c>）でテキストを読む。存在しなければ <c>null</c>。
    /// </summary>
    Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>ローカルフォルダから読む（ボット・CLI 用）。</summary>
public sealed class FileDataSource : IDataSource
{
    public string RootDirectory { get; }

    public FileDataSource(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(RootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return null;
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>HTTP で読む（Blazor WebAssembly、または公開サイトからボットが取得する用途）。</summary>
public sealed class HttpDataSource : IDataSource
{
    /// <summary>
    /// 公開サイトがゲームデータ（CSV）を配っているパス（サイトの URL からの相対。例: <c>https://pokemon.fara-labs.com/</c> + <c>api/game/</c>）。
    /// 静的アセットとしてそのまま配られ、登録データの API（<c>/api/my/...</c>）と違って Worker は通らない。
    /// </summary>
    public const string GameDataPath = "api/game/";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    /// <param name="httpClient">HttpClient。</param>
    /// <param name="baseUrl">データルートの URL（例: <c>https://pokemon.fara-labs.com/api/game/</c> = サイトの URL + <see cref="GameDataPath"/>）。</param>
    public HttpDataSource(HttpClient httpClient, string baseUrl)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(_baseUrl + relativePath, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        // 公開サイトは SPA 設定（not_found_handling: single-page-application）なので、
        // 存在しないファイルにも 200 で index.html が返る。HTML はデータとして扱わず「無い」とみなす。
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            return null;
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return LooksLikeHtml(text) ? null : text;
    }

    /// <summary>
    /// 本文が HTML 文書（SPA のフォールバックの index.html など）に見えるか。データファイル（CSV・テキスト）は <c>&lt;</c> で始まらない。
    /// </summary>
    public static bool LooksLikeHtml(string text)
    {
        var t = text.AsSpan().TrimStart().TrimStart('\uFEFF').TrimStart();
        return t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 別のデータソースの結果をローカルにキャッシュする。保持期間を過ぎたファイルは取り直す。
/// 取り直しに失敗した場合は古いキャッシュをそのまま使う（配信中にネットワークが落ちても計算は続けられる）。
/// </summary>
public sealed class CachingDataSource : IDataSource
{
    private readonly IDataSource _inner;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _maxAge;

    public CachingDataSource(IDataSource inner, string cacheDirectory, TimeSpan maxAge)
    {
        _inner = inner;
        _cacheDirectory = cacheDirectory;
        _maxAge = maxAge;
    }

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var cachePath = Path.Combine(_cacheDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var missingMarker = cachePath + ".missing";
        var fresh = File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < _maxAge;
        if (fresh)
        {
            var cached = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
            // 以前の版が SPA のフォールバック（index.html）をデータとして保存していた場合は使わずに取り直す
            if (!HttpDataSource.LooksLikeHtml(cached))
                return cached;
            fresh = false;
        }
        if (File.Exists(missingMarker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missingMarker) < _maxAge)
            return null;

        string? text;
        try
        {
            text = await _inner.ReadTextAsync(relativePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken) && File.Exists(cachePath))
        {
            // ネットワーク断・HttpClient のタイムアウト（TaskCanceledException）などは古いキャッシュで続行する。
            var stale = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
            if (HttpDataSource.LooksLikeHtml(stale))
                throw;
            return stale;
        }

        // 取得できたらキャッシュを更新する。書き込みに失敗しても取得した内容はそのまま返す。
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            if (text is null)
            {
                await File.WriteAllTextAsync(missingMarker, string.Empty, cancellationToken).ConfigureAwait(false);
                // 以前の版が保存した HTML などの古いキャッシュが残っていると、取り直し失敗時にそれを返してしまうので消す
                if (File.Exists(cachePath))
                    File.Delete(cachePath);
            }
            else
            {
                await File.WriteAllTextAsync(cachePath, text, cancellationToken).ConfigureAwait(false);
                if (File.Exists(missingMarker))
                    File.Delete(missingMarker);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return text;
    }

    /// <summary>
    /// 取り直しの失敗のうち、古いキャッシュで代替してよいもの。
    /// 呼び出し側がキャンセルした <see cref="OperationCanceledException"/> は代替せずそのまま投げる。
    /// </summary>
    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        HttpRequestException or IOException or System.Net.Sockets.SocketException => true,
        _ => false,
    };
}
