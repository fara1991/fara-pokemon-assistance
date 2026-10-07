using Microsoft.AspNetCore.Mvc;
using FaraPokemonAssistance.Services;

namespace FaraPokemonAssistance.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PokemonController : ControllerBase
    {
        private readonly IPokemonDataService _pokemonDataService;

        public PokemonController(IPokemonDataService pokemonDataService)
        {
            _pokemonDataService = pokemonDataService;
        }

        [HttpGet]
        public async Task<IActionResult> GetPokemon([FromQuery] int generation = 9)
        {
            var pokemon = await _pokemonDataService.GetPokemonAsync(generation);
            return Ok(pokemon);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetPokemonById(int id, [FromQuery] int generation = 9)
        {
            var pokemon = await _pokemonDataService.GetPokemonByIdAsync(id, generation);
            if (pokemon == null)
                return NotFound();

            return Ok(pokemon);
        }
    }
}
