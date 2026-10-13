using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 登録データの保存先（アプリ全体の <see cref="IRosterStore"/>）。アクセスキーがあればクラウド（<see cref="RemoteRosterStore"/>）、
/// 無ければこのブラウザの localStorage（<see cref="BrowserRosterStore"/>）を使う。
/// </summary>
/// <remarks>
/// データ共有中はクラウドだけが正で、このブラウザには登録データを残さない（localStorage に置くのはアクセスキーだけ）。
/// アクセスキーはパスワードと同じ扱いで、ログ・エラー表示には出さない。
/// </remarks>
public sealed class CloudSync : IRosterStore
{
    /// <summary>アクセスキーを置く localStorage のキー。</summary>
    public const string KeyStorageKey = "fara-pokemon-battle-assistant.syncKey";

    private readonly IJSRuntime _js;
    private readonly HttpClient _http;
    private readonly string _baseUri;
    private readonly BrowserRosterStore _local;
    private readonly IServiceProvider _services;
    private RemoteRosterStore? _remote;
    private string? _key;
    private bool _initialized;
    private bool _conflicted;

    public CloudSync(IJSRuntime js, HttpClient http, string baseUri, IServiceProvider services)
    {
        _js = js;
        _http = http;
        _baseUri = baseUri;
        _local = new BrowserRosterStore(js);
        _services = services;
    }

    /// <summary>データ共有中か。</summary>
    public bool IsSyncing => _remote is not null;

    /// <summary>アクセスキー（データ共有していなければ null）。画面に出すときは伏せ字にする。</summary>
    public string? Key => _key;

    /// <summary>最後にクラウドとの読み書きが成功した時刻。</summary>
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>最後の読み書きが失敗したときのメッセージ（成功したら消える）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最後の保存で他の場所の更新と競合し、読み直して保存し直したときのお知らせ。</summary>
    public string? LastNotice { get; private set; }

    /// <summary>データ共有の状態（開始・停止・成功・失敗）が変わったとき。</summary>
    public event Action? Changed;

    public bool AlwaysReload => _remote is not null;

    // ---- IRosterStore

    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        var remote = await CurrentRemoteAsync();
        if (remote is null) return await _local.LoadAsync(ct);
        try
        {
            var json = await remote.LoadAsync(ct);
            RecordSuccess();
            return json;
        }
        catch (RosterUnavailableException ex)
        {
            RecordError(ex.Message);
            throw;
        }
    }

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        var remote = await CurrentRemoteAsync();
        if (remote is null)
        {
            await _local.SaveAsync(json, ct);
            return;
        }
        await SaveRemoteAsync(() => remote.SaveAsync(json, ct));
    }

    /// <summary>1 つのゲームのポケモンかチームだけを保存する（クラウドでは /api/my-pokemon か /api/my-team だけを呼ぶ）。</summary>
    public async Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default)
    {
        var remote = await CurrentRemoteAsync();
        if (remote is null)
        {
            await _local.SavePartAsync(state, dataSetKey, part, ct);
            return;
        }
        await SaveRemoteAsync(() => remote.SavePartAsync(state, dataSetKey, part, ct));
    }

    private async Task SaveRemoteAsync(Func<Task> save)
    {
        try
        {
            await save();
            // 競合して読み直したあとの保存なら、そのことを知らせる
            LastNotice = _conflicted ? "他の場所（ボットや別の端末）で更新されていたので、最新の内容を読み込み直してから保存しました" : null;
            _conflicted = false;
            RecordSuccess();
        }
        catch (RosterConflictException)
        {
            // RosterRepository が読み直してやり直す
            _conflicted = true;
            throw;
        }
        catch (RosterUnavailableException ex)
        {
            _conflicted = false;
            RecordError(ex.Message);
            throw;
        }
    }

    // ---- データ共有の開始・参加・作り直し・停止

    /// <summary>
    /// 保存してあるアクセスキーを読み、データ共有中かどうかを確かめる（データ共有ページを直接開いたときに、まだ何も読み込んでいなくても
    /// 「データ共有中」と表示するため）。
    /// </summary>
    public Task InitializeAsync() => EnsureInitializedAsync();

    /// <summary>このブラウザに（データ共有していないときの）登録データがあるか。</summary>
    public async Task<bool> LocalHasDataAsync()
    {
        await EnsureInitializedAsync();
        return HasData(RosterRepository.Deserialize(await _local.LoadAsync()));
    }

    /// <summary>新しいアクセスキーを作ってデータ共有を始める。このブラウザの登録データをクラウドに上げ、ブラウザからは消す。</summary>
    public async Task StartNewAsync()
    {
        await EnsureInitializedAsync();
        if (IsSyncing) throw new InvalidOperationException("すでにデータ共有しています");
        var key = RemoteRosterStore.GenerateKey();
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        var local = RosterRepository.Deserialize(await _local.LoadAsync());
        if (HasData(local))
            await MoveToNewKeyAsync(remote, RosterRepository.Serialize(local));
        else
            await remote.LoadAsync(); // つながるか確かめる
        await SwitchToAsync(key, remote);
        await _local.ClearAsync();
    }

    /// <summary>
    /// 既存のアクセスキーで参加する。このブラウザの登録データは消え、クラウドの内容に置き換わる。
    /// クラウドにデータがあれば true（無ければキーの打ち間違いかもしれない）。
    /// </summary>
    public async Task<bool> JoinAsync(string key)
    {
        await EnsureInitializedAsync();
        if (IsSyncing) throw new InvalidOperationException("すでにデータ共有しています");
        key = key.Trim();
        if (!RemoteRosterStore.IsValidKey(key))
            throw new ArgumentException("アクセスキーの形式が正しくありません（43 文字の英数字・-・_）");
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        var json = await remote.LoadAsync();
        await SwitchToAsync(key, remote);
        await _local.ClearAsync();
        return json is not null;
    }

    /// <summary>
    /// アクセスキーを作り直す。今のデータを新しいキーの置き場に移し、古いキーの置き場は消す（古いキーは使えなくなる）。
    /// 古い置き場を消せなかったときは false（新しいキーへの切り替えは済んでいる）。
    /// </summary>
    public async Task<bool> RotateAsync()
    {
        await EnsureInitializedAsync();
        var old = _remote ?? throw new InvalidOperationException("データ共有していません");
        var key = RemoteRosterStore.GenerateKey();
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        var json = await old.LoadAsync();
        if (json is not null) await MoveToNewKeyAsync(remote, json);
        else await remote.LoadAsync(); // つながるか確かめる
        await SwitchToAsync(key, remote);
        try
        {
            await old.DeleteAsync();
            return true;
        }
        catch (RosterUnavailableException)
        {
            return false;
        }
    }

    /// <summary>データ共有をやめる（このブラウザでキーを忘れる）。クラウドのデータは残り、このブラウザの登録データは空になる。</summary>
    public async Task StopAsync()
    {
        await EnsureInitializedAsync();
        await _js.InvokeVoidAsync("localStorage.removeItem", KeyStorageKey);
        _remote = null;
        _key = null;
        LastSyncedAt = null;
        LastError = null;
        LastNotice = null;
        AfterSwitch();
    }

    /// <summary>登録データの読み書きで出た例外を、画面に出す文に直す。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        RosterConflictException => "他の場所（ボットや別の端末）で同時に更新されたため保存できませんでした。最新の内容を読み込み直したので、もう一度操作してください",
        RosterUnavailableException u => u.Message,
        HttpRequestException => "クラウドに接続できませんでした",
        _ => ex.Message,
    };

    // ---- 内部

    /// <summary>
    /// 新しいキーの空の置き場（版 0）にデータを移す（ポケモン → チームの順に 2 回 PUT）。
    /// 新しいキーはまだ誰も使っていないので、移し終わってから切り替えれば途中の状態は見えない。
    /// 途中で失敗したら新しいキーは使わずに捨てる（置き場も消しておく）。
    /// </summary>
    private static async Task MoveToNewKeyAsync(RemoteRosterStore remote, string json)
    {
        try
        {
            await remote.SaveAsync(json);
        }
        catch
        {
            try { await remote.DeleteAsync(); } catch (Exception) { /* 消せなくても新しいキーは使わない */ }
            throw;
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await RefreshKeyFromStorageAsync();
    }

    /// <summary>
    /// 今の保存先。データ共有していないときは毎回 localStorage のキーを確かめ、別のタブでデータ共有を始めていたらクラウドに切り替える。
    /// </summary>
    private async Task<RemoteRosterStore?> CurrentRemoteAsync()
    {
        if (!_initialized || _remote is null)
        {
            _initialized = true;
            await RefreshKeyFromStorageAsync();
        }
        return _remote;
    }

    private async Task RefreshKeyFromStorageAsync()
    {
        string? key;
        try
        {
            key = await _js.InvokeAsync<string?>("localStorage.getItem", KeyStorageKey);
        }
        catch (JSException)
        {
            return;
        }
        if (key is not null && RemoteRosterStore.IsValidKey(key) && key != _key)
        {
            _key = key;
            _remote = new RemoteRosterStore(_http, _baseUri, key);
            Changed?.Invoke();
        }
    }

    private async Task SwitchToAsync(string key, RemoteRosterStore remote)
    {
        // キーを保存できなかったら（localStorage が使えないなど）切り替えない
        await _js.InvokeVoidAsync("localStorage.setItem", KeyStorageKey, key);
        _key = key;
        _remote = remote;
        LastSyncedAt = DateTimeOffset.Now;
        LastError = null;
        LastNotice = null;
        AfterSwitch();
    }

    private void AfterSwitch()
    {
        _services.GetRequiredService<RosterRepository>().Invalidate();
        Changed?.Invoke();
    }

    private void RecordSuccess()
    {
        LastSyncedAt = DateTimeOffset.Now;
        LastError = null;
        Changed?.Invoke();
    }

    private void RecordError(string message)
    {
        LastError = message;
        Changed?.Invoke();
    }

    private static bool HasData(RosterState state) =>
        state.DataSets.Values.Any(r => r.Pokemon.Count > 0 || r.Teams.Count > 0);
}

/// <summary>使用中チームを読む（計算機・素早さ比較用）。クラウドに届かなくてもページは動くように、エラーは文で返す。</summary>
public static class RosterReading
{
    /// <summary>「登録から選ぶ」用に、登録済みの構成をチームごとに読む。</summary>
    public static async Task<(IReadOnlyList<RosterChoiceGroup> Groups, string Error)> ChoiceGroupsAsync(RosterRepository repo, string dataSetKey, PokemonDataSet data)
    {
        try
        {
            return (await repo.ChoiceGroupsAsync(dataSetKey, data), "");
        }
        catch (Exception ex) when (ex is RosterUnavailableException or HttpRequestException)
        {
            return (Array.Empty<RosterChoiceGroup>(), $"登録を読み込めませんでした（{CloudSync.Describe(ex)}）。登録なしで計算します");
        }
    }

    public static async Task<(IReadOnlyList<(RegisteredPokemon Entry, Item? Item)> Members, string Error)> ActiveTeamMembersAsync(
        RosterRepository repo, string dataSetKey, PokemonDataSet data)
    {
        try
        {
            return (await repo.ActiveTeamMembersAsync(dataSetKey, data), "");
        }
        catch (Exception ex) when (ex is RosterUnavailableException or HttpRequestException)
        {
            return (Array.Empty<(RegisteredPokemon, Item?)>(), $"使用チームを読み込めませんでした（{CloudSync.Describe(ex)}）。チームなしで計算します");
        }
    }
}
