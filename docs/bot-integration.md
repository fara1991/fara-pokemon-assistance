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

## 2. 起動時に DataCatalog を作る

データは GitHub Pages に公開された CSV を使い、1 日キャッシュします。
ネットワークが落ちていても前回のキャッシュで動きます。初回だけはオンラインが必要です。

```csharp
using FaraPokemonAssistance.Core.Data;
using FaraPokemonAssistance.Core.Text;

public sealed class PokemonDamageService
{
    private const string DataUrl = "https://fara1991.github.io/fara-pokemon-assistance/data/";

    private readonly DamageCommand _command;

    public PokemonDamageService()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FaraBotModerator", "pokemon-data");
        var source = new CachingDataSource(new HttpDataSource(new HttpClient(), DataUrl), cacheDir, TimeSpan.FromDays(1));
        _command = new DamageCommand(new DataCatalog(source));
    }

    /// <summary>"!dmg ..." の 1 行を受け取り、チャットに返す 1 行を返す。</summary>
    public async Task<string> HandleAsync(string message, string defaultDataSet = "Champions")
    {
        var args = message.Length > 4 ? message[4..] : ""; // "!dmg" を落とす
        var result = await _command.ExecuteAsync(args, new DamageCommandOptions
        {
            DataSetKey = defaultDataSet,          // 配信しているゲームに合わせる
            Format = Models.BattleFormat.Singles, // ダブル配信なら Doubles
        });
        return result.Message;
    }
}
```

ローカルの CSV を使いたい場合は `new FileDataSource(@"C:\repos\fara-pokemon-assistance\src\FaraPokemonAssistance.Web\wwwroot\data")` に差し替えるだけです。

## 3. チャットメッセージをさばく

`TwitchClientController.TwitchClientOnMessageReceived` の先頭に追加します。

```csharp
private readonly PokemonDamageService _pokemonDamage = new();

private async void TwitchClientOnMessageReceived(object? sender, OnMessageReceivedArgs e)
{
    var text = e.ChatMessage.Message.Trim();
    if (text.StartsWith("!dmg", StringComparison.OrdinalIgnoreCase))
    {
        var reply = await _pokemonDamage.HandleAsync(text);
        SendMessage(e.ChatMessage.Channel, reply);
        return;
    }

    // 既存の処理 …
}
```

## 動作確認

ボットに組み込む前に、同じ文字列をターミナルやブラウザで試せます。

```bash
dotnet run --project src/FaraPokemonAssistance.Cli -- champions イエッサン♂ ワイドフォース メガリザードンX
dotnet run --project src/FaraPokemonAssistance.Cli -- --data https://fara1991.github.io/fara-pokemon-assistance/data/ ガブリアス じしん ハバタクカミ
```

ブラウザ: https://fara1991.github.io/fara-pokemon-assistance/command

## 注意

- Twitch は `/` 始まりのメッセージを自身のコマンドとして扱うため、`!` 始まりにしています。
- 1 メッセージ 500 文字制限に合わせて、結果は `DamageCommandOptions.MaxLength`（既定 480）で切り詰めます。
- 連投対策（同一ユーザーのクールダウンなど）はボット側で行ってください。計算自体は数ミリ秒です。
