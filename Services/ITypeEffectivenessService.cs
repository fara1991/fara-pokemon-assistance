namespace FaraPokemonTools.Services
{
    public interface ITypeEffectivenessService
    {
        Task<double> GetEffectivenessAsync(string attackType, string defenseType1, string defenseType2, int generation);
    }
}
