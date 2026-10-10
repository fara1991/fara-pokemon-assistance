using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>特性が場に出したときに作る天候・フィールド。計算機の既定値に使う。</summary>
public static class FieldEffects
{
    public static Weather? WeatherFromAbility(string? identifier) => identifier switch
    {
        "drought" or "desolate-land" or "orichalcum-pulse" => Weather.Sun,
        "drizzle" or "primordial-sea" => Weather.Rain,
        "sand-stream" or "sand-spit" => Weather.Sand,
        "snow-warning" => Weather.Snow,
        _ => null,
    };

    public static Terrain? TerrainFromAbility(string? identifier) => identifier switch
    {
        "electric-surge" or "hadron-engine" => Terrain.Electric,
        "grassy-surge" or "seed-sower" => Terrain.Grassy,
        "psychic-surge" => Terrain.Psychic,
        "misty-surge" => Terrain.Misty,
        _ => null,
    };
}
