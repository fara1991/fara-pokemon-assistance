using CsvHelper;
using FaraPokemonBattleApi.Models;
using System.Globalization;

namespace FaraPokemonBattleApi.Services
{
    public class ItemDataService : IItemDataService
    {
        private readonly Dictionary<int, List<Item>> _itemCache = new();

        public async Task<List<Item>> GetItemsAsync(int generation)
        {
            if (_itemCache.ContainsKey(generation))
                return _itemCache[generation];

            var filePath = Path.Combine("Data", $"Gen{generation}", "items.csv");
            if (!File.Exists(filePath))
                return new List<Item>();

            using var reader = new StringReader(await File.ReadAllTextAsync(filePath));
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            
            var records = csv.GetRecords<dynamic>().ToList();
            var items = records.Select(r => new Item
            {
                Id = int.Parse(r.Id),
                Name = r.Name,
                Category = r.Category ?? "",
                Effect = r.Effect ?? "",
                AttackMultiplier = double.Parse(r.AttackMultiplier ?? "1.0"),
                DefenseMultiplier = double.Parse(r.DefenseMultiplier ?? "1.0"),
                SpAttackMultiplier = double.Parse(r.SpAttackMultiplier ?? "1.0"),
                SpDefenseMultiplier = double.Parse(r.SpDefenseMultiplier ?? "1.0"),
                DamageMultiplier = double.Parse(r.DamageMultiplier ?? "1.0"),
                TypeBoost = r.TypeBoost ?? "",
                TypeBoostMultiplier = double.Parse(r.TypeBoostMultiplier ?? "1.0")
            }).ToList();

            _itemCache[generation] = items;
            return items;
        }

        public async Task<Item?> GetItemByIdAsync(int id, int generation)
        {
            var items = await GetItemsAsync(generation);
            return items.FirstOrDefault(i => i.Id == id);
        }
    }
}
