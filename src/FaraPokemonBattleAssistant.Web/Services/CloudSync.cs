using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using FaraPokemonBattleAssistant.Core.Roster;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 登録データの保存先（アプリ全体の <see cref="IRosterStore"/>）。同期キーがあればクラウド（<see cref="RemoteRosterStore"/>）、
/// 無ければこのブラウザの localStorage（<see cref="BrowserRosterStore"/>）を使う。
/// </summary>
/// <remarks>
/// 同期中はクラウドだけが正で、このブラウザには登録データを残さない（localStorage に置くのは同期キーだけ）。
/// 同期キーはパスワードと同じ扱いで、ログ・エラー表示には出さない。
/// </remarks>
public sealed class CloudSync : IRosterStore
{
    /// <summary>同期キーを置く localStorage のキー。</summary>
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

    /// <summary>同期中か。</summary>
    public bool IsSyncing => _remote is not null;

    /// <summary>同期キー（同期していなければ null）。画面に出すときは伏せ字にする。</summary>
    public string? Key => _key;

    /// <summary>最後にクラウドとの読み書きが成功した時刻。</summary>
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>最後の読み書きが失敗したときのメッセージ（成功したら消える）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最後の保存で他の場所の更新と競合し、読み直して保存し直したときのお知らせ。</summary>
    public string? LastNotice { get; private set; }

    /// <summary>同期の状態（開始・停止・成功・失敗）が変わったとき。</summary>
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
        try
        {
            await remote.SaveAsync(json, ct);
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

    // ---- 同期の開始・参加・作り直し・停止

    /// <summary>このブラウザに（同期していないときの）登録データがあるか。</summary>
    public async Task<bool> LocalHasDataAsync()
    {
        await EnsureInitializedAsync();
        return HasData(RosterRepository.Deserialize(await _local.LoadAsync()));
    }

    /// <summary>新しい同期キーを作って同期を始める。このブラウザの登録データをクラウドに上げ、ブラウザからは消す。</summary>
    public async Task StartNewAsync()
    {
        await EnsureInitializedAsync();
        if (IsSyncing) throw new InvalidOperationException("すでに同期しています");
        var key = RemoteRosterStore.GenerateKey();
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        var local = RosterRepository.Deserialize(await _local.LoadAsync());
        if (HasData(local))
            await remote.SaveAsync(RosterRepository.Serialize(local)); // 新しいキーの置き場は空（版 0）
        else
            await remote.LoadAsync(); // つながるか確かめる
        await SwitchToAsync(key, remote);
        await _local.ClearAsync();
    }

    /// <summary>
    /// 既存の同期キーで参加する。このブラウザの登録データは消え、クラウドの内容に置き換わる。
    /// クラウドにデータがあれば true（無ければキーの打ち間違いかもしれない）。
    /// </summary>
    public async Task<bool> JoinAsync(string key)
    {
        await EnsureInitializedAsync();
        if (IsSyncing) throw new InvalidOperationException("すでに同期しています");
        key = key.Trim();
        if (!RemoteRosterStore.IsValidKey(key))
            throw new ArgumentException("同期キーの形式が正しくありません（43 文字の英数字・-・_）");
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        var json = await remote.LoadAsync();
        await SwitchToAsync(key, remote);
        await _local.ClearAsync();
        return json is not null;
    }

    /// <summary>
    /// 同期キーを作り直す。今のデータを新しいキーの置き場に移し、古いキーの置き場は消す（古いキーは使えなくなる）。
    /// 古い置き場を消せなかったときは false（新しいキーへの切り替えは済んでいる）。
    /// </summary>
    public async Task<bool> RotateAsync()
    {
        await EnsureInitializedAsync();
        var old = _remote ?? throw new InvalidOperationException("同期していません");
        var key = RemoteRosterStore.GenerateKey();
        var remote = new RemoteRosterStore(_http, _baseUri, key);
        var json = await old.LoadAsync();
        if (json is not null) await remote.SaveAsync(json);
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

    /// <summary>同期をやめる（このブラウザでキーを忘れる）。クラウドのデータは残り、このブラウザの登録データは空になる。</summary>
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

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await RefreshKeyFromStorageAsync();
    }

    /// <summary>
    /// 今の保存先。同期していないときは毎回 localStorage のキーを確かめ、別のタブで同期を始めていたらクラウドに切り替える。
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
