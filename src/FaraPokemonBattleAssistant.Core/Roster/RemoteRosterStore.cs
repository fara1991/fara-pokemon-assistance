using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FaraPokemonBattleAssistant.Core.Roster;

/// <summary>
/// 登録データをクラウド（公開サイトの <c>/api/my-pokemon</c>・<c>/api/my-team</c>）に保存する。サイトとボットで同じアクセスキーを使うと、同じデータを読み書きする。
/// </summary>
/// <remarks>
/// <para>
/// アクセスキーは 256 bit の乱数を base64url（パディングなし・43 文字）にしたもの（<see cref="GenerateKey"/>）。パスワードと同じ扱いで、
/// ログや例外のメッセージには出さない。サーバーには <c>Authorization: Bearer</c> ヘッダーで送る（URL には入れない）。
/// </para>
/// <para>
/// ポケモンとチームは別の API だが、サーバーでは 1 つの文書として保存していて、版（<see cref="Revision"/>）は共通。
/// 読み込みは両方を GET して組み立て、2 回の GET の間に他の場所が書いて版が食い違ったら読み直す。
/// </para>
/// <para>
/// 書き込みは楽観的排他。<see cref="LoadAsync"/> で読んだ版を基準に <see cref="SavePartAsync"/> し、
/// 他の場所が先に書いていたら <see cref="RosterConflictException"/> を投げる（<see cref="RosterRepository"/> は読み直してやり直す）。
/// ポケモンを消したときにチームから外す・空のチームを消す・使用中を解除する処理は、サーバーが同じ処理の中で行う。
/// </para>
/// </remarks>
public sealed class RemoteRosterStore : IRosterStore
{
    /// <summary>本番サイト。</summary>
    public const string ProductionBaseUrl = "https://pokemon.fara-labs.com/";
    /// <summary>
    /// 確認用サイト（データは本番と別。Cloudflare Access で /api/my-pokemon・/api/my-team・/api/access-key を Bypass にしておく必要がある）。
    /// </summary>
    public const string DevelopBaseUrl = "https://pokemon-dev.fara-labs.com/";
    /// <summary>アクセスキーの文字数。</summary>
    public const int KeyLength = 43;
    /// <summary>読み込みで版が食い違ったときに読み直す回数。</summary>
    public const int MaxLoadRetries = 3;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // 日本語を \uXXXX にしない（本文の上限 256 KB に対してサイズを抑える）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ItemOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly Uri _pokemonEndpoint;
    private readonly Uri _teamsEndpoint;
    private readonly Uri _accessKeyEndpoint;
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
        _pokemonEndpoint = new Uri(baseUri, "api/my-pokemon");
        _teamsEndpoint = new Uri(baseUri, "api/my-team");
        _accessKeyEndpoint = new Uri(baseUri, "api/access-key");
        _key = syncKey;
    }

    /// <summary>他の端末・ボットからも書き換えられるので、<see cref="RosterRepository"/> は読み書きのたびに読み直す。</summary>
    public bool AlwaysReload => true;

    /// <summary>最後に読んだ（または保存した）版。まだ読んでいなければ null（保存は 0 = 空の置き場を基準にする）。</summary>
    public long? Revision { get; private set; }

    /// <summary>最後にクラウドとの読み書きが成功した時刻。</summary>
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>新しいアクセスキーを作る（暗号論的乱数 256 bit → base64url・パディングなし）。サーバーへの登録は要らない。</summary>
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

    /// <summary>
    /// ポケモン（/api/my-pokemon）とチーム（/api/my-team）を読み、登録データ全体（<see cref="RosterState"/> の JSON）に組み立てる。
    /// どのゲームにもデータが無ければ null。
    /// </summary>
    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var pokemon = await GetAsync(_pokemonEndpoint, ct).ConfigureAwait(false);
            using var teams = await GetAsync(_teamsEndpoint, ct).ConfigureAwait(false);
            var (pokemonRevision, pokemonSets) = ReadDataSets(pokemon);
            var (teamRevision, teamSets) = ReadDataSets(teams);
            if (pokemonRevision != teamRevision)
            {
                // 2 回の GET の間に他の場所が書いた。読み直す
                if (attempt < MaxLoadRetries) continue;
                throw new RosterUnavailableException(RosterUnavailableReason.Server, null,
                    "他の場所で続けて更新されているため、登録データを読み込めませんでした。時間をおいて試してください");
            }
            var json = Combine(pokemonSets, teamSets);
            Revision = pokemonRevision;
            LastSyncedAt = DateTimeOffset.Now;
            return json;
        }
    }

    /// <summary>
    /// 登録データ全体を、ポケモン（/api/my-pokemon）→ チーム（/api/my-team）の順で保存する。
    /// アクセスキーを作ったとき・作り直したときに、新しいキーの空の置き場へデータを移すためのもの
    /// （2 回の PUT なので、2 回目が失敗したらチームだけが移っていない状態になる。呼び出し側は新しいキーに切り替えずに捨てる）。
    /// JSON に無いゲームは変えない。
    /// </summary>
    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        RosterState state;
        try
        {
            state = RosterRepository.Deserialize(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("登録データは RosterState の JSON にしてください", nameof(json), ex);
        }
        var keys = state.DataSets.Keys.ToList();
        if (keys.Count == 0) return;
        // 先にポケモン、次にチーム（チームは登録ポケモンに無い ID を受け付けないため）
        await PutAsync(_pokemonEndpoint, state, keys, RosterPart.Pokemon, ct).ConfigureAwait(false);
        await PutAsync(_teamsEndpoint, state, keys, RosterPart.Teams, ct).ConfigureAwait(false);
    }

    /// <summary>1 つのゲームの、ポケモン（/api/my-pokemon）かチーム（/api/my-team）だけを保存する。</summary>
    public Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default) =>
        PutAsync(part == RosterPart.Pokemon ? _pokemonEndpoint : _teamsEndpoint, state, new[] { dataSetKey }, part, ct);

    /// <summary>
    /// このアクセスキーを使えなくし、その置き場を消す（<c>DELETE /api/access-key</c>。キーを作り直したあと、古いキーに対して呼ぶ）。
    /// 以後このキーは使えなくなる（<see cref="RosterUnavailableReason.KeyRetired"/>）。
    /// </summary>
    public async Task DeleteAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, _accessKeyEndpoint, null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        Revision = null;
    }

    private async Task<JsonDocument> GetAsync(Uri endpoint, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, endpoint, null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    private async Task PutAsync(Uri endpoint, RosterState state, IReadOnlyList<string> dataSetKeys, RosterPart part, CancellationToken ct)
    {
        using var content = BuildPutContent(Revision ?? 0, state, dataSetKeys, part);
        using var response = await SendAsync(HttpMethod.Put, endpoint, content, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            using var current = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (current.RootElement.ValueKind != JsonValueKind.Object || !TryGetRevision(current.RootElement, out var rev))
                throw InvalidResponse();
            Revision = rev;
            throw new RosterConflictException(rev);
        }
        if (response.StatusCode != HttpStatusCode.OK) throw await UnavailableAsync(response, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        Revision = doc.RootElement.ValueKind == JsonValueKind.Object && TryGetRevision(doc.RootElement, out var revision)
            ? revision
            : throw InvalidResponse();
        LastSyncedAt = DateTimeOffset.Now;
    }

    /// <summary>
    /// PUT の本文。ポケモンは <c>{"baseRevision": n, "dataSets": {"Champions": {"nextId": n, "pokemon": [...]}}}</c>、
    /// チームは <c>{"baseRevision": n, "dataSets": {"Champions": {"teams": [...], "activeTeam": n | null}}}</c>。
    /// </summary>
    private static ByteArrayContent BuildPutContent(long baseRevision, RosterState state, IReadOnlyList<string> dataSetKeys, RosterPart part)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("baseRevision", baseRevision);
            writer.WriteStartObject("dataSets");
            foreach (var key in dataSetKeys)
            {
                var roster = state.DataSets.TryGetValue(key, out var r) ? r : new DataSetRoster();
                writer.WriteStartObject(key);
                if (part == RosterPart.Pokemon)
                {
                    writer.WriteNumber("nextId", roster.NextId);
                    writer.WritePropertyName("pokemon");
                    JsonSerializer.Serialize(writer, roster.Pokemon, ItemOptions);
                }
                else
                {
                    var teams = ValidTeams(roster);
                    writer.WritePropertyName("teams");
                    JsonSerializer.Serialize(writer, teams, ItemOptions);
                    if (roster.ActiveTeam is { } active && teams.Any(t => t.Number == active))
                        writer.WriteNumber("activeTeam", active);
                    else
                        writer.WriteNull("activeTeam");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>
    /// サーバーが受け付ける形のチーム。以前の API は中身を確かめずに保存していたので、登録に無いポケモンを指す枠・同じポケモンの重複・
    /// 0 体のチームが残っていることがある。それらを除いて送る（そのままだと 400 になり、チームの操作やデータの移し替えができなくなる）。
    /// </summary>
    private static List<Team> ValidTeams(DataSetRoster roster)
    {
        var ids = roster.Pokemon.Select(p => p.Id).ToHashSet();
        var teams = new List<Team>();
        foreach (var team in roster.Teams.GroupBy(t => t.Number).Select(g => g.First()))
        {
            var slots = team.Slots.Where(s => ids.Contains(s.EntryId)).GroupBy(s => s.EntryId).Select(g => g.First()).Take(6).ToList();
            if (slots.Count > 0) teams.Add(new Team { Number = team.Number, Slots = slots });
        }
        return teams;
    }

    /// <summary>GET の応答 <c>{"revision": n, "dataSets": {...}}</c> を読む。</summary>
    private static (long Revision, JsonElement DataSets) ReadDataSets(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetRevision(root, out var revision)
            || !root.TryGetProperty("dataSets", out var dataSets)
            || dataSets.ValueKind != JsonValueKind.Object)
            throw InvalidResponse();
        return (revision, dataSets);
    }

    /// <summary>ポケモンとチームの応答を、保存先の形式（<see cref="RosterState"/> の JSON）に組み立てる。</summary>
    private static string? Combine(JsonElement pokemonSets, JsonElement teamSets)
    {
        var keys = new List<string>();
        foreach (var p in pokemonSets.EnumerateObject()) keys.Add(p.Name);
        foreach (var p in teamSets.EnumerateObject())
            if (!keys.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) keys.Add(p.Name);
        if (keys.Count == 0) return null;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", 1);
            writer.WriteStartObject("DataSets");
            foreach (var key in keys)
            {
                writer.WriteStartObject(key);
                if (pokemonSets.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    if (p.TryGetProperty("nextId", out var nextId) && nextId.ValueKind == JsonValueKind.Number) writer.WriteNumber("NextId", nextId.GetInt32());
                    WriteArray(writer, "Pokemon", p, "pokemon");
                }
                if (teamSets.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    WriteArray(writer, "Teams", t, "teams");
                    if (t.TryGetProperty("activeTeam", out var active) && active.ValueKind == JsonValueKind.Number)
                        writer.WriteNumber("ActiveTeam", active.GetInt32());
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteArray(Utf8JsonWriter writer, string name, JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        writer.WritePropertyName(name);
        array.WriteTo(writer);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
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

    private static bool TryGetRevision(JsonElement root, out long revision)
    {
        revision = 0;
        return root.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out revision) && revision >= 0;
    }

    private static RosterUnavailableException InvalidResponse() =>
        new(RosterUnavailableReason.InvalidResponse, null,
            "クラウドから想定外の応答がありました（確認用サイトでは Cloudflare Access の設定を確認してください）");

    private static async Task<RosterUnavailableException> UnavailableAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status == 400)
        {
            // 理由（登録に無いポケモン・7 体以上など）はサーバーのメッセージを使う（アクセスキーは含まない）
            var reason = await ReadMessageAsync(response, ct).ConfigureAwait(false);
            return new(RosterUnavailableReason.Rejected, status,
                reason is null ? "クラウドに保存できませんでした（データの形式が正しくありません）" : $"クラウドに保存できませんでした（{reason}）");
        }
        return status switch
        {
            401 => new(RosterUnavailableReason.Unauthorized, status, "アクセスキーが正しくありません"),
            410 => new(RosterUnavailableReason.KeyRetired, status, "このアクセスキーは作り直されたため使えません。新しいアクセスキーを設定してください"),
            413 => new(RosterUnavailableReason.TooLarge, status, "登録データが大きすぎてクラウドに保存できません"),
            >= 500 => new(RosterUnavailableReason.Server, status, $"クラウドでエラーが発生しました（HTTP {status}）。時間をおいて試してください"),
            _ => new(RosterUnavailableReason.InvalidResponse, status,
                $"クラウドから想定外の応答がありました（HTTP {status}。確認用サイトでは Cloudflare Access の設定を確認してください）"),
        };
    }

    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                   && m.GetString() is { Length: > 0 and <= 200 } text
                ? text
                : null;
        }
        catch (RosterUnavailableException)
        {
            return null;
        }
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
