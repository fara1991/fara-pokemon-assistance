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
    /// <param name="baseUrl">データルートの URL（例: <c>https://fara1991.github.io/fara-pokemon-assistance/data/</c>）。</param>
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
/// 別のデータソースの結果をローカルにキャッシュする。<paramref name="maxAge"/> を過ぎたファイルは取り直す。
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

        try
        {
            var text = await _inner.ReadTextAsync(relativePath, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            if (text is null)
            {
                await File.WriteAllTextAsync(missingMarker, string.Empty, cancellationToken).ConfigureAwait(false);
                return null;
            }
            await File.WriteAllTextAsync(cachePath, text, cancellationToken).ConfigureAwait(false);
            if (File.Exists(missingMarker))
                File.Delete(missingMarker);
            return text;
        }
        catch (HttpRequestException) when (File.Exists(cachePath))
        {
            return await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }
    }
}
