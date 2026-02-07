using Microsoft.AspNetCore.Mvc;
using FaraPokemonBattleApi.Models;
using FaraPokemonBattleApi.Services;

namespace FaraPokemonBattleApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DamageController : ControllerBase
    {
        private readonly IDamageCalculationService _damageCalculationService;

        public DamageController(IDamageCalculationService damageCalculationService)
        {
            _damageCalculationService = damageCalculationService;
        }

        [HttpPost("calculate")]
        public async Task<ActionResult<DamageCalculationResult>> CalculateDamage([FromBody] DamageCalculationRequest request)
        {
            try
            {
                var result = await _damageCalculationService.CalculateDamageAsync(request);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
    }
}
