namespace FaraPokemonBattleAssistant.Web.Services;

public enum ToastKind { Success, Warning, Error }

public sealed record ToastItem(int Id, ToastKind Kind, string Text);

/// <summary>
/// 画面の右上に重ねて出す通知（トースト）。サイト全体で使う。
/// 登録・削除・更新・データ共有など、データを変える処理の結果を知らせるときに呼ぶ。
/// 数秒で自動で消え、複数出たら縦に積む（表示は Components/Toasts.razor）。
/// </summary>
public sealed class ToastService
{
    private readonly List<ToastItem> items = new();
    private int nextId;

    /// <summary>出ている通知（古い順）。</summary>
    public IReadOnlyList<ToastItem> Items => items;

    /// <summary>一覧が変わったとき（表示側が描き直す）。</summary>
    public event Action? Changed;

    public void Success(string text) => Show(ToastKind.Success, text);
    public void Warning(string text) => Show(ToastKind.Warning, text);
    public void Error(string text) => Show(ToastKind.Error, text);

    /// <summary>成功 / 失敗をまとめて受ける。</summary>
    public void Show(string text, bool isError) => Show(isError ? ToastKind.Error : ToastKind.Success, text);

    public void Show(ToastKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var item = new ToastItem(++nextId, kind, text);
        items.Add(item);
        // 同時に出すのは 5 つまで（古いものから消す）
        while (items.Count > 5) items.RemoveAt(0);
        Changed?.Invoke();
        _ = AutoDismissAsync(item);
    }

    public void Dismiss(int id)
    {
        if (items.RemoveAll(i => i.Id == id) > 0) Changed?.Invoke();
    }

    /// <summary>自動で消えるまでの時間。失敗・警告は読む時間を長めにする。</summary>
    public static TimeSpan Lifetime(ToastKind kind) => kind switch
    {
        ToastKind.Error => TimeSpan.FromSeconds(9),
        ToastKind.Warning => TimeSpan.FromSeconds(7),
        _ => TimeSpan.FromSeconds(4.5),
    };

    private async Task AutoDismissAsync(ToastItem item)
    {
        await Task.Delay(Lifetime(item.Kind));
        Dismiss(item.Id);
    }
}
