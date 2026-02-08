using CsvHelper;
using FaraPokemonTools.Models;
using System.Globalization;

namespace FaraPokemonTools.Services
{
    public class MoveDataService : IMoveDataService
    {
        private readonly Dictionary<int, List<Move>> _moveCache = new();
        private readonly Dictionary<int, Dictionary<int, List<int>>> _learnsetCache = new();

        public async Task<List<Move>> GetMovesAsync(int generation)
        {
            if (_moveCache.ContainsKey(generation))
                return _moveCache[generation];

            var filePath = Path.Combine("Data", $"Gen{generation}", "moves.csv");
            if (!File.Exists(filePath))
                return new List<Move>();

            using var reader = new StringReader(await File.ReadAllTextAsync(filePath));
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            
            var records = csv.GetRecords<dynamic>().ToList();
            var moves = records.Select(r => new Move
            {
                Id = int.Parse(r.Id),
                Name = r.Name,
                Type = r.Type,
                Power = int.Parse(r.Power),
                Accuracy = int.Parse(r.Accuracy),
                PP = int.Parse(r.PP),
                Category = r.Category,
                Description = r.Description ?? ""
            }).ToList();

            _moveCache[generation] = moves;
            return moves;
        }

        public async Task<Move?> GetMoveByIdAsync(int id, int generation)
        {
            var moves = await GetMovesAsync(generation);
            return moves.FirstOrDefault(m => m.Id == id);
        }

        public async Task<List<int>> GetLearnsetAsync(int pokemonId, int generation)
        {
            if (!_learnsetCache.ContainsKey(generation))
            {
                var filePath = Path.Combine("Data", $"Gen{generation}", "learnsets.csv");
                if (!File.Exists(filePath))
                {
                    _learnsetCache[generation] = new Dictionary<int, List<int>>();
                }
                else
                {
                    var dict = new Dictionary<int, List<int>>();
                    var lines = await File.ReadAllLinesAsync(filePath);
                    foreach (var line in lines.Skip(1))
                    {
                        var parts = line.Split(',', 2);
                        if (parts.Length < 2 || !int.TryParse(parts[0], out var pid))
                            continue;
                        var moveIds = parts[1]
                            .Split(';', StringSplitOptions.RemoveEmptyEntries)
                            .Where(s => int.TryParse(s, out _))
                            .Select(int.Parse)
                            .ToList();
                        dict[pid] = moveIds;
                    }
                    _learnsetCache[generation] = dict;
                }
            }

            return _learnsetCache[generation].TryGetValue(pokemonId, out var list) ? list : new List<int>();
        }
    }
}
