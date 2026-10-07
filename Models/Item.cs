namespace FaraPokemonAssistance.Models
{
    public class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Effect { get; set; } = string.Empty;
        public double AttackMultiplier { get; set; } = 1.0;
        public double DefenseMultiplier { get; set; } = 1.0;
        public double SpAttackMultiplier { get; set; } = 1.0;
        public double SpDefenseMultiplier { get; set; } = 1.0;
        public double DamageMultiplier { get; set; } = 1.0;
        public string TypeBoost { get; set; } = string.Empty;
        public double TypeBoostMultiplier { get; set; } = 1.0;
    }
}
