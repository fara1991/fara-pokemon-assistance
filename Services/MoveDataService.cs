using CsvHelper;
using FaraPokemonBattleApi.Models;
using System.Globalization;

namespace FaraPokemonBattleApi.Services
{
    public class MoveDataService : IMoveDataService
    {
        private readonly Dictionary<int, List<Move>> _moveCache = new();

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
    }
}
