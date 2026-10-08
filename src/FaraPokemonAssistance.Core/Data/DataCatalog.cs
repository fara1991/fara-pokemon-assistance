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
            return _dataSets ??= LoadDataSetsAsync(ct);
        }
    }

    public Task<IReadOnlyList<Nature>> GetNaturesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return _natures ??= LoadNaturesAsync(ct);
        }
    }

    public Task<PokemonDataSet> GetDataSetAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_loaded.TryGetValue(key, out var task))
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
            _dataSets = null;
            _natures = null;
            _loaded.Clear();
        }
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
            return new DataSetInfo
            {
                Key = key,
                Name = table.Get(row, "Name"),
                Generation = table.GetInt(row, "Generation", 9),
                EvSystem = evSystem,
                CommandPrefix = prefix,
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
