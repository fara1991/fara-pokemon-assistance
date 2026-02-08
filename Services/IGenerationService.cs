using FaraPokemonTools.Models;

namespace FaraPokemonTools.Services
{
    public interface IGenerationService
    {
        Task<List<Nature>> GetNaturesAsync();
        List<int> GetAvailableGenerations();
    }
}
