using CsvHelper;
using FaraPokemonTools.Models;
using System.Globalization;

namespace FaraPokemonTools.Services
{
    public class PokemonDataService : IPokemonDataService
    {
        private readonly Dictionary<int, List<Pokemon>> _pokemonCache = new();

        public async Task<List<Pokemon>> GetPokemonAsync(int generation)
        {
            if (_pokemonCache.ContainsKey(generation))
                return _pokemonCache[generation];

            var filePath = Path.Combine("Data", $"Gen{generation}", "pokemon.csv");
            if (!File.Exists(filePath))
                return new List<Pokemon>();

            using var reader = new StringReader(await File.ReadAllTextAsync(filePath));
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            
            var records = csv.GetRecords<dynamic>().ToList();
            var pokemon = records.Select(r => new Pokemon
            {
                Id = int.Parse(r.Id),
                Name = r.Name,
                Type1 = r.Type1,
                Type2 = r.Type2 ?? "",
                BaseStats = new BaseStats
                {
                    HP = int.Parse(r.HP),
                    Attack = int.Parse(r.Attack),
                    Defense = int.Parse(r.Defense),
                    SpAttack = int.Parse(r.SpAttack),
                    SpDefense = int.Parse(r.SpDefense),
                    Speed = int.Parse(r.Speed)
                },
                Icon = ((IDictionary<string, object>)r).ContainsKey("Icon") ? (r.Icon ?? "") : ""
            }).ToList();

            _pokemonCache[generation] = pokemon;
            return pokemon;
        }

        public async Task<Pokemon?> GetPokemonByIdAsync(int id, int generation)
        {
            var pokemon = await GetPokemonAsync(generation);
            return pokemon.FirstOrDefault(p => p.Id == id);
        }
    }
}
