using System.Text.Json;
using System.Text.Json.Serialization;
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Roster;

/// <summary>登録データ（育成済みポケモン・チーム）の保存先。JSON 文字列を丸ごと読み書きする。</summary>
public interface IRosterStore
{
    Task<string?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(string json, CancellationToken ct = default);
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
}

/// <summary>メモリ上だけ（テスト用）。</summary>
public sealed class MemoryRosterStore : IRosterStore
{
    public string? Json { get; set; }
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Json);
    public Task SaveAsync(string json, CancellationToken ct = default) { Json = json; return Task.CompletedTask; }
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

    [JsonIgnore]
    public StatSet EVs
    {
        get => new(HP, Attack, Defense, SpAttack, SpDefense, Speed);
        set { HP = value.HP; Attack = value.Attack; Defense = value.Defense; SpAttack = value.SpAttack; SpDefense = value.SpDefense; Speed = value.Speed; }
    }
}

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
public sealed class RosterRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IRosterStore _store;
    private RosterState? _state;

    public RosterRepository(IRosterStore store)
    {
        _store = store;
    }

    public async Task<RosterState> GetAsync(CancellationToken ct = default)
    {
        if (_state is not null) return _state;
        var json = await _store.LoadAsync(ct).ConfigureAwait(false);
        _state = string.IsNullOrWhiteSpace(json) ? new RosterState() : JsonSerializer.Deserialize<RosterState>(json!, JsonOptions) ?? new RosterState();
        return _state;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_state is null) return;
        await _store.SaveAsync(JsonSerializer.Serialize(_state, JsonOptions), ct).ConfigureAwait(false);
    }

    /// <summary>キャッシュを捨てて次回読み直す（別プロセスが書いた場合など）。</summary>
    public void Invalidate() => _state = null;

    // ---- 登録ポケモン

    public async Task<RegisteredPokemon> AddPokemonAsync(string dataSetKey, Pokemon pokemon, StatSet evs, Nature nature, Ability? ability, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        var entry = new RegisteredPokemon
        {
            Id = roster.NextId++,
            PokemonId = pokemon.Id,
            PokemonName = pokemon.Name,
            EVs = evs,
            Nature = nature.Name,
            AbilityId = ability?.Id,
        };
        roster.Pokemon.Add(entry);
        await SaveAsync(ct).ConfigureAwait(false);
        return entry;
    }

    public async Task<bool> RemovePokemonAsync(string dataSetKey, int entryId, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        var removed = roster.Pokemon.RemoveAll(p => p.Id == entryId) > 0;
        foreach (var team in roster.Teams)
            team.Slots.RemoveAll(s => s.EntryId == entryId);
        RemoveEmptyTeams(roster);
        if (removed) await SaveAsync(ct).ConfigureAwait(false);
        return removed;
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
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
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
        await SaveAsync(ct).ConfigureAwait(false);
        return (team, existing, replaced);
    }

    public async Task<bool> RemoveFromTeamAsync(string dataSetKey, int teamNumber, Func<RegisteredPokemon, bool> predicate, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        var team = roster.Teams.FirstOrDefault(t => t.Number == teamNumber);
        if (team is null) return false;
        var byId = roster.Pokemon.ToDictionary(p => p.Id);
        var removed = team.Slots.RemoveAll(s => byId.TryGetValue(s.EntryId, out var p) && predicate(p)) > 0;
        RemoveEmptyTeams(roster);
        if (removed) await SaveAsync(ct).ConfigureAwait(false);
        return removed;
    }

    public async Task<bool> RemoveTeamAsync(string dataSetKey, int teamNumber, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        var removed = roster.Teams.RemoveAll(t => t.Number == teamNumber) > 0;
        if (roster.ActiveTeam == teamNumber) roster.ActiveTeam = null;
        if (removed) await SaveAsync(ct).ConfigureAwait(false);
        return removed;
    }

    public async Task<bool> UseTeamAsync(string dataSetKey, int teamNumber, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        if (roster.Teams.All(t => t.Number != teamNumber)) return false;
        roster.ActiveTeam = teamNumber;
        await SaveAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>使用チームを解除する。解除したら true（もともと無ければ false）。</summary>
    public async Task<bool> ClearActiveTeamAsync(string dataSetKey, CancellationToken ct = default)
    {
        var roster = (await GetAsync(ct).ConfigureAwait(false)).For(dataSetKey);
        if (roster.ActiveTeam is null) return false;
        roster.ActiveTeam = null;
        await SaveAsync(ct).ConfigureAwait(false);
        return true;
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
