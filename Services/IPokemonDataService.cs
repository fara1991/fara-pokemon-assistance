using FaraPokemonAssistance.Models;

namespace FaraPokemonAssistance.Services
{
    public interface IPokemonDataService
    {
        Task<List<Pokemon>> GetPokemonAsync(int generation);
        Task<Pokemon?> GetPokemonByIdAsync(int id, int generation);
    }
}
