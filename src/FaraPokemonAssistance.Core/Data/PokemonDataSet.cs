using FaraPokemonAssistance.Core.Csv;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Data;

/// <summary>
/// 1 つのデータセット（例: Gen9、Champions）のポケモン・技・持ち物・タイプ相性・使用率。
/// <see cref="DataCatalog.GetDataSetAsync"/> から取得する。
/// </summary>
public sealed class PokemonDataSet
{
    private readonly IDataSource _source;
    private readonly Dictionary<BattleFormat, Task<UsageData>> _usage = new();

    public DataSetInfo Info { get; }
    public IReadOnlyList<Pokemon> Pokemon { get; private set; } = Array.Empty<Pokemon>();
    public IReadOnlyList<Move> Moves { get; private set; } = Array.Empty<Move>();
    public IReadOnlyList<Item> Items { get; private set; } = Array.Empty<Item>();
    public IReadOnlyList<Ability> Abilities { get; private set; } = Array.Empty<Ability>();
    public IReadOnlyList<Nature> Natures { get; }
    public TypeChart TypeChart { get; private set; } = TypeChart.Empty;

    private Dictionary<int, Pokemon> _pokemonById = new();
    private Dictionary<int, Move> _moveById = new();
    private Dictionary<int, Item> _itemById = new();
    private Dictionary<int, Ability> _abilityById = new();
    private Dictionary<int, int[]> _learnsets = new();
    private Dictionary<int, int> _speciesMap = new();

    internal PokemonDataSet(IDataSource source, DataSetInfo info, IReadOnlyList<Nature> natures)
    {
        _source = source;
        Info = info;
        Natures = natures;
    }

    internal async Task LoadAsync(CancellationToken ct)
    {
        var dir = Info.Key;
        var pokemonText = await _source.ReadTextAsync($"{dir}/pokemon.csv", ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"{dir}/pokemon.csv が見つかりません");
        var movesText = await _source.ReadTextAsync($"{dir}/moves.csv", ct).ConfigureAwait(false) ?? "";
        var itemsText = await _source.ReadTextAsync($"{dir}/items.csv", ct).ConfigureAwait(false) ?? "";
        var learnText = await _source.ReadTextAsync($"{dir}/learnsets.csv", ct).ConfigureAwait(false) ?? "";
        var speciesText = await _source.ReadTextAsync($"{dir}/species_map.csv", ct).ConfigureAwait(false) ?? "";
        var typeText = await _source.ReadTextAsync($"{dir}/type_effectiveness.csv", ct).ConfigureAwait(false) ?? "";
        var abilityText = await _source.ReadTextAsync($"{dir}/abilities.csv", ct).ConfigureAwait(false) ?? "";

        var abilityTable = CsvTable.Parse(abilityText);
        var abilities = new List<Ability>();
        foreach (var row in abilityTable.Rows)
        {
            abilities.Add(new Ability
            {
                Id = abilityTable.GetInt(row, "Id"),
                Identifier = abilityTable.Get(row, "Identifier"),
                Name = abilityTable.Get(row, "Name"),
            });
        }
        Abilities = abilities;
        _abilityById = abilities.ToDictionary(a => a.Id);

        var speciesMap = new Dictionary<int, int>();
        var speciesTable = CsvTable.Parse(speciesText);
        foreach (var row in speciesTable.Rows)
            speciesMap[speciesTable.GetInt(row, "FormId")] = speciesTable.GetInt(row, "SpeciesId");
        _speciesMap = speciesMap;

        var pokemonTable = CsvTable.Parse(pokemonText);
        var pokemon = new List<Pokemon>();
        foreach (var row in pokemonTable.Rows)
        {
            var id = pokemonTable.GetInt(row, "Id");
            pokemon.Add(new Pokemon
            {
                Id = id,
                Name = pokemonTable.Get(row, "Name"),
                Type1 = pokemonTable.Get(row, "Type1"),
                Type2 = pokemonTable.Get(row, "Type2"),
                BaseStats = new StatSet(
                    pokemonTable.GetInt(row, "HP"), pokemonTable.GetInt(row, "Attack"), pokemonTable.GetInt(row, "Defense"),
                    pokemonTable.GetInt(row, "SpAttack"), pokemonTable.GetInt(row, "SpDefense"), pokemonTable.GetInt(row, "Speed")),
                Icon = pokemonTable.Get(row, "Icon"),
                SpeciesId = pokemonTable.HasColumn("SpeciesId")
                    ? pokemonTable.GetInt(row, "SpeciesId", id)
                    : speciesMap.GetValueOrDefault(id, id),
                NotFullyEvolved = pokemonTable.GetInt(row, "NotFullyEvolved") == 1,
                AbilityIds = ParseIdList(pokemonTable.Get(row, "Abilities")),
                IsProvisional = pokemonTable.GetInt(row, "Provisional") == 1,
            });
        }
        Pokemon = pokemon;
        _pokemonById = pokemon.ToDictionary(p => p.Id);

        var moveTable = CsvTable.Parse(movesText);
        var moves = new List<Move>();
        foreach (var row in moveTable.Rows)
        {
            moves.Add(new Move
            {
                Id = moveTable.GetInt(row, "Id"),
                Name = moveTable.Get(row, "Name"),
                Type = moveTable.Get(row, "Type"),
                Power = moveTable.GetInt(row, "Power"),
                Accuracy = moveTable.GetInt(row, "Accuracy"),
                PP = moveTable.GetInt(row, "PP"),
                Category = Enum.TryParse<MoveCategory>(moveTable.Get(row, "Category"), true, out var cat) ? cat : MoveCategory.Status,
                Target = Enum.TryParse<MoveTarget>(moveTable.Get(row, "Target"), true, out var target) ? target : MoveTarget.Single,
                Priority = moveTable.GetInt(row, "Priority"),
                Flags = new HashSet<string>(moveTable.Get(row, "Flags").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                EffectChance = moveTable.GetInt(row, "EffectChance"),
                Description = moveTable.Get(row, "Description"),
            });
        }
        Moves = moves;
        _moveById = moves.ToDictionary(m => m.Id);

        var itemTable = CsvTable.Parse(itemsText);
        var items = new List<Item>();
        foreach (var row in itemTable.Rows)
        {
            items.Add(new Item
            {
                Id = itemTable.GetInt(row, "Id"),
                Name = itemTable.Get(row, "Name"),
                Category = Enum.TryParse<ItemCategory>(itemTable.Get(row, "Category"), true, out var ic) ? ic : ItemCategory.Other,
                Effect = itemTable.Get(row, "Effect"),
                AttackMultiplier = itemTable.GetDouble(row, "AttackMultiplier", 1.0),
                DefenseMultiplier = itemTable.GetDouble(row, "DefenseMultiplier", 1.0),
                SpAttackMultiplier = itemTable.GetDouble(row, "SpAttackMultiplier", 1.0),
                SpDefenseMultiplier = itemTable.GetDouble(row, "SpDefenseMultiplier", 1.0),
                DamageMultiplier = itemTable.GetDouble(row, "DamageMultiplier", 1.0),
                TypeBoost = itemTable.Get(row, "TypeBoost"),
                TypeBoostMultiplier = itemTable.GetDouble(row, "TypeBoostMultiplier", 1.0),
            });
        }
        Items = items;
        _itemById = items.ToDictionary(i => i.Id);

        var learnTable = CsvTable.Parse(learnText);
        var learnsets = new Dictionary<int, int[]>();
        foreach (var row in learnTable.Rows)
        {
            learnsets[learnTable.GetInt(row, "PokemonId")] = ParseIdList(learnTable.Get(row, "MoveIds"));
        }
        _learnsets = learnsets;

        TypeChart = TypeChart.Parse(typeText);
    }

    private static int[] ParseIdList(string text) => text
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.TryParse(s, out var v) ? v : -1)
        .Where(v => v > 0)
        .ToArray();

    public Pokemon? FindPokemon(int id) => _pokemonById.GetValueOrDefault(id);
    public Ability? FindAbility(int id) => _abilityById.GetValueOrDefault(id);

    /// <summary>そのポケモンが持ちうる特性（通常 → 隠れ特性の順）。</summary>
    public IReadOnlyList<Ability> AbilitiesOf(Pokemon pokemon) =>
        pokemon.AbilityIds.Select(FindAbility).Where(a => a is not null).Select(a => a!).ToList();
    public Move? FindMove(int id) => _moveById.GetValueOrDefault(id);
    public Item? FindItem(int id) => _itemById.GetValueOrDefault(id);
    public Nature? FindNature(int id) => Natures.FirstOrDefault(n => n.Id == id);
    public Nature? FindNature(string name) => Natures.FirstOrDefault(n => n.Name == name);

    /// <summary>フォルム ID を種族 ID に変換する（使用率データは種族単位）。</summary>
    public int SpeciesIdOf(int pokemonId) =>
        _pokemonById.TryGetValue(pokemonId, out var p) ? p.SpeciesId : _speciesMap.GetValueOrDefault(pokemonId, pokemonId);

    /// <summary>覚える技。データが無ければ全技を返す。</summary>
    public IReadOnlyList<Move> LearnableMoves(int pokemonId)
    {
        if (!_learnsets.TryGetValue(pokemonId, out var ids) || ids.Length == 0)
            return Moves;
        var set = new HashSet<int>(ids);
        return Moves.Where(m => set.Contains(m.Id)).ToList();
    }

    public bool CanLearn(int pokemonId, int moveId) =>
        !_learnsets.TryGetValue(pokemonId, out var ids) || ids.Length == 0 || Array.IndexOf(ids, moveId) >= 0;

    public Task<UsageData> GetUsageAsync(BattleFormat format, CancellationToken ct = default)
    {
        lock (_usage)
        {
            if (!_usage.TryGetValue(format, out var task))
            {
                task = UsageData.LoadAsync(_source, Info.Key, format, this, ct);
                _usage[format] = task;
            }
            return task;
        }
    }
}

/// <summary>タイプ相性表。</summary>
public sealed class TypeChart
{
    private readonly Dictionary<(string, string), double> _table;

    public static TypeChart Empty { get; } = new(new Dictionary<(string, string), double>());

    private TypeChart(Dictionary<(string, string), double> table)
    {
        _table = table;
    }

    public static TypeChart Parse(string csvText)
    {
        var table = CsvTable.Parse(csvText);
        var dict = new Dictionary<(string, string), double>();
        foreach (var row in table.Rows)
            dict[(table.Get(row, "AttackType"), table.Get(row, "DefenseType"))] = table.GetDouble(row, "Multiplier", 1.0);
        return new TypeChart(dict);
    }

    public double Against(string attackType, string defenseType) =>
        string.IsNullOrEmpty(defenseType) ? 1.0 : _table.GetValueOrDefault((attackType, defenseType), 1.0);

    public double Against(string attackType, Pokemon defender) =>
        Against(attackType, defender.Type1) * Against(attackType, defender.Type2);
}

/// <summary>ポケモン HOME のランクバトル使用率。種族 ID をキーに、使用率順の ID を持つ。</summary>
public sealed class UsageData
{
    public BattleFormat Format { get; }
    public IReadOnlyDictionary<int, int> PokemonRank { get; }
    private readonly Dictionary<int, List<int>> _moves;
    private readonly Dictionary<int, List<int>> _items;
    private readonly Dictionary<int, List<int>> _natures;
    private readonly Dictionary<int, List<int>> _abilities;
    private readonly PokemonDataSet _dataSet;

    public bool IsEmpty => PokemonRank.Count == 0;

    private UsageData(BattleFormat format, PokemonDataSet dataSet, Dictionary<int, int> rank,
        Dictionary<int, List<int>> moves, Dictionary<int, List<int>> items, Dictionary<int, List<int>> natures,
        Dictionary<int, List<int>> abilities)
    {
        Format = format;
        _dataSet = dataSet;
        PokemonRank = rank;
        _moves = moves;
        _items = items;
        _natures = natures;
        _abilities = abilities;
    }

    internal static async Task<UsageData> LoadAsync(IDataSource source, string dir, BattleFormat format, PokemonDataSet dataSet, CancellationToken ct)
    {
        var suffix = format == BattleFormat.Doubles ? "doubles" : "singles";
        var rank = new Dictionary<int, int>();
        var rankTable = CsvTable.Parse(await source.ReadTextAsync($"{dir}/usage_pokemon_{suffix}.csv", ct).ConfigureAwait(false) ?? "");
        foreach (var row in rankTable.Rows)
            rank.TryAdd(rankTable.GetInt(row, "SpeciesId"), rankTable.GetInt(row, "Rank"));

        async Task<Dictionary<int, List<int>>> LoadListAsync(string file, string column)
        {
            var table = CsvTable.Parse(await source.ReadTextAsync($"{dir}/{file}", ct).ConfigureAwait(false) ?? "");
            var dict = new Dictionary<int, List<int>>();
            foreach (var row in table.Rows)
            {
                var species = table.GetInt(row, "SpeciesId");
                if (!dict.TryGetValue(species, out var list))
                    dict[species] = list = new List<int>();
                list.Add(table.GetInt(row, column));
            }
            return dict;
        }

        var moves = await LoadListAsync($"usage_moves_{suffix}.csv", "MoveId").ConfigureAwait(false);
        var items = await LoadListAsync($"usage_items_{suffix}.csv", "ItemId").ConfigureAwait(false);
        var natures = await LoadListAsync($"usage_natures_{suffix}.csv", "NatureId").ConfigureAwait(false);
        var abilities = await LoadListAsync($"usage_abilities_{suffix}.csv", "AbilityId").ConfigureAwait(false);
        return new UsageData(format, dataSet, rank, moves, items, natures, abilities);
    }

    public int RankOf(int pokemonId) =>
        PokemonRank.TryGetValue(pokemonId, out var r) ? r
        : PokemonRank.TryGetValue(_dataSet.SpeciesIdOf(pokemonId), out r) ? r
        : int.MaxValue;

    private static IReadOnlyList<int> Lookup(Dictionary<int, List<int>> dict, int pokemonId, int speciesId) =>
        dict.TryGetValue(pokemonId, out var list) ? list
        : dict.TryGetValue(speciesId, out list) ? list
        : Array.Empty<int>();

    public IReadOnlyList<int> MoveOrder(int pokemonId) => Lookup(_moves, pokemonId, _dataSet.SpeciesIdOf(pokemonId));
    public IReadOnlyList<int> ItemOrder(int pokemonId) => Lookup(_items, pokemonId, _dataSet.SpeciesIdOf(pokemonId));
    public IReadOnlyList<int> NatureOrder(int pokemonId) => Lookup(_natures, pokemonId, _dataSet.SpeciesIdOf(pokemonId));
    public IReadOnlyList<int> AbilityOrder(int pokemonId) => Lookup(_abilities, pokemonId, _dataSet.SpeciesIdOf(pokemonId));

    /// <summary>使用率 1 位の特性。使用率データが無ければそのポケモンの第 1 特性。</summary>
    public Ability? TopAbility(Pokemon pokemon)
    {
        var allowed = new HashSet<int>(pokemon.AbilityIds);
        var top = AbilityOrder(pokemon.Id).Where(allowed.Contains).Select(_dataSet.FindAbility).FirstOrDefault(a => a is not null);
        return top ?? _dataSet.AbilitiesOf(pokemon).FirstOrDefault();
    }

    public Move? TopMove(int pokemonId) =>
        MoveOrder(pokemonId).Select(_dataSet.FindMove).FirstOrDefault(m => m is not null && m.IsDamaging && _dataSet.CanLearn(pokemonId, m.Id));

    public Item? TopItem(int pokemonId) =>
        ItemOrder(pokemonId).Select(_dataSet.FindItem).FirstOrDefault(i => i is not null);

    public Nature? TopNature(int pokemonId) =>
        NatureOrder(pokemonId).Select(_dataSet.FindNature).FirstOrDefault(n => n is not null);

    /// <summary>使用率順に並べ替える（使用率が無いものは元の順序のまま後ろ）。</summary>
    public IReadOnlyList<T> SortByUsage<T>(IEnumerable<T> items, Func<T, int> idSelector, IReadOnlyList<int> order)
    {
        var position = new Dictionary<int, int>();
        for (var i = 0; i < order.Count; i++)
            position.TryAdd(order[i], i);
        return items.OrderBy(x => position.TryGetValue(idSelector(x), out var p) ? p : int.MaxValue).ToList();
    }
}
