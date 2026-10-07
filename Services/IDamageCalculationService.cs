using FaraPokemonAssistance.Models;

namespace FaraPokemonAssistance.Services
{
    public interface IDamageCalculationService
    {
        Task<DamageCalculationResult> CalculateDamageAsync(DamageCalculationRequest request);
    }
}
