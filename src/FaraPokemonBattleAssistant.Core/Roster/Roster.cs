using System.Text.Json;
using System.Text.Json.Serialization;
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Roster;

/// <summary>登録データのうち、1 回の変更で書き換える部分。</summary>
public enum RosterPart
{
    /// <summary>登録ポケモン（と次に振る登録 ID）。消したポケモンはチームからも外れる。</summary>
    Pokemon,
    /// <summary>チームと使用中チーム。</summary>
    Teams,
}

/// <summary>登録データ（育成済みポケモン・チーム）の保存先。</summary>
public interface IRosterStore
{
    /// <summary>登録データ全体を JSON（<see cref="RosterState"/>）で読む。まだ何も無ければ null。</summary>
    Task<string?> LoadAsync(CancellationToken ct = default);

    /// <summary>登録データ全体を保存する（ファイル・ブラウザ。クラウドではデータの移し替え用）。</summary>
    Task SaveAsync(string json, CancellationToken ct = default);

    /// <summary>
    /// 1 つのゲーム（<paramref name="dataSetKey"/>）の、<paramref name="part"/> の部分だけを保存する。
    /// <paramref name="state"/> は変更後の登録データ全体。ファイル・ブラウザは全体をそのまま保存し、
    /// クラウド（<see cref="RemoteRosterStore"/>）はその部分の API（/api/my/pokemon・/api/my/team）だけを呼ぶ。
    /// </summary>
    Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default);

    /// <summary>
    /// 他の場所（別の端末・ボット）からも書き換えられる保存先なら true（<see cref="RemoteRosterStore"/>）。
    /// <see cref="RosterRepository"/> は読み込んだ内容をキャッシュせず、読み書きのたびに読み直す。
    /// </summary>
    bool AlwaysReload => false;
}

/// <summary>ファイルに保存する（ボット用）。</summary>
public sealed class FileRosterStore : IRosterStore
{
    public string Path { get; }

    public FileRosterStore(string path)
    {
        Path = path;
    }

    public async Task<string?> LoadAsync(CancellationToken ct = default) =>
        File.Exists(Path) ? await File.ReadAllTextAsync(Path, ct).ConfigureAwait(false) : null;

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, Path, overwrite: true);
    }

    public Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default) =>
        SaveAsync(RosterRepository.Serialize(state), ct);
}

/// <summary>メモリ上だけ（テスト用）。</summary>
public sealed class MemoryRosterStore : IRosterStore
{
    public string? Json { get; set; }
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Json);
    public Task SaveAsync(string json, CancellationToken ct = default) { Json = json; return Task.CompletedTask; }
    public Task SavePartAsync(RosterState state, string dataSetKey, RosterPart part, CancellationToken ct = default) =>
        SaveAsync(RosterRepository.Serialize(state), ct);
}

public sealed class RegisteredPokemon
{
    public int Id { get; set; }
    public int PokemonId { get; set; }
    /// <summary>表示用（データ更新で名前が変わっても一覧に出せるように保持）。</summary>
    public string PokemonName { get; set; } = "";
    public int HP { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public int SpAttack { get; set; }
    public int SpDefense { get; set; }
    public int Speed { get; set; }
    public string Nature { get; set; } = "";
    public int? AbilityId { get; set; }
    /// <summary>
    /// 一時期、登録時に持ち物を保存していたときの項目。今は使わない（持ち物はチームに入れるときに選ぶ）が、
    /// そのころの保存データを読み書きしても消えないように残している。
    /// </summary>
    public int? ItemId { get; set; }
    public string? TeraType { get; set; }
    /// <summary>登録時に選んだ技（技の Id。0〜<see cref="MaxMoves"/> 個）。選ばなかった古い登録では空。</summary>
    public List<int> MoveIds { get; set; } = new();

    /// <summary>登録できる技の数。</summary>
    public const int MaxMoves = 4;

    [JsonIgnore]
    public StatSet EVs
    {
        get => new(HP, Attack, Defense, SpAttack, SpDefense, Speed);
        set { HP = value.HP; Attack = value.Attack; Defense = value.Defense; SpAttack = value.SpAttack; SpDefense = value.SpDefense; Speed = value.Speed; }
    }
}

/// <summary>「登録から選ぶ」の 1 件（登録ポケモンと、チームで持たせた持ち物）。</summary>
public sealed record RosterChoice(string Key, RegisteredPokemon Entry, Item? Item)
{
    public string Label => $"#{Entry.Id} {Entry.PokemonName}{(Item is null ? "" : " @" + Item.Name)}";
}

public sealed record RosterChoiceGroup(string Label, IReadOnlyList<RosterChoice> Choices);

public sealed class TeamSlot
{
    public int EntryId { get; set; }
    public int? ItemId { get; set; }
}

public sealed class Team
{
    public int Number { get; set; }
    public List<TeamSlot> Slots { get; set; } = new();
}

public sealed class DataSetRoster
{
    public int NextId { get; set; } = 1;
    public List<RegisteredPokemon> Pokemon { get; set; } = new();
    public List<Team> Teams { get; set; } = new();
    public int? ActiveTeam { get; set; }
}

public sealed class RosterState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, DataSetRoster> DataSets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DataSetRoster For(string dataSetKey)
    {
        if (!DataSets.TryGetValue(dataSetKey, out var r))
            DataSets[dataSetKey] = r = new DataSetRoster();
        return r;
    }
}

/// <summary>
/// 登録データの読み書きと、登録内容から <see cref="PokemonBuild"/> を組み立てる処理。
/// Web とボットで共通。保存先だけ <see cref="IRosterStore"/> で差し替える。
/// </summary>
/// <remarks>
/// 変更する操作は「読み込む → 変更する → 保存する」を 1 つずつ（同時に走らないように）行う。
/// 保存先が <see cref="IRosterStore.AlwaysReload"/> のとき（クラウド）は毎回読み直し、保存時に他の場所の更新と競合したら
/// （<see cref="RosterConflictException"/>）読み直して最大 <see cref="MaxConflictRetries"/> 回やり直す。
/// 保存は変えた部分だけ（ポケモンの操作はポケモン、チームの操作はチーム。<see cref="IRosterStore.SavePartAsync"/>）。
/// ポケモンを消したときにチームから外す処理は、クラウドではサーバーが同じ処理の中で行う（ここでも同じ変更をして表示に使う）。
/// </remarks>
public sealed class RosterRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>保存が競合したときにやり直す回数。</summary>
    public const int MaxConflictRetries = 3;

    private readonly IRosterStore _store;
    // 読み込み・保存を 1 つずつにする（クラウドでは、読んだ版を基準に保存するため）
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RosterState? _state;

    public RosterRepository(IRosterStore store)
    {
        _store = store;
    }

    /// <summary>保存先。</summary>
    public IRosterStore Store => _store;

    /// <summary>
    /// 登録データ。ファイル・ブラウザの保存先では一度読んだら使い回す。
    /// クラウド（<see cref="IRosterStore.AlwaysReload"/>）では呼ぶたびに読み直す。
    /// </summary>
    public async Task<RosterState> GetAsync(CancellationToken ct = default)
    {
        if (_state is not null && !_store.AlwaysReload) return _state;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store.AlwaysReload) _state = null;
            return await LoadCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state is null) return;
            await _store.SaveAsync(Serialize(_state), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>キャッシュを捨てて次回読み直す（別プロセスが書いた場合や、保存先を切り替えた場合など）。</summary>
    public void Invalidate() => _state = null;

    /// <summary>登録データを JSON にする（保存先に書く形式）。</summary>
    public static string Serialize(RosterState state) => JsonSerializer.Serialize(state, JsonOptions);

    /// <summary>保存先の JSON を読む。空なら新しい登録データ。</summary>
    public static RosterState Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new RosterState() : JsonSerializer.Deserialize<RosterState>(json!, JsonOptions) ?? new RosterState();

    private async Task<RosterState> LoadCoreAsync(CancellationToken ct)
    {
        if (_state is not null) return _state;
        var state = Deserialize(await _store.LoadAsync(ct).ConfigureAwait(false));
        _state = state;
        return state;
    }

    /// <summary>
    /// 読み込む → <paramref name="apply"/> で変更する → 変更があれば保存する。
    /// クラウドでは毎回読み直し、競合したら読み直してやり直す（<paramref name="apply"/> は何度か呼ばれることがある）。
    /// </summary>
    private async Task<T> MutateAsync<T>(string dataSetKey, RosterPart part, Func<DataSetRoster, (T Result, bool Changed)> apply, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (_store.AlwaysReload) _state = null;
                var state = await LoadCoreAsync(ct).ConfigureAwait(false);
                var (result, changed) = apply(state.For(dataSetKey));
                if (!changed) return result;
                try
                {
                    await _store.SavePartAsync(state, dataSetKey, part, ct).ConfigureAwait(false);
                    return result;
                }
                catch (RosterConflictException) when (attempt < MaxConflictRetries)
                {
                    // 他の場所が先に書いていた。読み直してやり直す
                    _state = null;
                }
                catch when (_store.AlwaysReload)
                {
                    // 保存できなかった変更は捨てる（次に読むときはクラウドの内容）
                    _state = null;
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- 登録ポケモン

    public async Task<RegisteredPokemon> AddPokemonAsync(string dataSetKey, Pokemon pokemon, StatSet evs, Nature nature, Ability? ability, CancellationToken ct = default) =>
        await AddPokemonAsync(dataSetKey, pokemon, evs, nature, ability, null, ct).ConfigureAwait(false);

    /// <param name="moveIds">登録する技の Id（重複と 5 個目以降は無視する）。</param>
    public async Task<RegisteredPokemon> AddPokemonAsync(string dataSetKey, Pokemon pokemon, StatSet evs, Nature nature, Ability? ability, IEnumerable<int>? moveIds, CancellationToken ct = default)
    {
        var moves = (moveIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().Take(RegisteredPokemon.MaxMoves).ToList();
        return await MutateAsync(dataSetKey, RosterPart.Pokemon, roster =>
        {
            var entry = new RegisteredPokemon
            {
                Id = roster.NextId++,
                PokemonId = pokemon.Id,
                PokemonName = pokemon.Name,
                EVs = evs,
                Nature = nature.Name,
                AbilityId = ability?.Id,
                MoveIds = moves,
            };
            roster.Pokemon.Add(entry);
            return (entry, true);
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemovePokemonAsync(string dataSetKey, int entryId, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Pokemon, roster =>
        {
            var removed = roster.Pokemon.RemoveAll(p => p.Id == entryId) > 0;
            foreach (var team in roster.Teams)
                team.Slots.RemoveAll(s => s.EntryId == entryId);
            RemoveEmptyTeams(roster);
            return (removed, removed);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>0 体になったチームを削除する（使用中なら使用も解除）。削除したチーム番号を返す。</summary>
    public static IReadOnlyList<int> RemoveEmptyTeams(DataSetRoster roster)
    {
        var empty = roster.Teams.Where(t => t.Slots.Count == 0).Select(t => t.Number).ToList();
        roster.Teams.RemoveAll(t => t.Slots.Count == 0);
        if (roster.ActiveTeam is { } active && empty.Contains(active)) roster.ActiveTeam = null;
        return empty;
    }

    // ---- チーム

    public async Task<(Team Team, TeamSlot Slot, bool Replaced)> AddToTeamAsync(string dataSetKey, int teamNumber, int entryId, Item? item, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            if (roster.Pokemon.All(p => p.Id != entryId))
                throw new ArgumentException($"登録ID {entryId} はありません");
            var team = roster.Teams.FirstOrDefault(t => t.Number == teamNumber);
            if (team is null)
            {
                team = new Team { Number = teamNumber };
                roster.Teams.Add(team);
                roster.Teams.Sort((a, b) => a.Number.CompareTo(b.Number));
            }
            var existing = team.Slots.FirstOrDefault(s => s.EntryId == entryId);
            var replaced = existing is not null;
            if (existing is null)
            {
                if (team.Slots.Count >= 6)
                    throw new InvalidOperationException($"チーム{teamNumber}は既に 6 体います");
                existing = new TeamSlot { EntryId = entryId };
                team.Slots.Add(existing);
            }
            existing.ItemId = item?.Id;
            return ((team, existing, replaced), true);
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveFromTeamAsync(string dataSetKey, int teamNumber, Func<RegisteredPokemon, bool> predicate, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            var team = roster.Teams.FirstOrDefault(t => t.Number == teamNumber);
            if (team is null) return (false, false);
            var byId = roster.Pokemon.ToDictionary(p => p.Id);
            var removed = team.Slots.RemoveAll(s => byId.TryGetValue(s.EntryId, out var p) && predicate(p)) > 0;
            RemoveEmptyTeams(roster);
            return (removed, removed);
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveTeamAsync(string dataSetKey, int teamNumber, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            var removed = roster.Teams.RemoveAll(t => t.Number == teamNumber) > 0;
            if (roster.ActiveTeam == teamNumber) roster.ActiveTeam = null;
            return (removed, removed);
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> UseTeamAsync(string dataSetKey, int teamNumber, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            if (roster.Teams.All(t => t.Number != teamNumber)) return (false, false);
            roster.ActiveTeam = teamNumber;
            return (true, true);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>使用チームを解除する。解除したら true（もともと無ければ false）。</summary>
    public async Task<bool> ClearActiveTeamAsync(string dataSetKey, CancellationToken ct = default)
    {
        return await MutateAsync(dataSetKey, RosterPart.Teams, roster =>
        {
            if (roster.ActiveTeam is null) return (false, false);
            roster.ActiveTeam = null;
            return (true, true);
        }, ct).ConfigureAwait(false);
    }

    // ---- 構成の組み立て

    /// <summary>登録内容から構成を作る。データに無いポケモンなら null。</summary>
    public static PokemonBuild? ToBuild(RegisteredPokemon entry, PokemonDataSet data, Item? item = null)
    {
        var pokemon = data.FindPokemon(entry.PokemonId);
        if (pokemon is null) return null;
        return new PokemonBuild(pokemon, data.Info.EvSystem)
        {
            EVs = entry.EVs,
            Nature = data.FindNature(entry.Nature),
            Ability = entry.AbilityId is { } aid ? data.FindAbility(aid) : data.AbilitiesOf(pokemon).FirstOrDefault(),
            Item = item,
            TeraType = entry.TeraType,
        };
    }

    /// <summary>
    /// 計算機・素早さ比較の「登録から選ぶ」用に、登録済みの構成をチームごとにまとめて返す。
    /// チームのメンバー（チームで持たせた持ち物つき）と、どのチームにも入っていない登録ポケモンの順。
    /// 返す構成は登録データの写しで、呼び出し側で値を変えても登録データは変わらない（<see cref="ToBuild"/>）。
    /// </summary>
    public async Task<IReadOnlyList<RosterChoiceGroup>> ChoiceGroupsAsync(string dataSetKey, PokemonDataSet data, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        var byId = roster.Pokemon.ToDictionary(p => p.Id);
        var groups = new List<RosterChoiceGroup>();
        var inTeam = new HashSet<int>();
        foreach (var team in roster.Teams.OrderBy(t => t.Number))
        {
            var choices = new List<RosterChoice>();
            foreach (var slot in team.Slots)
            {
                if (!byId.TryGetValue(slot.EntryId, out var entry)) continue;
                inTeam.Add(entry.Id);
                var item = slot.ItemId is { } iid ? data.FindItem(iid) : null;
                choices.Add(new RosterChoice($"t{team.Number}-{entry.Id}", entry, item));
            }
            if (choices.Count > 0)
                groups.Add(new RosterChoiceGroup($"チーム{team.Number}{(roster.ActiveTeam == team.Number ? "（使用中）" : "")}", choices));
        }
        var others = roster.Pokemon
            .Where(p => !inTeam.Contains(p.Id))
            .Select(p => new RosterChoice($"r{p.Id}", p, p.ItemId is { } iid ? data.FindItem(iid) : null))
            .ToList();
        if (others.Count > 0) groups.Add(new RosterChoiceGroup("チームに入っていない登録", others));
        return groups;
    }

    /// <summary>使用中チームのメンバー（登録ポケモンと持ち物）。</summary>
    public async Task<IReadOnlyList<(RegisteredPokemon Entry, Item? Item)>> ActiveTeamMembersAsync(string dataSetKey, PokemonDataSet data, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        if (roster.ActiveTeam is not { } n) return Array.Empty<(RegisteredPokemon, Item?)>();
        var team = roster.Teams.FirstOrDefault(t => t.Number == n);
        if (team is null) return Array.Empty<(RegisteredPokemon, Item?)>();
        var byId = roster.Pokemon.ToDictionary(p => p.Id);
        return team.Slots
            .Where(s => byId.ContainsKey(s.EntryId))
            .Select(s => (byId[s.EntryId], s.ItemId is { } iid ? data.FindItem(iid) : null))
            .ToList();
    }

    /// <summary>
    /// 使用中チームから、指定ポケモンの構成を返す（<see cref="Text.DamageCommandOptions.BuildProvider"/> 用）。
    /// 同じポケモンが複数いるときは先頭。
    /// </summary>
    public async Task<Func<Pokemon, bool, PokemonBuild?>> ActiveTeamBuildProviderAsync(string dataSetKey, PokemonDataSet data, CancellationToken ct = default)
    {
        var members = await ActiveTeamMembersAsync(dataSetKey, data, ct).ConfigureAwait(false);
        var builds = new Dictionary<int, PokemonBuild>();
        foreach (var (entry, item) in members)
        {
            if (!builds.ContainsKey(entry.PokemonId) && ToBuild(entry, data, item) is { } build)
                builds[entry.PokemonId] = build;
        }
        return (pokemon, _) => builds.GetValueOrDefault(pokemon.Id);
    }
}
