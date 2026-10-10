using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>素早さの実数値と比較。</summary>
public static class SpeedCalculator
{
    private const int ItemIdChoiceScarf = 264;
    private const int ItemIdIronBall = 255;
    /// <summary>パワーリスト・パワーレンズ・パワーバンド・パワーアンクル・パワーウエイト（パワーベルトは 267）。素早さ 0.5 倍。</summary>
    private static readonly HashSet<int> PowerItemIds = new() { 266, 267, 268, 269, 270, 271 };

    public sealed record SpeedLine(string Label, int Value, string? Note);

    /// <summary>素早さに掛かる補正 1 つ（特性・まひ・おいかぜなど）。</summary>
    public sealed record SpeedFactor(string Label, double Multiplier);

    private const int ItemIdBoosterEnergy = 1696;

    /// <summary>ランク・持ち物・特性・まひ・おいかぜを含めた素早さ。</summary>
    public static (int Speed, List<string> Notes) Effective(PokemonBuild build, Weather weather = Weather.None, Terrain terrain = Terrain.None, bool tailwind = false)
    {
        var notes = new List<string>();
        var speed = StatCalculator.Calculate(build, Stat.Speed);
        var boost = build.Boosts[Stat.Speed];
        if (boost != 0)
        {
            speed = StatCalculator.ApplyBoost(speed, boost);
            notes.Add($"S{(boost > 0 ? "+" : "")}{boost}");
        }
        foreach (var factor in ConditionFactors(build, weather, terrain, tailwind))
        {
            speed = DamageCalculator.PokeRound(speed * factor.Multiplier);
            notes.Add(factor.Label);
        }
        if (ItemFactor(build) is { } item)
        {
            speed = DamageCalculator.PokeRound(speed * item.Multiplier);
            notes.Add(item.Label);
        }
        return (speed, notes);
    }

    /// <summary>
    /// 今の天候・フィールド・状態で掛かる補正（特性・ブーストエナジー・まひ・おいかぜ）。持ち物（スカーフ等）とランクは含めない。
    /// </summary>
    public static IReadOnlyList<SpeedFactor> ConditionFactors(PokemonBuild build, Weather weather = Weather.None, Terrain terrain = Terrain.None, bool tailwind = false)
    {
        var list = new List<SpeedFactor>();
        var ability = build.Ability?.Identifier ?? "";
        var name = build.Ability?.Name ?? "";
        var doubled = ability switch
        {
            "swift-swim" => weather == Weather.Rain,
            "chlorophyll" => weather == Weather.Sun,
            "sand-rush" => weather == Weather.Sand,
            "slush-rush" => weather == Weather.Snow,
            "surge-surfer" => terrain == Terrain.Electric,
            _ => false, // かるわざは発動したか分からないので、ここでは掛けない（PotentialFactor で別に出す）
        };
        if (doubled) list.Add(new SpeedFactor($"{name}(2倍)", 2.0));
        if (ability is "protosynthesis" or "quark-drive" && ParadoxActive(build, ability, weather, terrain) &&
            DamageCalculator.HighestStat(build) == Stat.Speed)
            list.Add(new SpeedFactor($"{name}(1.5倍)", 1.5));

        if (build.Status != StatusCondition.None && ability == "quick-feet")
            list.Add(new SpeedFactor(build.Status == StatusCondition.Paralysis ? $"まひ+{name}(1.5倍)" : $"{name}(1.5倍)", 1.5));
        else if (build.Status == StatusCondition.Paralysis)
            list.Add(new SpeedFactor("まひ(0.5倍)", 0.5));

        if (tailwind) list.Add(new SpeedFactor("おいかぜ(2倍)", 2.0));
        return list;
    }

    /// <summary>
    /// 選んでいる特性が、条件がそろえば素早さを上げる（今はまだ上がっていない）場合、その条件と倍率。
    /// 例: ようりょくそで晴れでない → 「晴れ(ようりょくそ)」2 倍。かるわざは「持ち物消費後」。
    /// </summary>
    public static SpeedFactor? PotentialFactor(PokemonBuild build, Weather weather = Weather.None, Terrain terrain = Terrain.None)
    {
        var ability = build.Ability?.Identifier ?? "";
        var name = build.Ability?.Name ?? "";
        var active = ConditionFactors(build, weather, terrain);
        if (name.Length > 0 && active.Any(f => f.Label.Contains(name, StringComparison.Ordinal))) return null;
        return ability switch
        {
            "swift-swim" => new SpeedFactor($"雨のとき({name} 2倍)", 2.0),
            "chlorophyll" => new SpeedFactor($"晴れのとき({name} 2倍)", 2.0),
            "sand-rush" => new SpeedFactor($"砂嵐のとき({name} 2倍)", 2.0),
            "slush-rush" => new SpeedFactor($"雪のとき({name} 2倍)", 2.0),
            "surge-surfer" => new SpeedFactor($"エレキフィールドのとき({name} 2倍)", 2.0),
            "unburden" => new SpeedFactor($"持ち物がなくなったとき({name} 2倍)", 2.0),
            "quick-feet" => new SpeedFactor($"状態異常のとき({name} 1.5倍)", 1.5),
            "protosynthesis" when DamageCalculator.HighestStat(build) == Stat.Speed => new SpeedFactor($"晴れ・ブーストエナジー({name} 1.5倍)", 1.5),
            "quark-drive" when DamageCalculator.HighestStat(build) == Stat.Speed => new SpeedFactor($"エレキフィールド・ブーストエナジー({name} 1.5倍)", 1.5),
            _ => null,
        };
    }

    /// <summary>条件がそろうと素早さが上がる特性（すいすい・ようりょくそ・かるわざなど）。素早さ比較の既定の特性に使う。</summary>
    public static bool IsSpeedAbility(string? ability) => ability is
        "swift-swim" or "chlorophyll" or "sand-rush" or "slush-rush" or "surge-surfer" or "unburden" or "quick-feet";

    /// <summary>素早さに補正を順に掛ける。</summary>
    public static int Apply(int speed, IEnumerable<SpeedFactor> factors)
    {
        foreach (var f in factors) speed = DamageCalculator.PokeRound(speed * f.Multiplier);
        return speed;
    }

    private static SpeedFactor? ItemFactor(PokemonBuild build) => build.Item switch
    {
        null => null,
        { Id: ItemIdChoiceScarf } item => new SpeedFactor($"{item.Name}(1.5倍)", 1.5),
        { Id: ItemIdIronBall } item => new SpeedFactor($"{item.Name}(0.5倍)", 0.5),
        var item when PowerItemIds.Contains(item.Id) => new SpeedFactor($"{item.Name}(0.5倍)", 0.5),
        _ => null,
    };

    /// <summary>素早さが変わる持ち物（こだわりスカーフ・くろいてっきゅう・パワー系）か。素早さ比較の持ち物の候補に使う。</summary>
    public static bool IsSpeedItem(Item item) =>
        item.Id is ItemIdChoiceScarf or ItemIdIronBall || PowerItemIds.Contains(item.Id);

    private static bool ParadoxActive(PokemonBuild build, string ability, Weather weather, Terrain terrain) =>
        build.Item?.Id == ItemIdBoosterEnergy || (ability == "protosynthesis" ? weather == Weather.Sun : terrain == Terrain.Electric);

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
