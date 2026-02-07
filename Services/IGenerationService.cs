using FaraPokemonBattleApi.Models;

namespace FaraPokemonBattleApi.Services
{
    public interface IGenerationService
    {
        Task<List<Nature>> GetNaturesAsync();
        List<int> GetAvailableGenerations();
    }
}
