namespace FaraPokemonTools.Services
{
    public interface IUsageDataService
    {
        Task<Dictionary<int, int>> GetPokemonRankingAsync(int generation, string battleFormat = "singles");
        Task<List<int>> GetMoveUsageOrderAsync(int pokemonId, int generation, string battleFormat = "singles");
        Task<List<int>> GetItemUsageOrderAsync(int pokemonId, int generation, string battleFormat = "singles");
        Task<List<int>> GetNatureUsageOrderAsync(int pokemonId, int generation, string battleFormat = "singles");
    }

    public class UsageDataService : IUsageDataService
    {
        private readonly Dictionary<string, Dictionary<int, int>> _pokemonRankCache = new();
        private readonly Dictionary<string, Dictionary<int, List<int>>> _moveUsageCache = new();
        private readonly Dictionary<string, Dictionary<int, List<int>>> _itemUsageCache = new();
        private readonly Dictionary<string, Dictionary<int, List<int>>> _natureUsageCache = new();
        private readonly Dictionary<int, Dictionary<int, int>> _speciesMapCache = new();

        private static string CacheKey(int generation, string format) => $"{generation}_{format}";

        public void ClearCache()
        {
            _pokemonRankCache.Clear();
            _moveUsageCache.Clear();
            _itemUsageCache.Clear();
            _natureUsageCache.Clear();
            _speciesMapCache.Clear();
        }

        public async Task<Dictionary<int, int>> GetPokemonRankingAsync(int generation, string battleFormat = "singles")
        {
            var key = CacheKey(generation, battleFormat);
            if (_pokemonRankCache.ContainsKey(key))
                return _pokemonRankCache[key];

            var speciesMap = await LoadSpeciesMapAsync(generation);
            var filePath = Path.Combine("Data", $"Gen{generation}", $"usage_pokemon_{battleFormat}.csv");
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

            _pokemonRankCache[key] = ranking;
            return ranking;
        }

        public async Task<List<int>> GetMoveUsageOrderAsync(int pokemonId, int generation, string battleFormat = "singles")
        {
            var key = CacheKey(generation, battleFormat);
            if (!_moveUsageCache.ContainsKey(key))
            {
                var speciesMap = await LoadSpeciesMapAsync(generation);
                var filePath = Path.Combine("Data", $"Gen{generation}", $"usage_moves_{battleFormat}.csv");
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

                _moveUsageCache[key] = cache;
            }

            return _moveUsageCache[key].TryGetValue(pokemonId, out var list) ? list : new List<int>();
        }

        public async Task<List<int>> GetItemUsageOrderAsync(int pokemonId, int generation, string battleFormat = "singles")
        {
            var key = CacheKey(generation, battleFormat);
            if (!_itemUsageCache.ContainsKey(key))
            {
                var speciesMap = await LoadSpeciesMapAsync(generation);
                var filePath = Path.Combine("Data", $"Gen{generation}", $"usage_items_{battleFormat}.csv");
                var cache = new Dictionary<int, List<int>>();

                if (File.Exists(filePath))
                {
                    var lines = await File.ReadAllLinesAsync(filePath);
                    foreach (var line in lines.Skip(1))
                    {
                        var parts = line.Split(',');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out var speciesId) && int.TryParse(parts[1], out var itemId))
                        {
                            if (!cache.ContainsKey(speciesId))
                                cache[speciesId] = new List<int>();
                            cache[speciesId].Add(itemId);
                        }
                    }
                }

                foreach (var (formId, speciesId) in speciesMap)
                {
                    if (cache.ContainsKey(speciesId) && !cache.ContainsKey(formId))
                        cache[formId] = cache[speciesId];
                }

                _itemUsageCache[key] = cache;
            }

            return _itemUsageCache[key].TryGetValue(pokemonId, out var list) ? list : new List<int>();
        }

        public async Task<List<int>> GetNatureUsageOrderAsync(int pokemonId, int generation, string battleFormat = "singles")
        {
            var key = CacheKey(generation, battleFormat);
            if (!_natureUsageCache.ContainsKey(key))
            {
                var speciesMap = await LoadSpeciesMapAsync(generation);
                var filePath = Path.Combine("Data", $"Gen{generation}", $"usage_natures_{battleFormat}.csv");
                var cache = new Dictionary<int, List<int>>();

                if (File.Exists(filePath))
                {
                    var lines = await File.ReadAllLinesAsync(filePath);
                    foreach (var line in lines.Skip(1))
                    {
                        var parts = line.Split(',');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out var speciesId) && int.TryParse(parts[1], out var natureId))
                        {
                            if (!cache.ContainsKey(speciesId))
                                cache[speciesId] = new List<int>();
                            cache[speciesId].Add(natureId);
                        }
                    }
                }

                foreach (var (formId, speciesId) in speciesMap)
                {
                    if (cache.ContainsKey(speciesId) && !cache.ContainsKey(formId))
                        cache[formId] = cache[speciesId];
                }

                _natureUsageCache[key] = cache;
            }

            return _natureUsageCache[key].TryGetValue(pokemonId, out var list) ? list : new List<int>();
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
