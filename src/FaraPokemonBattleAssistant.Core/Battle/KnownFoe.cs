using System.Collections.Concurrent;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Core.Battle;

/// <summary>
/// 推定などで分かった「相手の振り方」の情報（範囲でもよい）。この後のダメージ計算で、防御側にこの情報を使う。
/// 範囲のときは、耐久が最も低い想定（<see cref="Low"/>）と最も高い想定（<see cref="High"/>）の 2 つで計算する。
/// </summary>
public sealed class KnownFoe
{
    public string DataSetKey { get; init; } = "";
    public required Pokemon Pokemon { get; init; }
    public required Stat DefenseStat { get; init; }
    /// <summary>耐久が最も低い想定の構成（ダメージが最大になる側）。</summary>
    public required PokemonBuild Low { get; init; }
    /// <summary>耐久が最も高い想定の構成（ダメージが最小になる側）。</summary>
    public required PokemonBuild High { get; init; }
    /// <summary>画面・チャット用の説明（例: <c>補正なし:B0〜6pt / 上昇補正:B0pt H4〜8pt</c>）。</summary>
    public string Summary { get; init; } = "";

    /// <summary>振り方が 1 つに決まっていない（範囲）か。</summary>
    public bool IsRange => Low.EVs.ToShortString() != High.EVs.ToShortString() || Low.Nature?.Id != High.Nature?.Id;

    /// <summary>特性ごとに推定が分かれたとき、まとめて 1 つにする（耐久が最も低い想定と最も高い想定を、すべての中から選ぶ）。</summary>
    public static KnownFoe? Merge(IReadOnlyList<KnownFoe> foes)
    {
        if (foes.Count == 0) return null;
        if (foes.Count == 1) return foes[0];
        var stat = foes[0].DefenseStat;
        return new KnownFoe
        {
            DataSetKey = foes[0].DataSetKey,
            Pokemon = foes[0].Pokemon,
            DefenseStat = stat,
            Low = foes.Select(f => f.Low).OrderBy(b => StatCalculator.Calculate(b, stat)).First(),
            High = foes.Select(f => f.High).OrderByDescending(b => StatCalculator.Calculate(b, stat)).First(),
            Summary = string.Join(" ｜ ", foes.Select(f => f.Summary).Distinct()),
        };
    }

    /// <summary>推定の結果から作る。結果が無ければ null。</summary>
    public static KnownFoe? FromEstimate(string dataSetKey, EvEstimator.Estimate e, Pokemon pokemon, EvSystem system, IReadOnlyList<Nature> natures)
    {
        if (!e.HasResult) return null;
        // 実数値が分かるときは実数値で、分からないときは補正と振り方の順で、いちばん低い帯・高い帯を選ぶ
        var hasStats = e.Bands.All(b => b.MinStat > 0);
        var lowBand = (hasStats ? e.Bands.OrderBy(b => b.MinStat) : e.Bands.OrderBy(b => b.Modifier).ThenBy(b => b.MinEv)).First();
        var highBand = (hasStats ? e.Bands.OrderByDescending(b => b.MaxStat) : e.Bands.OrderByDescending(b => b.Modifier).ThenByDescending(b => b.MaxEv)).First();

        Nature? NatureFor(double modifier) =>
            modifier > 1.0 ? NatureSelection.WithIncreased(natures, null, e.DefenseStat)
            : modifier < 1.0 ? NatureSelection.WithDecreased(natures, null, e.DefenseStat)
            : NatureSelection.Neutral(natures);

        PokemonBuild Make(EvEstimator.NatureBand band, int ev, int hp)
        {
            var build = new PokemonBuild(pokemon, system) { Nature = NatureFor(band.Modifier) };
            build.EVs[Stat.HP] = hp;
            build.EVs[e.DefenseStat] = ev;
            return build;
        }

        var low = Make(lowBand, lowBand.MinEv, e.HpEvRange?.MinEv ?? 0);
        var high = Make(highBand, highBand.MaxEv, e.HpEvRange?.MaxEv ?? 0);

        var unit = system == EvSystem.Points ? "pt" : "";
        var letter = StatNames.Letter(e.DefenseStat);
        var bands = string.Join(" / ", e.Bands.Select(b => b.MinEv == b.MaxEv ? $"{b.Label}:{letter}{b.MinEv}{unit}" : $"{b.Label}:{letter}{b.MinEv}〜{b.MaxEv}{unit}"));
        var hpText = e.HpEvRange is { } h ? (h.MinEv == h.MaxEv ? $" H{h.MinEv}{unit}" : $" H{h.MinEv}〜{h.MaxEv}{unit}") : "";
        return new KnownFoe
        {
            DataSetKey = dataSetKey,
            Pokemon = pokemon,
            DefenseStat = e.DefenseStat,
            Low = low,
            High = high,
            Summary = bands + hpText,
        };
    }
}

/// <summary>相手の情報の置き場（アプリを開いている間だけ。試合ごとに相手が変わるので、保存はしない）。</summary>
public sealed class FoeBook
{
    private readonly ConcurrentDictionary<string, KnownFoe> _foes = new();

    /// <summary>登録・解除があったとき。</summary>
    public event Action? Changed;

    private static string Key(string dataSetKey, int pokemonId) => $"{dataSetKey}:{pokemonId}";

    public void Set(KnownFoe foe)
    {
        _foes[Key(foe.DataSetKey, foe.Pokemon.Id)] = foe;
        Changed?.Invoke();
    }

    public KnownFoe? Get(string dataSetKey, int pokemonId) => _foes.TryGetValue(Key(dataSetKey, pokemonId), out var foe) ? foe : null;

    public bool Remove(string dataSetKey, int pokemonId)
    {
        var removed = _foes.TryRemove(Key(dataSetKey, pokemonId), out _);
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>そのデータセットの登録をすべて解除し、解除した数を返す。</summary>
    public int Clear(string dataSetKey)
    {
        var keys = _foes.Where(kv => kv.Value.DataSetKey == dataSetKey).Select(kv => kv.Key).ToList();
        foreach (var key in keys) _foes.TryRemove(key, out _);
        if (keys.Count > 0) Changed?.Invoke();
        return keys.Count;
    }

    public IReadOnlyList<KnownFoe> List(string dataSetKey) =>
        _foes.Values.Where(f => f.DataSetKey == dataSetKey).OrderBy(f => f.Pokemon.Id).ToList();
}
