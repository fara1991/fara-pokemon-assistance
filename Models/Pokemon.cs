namespace FaraPokemonBattleApi.Models
{
    public class Pokemon
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type1 { get; set; } = string.Empty;
        public string Type2 { get; set; } = string.Empty;
        public BaseStats BaseStats { get; set; } = new();
    }

    public class BaseStats
    {
        public int HP { get; set; }
        public int Attack { get; set; }
        public int Defense { get; set; }
        public int SpAttack { get; set; }
        public int SpDefense { get; set; }
        public int Speed { get; set; }
    }

    public class PokemonInstance
    {
        public int Level { get; set; } = 50;
        public BaseStats IVs { get; set; } = new() { HP = 31, Attack = 31, Defense = 31, SpAttack = 31, SpDefense = 31, Speed = 31 };
        public BaseStats EVs { get; set; } = new();
        public string Nature { get; set; } = "Hardy";
        public int? ItemId { get; set; }
    }
}
