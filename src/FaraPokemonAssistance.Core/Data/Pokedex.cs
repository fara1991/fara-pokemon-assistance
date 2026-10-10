using FaraPokemonAssistance.Core.Models;
using FaraPokemonAssistance.Core.Text;

namespace FaraPokemonAssistance.Core.Data;

/// <summary>ポケモン一覧の並び順。</summary>
public enum PokedexSort
{
    /// <summary>全国図鑑順（同じ種族のフォルム・メガシンカは元のすぐ後ろ）。</summary>
    Dex,
    HP,
    Attack,
    Defense,
    SpAttack,
    SpDefense,
    Speed,
    /// <summary>種族値の合計。</summary>
    Total,
}

/// <summary>ポケモン一覧の絞り込み条件。すべて AND で組み合わせる。</summary>
public sealed class PokedexFilter
{
    /// <summary>名前（カタカナ・ひらがな・ローマ字）。</summary>
    public string Name { get; set; } = "";
    /// <summary>タイプ（2 つまで。両方を持つポケモン）。</summary>
    public List<string> Types { get; set; } = new();
    /// <summary>特性（0 なら指定なし）。</summary>
    public int AbilityId { get; set; }
    /// <summary>覚える技（2 つまで。0 は指定なし。両方を覚えるポケモン）。</summary>
    public List<int> MoveIds { get; set; } = new();
    /// <summary>世代（0 なら指定なし）。</summary>
    public int Generation { get; set; }
    /// <summary>メガシンカ後のポケモンだけ。</summary>
    public bool MegaOnly { get; set; }
    /// <summary>未収録（他のデータで補ったポケモン）も出す。</summary>
    public bool IncludeProvisional { get; set; }
    public PokedexSort Sort { get; set; } = PokedexSort.Dex;
    public bool Descending { get; set; }

    public PokedexFilter Clone() => new()
    {
        Name = Name, Types = Types.ToList(), AbilityId = AbilityId, MoveIds = MoveIds.ToList(), Generation = Generation,
        MegaOnly = MegaOnly, IncludeProvisional = IncludeProvisional, Sort = Sort, Descending = Descending,
    };
}

/// <summary>
/// ポケモン一覧（図鑑）の絞り込みと並べ替え。
/// 図鑑番号は種族 ID（PokeAPI の species id ＝ 全国図鑑番号）。フォルム違い・メガシンカは同じ番号で、元の姿のすぐ後ろに ID 順で並ぶ。
/// 技で絞り込むときの「覚えるポケモン」は技ごとに 1 回だけ求めて覚えておく。
/// </summary>
public sealed class Pokedex
{
    private readonly PokemonDataSet _data;
    private readonly Dictionary<int, HashSet<int>> _learners = new();

    public Pokedex(PokemonDataSet data)
    {
        _data = data;
    }

    /// <summary>全国図鑑番号から世代（1〜9）。</summary>
    public static int GenerationOf(int dexNumber) => dexNumber switch
    {
        <= 0 => 0,
        <= 151 => 1,
        <= 251 => 2,
        <= 386 => 3,
        <= 493 => 4,
        <= 649 => 5,
        <= 721 => 6,
        <= 809 => 7,
        <= 905 => 8,
        _ => 9,
    };

    /// <summary>図鑑番号（種族 ID。無ければポケモンの ID）。</summary>
    public static int DexNumber(Pokemon p) => p.SpeciesId > 0 ? p.SpeciesId : p.Id;

    /// <summary>全国図鑑順の比較（番号 → 元の姿 → フォルムの ID）。</summary>
    public static int CompareDex(Pokemon a, Pokemon b)
    {
        var c = DexNumber(a).CompareTo(DexNumber(b));
        if (c != 0) return c;
        c = (a.Id == DexNumber(a) ? 0 : 1).CompareTo(b.Id == DexNumber(b) ? 0 : 1);
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }

    /// <summary>並べ替えに使う値。</summary>
    public static int SortValue(Pokemon p, PokedexSort sort) => sort switch
    {
        PokedexSort.HP => p.BaseStats.HP,
        PokedexSort.Attack => p.BaseStats.Attack,
        PokedexSort.Defense => p.BaseStats.Defense,
        PokedexSort.SpAttack => p.BaseStats.SpAttack,
        PokedexSort.SpDefense => p.BaseStats.SpDefense,
        PokedexSort.Speed => p.BaseStats.Speed,
        PokedexSort.Total => p.BaseStats.Total,
        _ => 0,
    };

    /// <summary>その技を覚えるポケモンの ID（技ごとに 1 回だけ求める）。</summary>
    public IReadOnlySet<int> LearnersOf(int moveId)
    {
        if (!_learners.TryGetValue(moveId, out var set))
        {
            set = _data.Pokemon.Where(p => _data.CanLearn(p.Id, moveId)).Select(p => p.Id).ToHashSet();
            _learners[moveId] = set;
        }
        return set;
    }

    /// <summary>条件に合うポケモンを、指定の順に並べて返す。同じ値どうしは全国図鑑順。</summary>
    public IReadOnlyList<Pokemon> Query(PokedexFilter filter)
    {
        IEnumerable<Pokemon> list = _data.Pokemon;
        if (!filter.IncludeProvisional) list = list.Where(p => !p.IsProvisional);
        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            var match = KanaSearch.Create(filter.Name);
            list = list.Where(p => match(p.Name));
        }
        foreach (var t in filter.Types.Where(t => !string.IsNullOrEmpty(t)))
            list = list.Where(p => p.HasType(t));
        if (filter.AbilityId > 0)
            list = list.Where(p => p.AbilityIds.Contains(filter.AbilityId));
        foreach (var moveId in filter.MoveIds.Where(id => id > 0).Distinct())
        {
            var learners = LearnersOf(moveId);
            list = list.Where(p => learners.Contains(p.Id));
        }
        if (filter.Generation > 0)
            list = list.Where(p => GenerationOf(DexNumber(p)) == filter.Generation);
        if (filter.MegaOnly)
            list = list.Where(p => p.IsMega);

        var result = list.ToList();
        var sign = filter.Descending ? -1 : 1;
        Comparison<Pokemon> compare = filter.Sort == PokedexSort.Dex
            ? (a, b) => sign * CompareDex(a, b)
            // 同じ値どうしは、降順でも全国図鑑順のまま
            : (a, b) => sign * SortValue(a, filter.Sort).CompareTo(SortValue(b, filter.Sort)) is var c && c != 0 ? c : CompareDex(a, b);
        result.Sort(compare);
        return result;
    }
}
