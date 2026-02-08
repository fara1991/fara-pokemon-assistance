namespace FaraPokemonTools.Services
{
    public interface IUsageDataService
    {
        Task<Dictionary<int, int>> GetPokemonRankingAsync(int generation);
        Task<List<int>> GetMoveUsageOrderAsync(int pokemonId, int generation);
        Task<List<int>> GetItemUsageOrderAsync(int pokemonId, int generation);
    }

    public class UsageDataService : IUsageDataService
    {
        private readonly Dictionary<int, Dictionary<int, int>> _pokemonRankCache = new();
        private readonly Dictionary<int, Dictionary<int, List<int>>> _moveUsageCache = new();
        private readonly Dictionary<int, Dictionary<int, List<int>>> _itemUsageCache = new();
        private readonly Dictionary<int, Dictionary<int, int>> _speciesMapCache = new();

        private static readonly Dictionary<string, int> ItemNameToId = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Choice Band", 1 },
            { "Choice Specs", 2 },
            { "Focus Sash", 3 },
            { "Life Orb", 4 },
            { "Expert Belt", 5 },
            { "Light Ball", 6 },
            { "Zap Plate", 7 },
            { "Splash Plate", 8 },
            { "Flame Plate", 9 },
            { "Meadow Plate", 10 },
            { "Icicle Plate", 11 },
            { "Fist Plate", 12 },
            { "Toxic Plate", 13 },
            { "Earth Plate", 14 },
            { "Sky Plate", 15 },
            { "Mind Plate", 16 },
            { "Insect Plate", 17 },
            { "Stone Plate", 18 },
            { "Spooky Plate", 19 },
            { "Draco Plate", 20 },
            { "Dread Plate", 21 },
            { "Iron Plate", 22 },
            { "Pixie Plate", 23 },
            { "Choice Scarf", 24 },
            { "Assault Vest", 25 },
            { "Eviolite", 26 },
            { "Rocky Helmet", 27 },
            { "Leftovers", 28 },
            { "Sitrus Berry", 29 },
            { "Lum Berry", 30 },
            { "Booster Energy", 31 },
            { "Clear Amulet", 32 },
            { "Covert Cloak", 33 },
            { "Wise Glasses", 34 },
            { "Muscle Band", 35 },
        };

        public async Task<Dictionary<int, int>> GetPokemonRankingAsync(int generation)
        {
            if (_pokemonRankCache.ContainsKey(generation))
                return _pokemonRankCache[generation];

            var speciesMap = await LoadSpeciesMapAsync(generation);
            var filePath = Path.Combine("Data", $"Gen{generation}", "usage_pokemon.csv");
            var ranking = new Dictionary<int, int>();

            if (File.Exists(filePath))
            {
                var lines = await File.ReadAllLinesAsync(filePath);
                foreach (var line in lines.Skip(1))
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var rank) && int.TryParse(parts[1], out var speciesId))
                    {
                        if (!ranking.ContainsKey(speciesId))
                            ranking[speciesId] = rank;
                    }
                }
            }

            foreach (var (formId, speciesId) in speciesMap)
            {
                if (ranking.ContainsKey(speciesId) && !ranking.ContainsKey(formId))
                    ranking[formId] = ranking[speciesId];
            }

            _pokemonRankCache[generation] = ranking;
            return ranking;
        }

        public async Task<List<int>> GetMoveUsageOrderAsync(int pokemonId, int generation)
        {
            if (!_moveUsageCache.ContainsKey(generation))
            {
                var speciesMap = await LoadSpeciesMapAsync(generation);
                var filePath = Path.Combine("Data", $"Gen{generation}", "usage_moves.csv");
                var cache = new Dictionary<int, List<int>>();

                if (File.Exists(filePath))
                {
                    var lines = await File.ReadAllLinesAsync(filePath);
                    foreach (var line in lines.Skip(1))
                    {
                        var parts = line.Split(',');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out var speciesId) && int.TryParse(parts[1], out var moveId))
                        {
                            if (!cache.ContainsKey(speciesId))
                                cache[speciesId] = new List<int>();
                            cache[speciesId].Add(moveId);
                        }
                    }
                }

                foreach (var (formId, speciesId) in speciesMap)
                {
                    if (cache.ContainsKey(speciesId) && !cache.ContainsKey(formId))
                        cache[formId] = cache[speciesId];
                }

                _moveUsageCache[generation] = cache;
            }

            return _moveUsageCache[generation].TryGetValue(pokemonId, out var list) ? list : new List<int>();
        }

        public async Task<List<int>> GetItemUsageOrderAsync(int pokemonId, int generation)
        {
            if (!_itemUsageCache.ContainsKey(generation))
            {
                var speciesMap = await LoadSpeciesMapAsync(generation);
                var filePath = Path.Combine("Data", $"Gen{generation}", "usage_items.csv");
                var cache = new Dictionary<int, List<int>>();

                if (File.Exists(filePath))
                {
                    var lines = await File.ReadAllLinesAsync(filePath);
                    foreach (var line in lines.Skip(1))
                    {
                        var parts = line.Split(',');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out var speciesId))
                        {
                            var itemName = parts[1];
                            if (ItemNameToId.TryGetValue(itemName, out var itemId))
                            {
                                if (!cache.ContainsKey(speciesId))
                                    cache[speciesId] = new List<int>();
                                cache[speciesId].Add(itemId);
                            }
                        }
                    }
                }

                foreach (var (formId, speciesId) in speciesMap)
                {
                    if (cache.ContainsKey(speciesId) && !cache.ContainsKey(formId))
                        cache[formId] = cache[speciesId];
                }

                _itemUsageCache[generation] = cache;
            }

            return _itemUsageCache[generation].TryGetValue(pokemonId, out var list) ? list : new List<int>();
        }

        private async Task<Dictionary<int, int>> LoadSpeciesMapAsync(int generation)
        {
            if (_speciesMapCache.ContainsKey(generation))
                return _speciesMapCache[generation];

            var filePath = Path.Combine("Data", $"Gen{generation}", "species_map.csv");
            var map = new Dictionary<int, int>();

            if (File.Exists(filePath))
            {
                var lines = await File.ReadAllLinesAsync(filePath);
                foreach (var line in lines.Skip(1))
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var formId) && int.TryParse(parts[1], out var speciesId))
                        map[formId] = speciesId;
                }
            }

            _speciesMapCache[generation] = map;
            return map;
        }
    }
}
