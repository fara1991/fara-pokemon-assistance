using FaraPokemonAssistance.Models;

namespace FaraPokemonAssistance.Services
{
    public interface IGenerationService
    {
        Task<List<Nature>> GetNaturesAsync();
        List<int> GetAvailableGenerations();
    }
}
