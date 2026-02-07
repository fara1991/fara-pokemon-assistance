using FaraPokemonBattleApi.Models;

namespace FaraPokemonBattleApi.Services
{
    public interface IDamageCalculationService
    {
        Task<DamageCalculationResult> CalculateDamageAsync(DamageCalculationRequest request);
    }
}
