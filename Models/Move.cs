namespace FaraPokemonTools.Models
{
    public class Move
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int Power { get; set; }
        public int Accuracy { get; set; }
        public int PP { get; set; }
        public string Category { get; set; } = string.Empty; // Physical, Special, Status
        public string Description { get; set; } = string.Empty;
    }
}
