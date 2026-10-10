using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;

namespace FaraPokemonAssistance.Core.Battle;

/// <summary>受けたダメージから、相手の防御側の努力値（と性格補正）を逆算する。</summary>
public sealed class EvEstimator
{
    private readonly TypeChart _typeChart;

    public EvEstimator(TypeChart typeChart)
    {
        _typeChart = typeChart;
    }

    public sealed record NatureBand(double Modifier, string Label, int MinEv, int MaxEv);

    public sealed class Estimate
    {
        public required Stat DefenseStat { get; init; }
        public required int ObservedDamage { get; init; }
        public required bool ObservedAsPercent { get; init; }
        /// <summary>観測ダメージを出しうる防御実数値の範囲。</summary>
        public required int MinDefenseStat { get; init; }
        public required int MaxDefenseStat { get; init; }
        /// <summary>性格補正ごとの努力値範囲（該当なしの補正は含まない）。</summary>
        public required IReadOnlyList<NatureBand> Bands { get; init; }
        /// <summary>% 指定時に推定した HP 努力値の範囲（実数値指定なら null）。</summary>
        public (int MinEv, int MaxEv)? HpEvRange { get; init; }
        public bool HasResult => Bands.Count > 0;
    }

    /// <summary>
    /// <paramref name="observed"/> は実ダメージ、<paramref name="asPercent"/> が true なら最大 HP に対する %。
    /// 相手（defender）の努力値と性格は無視して総当たりし、観測値が乱数範囲に入る組み合わせを集める。
    /// </summary>
    public Estimate Run(DamageRequest request, double observed, bool asPercent)
    {
        var template = request.Defender;
        var system = template.EvSystem;
        var defStat = request.Move.DefenseStatUsed;
        var step = EvRules.Step(system);
        var max = EvRules.MaxPerStat(system);
        var evValues = Enumerable.Range(0, max / step + 1).Select(i => Math.Min(max, i * step)).Distinct().ToList();
        if (system == EvSystem.Classic && !evValues.Contains(4)) evValues.Insert(1, 4);

        var natures = new (double Mod, string Label)[] { (1.1, "上昇補正"), (1.0, "補正なし"), (0.9, "下降補正") };
        var calc = new DamageCalculator(_typeChart);
        var matchedStats = new HashSet<int>();
        var bands = new List<NatureBand>();
        int? hpMin = null, hpMax = null;

        foreach (var (mod, label) in natures)
        {
            int? lo = null, hi = null;
            foreach (var ev in evValues)
            {
                var defender = template.Clone();
                defender.EVs = template.EVs.Clone();
                defender.EVs[defStat] = ev;
                defender.Nature = SyntheticNature(defStat, mod, template.Nature);

                // HP 振りは % 指定のときだけ総当たり（実数ダメージなら無関係）
                var hpCandidates = asPercent ? evValues : new List<int> { template.EVs.HP };
                var matched = false;
                foreach (var hpEv in hpCandidates)
                {
                    defender.EVs[Stat.HP] = hpEv;
                    var result = calc.Calculate(request.With(defender: defender));
                    if (result.MaxDamage == 0) continue;
                    var min = asPercent ? result.MinDamage * 100.0 / result.DefenderHP : result.MinDamage;
                    var maxv = asPercent ? result.MaxDamage * 100.0 / result.DefenderHP : result.MaxDamage;
                    var tolerance = asPercent ? 0.6 : 0;
                    if (observed >= min - tolerance && observed <= maxv + tolerance)
                    {
                        matched = true;
                        matchedStats.Add(result.DefenseStat);
                        if (asPercent)
                        {
                            hpMin = hpMin is null ? hpEv : Math.Min(hpMin.Value, hpEv);
                            hpMax = hpMax is null ? hpEv : Math.Max(hpMax.Value, hpEv);
                        }
                    }
                }
                if (matched)
                {
                    lo ??= ev;
                    hi = ev;
                }
            }
            if (lo is not null)
                bands.Add(new NatureBand(mod, label, lo.Value, hi!.Value));
        }

        return new Estimate
        {
            DefenseStat = defStat,
            ObservedDamage = (int)Math.Round(observed),
            ObservedAsPercent = asPercent,
            MinDefenseStat = matchedStats.Count > 0 ? matchedStats.Min() : 0,
            MaxDefenseStat = matchedStats.Count > 0 ? matchedStats.Max() : 0,
            Bands = bands,
            HpEvRange = asPercent && hpMin is not null ? (hpMin.Value, hpMax!.Value) : null,
        };
    }

    /// <summary>防御側の性格を「対象能力の補正だけ」持つ仮の性格に置き換える。</summary>
    private static Nature SyntheticNature(Stat stat, double modifier, Nature? original)
    {
        var other = stat == Stat.Defense ? Stat.SpDefense : Stat.Defense;
        return modifier switch
        {
            > 1.0 => new Nature { Id = -1, Name = "上昇", IncreasedStat = stat, DecreasedStat = Stat.Speed },
            < 1.0 => new Nature { Id = -2, Name = "下降", IncreasedStat = other, DecreasedStat = stat },
            _ => new Nature { Id = -3, Name = "無補正" },
        };
    }

    public static string Format(Estimate e, Pokemon defender, Move move, EvSystem system)
    {
        var unit = system == EvSystem.Points ? "pt" : "";
        var statLetter = StatNames.Letter(e.DefenseStat);
        var observed = e.ObservedAsPercent ? $"{e.ObservedDamage}%" : $"{e.ObservedDamage}";
        if (!e.HasResult)
            return $"{defender.Name}が{move.Name}で{observed}ダメージになる{StatNames.Japanese(e.DefenseStat)}の振り方は見つかりませんでした（持ち物・特性・ランクが違う可能性）";

        var bandText = string.Join(" / ", e.Bands.Select(b =>
            b.MinEv == b.MaxEv ? $"{b.Label}:{statLetter}{b.MinEv}{unit}" : $"{b.Label}:{statLetter}{b.MinEv}〜{b.MaxEv}{unit}"));
        var hpText = e.HpEvRange is { } hp ? $" H{hp.MinEv}〜{hp.MaxEv}{unit}" : "";
        var statText = e.MinDefenseStat == e.MaxDefenseStat ? $"{e.MinDefenseStat}" : $"{e.MinDefenseStat}〜{e.MaxDefenseStat}";
        return $"{defender.Name}が{move.Name}で{observed}ダメージ → {StatNames.Japanese(e.DefenseStat)}実数値 {statText}: {bandText}{hpText}";
    }
}
