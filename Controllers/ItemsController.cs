using Microsoft.AspNetCore.Mvc;
using FaraPokemonTools.Services;

namespace FaraPokemonTools.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ItemsController : ControllerBase
    {
        private readonly IItemDataService _itemDataService;

        public ItemsController(IItemDataService itemDataService)
        {
            _itemDataService = itemDataService;
        }

        [HttpGet]
        public async Task<IActionResult> GetItems([FromQuery] int generation = 9)
        {
            var items = await _itemDataService.GetItemsAsync(generation);
            return Ok(items);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetItemById(int id, [FromQuery] int generation = 9)
        {
            var item = await _itemDataService.GetItemByIdAsync(id, generation);
            if (item == null)
                return NotFound();

            return Ok(item);
        }
    }
}
