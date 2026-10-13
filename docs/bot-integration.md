# FaraBotModerator に `!dmg` を組み込む

FaraBotModerator（WPF / TwitchLib）側に必要な変更は 3 箇所です。

## 1. Core を参照する

### 方法 A: 隣に clone して ProjectReference（開発中はこれが楽）

```
C:\repos\FaraBotModerator\
C:\repos\fara-pokemon-battle-assistant\
```

`FaraBotModerator.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\fara-pokemon-battle-assistant\src\FaraPokemonBattleAssistant.Core\FaraPokemonBattleAssistant.Core.csproj" />
</ItemGroup>
```

### 方法 B: NuGet パッケージ

`build.yml` が `FaraPokemonBattleAssistant.Core.<version>.nupkg` を Actions の成果物として出力します。
ローカルフォルダをパッケージソースに追加して参照してください。

```powershell
dotnet nuget add source C:\packages --name local
dotnet add package FaraPokemonBattleAssistant.Core --source local
```

## 2. 起動時に DataCatalog と登録データを用意する

データは https://pokemon.fara-labs.com/ に公開された CSV を使い、1 日キャッシュします。取り直しに失敗したとき（回線断・タイムアウト）は古いキャッシュで続行します。登録したポケモン・チームはローカルの JSON に保存します。

```csharp
using FaraPokemonBattleAssistant.Core.Data;
using FaraPokemonBattleAssistant.Core.Roster;
using FaraPokemonBattleAssistant.Core.Text;

public sealed class PokemonAssistService
{
    private const string DataUrl = "https://pokemon.fara-labs.com/data/";

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
            AllowMutations = canEdit,               // add -p / rm -p / add -t など登録系は配信者本人とモデレーターだけ
        });
        return result.Message;
    }
}
```

### 登録データをサイトと共有する（データ連携）

サイトの「データ連携」ページ（`/sync`）で **データ連携を始める** を押すとアクセスキー（43 文字）が作られ、登録データがクラウド（`https://pokemon.fara-labs.com/api/roster`）に保存されます。
ボット（Auto Bot Settings の Game タブ → ポケモンシリーズ の「アクセスキー」）に同じアクセスキーを設定し、`FileRosterStore` の代わりに `RemoteRosterStore` を使うと、サイトとボットが同じデータを読み書きします（データはクラウドの 1 か所だけにあり、コピーを同期するのではありません）。
画面上の呼び方は「データ連携」「アクセスキー」ですが、コード上の名前（`syncKey` 引数、サイトの localStorage キー `fara-pokemon-battle-assistant.syncKey` など）は互換性のためそのままです。

```csharp
// syncKey: ボットの Game タブ（ポケモンシリーズ）の「アクセスキー」欄に貼り付けた値（パスワードと同じ扱い。ログに出さない）
IRosterStore store = RemoteRosterStore.IsValidKey(syncKey)
    ? new RemoteRosterStore(httpClient, RemoteRosterStore.ProductionBaseUrl, syncKey)
    : new FileRosterStore(Path.Combine(appDir, "pokemon-roster.json"));
var roster = new RosterRepository(store);
```

- `RemoteRosterStore(HttpClient http, string apiBaseUrl, string syncKey)`。`apiBaseUrl` は本番 `RemoteRosterStore.ProductionBaseUrl`（`https://pokemon.fara-labs.com/`）、確認用 `RemoteRosterStore.DevelopBaseUrl`（`https://pokemon-dev.fara-labs.com/`。データは本番と別）。キーの形式が違うと `ArgumentException`
- `RosterRepository` は読み書きのたびにクラウドから読み直し、他の場所（サイト）の更新と競合したら読み直して最大 3 回やり直します（`RosterRepository.MaxConflictRetries`）
- クラウドに届かないときは `RosterUnavailableException`（`Reason`: `Network` / `Unauthorized`＝キーが正しくない / `KeyRetired`＝サイトでキーが作り直された / `TooLarge` / `Rejected` / `Server` / `InvalidResponse`）。`Message` はそのまま表示できる日本語で、キーは含みません。やり直しても競合が続くと `RosterConflictException`
- `PokeCommand.ExecuteAsync` / `TryExecuteAsync` はこれらの例外を受け止めて「失敗しました: アクセスキーが正しくありません」のような結果を返します（チャットにそのまま返せます）
- サイトでキーを作り直すと、古いキーは `KeyRetired` になります。新しいキーを設定し直してください

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
dotnet run --project src/FaraPokemonBattleAssistant.Cli -- "!pokech dmg イエッサン♂ ワイドフォース メガリザードンX サイコフィールド"
dotnet run --project src/FaraPokemonBattleAssistant.Cli -- --data https://pokemon.fara-labs.com/data/ "!pokesv faster ガブリアス ハバタクカミ"
```

ブラウザ: https://pokemon.fara-labs.com/command

## 注意

- Twitch は `/` 始まりのメッセージを自身のコマンドとして扱うため、`!` 始まりにしています。
- 1 メッセージ 500 文字制限に合わせて、結果は `DamageCommandOptions.MaxLength`（既定 480）で切り詰めます。
- 連投対策（同一ユーザーのクールダウンなど）はボット側で行ってください。計算自体は数ミリ秒です。
- `!poke help` で使えるコマンド一覧が返ります（`!poke` はチャンピオンズ扱い）。
- 登録・チームは短い形の `-p` / `-t` が主です（例: `!pokech add -p ガブリアス 4 32 0 0 0 30 ようき`、`!pokech use -t 1`）。長い形の `--pokemon` / `--team` も同じ意味で使えます（例: `!pokech add --pokemon ガブリアス 4 32 0 0 0 30 ようき`、`!pokech use --team 1`）。
- 返信の性格は、性格名ではなく上がる・下がる能力で出ます（例: `イエッサン(C252 C↑A↓ …)`）。補正のない性格（まじめ・てれや など）は出しません。入力は今までどおり性格名です。
- `AllowMutations = false` のとき、`add` / `rm` / `use -t`（pokemon・team とも） は「配信者・モデレーターのみ行えます」と返し、閲覧・計算系はそのまま動きます。
