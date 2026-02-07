using FaraPokemonBattleApi.Models;

namespace FaraPokemonBattleApi.Services
{
    public interface IMoveDataService
    {
        Task<List<Move>> GetMovesAsync(int generation);
        Task<Move?> GetMoveByIdAsync(int id, int generation);
    }
}
