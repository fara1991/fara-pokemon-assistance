using Microsoft.AspNetCore.Mvc;
using FaraPokemonAssistance.Services;

namespace FaraPokemonAssistance.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MovesController : ControllerBase
    {
        private readonly IMoveDataService _moveDataService;

        public MovesController(IMoveDataService moveDataService)
        {
            _moveDataService = moveDataService;
        }

        [HttpGet]
        public async Task<IActionResult> GetMoves([FromQuery] int generation = 9)
        {
            var moves = await _moveDataService.GetMovesAsync(generation);
            return Ok(moves);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetMoveById(int id, [FromQuery] int generation = 9)
        {
            var move = await _moveDataService.GetMoveByIdAsync(id, generation);
            if (move == null)
                return NotFound();

            return Ok(move);
        }
    }
}
