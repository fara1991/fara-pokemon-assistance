using CsvHelper;
using FaraPokemonAssistance.Models;
using System.Globalization;

namespace FaraPokemonAssistance.Services
{
    public class GenerationService : IGenerationService
    {
        private List<Nature>? _naturesCache;

        public async Task<List<Nature>> GetNaturesAsync()
        {
            if (_naturesCache != null)
                return _naturesCache;

            var filePath = Path.Combine("Data", "natures.csv");
            if (!File.Exists(filePath))
                return new List<Nature>();

            using var reader = new StringReader(await File.ReadAllTextAsync(filePath));
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            
            var records = csv.GetRecords<dynamic>().ToList();
            var natures = records.Select(r => new Nature
            {
                Name = r.Name,
                IncreasedStat = r.IncreasedStat ?? "",
                DecreasedStat = r.DecreasedStat ?? "",
                Multiplier = double.Parse(r.Multiplier ?? "1.1")
            }).ToList();

            _naturesCache = natures;
            return natures;
        }

        public List<int> GetAvailableGenerations()
        {
            var dataPath = Path.Combine("Data");
            if (!Directory.Exists(dataPath))
                return new List<int>();

            var generations = new List<int>();
            for (int i = 1; i <= 9; i++)
            {
                var genPath = Path.Combine(dataPath, $"Gen{i}");
                if (Directory.Exists(genPath))
                    generations.Add(i);
            }

            return generations;
        }
    }
}
