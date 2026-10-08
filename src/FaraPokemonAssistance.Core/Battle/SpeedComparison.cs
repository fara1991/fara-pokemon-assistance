using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>素早さの実数値と比較。</summary>
public static class SpeedCalculator
{
    private const int ItemIdChoiceScarf = 264;
    private const int ItemIdIronBall = 278;

    public sealed record SpeedLine(string Label, int Value, string? Note);

    /// <summary>ランク・持ち物・一部の特性を含めた素早さ。</summary>
    public static (int Speed, List<string> Notes) Effective(PokemonBuild build, Weather weather = Weather.None, Terrain terrain = Terrain.None)
    {
        var notes = new List<string>();
        var speed = StatCalculator.Calculate(build, Stat.Speed);
        var boost = build.Boosts[Stat.Speed];
        if (boost != 0)
        {
            speed = StatCalculator.ApplyBoost(speed, boost);
            notes.Add($"S{(boost > 0 ? "+" : "")}{boost}");
        }
        var ability = build.Ability?.Identifier ?? "";
        var abilityMod = ability switch
        {
            "swift-swim" when weather == Weather.Rain => 2.0,
            "chlorophyll" when weather == Weather.Sun => 2.0,
            "sand-rush" when weather == Weather.Sand => 2.0,
            "slush-rush" when weather == Weather.Snow => 2.0,
            "surge-surfer" when terrain == Terrain.Electric => 2.0,
            "unburden" => 1.0, // 発動条件が不明なので適用しない
            _ => 1.0,
        };
        if (abilityMod != 1.0)
        {
            speed = DamageCalculator.PokeRound(speed * abilityMod);
            notes.Add($"{build.Ability!.Name}(2倍)");
        }
        if (build.Status == StatusCondition.Paralysis)
        {
            if (ability == "quick-feet")
            {
                speed = DamageCalculator.PokeRound(speed * 1.5);
                notes.Add($"まひ+{build.Ability!.Name}(1.5倍)");
            }
            else
            {
                speed = DamageCalculator.PokeRound(speed * 0.5);
                notes.Add("まひ(0.5倍)");
            }
        }
        if (build.Item?.Id == ItemIdChoiceScarf)
        {
            speed = DamageCalculator.PokeRound(speed * 1.5);
            notes.Add("こだわりスカーフ(1.5倍)");
        }
        else if (build.Item?.Id == ItemIdIronBall)
        {
            speed = DamageCalculator.PokeRound(speed * 0.5);
            notes.Add("くろいてっきゅう(0.5倍)");
        }
        return (speed, notes);
    }

    /// <summary>相手の努力値・性格が不明なときの代表値（無振り / 準速 / 最速 / スカーフ最速）。</summary>
    public static IReadOnlyList<SpeedLine> OpponentReference(Pokemon pokemon, EvSystem system, Item? scarf, bool paralyzed = false)
    {
        var full = EvRules.Full(system);
        int Calc(int ev, double mod)
        {
            var v = StatCalculator.Calculate(new PokemonBuild(pokemon, system), Stat.Speed, ev, mod);
            return paralyzed ? DamageCalculator.PokeRound(v * 0.5) : v;
        }

        var lines = new List<SpeedLine>
        {
            new("無振り", Calc(0, 1.0), null),
            new("準速", Calc(full, 1.0), null),
            new("最速", Calc(full, 1.1), null),
        };
        if (scarf is not null)
            lines.Add(new("スカーフ最速", DamageCalculator.PokeRound(Calc(full, 1.1) * 1.5), scarf.Name));
        lines.Add(new("最遅", Calc(0, 0.9), "個体値0"));
        if (paralyzed) lines.Add(new("状態", 0, "まひ"));
        return lines;
    }

    public static string Format(PokemonBuild mine, int mySpeed, IReadOnlyList<string> myNotes, Pokemon opponent, PokemonBuild? opponentBuild, int? opponentSpeed, IReadOnlyList<string>? opponentNotes, IReadOnlyList<SpeedLine> reference)
    {
        var myNote = myNotes.Count > 0 ? $"({string.Join(",", myNotes)})" : "";
        var sb = new System.Text.StringBuilder();
        sb.Append($"{mine.Pokemon.Name} S{mySpeed}{myNote} vs {opponent.Name}{(reference.Any(r => r.Note == "まひ") ? "(まひ)" : "")} ");
        if (opponentBuild is not null && opponentSpeed is { } os)
        {
            var oNote = opponentNotes is { Count: > 0 } ? $"({string.Join(",", opponentNotes)})" : "";
            sb.Append($"S{os}{oNote}: ");
            sb.Append(mySpeed > os ? "自分が先手" : mySpeed < os ? "相手が先手" : "同速");
            return sb.ToString();
        }

        var shown = reference.Where(r => r.Label != "最遅" && r.Label != "状態").ToList();
        sb.Append(string.Join(" / ", shown.Select(r => $"{r.Label}{r.Value}")));
        sb.Append(" → ");
        var faster = shown.Where(r => mySpeed > r.Value).Select(r => r.Label).ToList();
        var tie = shown.Where(r => mySpeed == r.Value).Select(r => r.Label).ToList();
        var slower = shown.Where(r => mySpeed < r.Value).Select(r => r.Label).ToList();
        if (faster.Count > 0) sb.Append($"{faster[^1]}まで抜ける");
        else sb.Append("無振りにも抜かれる");
        if (tie.Count > 0) sb.Append($"、{string.Join("・", tie)}と同速");
        if (slower.Count > 0) sb.Append($"、{slower[0]}には負ける");
        return sb.ToString();
    }
}
