# FaraBotModerator に `!dmg` を組み込む

FaraBotModerator（WPF / TwitchLib）側に必要な変更は 3 箇所です。

## 1. Core を参照する

### 方法 A: 隣に clone して ProjectReference（開発中はこれが楽）

```
C:\repos\FaraBotModerator\
C:\repos\fara-pokemon-assistance\
```

`FaraBotModerator.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\fara-pokemon-assistance\src\FaraPokemonAssistance.Core\FaraPokemonAssistance.Core.csproj" />
</ItemGroup>
```

### 方法 B: NuGet パッケージ

`build.yml` が `FaraPokemonAssistance.Core.<version>.nupkg` を Actions の成果物として出力します。
ローカルフォルダをパッケージソースに追加して参照してください。

```powershell
dotnet nuget add source C:\packages --name local
dotnet add package FaraPokemonAssistance.Core --source local
```

## 2. 起動時に DataCatalog と登録データを用意する

データは GitHub Pages に公開された CSV を使い、1 日キャッシュします。取り直しに失敗したとき（回線断・タイムアウト）は古いキャッシュで続行します。登録したポケモン・チームはローカルの JSON に保存します。

```csharp
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Roster;
using FaraPokemonAssistance.Core.Text;

public sealed class PokemonAssistService
{
    private const string DataUrl = "https://fara1991.github.io/fara-pokemon-assistance/data/";

    private readonly PokeCommand _command;

    public PokemonAssistService()
    {
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FaraBotModerator");
        var source = new CachingDataSource(new HttpDataSource(new HttpClient(), DataUrl),
            Path.Combine(appDir, "pokemon-data"), TimeSpan.FromDays(1));
        var roster = new RosterRepository(new FileRosterStore(Path.Combine(appDir, "pokemon-roster.json")));
        // 第 2 引数: 長時間起動しても週次更新のデータを取り込めるよう、1 日ごとに読み直す
        _command = new PokeCommand(new DataCatalog(source, TimeSpan.FromDays(1)), roster);
    }

    /// <summary>"!pokech ..." などのメッセージかどうか。</summary>
    public async Task<bool> HandlesAsync(string message) => await _command.MatchAsync(message) is not null;

    /// <summary>チャットに返す 1 行を返す。</summary>
    public async Task<string> HandleAsync(string message, bool canEdit)
    {
        var result = await _command.ExecuteAsync(message, new PokeCommandOptions
        {
            Format = Models.BattleFormat.Singles,   // ダブル配信なら Doubles
            AllowMutations = canEdit,               // add / rm / use team は配信者本人とモデレーターだけ
        });
        return result.Message;
    }
}
```

## 3. チャットメッセージをさばく

`TwitchClientController.TwitchClientOnMessageReceived` の先頭に追加します。

```csharp
private readonly PokemonAssistService _pokemon = new();

private async void TwitchClientOnMessageReceived(object? sender, OnMessageReceivedArgs e)
{
    var text = e.ChatMessage.Message.Trim();
    if (await _pokemon.HandlesAsync(text))
    {
        // 登録・削除・使用チームの変更は配信者本人とモデレーターだけ（視聴者には開放しない）
        var canEdit = e.ChatMessage.IsBroadcaster || e.ChatMessage.IsModerator;
        SendMessage(e.ChatMessage.Channel, await _pokemon.HandleAsync(text, canEdit));
        return;
    }

    // 既存の処理 …
}
```

## 動作確認

ボットに組み込む前に、同じ文字列をターミナルやブラウザで試せます。

```bash
dotnet run --project src/FaraPokemonAssistance.Cli -- "!pokech dmg イエッサン♂ ワイドフォース メガリザードンX サイコフィールド"
dotnet run --project src/FaraPokemonAssistance.Cli -- --data https://fara1991.github.io/fara-pokemon-assistance/data/ "!pokesv diff ガブリアス ハバタクカミ"
```

ブラウザ: https://fara1991.github.io/fara-pokemon-assistance/command

## 注意

- Twitch は `/` 始まりのメッセージを自身のコマンドとして扱うため、`!` 始まりにしています。
- 1 メッセージ 500 文字制限に合わせて、結果は `DamageCommandOptions.MaxLength`（既定 480）で切り詰めます。
- 連投対策（同一ユーザーのクールダウンなど）はボット側で行ってください。計算自体は数ミリ秒です。
- `!poke cmd ls` で使えるコマンド一覧が返ります（`!poke` はチャンピオンズ扱い）。
- `AllowMutations = false` のとき、`add` / `rm` / `use team` は「配信者・モデレーターのみ行えます」と返し、閲覧・計算系はそのまま動きます。
