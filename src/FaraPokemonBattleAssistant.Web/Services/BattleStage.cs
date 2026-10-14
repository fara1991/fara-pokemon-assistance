using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 背景のバトルフィールドに立たせる 2 体（自分側＝手前、相手側＝奥）。
/// ダメージ計算機（攻撃側・防御側）と素早さ比較（ポケモン1・2）で選んだポケモンを、最後に選んだページの分だけ出す。
/// どちらのページでもまだ選んでいないときは、ランダムな 2 体を出す（ページの入力欄には入れない）。
/// ページで選んだポケモン（PickedMine / PickedFoe）は、もう一方のページを開いたときにその欄にも入れる。
/// </summary>
public sealed class BattleStage
{
    /// <summary>今の 2 体を決めたときのデータセット。データセットが変わったら選び直す。</summary>
    public string DataSetKey { get; private set; } = "";
    public Pokemon? Mine { get; private set; }
    public Pokemon? Foe { get; private set; }

    /// <summary>ダメージ計算機・素早さ比較で最後に選んだポケモン（自分側＝攻撃側／ポケモン1、相手側＝防御側／ポケモン2）。ランダムな 2 体は入らない。</summary>
    public Pokemon? PickedMine { get; private set; }
    public Pokemon? PickedFoe { get; private set; }

    public event Action? Changed;

    /// <summary>そのデータセットで、まだ 2 体が決まっていなければランダムに決める。</summary>
    public void EnsureDefaults(string dataSetKey, PokemonDataSet data)
    {
        if (dataSetKey == DataSetKey && Mine is not null && Foe is not null) return;
        var candidates = data.Pokemon.Where(p => !p.IsProvisional && !string.IsNullOrEmpty(p.Icon)).ToList();
        if (candidates.Count < 2) return;
        var first = Random.Shared.Next(candidates.Count);
        var second = Random.Shared.Next(candidates.Count - 1);
        if (second >= first) second++;
        DataSetKey = dataSetKey;
        PickedMine = PickedFoe = null;
        Mine = candidates[first];
        Foe = candidates[second];
        Changed?.Invoke();
    }

    /// <summary>ページで選んでいる 2 体を出す。選んでいない側（null）は今のままにする。</summary>
    public void Show(string dataSetKey, Pokemon? mine, Pokemon? foe)
    {
        if (dataSetKey != DataSetKey) return;
        if (mine is not null) PickedMine = mine;
        if (foe is not null) PickedFoe = foe;
        var next = (Mine: mine ?? Mine, Foe: foe ?? Foe);
        if (next.Mine?.Id == Mine?.Id && next.Foe?.Id == Foe?.Id) return;
        Mine = next.Mine;
        Foe = next.Foe;
        Changed?.Invoke();
    }
}
