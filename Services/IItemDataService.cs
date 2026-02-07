using FaraPokemonBattleApi.Models;

namespace FaraPokemonBattleApi.Services
{
    public interface IItemDataService
    {
        Task<List<Item>> GetItemsAsync(int generation);
        Task<Item?> GetItemByIdAsync(int id, int generation);
    }
}
