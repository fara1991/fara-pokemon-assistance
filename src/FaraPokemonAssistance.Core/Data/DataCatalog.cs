using FaraPokemonAssistance.Core.Csv;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Data;

/// <summary>
/// データ全体への入口。データセット一覧・性格・各データセットを読み込みキャッシュする。
/// Web とボットで同じクラスを使い、違いは <see cref="IDataSource"/> だけ。
/// </summary>
public sealed class DataCatalog
{
    private readonly IDataSource _source;
    private readonly object _gate = new();
    private Task<IReadOnlyList<DataSetInfo>>? _dataSets;
    private Task<IReadOnlyList<Nature>>? _natures;
    private readonly Dictionary<string, Task<PokemonDataSet>> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan? _reloadInterval;
    private long _loadedAtTicks = Environment.TickCount64;

    /// <summary>
    /// <paramref name="reloadInterval"/> を指定すると、生成（または前回の破棄）からその時間が経過した次の呼び出しで
    /// 全データを読み直す（長時間起動するボットが週次更新のデータを取り込めるようにする）。
    /// 読み込み中に例外になったデータは保持せず、次の呼び出しで再試行する。
    /// </summary>
    public DataCatalog(IDataSource source, TimeSpan? reloadInterval)
        : this(source)
    {
        if (reloadInterval is { } v && v <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reloadInterval), "再読み込み間隔は正の値にしてください");
        _reloadInterval = reloadInterval;
    }

    /// <summary>何も指定されなかったときに使うデータセットのキー（存在しなければ <see cref="ResolveDefaultKeyAsync"/> が別のものを返す）。</summary>
    public const string DefaultDataSetKey = "Champions";

    /// <summary>既定キーが存在すればそれ、無ければ一覧の最後（最新）のキー。</summary>
    public async Task<string> ResolveDefaultKeyAsync(string? preferred = null, CancellationToken ct = default)
    {
        var sets = await GetDataSetsAsync(ct).ConfigureAwait(false);
        foreach (var key in new[] { preferred, DefaultDataSetKey })
        {
            if (key is not null && sets.Any(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)))
                return key;
        }
        return sets.Count > 0 ? sets[^1].Key : DefaultDataSetKey;
    }

    public DataCatalog(IDataSource source)
    {
        _source = source;
    }

    public Task<IReadOnlyList<DataSetInfo>> GetDataSetsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ExpireIfDue();
            if (_dataSets is null || _dataSets.IsFaulted || _dataSets.IsCanceled)
                _dataSets = LoadDataSetsAsync(ct);
            return _dataSets;
        }
    }

    public Task<IReadOnlyList<Nature>> GetNaturesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ExpireIfDue();
            if (_natures is null || _natures.IsFaulted || _natures.IsCanceled)
                _natures = LoadNaturesAsync(ct);
            return _natures;
        }
    }

    public Task<PokemonDataSet> GetDataSetAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ExpireIfDue();
            if (!_loaded.TryGetValue(key, out var task) || task.IsFaulted || task.IsCanceled)
            {
                task = LoadDataSetAsync(key, ct);
                _loaded[key] = task;
            }
            return task;
        }
    }

    /// <summary>読み込み済みキャッシュを捨てる。データ更新後に呼ぶ。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            InvalidateCore();
        }
    }

    private void InvalidateCore()
    {
        _dataSets = null;
        _natures = null;
        _loaded.Clear();
        _loadedAtTicks = Environment.TickCount64;
    }

    /// <summary>_gate を取った状態で呼ぶ。再読み込み間隔を過ぎていればキャッシュを捨てる。</summary>
    private void ExpireIfDue()
    {
        if (_reloadInterval is { } interval && Environment.TickCount64 - _loadedAtTicks >= (long)interval.TotalMilliseconds)
            InvalidateCore();
    }

    private async Task<IReadOnlyList<DataSetInfo>> LoadDataSetsAsync(CancellationToken ct)
    {
        var text = await _source.ReadTextAsync("datasets.csv", ct).ConfigureAwait(false);
        if (text is null)
        {
            // 旧レイアウト互換: Gen1〜Gen9 フォルダを探す
            var found = new List<DataSetInfo>();
            for (var g = 1; g <= 9; g++)
            {
                if (await _source.ReadTextAsync($"Gen{g}/pokemon.csv", ct).ConfigureAwait(false) is not null)
                    found.Add(new DataSetInfo { Key = $"Gen{g}", Name = $"第{g}世代", Generation = g });
            }
            return found;
        }

        var table = CsvTable.Parse(text);
        return table.Rows.Select(row =>
        {
            var key = table.Get(row, "Key");
            var evSystem = Enum.TryParse<EvSystem>(table.Get(row, "EvSystem"), true, out var parsed) ? parsed
                : key == "Champions" ? EvSystem.Points : EvSystem.Classic;
            var prefix = table.Get(row, "CommandPrefix");
            if (string.IsNullOrEmpty(prefix)) prefix = DefaultPrefix(key);
            var generation = table.GetInt(row, "Generation", 9);
            var gimmick = Enum.TryParse<BattleGimmick>(table.Get(row, "Gimmick"), true, out var g) ? g
                : key == "Champions" ? BattleGimmick.None
                : generation == 8 ? BattleGimmick.Dynamax
                : generation == 9 ? BattleGimmick.Terastal
                : BattleGimmick.None;
            return new DataSetInfo
            {
                Key = key,
                Name = table.Get(row, "Name"),
                Generation = generation,
                EvSystem = evSystem,
                CommandPrefix = prefix,
                Gimmick = gimmick,
                UsageFallback = table.HasColumn("UsageFallback") ? table.Get(row, "UsageFallback") : "",
            };
        }).ToList();
    }

    private static string DefaultPrefix(string key) => key switch
    {
        "Champions" => "pokech",
        "Gen9" => "pokesv",
        "Gen8" => "pokess",
        _ => "poke" + key.ToLowerInvariant(),
    };

    private async Task<IReadOnlyList<Nature>> LoadNaturesAsync(CancellationToken ct)
    {
        var text = await _source.ReadTextAsync("natures.csv", ct).ConfigureAwait(false) ?? "";
        var table = CsvTable.Parse(text);
        var natures = new List<Nature>();
        var index = 0;
        foreach (var row in table.Rows)
        {
            natures.Add(new Nature
            {
                Id = table.HasColumn("Id") ? table.GetInt(row, "Id", index) : index,
                Name = table.Get(row, "Name"),
                IncreasedStat = StatNames.Parse(table.Get(row, "IncreasedStat")),
                DecreasedStat = StatNames.Parse(table.Get(row, "DecreasedStat")),
            });
            index++;
        }
        return natures;
    }

    private async Task<PokemonDataSet> LoadDataSetAsync(string key, CancellationToken ct)
    {
        var infos = await GetDataSetsAsync(ct).ConfigureAwait(false);
        var info = infos.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"データセット '{key}' は存在しません（利用可能: {string.Join(", ", infos.Select(i => i.Key))}）");
        var natures = await GetNaturesAsync(ct).ConfigureAwait(false);
        var dataSet = new PokemonDataSet(_source, info, natures);
        await dataSet.LoadAsync(ct).ConfigureAwait(false);
        return dataSet;
    }
}
