using FaraPokemonAssistance.Models;

namespace FaraPokemonAssistance.Services
{
    public interface IMoveDataService
    {
        Task<List<Move>> GetMovesAsync(int generation);
        Task<Move?> GetMoveByIdAsync(int id, int generation);
        Task<List<int>> GetLearnsetAsync(int pokemonId, int generation);
    }
}
