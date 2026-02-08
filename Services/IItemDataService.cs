using FaraPokemonTools.Models;

namespace FaraPokemonTools.Services
{
    public interface IItemDataService
    {
        Task<List<Item>> GetItemsAsync(int generation);
        Task<Item?> GetItemByIdAsync(int id, int generation);
    }
}
