using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FaraPokemonBattleAssistant.Core.Roster;

/// <summary>
/// 登録データをクラウド（公開サイトの <c>/api/roster</c>）に保存する。サイトとボットで同じアクセスキーを使うと、同じデータを読み書きする。
/// </summary>
/// <remarks>
/// <para>
/// アクセスキーは 256 bit の乱数を base64url（パディングなし・43 文字）にしたもの（<see cref="GenerateKey"/>）。パスワードと同じ扱いで、
/// ログや例外のメッセージには出さない。サーバーには <c>Authorization: Bearer</c> ヘッダーで送る（URL には入れない）。
/// </para>
/// <para>
/// 書き込みは楽観的排他。<see cref="LoadAsync"/> で読んだ版（<see cref="Revision"/>）を基準に <see cref="SaveAsync"/> し、
/// 他の場所が先に書いていたら <see cref="RosterConflictException"/> を投げる（<see cref="RosterRepository"/> は読み直してやり直す）。
/// </para>
/// </remarks>
public sealed class RemoteRosterStore : IRosterStore
{
    /// <summary>本番サイト。</summary>
    public const string ProductionBaseUrl = "https://pokemon.fara-labs.com/";
    /// <summary>確認用サイト（データは本番と別。Cloudflare Access で /api/roster を Bypass にしておく必要がある）。</summary>
    public const string DevelopBaseUrl = "https://pokemon-dev.fara-labs.com/";
    /// <summary>アクセスキーの文字数。</summary>
    public const int KeyLength = 43;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // 日本語を \uXXXX にしない（本文の上限 256 KB に対してサイズを抑える）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _key;

    /// <param name="http">使い回す HttpClient（BaseAddress は使わない）。</param>
    /// <param name="apiBaseUrl">サイトの URL（例: <see cref="ProductionBaseUrl"/>）。末尾の / は無くてもよい。</param>
    /// <param name="syncKey">アクセスキー（<see cref="IsValidKey"/> を満たすもの）。</param>
    public RemoteRosterStore(HttpClient http, string apiBaseUrl, string syncKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (!IsValidKey(syncKey))
            throw new ArgumentException("アクセスキーの形式が正しくありません（43 文字の英数字・-・_）", nameof(syncKey));
        if (!Uri.TryCreate(apiBaseUrl.EndsWith('/') ? apiBaseUrl : apiBaseUrl + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("サイトの URL が正しくありません", nameof(apiBaseUrl));
        _endpoint = new Uri(baseUri, "api/roster");
        _key = syncKey;
    }

    /// <summary>他の端末・ボットからも書き換えられるので、<see cref="RosterRepository"/> は読み書きのたびに読み直す。</summary>
    public bool AlwaysReload => true;

    /// <summary>最後に読んだ（または保存した）版。まだ読んでいなければ null（保存は 0 = 空の置き場を基準にする）。</summary>
    public long? Revision { get; private set; }

    /// <summary>最後にクラウドとの読み書きが成功した時刻。</summary>
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>新しいアクセスキーを作る（暗号論的乱数 256 bit → base64url・パディングなし）。</summary>
    public static string GenerateKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>アクセスキーの形式（43 文字の A-Z a-z 0-9 - _）か。</summary>
    public static bool IsValidKey(string? key)
    {
        if (key is null || key.Length != KeyLength) return false;
        foreach (var c in key)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        }
        return true;
    }

    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Unavailable(response);
        var (revision, roster) = await ReadStateAsync(response, ct).ConfigureAwait(false);
        Revision = revision;
        LastSyncedAt = DateTimeOffset.Now;
        return roster;
    }

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        using var content = BuildPutContent(Revision ?? 0, json);
        using var response = await SendAsync(HttpMethod.Put, content, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var (current, _) = await ReadStateAsync(response, ct).ConfigureAwait(false);
            Revision = current;
            throw new RosterConflictException(current);
        }
        if (response.StatusCode != HttpStatusCode.OK) throw Unavailable(response);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        Revision = doc.RootElement.ValueKind == JsonValueKind.Object && TryGetRevision(doc.RootElement, out var rev)
            ? rev
            : throw InvalidResponse();
        LastSyncedAt = DateTimeOffset.Now;
    }

    /// <summary>
    /// このキーの置き場を消す（キーを作り直したあと、古いキーに対して呼ぶ）。以後このキーは使えなくなる
    /// （<see cref="RosterUnavailableReason.KeyRetired"/>）。
    /// </summary>
    public async Task DeleteAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent) throw Unavailable(response);
        Revision = null;
    }

    private static ByteArrayContent BuildPutContent(long baseRevision, string json)
    {
        using var roster = JsonDocument.Parse(json);
        if (roster.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("登録データは JSON のオブジェクトにしてください", nameof(json));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("baseRevision", baseRevision);
            writer.WritePropertyName("roster");
            roster.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }
        var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            throw new RosterUnavailableException(RosterUnavailableReason.Network, null,
                "クラウドに接続できませんでした。ネットワークの状態を確認してください", ex);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)) throw InvalidResponse();
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private static async Task<(long Revision, string? Roster)> ReadStateAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetRevision(root, out var revision)
            || !root.TryGetProperty("roster", out var roster))
            throw InvalidResponse();
        return roster.ValueKind switch
        {
            JsonValueKind.Object => (revision, roster.GetRawText()),
            JsonValueKind.Null => (revision, null),
            _ => throw InvalidResponse(),
        };
    }

    private static bool TryGetRevision(JsonElement root, out long revision)
    {
        revision = 0;
        return root.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out revision) && revision >= 0;
    }

    private static RosterUnavailableException InvalidResponse() =>
        new(RosterUnavailableReason.InvalidResponse, null,
            "クラウドから想定外の応答がありました（確認用サイトでは Cloudflare Access の設定を確認してください）");

    private static RosterUnavailableException Unavailable(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status switch
        {
            401 => new(RosterUnavailableReason.Unauthorized, status, "アクセスキーが正しくありません"),
            410 => new(RosterUnavailableReason.KeyRetired, status, "このアクセスキーは作り直されたため使えません。新しいアクセスキーを設定してください"),
            413 => new(RosterUnavailableReason.TooLarge, status, "登録データが大きすぎてクラウドに保存できません"),
            400 => new(RosterUnavailableReason.Rejected, status, "クラウドに保存できませんでした（データの形式が正しくありません）"),
            >= 500 => new(RosterUnavailableReason.Server, status, $"クラウドでエラーが発生しました（HTTP {status}）。時間をおいて試してください"),
            _ => new(RosterUnavailableReason.InvalidResponse, status,
                $"クラウドから想定外の応答がありました（HTTP {status}。確認用サイトでは Cloudflare Access の設定を確認してください）"),
        };
    }
}

/// <summary>クラウドの登録データが使えなかった理由。</summary>
public enum RosterUnavailableReason
{
    /// <summary>接続できない（回線断・タイムアウトなど）。</summary>
    Network,
    /// <summary>アクセスキーが正しくない（401）。</summary>
    Unauthorized,
    /// <summary>アクセスキーが作り直されて使えなくなった（410）。</summary>
    KeyRetired,
    /// <summary>データが大きすぎる（413）。</summary>
    TooLarge,
    /// <summary>送ったデータが受け付けられなかった（400）。</summary>
    Rejected,
    /// <summary>サーバーのエラー（5xx）。</summary>
    Server,
    /// <summary>想定外の応答（JSON でない、Cloudflare Access のログイン画面など）。</summary>
    InvalidResponse,
}

/// <summary>クラウドの登録データを読み書きできなかった。メッセージはそのまま利用者に見せられる（アクセスキーは含まない）。</summary>
public sealed class RosterUnavailableException : Exception
{
    public RosterUnavailableException(RosterUnavailableReason reason, int? statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
        StatusCode = statusCode;
    }

    public RosterUnavailableReason Reason { get; }
    /// <summary>HTTP のステータスコード（接続できなかったときは null）。</summary>
    public int? StatusCode { get; }
}

/// <summary>保存しようとしたら、読んだあとに他の場所（別の端末・ボット）が書き換えていた。</summary>
public sealed class RosterConflictException : Exception
{
    public RosterConflictException(long currentRevision)
        : base("他の場所で登録データが更新されていたため、保存できませんでした。読み込み直してください")
    {
        CurrentRevision = currentRevision;
    }

    /// <summary>クラウドの現在の版。</summary>
    public long CurrentRevision { get; }
}
