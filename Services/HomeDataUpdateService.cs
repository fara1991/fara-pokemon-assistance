using System.Text;
using System.Text.Json;

namespace FaraPokemonAssistance.Services
{
    public class HomeDataUpdateService : BackgroundService
    {
        private readonly ILogger<HomeDataUpdateService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly string _dataDir;

        private static readonly Dictionary<int, int> HomeItemToOurId = new()
        {
            { 220, 1 },   // Choice Band
            { 297, 2 },   // Choice Specs
            { 275, 3 },   // Focus Sash
            { 270, 4 },   // Life Orb
            { 268, 5 },   // Expert Belt
            { 236, 6 },   // Light Ball
            { 300, 7 },   // Zap Plate
            { 299, 8 },   // Splash Plate
            { 298, 9 },   // Flame Plate
            { 301, 10 },  // Meadow Plate
            { 302, 11 },  // Icicle Plate
            { 303, 12 },  // Fist Plate
            { 304, 13 },  // Toxic Plate
            { 305, 14 },  // Earth Plate
            { 306, 15 },  // Sky Plate
            { 307, 16 },  // Mind Plate
            { 308, 17 },  // Insect Plate
            { 309, 18 },  // Stone Plate
            { 310, 19 },  // Spooky Plate
            { 311, 20 },  // Draco Plate
            { 312, 21 },  // Dread Plate
            { 313, 22 },  // Iron Plate
            { 644, 23 },  // Pixie Plate
            { 287, 24 },  // Choice Scarf
            { 640, 25 },  // Assault Vest
            { 538, 26 },  // Eviolite
            { 540, 27 },  // Rocky Helmet
            { 234, 28 },  // Leftovers
            { 158, 29 },  // Sitrus Berry
            { 157, 30 },  // Lum Berry
            { 1880, 31 }, // Booster Energy
            { 1882, 32 }, // Clear Amulet
            { 1885, 33 }, // Covert Cloak
            { 267, 34 },  // Wise Glasses
            { 266, 35 },  // Muscle Band
        };

        public HomeDataUpdateService(ILogger<HomeDataUpdateService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _dataDir = Path.Combine("Data", "Gen9");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Run once at startup
            await UpdateDataAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var nextSunday = GetNextSundayMidnight(now);
                var delay = nextSunday - now;

                _logger.LogInformation("Next HOME data update scheduled at {NextUpdate}", nextSunday);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                await UpdateDataAsync(stoppingToken);
            }
        }

        private static DateTime GetNextSundayMidnight(DateTime from)
        {
            var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)from.DayOfWeek + 7) % 7;
            if (daysUntilSunday == 0 && from.TimeOfDay > TimeSpan.Zero)
                daysUntilSunday = 7;
            return from.Date.AddDays(daysUntilSunday);
        }

        private async Task UpdateDataAsync(CancellationToken ct)
        {
            _logger.LogInformation("Starting HOME data update...");

            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "FaraPokemonAssistance/1.0");
                httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

                var seasonData = await FetchSeasonListAsync(httpClient, ct);
                if (seasonData == null)
                {
                    _logger.LogWarning("Failed to fetch season list");
                    return;
                }

                var latestSeason = GetLatestCompletedSeason(seasonData.Value);
                if (latestSeason == null)
                {
                    _logger.LogWarning("No completed season found");
                    return;
                }

                foreach (var (format, ruleEntry) in latestSeason)
                {
                    await ProcessFormatAsync(httpClient, format, ruleEntry, ct);
                }

                // Clear UsageDataService cache by replacing the singleton
                var usageService = _serviceProvider.GetService<IUsageDataService>() as UsageDataService;
                usageService?.ClearCache();

                _logger.LogInformation("HOME data update completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HOME data update failed");
            }
        }

        private async Task<JsonElement?> FetchSeasonListAsync(HttpClient client, CancellationToken ct)
        {
            var content = new StringContent("{\"soft\":\"Sw\"}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync(
                "https://api.battle.pokemon-home.com/tt/cbd/competition/rankmatch/list", content, ct);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        private Dictionary<string, JsonElement>? GetLatestCompletedSeason(JsonElement seasonData)
        {
            if (!seasonData.TryGetProperty("list", out var list))
                return null;

            int maxSeason = 0;
            JsonElement? latestSeasonEntry = null;

            foreach (var seasonProp in list.EnumerateObject())
            {
                if (int.TryParse(seasonProp.Name, out var seasonNum) && seasonNum > maxSeason)
                {
                    maxSeason = seasonNum;
                    latestSeasonEntry = seasonProp.Value;
                }
            }

            if (latestSeasonEntry == null || !latestSeasonEntry.Value.TryGetProperty("rule", out var rules))
                return null;

            var result = new Dictionary<string, JsonElement>();
            foreach (var rule in rules.EnumerateArray())
            {
                var rst = rule.GetProperty("rst").GetInt32();
                if (rst != 2) continue; // Only completed seasons

                var ruleNum = rule.GetProperty("rule").GetInt32();
                var format = ruleNum == 0 ? "singles" : "doubles";
                result[format] = rule;
            }

            return result.Count > 0 ? result : null;
        }

        private async Task ProcessFormatAsync(HttpClient client, string format, JsonElement ruleEntry, CancellationToken ct)
        {
            var cId = ruleEntry.GetProperty("cId").GetString()!;
            var rst = ruleEntry.GetProperty("rst").GetInt32();
            var ts2 = ruleEntry.GetProperty("ts2").GetInt64();

            var baseUrl = $"https://resource.pokemon-home.com/battledata/ranking/scvi/{cId}/{rst}/{ts2}";

            _logger.LogInformation("Fetching {Format} data: cId={CId}", format, cId);

            // Fetch pokemon ranking
            var pokemonJson = await FetchResourceAsync(client, $"{baseUrl}/pokemon", ct);
            if (pokemonJson == null)
            {
                _logger.LogWarning("Failed to fetch {Format} pokemon ranking", format);
                return;
            }

            // Write pokemon ranking CSV
            var pokemonRanking = JsonSerializer.Deserialize<JsonElement>(pokemonJson);
            var pokemonLines = new List<string> { "Rank,SpeciesId" };
            int rank = 1;
            var seenSpecies = new HashSet<int>();
            foreach (var entry in pokemonRanking.EnumerateArray())
            {
                var speciesId = entry.GetProperty("id").GetInt32();
                if (seenSpecies.Add(speciesId))
                {
                    pokemonLines.Add($"{rank},{speciesId}");
                    rank++;
                }
            }
            await File.WriteAllLinesAsync(Path.Combine(_dataDir, $"usage_pokemon_{format}.csv"), pokemonLines, new UTF8Encoding(false), ct);

            // Fetch and process pdetail files
            var moveLines = new List<string> { "SpeciesId,MoveId" };
            var itemLines = new List<string> { "SpeciesId,ItemId" };
            var natureLines = new List<string> { "SpeciesId,NatureId" };
            var seenPokemon = new HashSet<int>();

            for (int i = 1; i <= 6; i++)
            {
                var detailJson = await FetchResourceAsync(client, $"{baseUrl}/pdetail-{i}", ct);
                if (detailJson == null) continue;

                var detailData = JsonSerializer.Deserialize<JsonElement>(detailJson);
                foreach (var pokeProp in detailData.EnumerateObject())
                {
                    if (!int.TryParse(pokeProp.Name, out var pokeId)) continue;
                    if (!seenPokemon.Add(pokeId)) continue;

                    foreach (var formProp in pokeProp.Value.EnumerateObject())
                    {
                        if (!formProp.Value.TryGetProperty("temoti", out var temoti)) break;

                        // Process moves
                        if (temoti.TryGetProperty("waza", out var waza))
                        {
                            foreach (var move in waza.EnumerateArray())
                            {
                                var moveIdStr = move.GetProperty("id").GetString();
                                if (int.TryParse(moveIdStr, out var moveId))
                                    moveLines.Add($"{pokeId},{moveId}");
                            }
                        }

                        // Process items
                        if (temoti.TryGetProperty("motimono", out var motimono))
                        {
                            foreach (var item in motimono.EnumerateArray())
                            {
                                var itemIdStr = item.GetProperty("id").GetString();
                                if (int.TryParse(itemIdStr, out var homeItemId) && HomeItemToOurId.TryGetValue(homeItemId, out var ourItemId))
                                    itemLines.Add($"{pokeId},{ourItemId}");
                            }
                        }

                        // Process natures
                        if (temoti.TryGetProperty("seikaku", out var seikaku))
                        {
                            foreach (var nature in seikaku.EnumerateArray())
                            {
                                var natureIdStr = nature.GetProperty("id").GetString();
                                if (int.TryParse(natureIdStr, out var natureId))
                                    natureLines.Add($"{pokeId},{natureId}");
                            }
                        }

                        break; // Only first form
                    }
                }
            }

            await File.WriteAllLinesAsync(Path.Combine(_dataDir, $"usage_moves_{format}.csv"), moveLines, new UTF8Encoding(false), ct);
            await File.WriteAllLinesAsync(Path.Combine(_dataDir, $"usage_items_{format}.csv"), itemLines, new UTF8Encoding(false), ct);
            await File.WriteAllLinesAsync(Path.Combine(_dataDir, $"usage_natures_{format}.csv"), natureLines, new UTF8Encoding(false), ct);

            _logger.LogInformation("{Format}: {PokemonCount} pokemon, {MoveCount} moves, {ItemCount} items, {NatureCount} natures",
                format, pokemonLines.Count - 1, moveLines.Count - 1, itemLines.Count - 1, natureLines.Count - 1);
        }

        private async Task<string?> FetchResourceAsync(HttpClient client, string url, CancellationToken ct)
        {
            try
            {
                var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("HTTP {StatusCode} fetching {Url}", response.StatusCode, url);
                    return null;
                }
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching {Url}", url);
                return null;
            }
        }
    }
}
