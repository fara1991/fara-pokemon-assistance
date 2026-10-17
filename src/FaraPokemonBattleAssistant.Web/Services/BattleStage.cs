using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Models;
using Microsoft.JSInterop;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>
/// 背景のバトルフィールドに立たせる 2 体（自分側＝手前、相手側＝奥）。
/// ダメージ計算機（攻撃側・防御側）と素早さ比較（ポケモン1・2）で選んだポケモンを、最後に選んだページの分だけ出す。
/// どちらのページでもまだ選んでいないときは、ランダムな 2 体を出す（ページの入力欄には入れない）。
/// ページで選んだポケモン（PickedMine / PickedFoe）は、もう一方のページを開いたときにその欄にも入れる。
/// </summary>
public sealed class BattleStage
{
    private const string StorageKey = "fara-pokemon-battle-assistant.battle-view";
    private readonly IJSRuntime _js;
    private Task? _init;

    public BattleStage(IJSRuntime js) => _js = js;

    /// <summary>「対戦画面で表示」のスイッチを前回の状態に戻す（ブラウザに保存している）。複数の場所から呼ばれても 1 回だけ読む。</summary>
    public Task InitializeAsync() => _init ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            var saved = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (saved == "1" && !BattleView)
            {
                BattleView = true;
                Changed?.Invoke();
            }
        }
        catch
        {
            // localStorage が使えない環境（プライベートモード等）ではオフのまま
        }
    }

    /// <summary>今の 2 体を決めたときのデータセット。データセットが変わったら選び直す。</summary>
    public string DataSetKey { get; private set; } = "";
    public Pokemon? Mine { get; private set; }
    public Pokemon? Foe { get; private set; }

    /// <summary>ダメージ計算機・素早さ比較で最後に選んだポケモン（自分側＝攻撃側／ポケモン1、相手側＝防御側／ポケモン2）。ランダムな 2 体は入らない。</summary>
    public Pokemon? PickedMine { get; private set; }
    public Pokemon? PickedFoe { get; private set; }
    /// <summary>その 2 体に選んでいる性格（もう一方のページを開いたときに引き継ぐ）。</summary>
    public Nature? PickedMineNature { get; private set; }
    public Nature? PickedFoeNature { get; private set; }

    public event Action? Changed;

    /// <summary>「対戦画面で表示」のスイッチ。オンで結果（Scene）があるとき、背景のフィールドを止めて結果を重ねて出す。</summary>
    public bool BattleView { get; private set; }

    /// <summary>今開いているページが出している結果。結果がないページ・まだ結果が出ていないときは null。</summary>
    public BattleScene? Scene { get; private set; }

    private object? sceneOwner;
    private object? conditionsOwner;

    /// <summary>いま開いているページで入力している天候・フィールド（ダメージ計算機・素早さ比較・育成ポケモン）。背景のフィールドに見た目で出る。</summary>
    public Weather Weather { get; private set; }
    public Terrain Terrain { get; private set; }

    /// <summary>
    /// 「計算」の見出しの中（ダメージ計算機・素早さ比較）で、行き来しても引き継ぐ天候・フィールド。
    /// 各ページは、開いたときにこの値を入れ、変わったら書き戻す。
    /// </summary>
    public Weather SharedWeather { get; private set; }
    public Terrain SharedTerrain { get; private set; }

    public void ShareField(Weather weather, Terrain terrain)
    {
        SharedWeather = weather;
        SharedTerrain = terrain;
    }

    /// <summary>ページが、入力している天候・フィールドを出す。同じ内容なら何もしない（描画のたびに呼んでよい）。</summary>
    public void SetConditions(object owner, Weather weather, Terrain terrain)
    {
        if (ReferenceEquals(conditionsOwner, owner) && Weather == weather && Terrain == terrain) return;
        conditionsOwner = owner;
        Weather = weather;
        Terrain = terrain;
        Changed?.Invoke();
    }

    /// <summary>対戦画面を出している状態か（スイッチがオンなら、どのページでも出す。結果が無いページ・まだ結果が無いときは案内だけを出す）。</summary>
    public bool BattleActive => BattleView;

    public void SetBattleView(bool on)
    {
        if (BattleView == on) return;
        BattleView = on;
        Changed?.Invoke();
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, BattleView ? "1" : "0"); }
        catch { /* 保存できなくても、その場の表示には困らない */ }
    }

    /// <summary>
    /// 背景に立たせる 2 体を、ページで「選んだ」ことにせずに出す（育成ポケモン用。ほかのページの入力欄には入れない）。
    /// 選んでいない側（null）は今のままにする。
    /// </summary>
    public void ShowOnly(string dataSetKey, Pokemon? mine, Pokemon? foe)
    {
        if (dataSetKey != DataSetKey) return;
        var next = (Mine: mine ?? Mine, Foe: foe ?? Foe);
        if (next.Mine?.Id == Mine?.Id && next.Foe?.Id == Foe?.Id) return;
        Mine = next.Mine;
        Foe = next.Foe;
        Changed?.Invoke();
    }

    /// <summary>ページが結果を出す。同じ内容なら何もしない（描画のたびに呼んでよい）。</summary>
    public void SetScene(object owner, BattleScene? scene)
    {
        if (scene is null) { ClearScene(owner); return; }
        if (ReferenceEquals(sceneOwner, owner) && Equals(Scene, scene)) return;
        sceneOwner = owner;
        Scene = scene;
        Changed?.Invoke();
    }

    /// <summary>ページが閉じたとき・結果がなくなったときに消す。別のページが出した結果は消さない。</summary>
    public void ClearScene(object owner)
    {
        var changed = false;
        if (ReferenceEquals(sceneOwner, owner))
        {
            sceneOwner = null;
            Scene = null;
            changed = true;
        }
        if (ReferenceEquals(conditionsOwner, owner))
        {
            conditionsOwner = null;
            Weather = Weather.None;
            Terrain = Terrain.None;
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

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
        PickedMineNature = PickedFoeNature = null;
        Mine = candidates[first];
        Foe = candidates[second];
        Changed?.Invoke();
    }

    /// <summary>ページで選んでいる 2 体を出す。選んでいない側（null）は今のままにする。</summary>
    public void Show(string dataSetKey, Pokemon? mine, Pokemon? foe, Nature? mineNature = null, Nature? foeNature = null)
    {
        if (dataSetKey != DataSetKey) return;
        if (mine is not null) { PickedMine = mine; PickedMineNature = mineNature; }
        if (foe is not null) { PickedFoe = foe; PickedFoeNature = foeNature; }
        var next = (Mine: mine ?? Mine, Foe: foe ?? Foe);
        if (next.Mine?.Id == Mine?.Id && next.Foe?.Id == Foe?.Id) return;
        Mine = next.Mine;
        Foe = next.Foe;
        Changed?.Invoke();
    }
}
