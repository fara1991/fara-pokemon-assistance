using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Models;
using Microsoft.JSInterop;

namespace FaraPokemonAssistance.Web.Services;

/// <summary>
/// 全ページで共有する設定（データセット・バトル形式・テーマ）。サイドバーで変更し、各ページが購読する。
/// </summary>
public sealed class AppState
{
    private const string StorageKey = "fara-pokemon-assistance.settings";
    private readonly DataCatalog _catalog;
    private readonly IJSRuntime _js;
    private Task? _init;

    public AppState(DataCatalog catalog, IJSRuntime js)
    {
        _catalog = catalog;
        _js = js;
    }

    public IReadOnlyList<DataSetInfo> DataSets { get; private set; } = Array.Empty<DataSetInfo>();
    public string DataSetKey { get; private set; } = DataCatalog.DefaultDataSetKey;
    public DataSetInfo? DataSet => DataSets.FirstOrDefault(d => d.Key == DataSetKey);
    public string CommandPrefix => DataSet?.CommandPrefix ?? "pokech";
    public BattleFormat Format { get; private set; } = BattleFormat.Singles;
    /// <summary>"light" / "dark"。</summary>
    public string Theme { get; private set; } = "light";

    /// <summary>データセット・バトル形式が変わったときに発火する。</summary>
    public event Func<Task>? Changed;

    /// <summary>初期化。複数のコンポーネントから同時に呼ばれても同じ処理を待つ。</summary>
    public Task InitializeAsync() => _init ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        DataSets = await _catalog.GetDataSetsAsync();
        DataSetKey = await _catalog.ResolveDefaultKeyAsync();
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<Saved>(json);
                if (saved is not null)
                {
                    if (DataSets.Any(d => d.Key == saved.DataSetKey)) DataSetKey = saved.DataSetKey!;
                    if (Enum.TryParse<BattleFormat>(saved.Format, out var f)) Format = f;
                    if (saved.Theme is "light" or "dark") Theme = saved.Theme;
                }
            }
        }
        catch
        {
            // localStorage が使えない環境（プライベートモード等）では既定値のまま
        }
        await ApplyThemeAsync();
    }

    public async Task SetDataSetAsync(string key)
    {
        if (key == DataSetKey || DataSets.All(d => d.Key != key)) return;
        DataSetKey = key;
        await SaveAsync();
        await RaiseChangedAsync();
    }

    public async Task SetFormatAsync(BattleFormat format)
    {
        if (format == Format) return;
        Format = format;
        await SaveAsync();
        await RaiseChangedAsync();
    }

    public async Task SetThemeAsync(string theme)
    {
        Theme = theme == "dark" ? "dark" : "light";
        await SaveAsync();
        await ApplyThemeAsync();
    }

    public Task ToggleThemeAsync() => SetThemeAsync(Theme == "dark" ? "light" : "dark");

    private async Task ApplyThemeAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("document.documentElement.setAttribute", "data-bs-theme", Theme);
        }
        catch
        {
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new Saved { DataSetKey = DataSetKey, Format = Format.ToString(), Theme = Theme });
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch
        {
        }
    }

    private async Task RaiseChangedAsync()
    {
        if (Changed is null) return;
        foreach (var handler in Changed.GetInvocationList().Cast<Func<Task>>())
            await handler();
    }

    private sealed class Saved
    {
        public string? DataSetKey { get; set; }
        public string? Format { get; set; }
        public string? Theme { get; set; }
    }
}

/// <summary>
/// ページを切り替えても入力内容を保持するための置き場。ページごとに状態オブジェクトを 1 つ持つ。
/// </summary>
public sealed class PageStateStore
{
    private readonly Dictionary<string, object> _states = new();

    public T Get<T>(string key) where T : class, new()
    {
        if (_states.TryGetValue(key, out var existing) && existing is T typed) return typed;
        var created = new T();
        _states[key] = created;
        return created;
    }
}
