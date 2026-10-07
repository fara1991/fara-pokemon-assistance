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

    /// <summary>何も指定されなかったときに使うデータセットのキー。</summary>
    public const string DefaultDataSetKey = "Gen9";

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
        return table.Rows.Select(row => new DataSetInfo
        {
            Key = table.Get(row, "Key"),
            Name = table.Get(row, "Name"),
            Generation = table.GetInt(row, "Generation", 9),
        }).ToList();
    }

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
