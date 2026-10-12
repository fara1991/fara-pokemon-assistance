using FaraPokemonBattleAssistant.Core.Roster;
using Microsoft.JSInterop;

namespace FaraPokemonBattleAssistant.Web.Services;

/// <summary>ブラウザの localStorage に登録データを保存する。</summary>
public sealed class BrowserRosterStore : IRosterStore
{
    private const string Key = "fara-pokemon-battle-assistant.roster";
    private readonly IJSRuntime _js;

    public BrowserRosterStore(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            return await _js.InvokeAsync<string?>("localStorage.getItem", ct, Key);
        }
        catch (JSException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        await _js.InvokeVoidAsync("localStorage.setItem", ct, Key, json);
    }

    /// <summary>このブラウザの登録データを消す（データ連携を始めたとき）。</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _js.InvokeVoidAsync("localStorage.removeItem", ct, Key);
    }
}
