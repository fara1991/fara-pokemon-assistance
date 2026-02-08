using FaraPokemonTools.Models;

namespace FaraPokemonTools.Services
{
    public interface IPokemonDataService
    {
        Task<List<Pokemon>> GetPokemonAsync(int generation);
        Task<Pokemon?> GetPokemonByIdAsync(int id, int generation);
    }
}
