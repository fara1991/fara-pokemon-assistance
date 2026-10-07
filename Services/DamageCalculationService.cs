using FaraPokemonAssistance.Models;

namespace FaraPokemonAssistance.Services
{
    public class DamageCalculationService : IDamageCalculationService
    {
        private readonly IPokemonDataService _pokemonService;
        private readonly IMoveDataService _moveService;
        private readonly IItemDataService _itemService;
        private readonly ITypeEffectivenessService _typeEffectivenessService;
        private readonly IGenerationService _generationService;

        public DamageCalculationService(
            IPokemonDataService pokemonService,
            IMoveDataService moveService,
            IItemDataService itemService,
            ITypeEffectivenessService typeEffectivenessService,
            IGenerationService generationService)
        {
            _pokemonService = pokemonService;
            _moveService = moveService;
            _itemService = itemService;
            _typeEffectivenessService = typeEffectivenessService;
            _generationService = generationService;
        }

        public async Task<DamageCalculationResult> CalculateDamageAsync(DamageCalculationRequest request)
        {
            var attacker = await _pokemonService.GetPokemonByIdAsync(request.AttackerPokemonId, request.Generation);
            var defender = await _pokemonService.GetPokemonByIdAsync(request.DefenderPokemonId, request.Generation);
            var move = await _moveService.GetMoveByIdAsync(request.MoveId, request.Generation);

            if (attacker == null || defender == null || move == null)
                throw new ArgumentException("Invalid Pokemon or Move ID");

            var attackerItem = request.Attacker.ItemId.HasValue
                ? await _itemService.GetItemByIdAsync(request.Attacker.ItemId.Value, request.Generation)
                : null;

            var defenderItem = request.Defender.ItemId.HasValue
                ? await _itemService.GetItemByIdAsync(request.Defender.ItemId.Value, request.Generation)
                : null;

            var natures = await _generationService.GetNaturesAsync();
            var attackerNature = natures.FirstOrDefault(n => n.Name == request.Attacker.Nature);
            var defenderNature = natures.FirstOrDefault(n => n.Name == request.Defender.Nature);

            // Calculate actual stats
            var attackerStats = CalculateStats(attacker, request.Attacker, attackerNature);
            var defenderStats = CalculateStats(defender, request.Defender, defenderNature);

            // Determine attacking and defending stats
            var attackStat = move.Category == "Physical" ? attackerStats.Attack : attackerStats.SpAttack;
            var defenseStat = move.Category == "Physical" ? defenderStats.Defense : defenderStats.SpDefense;

            // Apply item modifiers
            if (attackerItem != null)
            {
                if (move.Category == "Physical")
                    attackStat = (int)(attackStat * attackerItem.AttackMultiplier);
                else
                    attackStat = (int)(attackStat * attackerItem.SpAttackMultiplier);
            }

            if (defenderItem != null)
            {
                if (move.Category == "Physical")
                    defenseStat = (int)(defenseStat * defenderItem.DefenseMultiplier);
                else
                    defenseStat = (int)(defenseStat * defenderItem.SpDefenseMultiplier);
            }

            // Type effectiveness
            var typeEffectiveness = await _typeEffectivenessService.GetEffectivenessAsync(
                move.Type, defender.Type1, defender.Type2, request.Generation);

            // STAB (Same Type Attack Bonus)
            var isSTAB = move.Type == attacker.Type1 || move.Type == attacker.Type2;
            var stabMultiplier = isSTAB ? 1.5 : 1.0;

            // Base damage calculation (Gen 9 formula)
            var baseDamage = ((((request.Attacker.Level * 2.0 / 5.0 + 2.0) * move.Power * attackStat / defenseStat) / 50.0) + 2.0);

            // Apply multipliers
            baseDamage *= stabMultiplier;
            baseDamage *= typeEffectiveness;

            // Item damage multipliers
            if (attackerItem != null)
            {
                baseDamage *= attackerItem.DamageMultiplier;
                if (!string.IsNullOrEmpty(attackerItem.TypeBoost) && attackerItem.TypeBoost == move.Type)
                    baseDamage *= attackerItem.TypeBoostMultiplier;
            }

            // Random factor (85-100%)
            var minDamage = (int)(baseDamage * 0.85);
            var maxDamage = (int)baseDamage;

            var modifiers = new List<string>();
            if (isSTAB) modifiers.Add("STAB (1.5x)");
            if (typeEffectiveness > 1) modifiers.Add($"Super Effective ({typeEffectiveness}x)");
            if (typeEffectiveness < 1) modifiers.Add($"Not Very Effective ({typeEffectiveness}x)");
            if (attackerItem != null) modifiers.Add($"Item: {attackerItem.Name}");

            return new DamageCalculationResult
            {
                MinDamage = Math.Max(1, minDamage),
                MaxDamage = Math.Max(1, maxDamage),
                TypeEffectiveness = typeEffectiveness,
                STAB = isSTAB,
                AttackerStats = attackStat,
                DefenderStats = defenseStat,
                Modifiers = modifiers,
                Summary = $"{attacker.Name} uses {move.Name} against {defender.Name}: {Math.Max(1, minDamage)}-{Math.Max(1, maxDamage)} damage"
            };
        }

        private BaseStats CalculateStats(Pokemon pokemon, PokemonInstance instance, Nature? nature)
        {
            var stats = new BaseStats();

            // HP calculation
            stats.HP = (int)((2 * pokemon.BaseStats.HP + instance.IVs.HP + instance.EVs.HP / 4.0) * instance.Level / 100.0) + instance.Level + 10;

            // Other stats
            var baseAttack = (int)(((2 * pokemon.BaseStats.Attack + instance.IVs.Attack + instance.EVs.Attack / 4.0) * instance.Level / 100.0) + 5);
            var baseDefense = (int)(((2 * pokemon.BaseStats.Defense + instance.IVs.Defense + instance.EVs.Defense / 4.0) * instance.Level / 100.0) + 5);
            var baseSpAttack = (int)(((2 * pokemon.BaseStats.SpAttack + instance.IVs.SpAttack + instance.EVs.SpAttack / 4.0) * instance.Level / 100.0) + 5);
            var baseSpDefense = (int)(((2 * pokemon.BaseStats.SpDefense + instance.IVs.SpDefense + instance.EVs.SpDefense / 4.0) * instance.Level / 100.0) + 5);
            var baseSpeed = (int)(((2 * pokemon.BaseStats.Speed + instance.IVs.Speed + instance.EVs.Speed / 4.0) * instance.Level / 100.0) + 5);

            // Apply nature modifiers
            if (nature != null)
            {
                if (nature.IncreasedStat == "Attack") baseAttack = (int)(baseAttack * nature.Multiplier);
                if (nature.DecreasedStat == "Attack") baseAttack = (int)(baseAttack / nature.Multiplier);
                if (nature.IncreasedStat == "Defense") baseDefense = (int)(baseDefense * nature.Multiplier);
                if (nature.DecreasedStat == "Defense") baseDefense = (int)(baseDefense / nature.Multiplier);
                if (nature.IncreasedStat == "SpAttack") baseSpAttack = (int)(baseSpAttack * nature.Multiplier);
                if (nature.DecreasedStat == "SpAttack") baseSpAttack = (int)(baseSpAttack / nature.Multiplier);
                if (nature.IncreasedStat == "SpDefense") baseSpDefense = (int)(baseSpDefense * nature.Multiplier);
                if (nature.DecreasedStat == "SpDefense") baseSpDefense = (int)(baseSpDefense / nature.Multiplier);
                if (nature.IncreasedStat == "Speed") baseSpeed = (int)(baseSpeed * nature.Multiplier);
                if (nature.DecreasedStat == "Speed") baseSpeed = (int)(baseSpeed / nature.Multiplier);
            }

            stats.Attack = baseAttack;
            stats.Defense = baseDefense;
            stats.SpAttack = baseSpAttack;
            stats.SpDefense = baseSpDefense;
            stats.Speed = baseSpeed;

            return stats;
        }
    }
}
