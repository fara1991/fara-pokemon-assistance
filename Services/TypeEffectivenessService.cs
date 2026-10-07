using CsvHelper;
using System.Globalization;

namespace FaraPokemonAssistance.Services
{
    public class TypeEffectivenessService : ITypeEffectivenessService
    {
        private readonly Dictionary<int, Dictionary<string, Dictionary<string, double>>> _effectivenessCache = new();

        public async Task<double> GetEffectivenessAsync(string attackType, string defenseType1, string defenseType2, int generation)
        {
            var effectiveness = await GetTypeEffectivenessAsync(generation);
            
            var multiplier1 = effectiveness.ContainsKey(attackType) && effectiveness[attackType].ContainsKey(defenseType1)
                ? effectiveness[attackType][defenseType1] : 1.0;
                
            var multiplier2 = 1.0;
            if (!string.IsNullOrEmpty(defenseType2) && effectiveness.ContainsKey(attackType) && effectiveness[attackType].ContainsKey(defenseType2))
            {
                multiplier2 = effectiveness[attackType][defenseType2];
            }

            return multiplier1 * multiplier2;
        }

        private async Task<Dictionary<string, Dictionary<string, double>>> GetTypeEffectivenessAsync(int generation)
        {
            if (_effectivenessCache.ContainsKey(generation))
                return _effectivenessCache[generation];

            var filePath = Path.Combine("Data", $"Gen{generation}", "type_effectiveness.csv");
            if (!File.Exists(filePath))
                return new Dictionary<string, Dictionary<string, double>>();

            using var reader = new StringReader(await File.ReadAllTextAsync(filePath));
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            
            var records = csv.GetRecords<dynamic>().ToList();
            var effectiveness = new Dictionary<string, Dictionary<string, double>>();

            foreach (var record in records)
            {
                var attackType = (string)record.AttackType;
                var defenseType = (string)record.DefenseType;
                var multiplier = double.Parse((string)record.Multiplier);

                if (!effectiveness.ContainsKey(attackType))
                    effectiveness[attackType] = new Dictionary<string, double>();

                effectiveness[attackType][defenseType] = multiplier;
            }

            _effectivenessCache[generation] = effectiveness;
            return effectiveness;
        }
    }
}
