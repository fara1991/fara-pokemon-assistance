namespace FaraPokemonBattleApi.Models
{
    public class DamageCalculationRequest
    {
        public int AttackerPokemonId { get; set; }
        public int DefenderPokemonId { get; set; }
        public int MoveId { get; set; }
        public int Generation { get; set; } = 9;
        public PokemonInstance Attacker { get; set; } = new();
        public PokemonInstance Defender { get; set; } = new();
    }

    public class DamageCalculationResult
    {
        public int MinDamage { get; set; }
        public int MaxDamage { get; set; }
        public double TypeEffectiveness { get; set; }
        public bool STAB { get; set; }
        public int AttackerStats { get; set; }
        public int DefenderStats { get; set; }
        public List<string> Modifiers { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
    }
}
