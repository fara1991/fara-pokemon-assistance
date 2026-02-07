using FaraPokemonBattleApi.Models;

namespace FaraPokemonBattleApi.Services
{
    public interface IPokemonDataService
    {
        Task<List<Pokemon>> GetPokemonAsync(int generation);
        Task<Pokemon?> GetPokemonByIdAsync(int id, int generation);
    }
}
