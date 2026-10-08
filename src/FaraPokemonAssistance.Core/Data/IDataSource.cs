namespace FaraPokemonAssistance.Core.Data;

/// <summary>
/// データファイル（CSV）の取得元。ローカルフォルダ、HTTP（GitHub Pages）などを差し替えられる。
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

/// <summary>HTTP で読む（Blazor WebAssembly、または公開済みの GitHub Pages からボットが取得する用途）。</summary>
public sealed class HttpDataSource : IDataSource
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    /// <param name="httpClient">HttpClient。</param>
    /// <param name="baseUrl">データルートの URL（例: <c>https://pokemon.app-fara.com/data/</c>）。</param>
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
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            return await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
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
            return await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }

        // 取得できたらキャッシュを更新する。書き込みに失敗しても取得した内容はそのまま返す。
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            if (text is null)
            {
                await File.WriteAllTextAsync(missingMarker, string.Empty, cancellationToken).ConfigureAwait(false);
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
