using FaraPokemonAssistance.Web.Services;
using Microsoft.AspNetCore.Components;

namespace FaraPokemonAssistance.Web.Shared;

/// <summary>
/// サイドバーのデータセット・バトル形式の変更を受け取るページの基底クラス。
/// </summary>
public abstract class AppPageBase : ComponentBase, IDisposable
{
    [Inject] protected AppState App { get; set; } = default!;
    [Inject] protected PageStateStore PageStates { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await App.InitializeAsync();
        App.Changed += HandleAppChangedAsync;
        await OnAppReadyAsync();
    }

    /// <summary>AppState の初期化後に 1 回呼ばれる。</summary>
    protected virtual Task OnAppReadyAsync() => Task.CompletedTask;

    /// <summary>データセットまたはバトル形式が変わったときに呼ばれる。</summary>
    protected virtual Task OnAppChangedAsync() => Task.CompletedTask;

    private async Task HandleAppChangedAsync()
    {
        await OnAppChangedAsync();
        await InvokeAsync(StateHasChanged);
    }

    public virtual void Dispose()
    {
        App.Changed -= HandleAppChangedAsync;
    }
}
