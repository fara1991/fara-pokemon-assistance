using FaraPokemonTools.Models;

namespace FaraPokemonTools.Services
{
    public interface IDamageCalculationService
    {
        Task<DamageCalculationResult> CalculateDamageAsync(DamageCalculationRequest request);
    }
}
