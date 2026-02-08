namespace FaraPokemonTools.Models
{
    public class Nature
    {
        public string Name { get; set; } = string.Empty;
        public string IncreasedStat { get; set; } = string.Empty;
        public string DecreasedStat { get; set; } = string.Empty;
        public double Multiplier { get; set; } = 1.1;
    }
}
