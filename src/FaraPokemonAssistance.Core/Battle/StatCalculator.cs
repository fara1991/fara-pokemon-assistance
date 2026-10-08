using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>実数値計算（第3世代以降の式）。</summary>
public static class StatCalculator
{
    public static int Hp(int baseStat, int iv, int ev, int level)
    {
        if (baseStat == 1) return 1; // ヌケニン
        return (2 * baseStat + iv + ev / 4) * level / 100 + level + 10;
    }

    public static int Other(int baseStat, int iv, int ev, int level, double natureModifier)
    {
        var raw = (2 * baseStat + iv + ev / 4) * level / 100 + 5;
        return (int)Math.Floor(raw * natureModifier + 1e-9);
    }

    public static int Calculate(PokemonBuild build, Stat stat)
    {
        var b = build.Pokemon.BaseStats[stat];
        var iv = build.IVs[stat];
        var ev = build.EVs[stat];
        var nature = build.Nature?.Modifier(stat) ?? 1.0;
        if (build.EvSystem == EvSystem.Points)
        {
            // チャンピオンズ: 努力値 0 の実数値に、振ったポイント数をそのまま足す（性格補正は足す前に掛かる）
            if (stat == Stat.HP)
                return Hp(b, iv, 0, build.Level) + ev;
            return Other(b, iv, 0, build.Level, nature) + ev;
        }
        if (stat == Stat.HP)
            return Hp(b, iv, ev, build.Level);
        return Other(b, iv, ev, build.Level, nature);
    }

    /// <summary>任意の努力値での実数値（推定に使う）。</summary>
    public static int Calculate(PokemonBuild build, Stat stat, int ev, double natureModifier)
    {
        var b = build.Pokemon.BaseStats[stat];
        var iv = build.IVs[stat];
        if (build.EvSystem == EvSystem.Points)
            return (stat == Stat.HP ? Hp(b, iv, 0, build.Level) : Other(b, iv, 0, build.Level, natureModifier)) + ev;
        return stat == Stat.HP ? Hp(b, iv, ev, build.Level) : Other(b, iv, ev, build.Level, natureModifier);
    }

    public static StatSet CalculateAll(PokemonBuild build)
    {
        var result = new StatSet();
        foreach (var stat in Enum.GetValues<Stat>())
            result[stat] = Calculate(build, stat);
        return result;
    }

    /// <summary>ランク補正を掛ける（-6〜+6）。</summary>
    public static int ApplyBoost(int stat, int stage)
    {
        stage = Math.Clamp(stage, -6, 6);
        return stage >= 0
            ? stat * (2 + stage) / 2
            : stat * 2 / (2 - stage);
    }
}
