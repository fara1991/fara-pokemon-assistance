# FaraPokemonAssistance

ポケモンのダメージ計算を **ブラウザ**（GitHub Pages）と **Twitch チャットのコマンド**（FaraBotModerator）の両方から使えるようにするプロジェクトです。

- Web: https://fara1991.github.io/fara-pokemon-assistance/
- チャット: `!dmg イエッサン♂ ワイドフォース メガリザードンX` → `イエッサン(C252 ひかえめ こだわりメガネ) ワイドフォース → メガリザードンX(H252 いじっぱり HP185): 61.6〜72.9% (114〜135) 確定2発 [効果抜群(2倍)]` のような 1 行が返ります。

## 構成

```
fara-pokemon-assistance/
├── src/
│   ├── FaraPokemonAssistance.Core/   計算ロジック・CSV 読み込み・名前解決・!dmg コマンド解析（クラスライブラリ）
│   ├── FaraPokemonAssistance.Web/    Blazor WebAssembly（GitHub Pages に公開）
│   │   └── wwwroot/data/             ★ データ CSV。Web もボットもこのファイルを読む
│   └── FaraPokemonAssistance.Cli/    コマンドをターミナルで試す / ボット組み込み前の動作確認用
├── tests/FaraPokemonAssistance.Core.Tests/
├── tools/
│   ├── sync_data.py                  PokeAPI と ポケモン HOME からデータを再生成するスクリプト
│   ├── datasets.json                 データセット定義（世代・バージョングループ・HOME 設定）
│   └── item_effects.csv              持ち物の効果（手入力）
└── .github/workflows/
    ├── build.yml                     ビルド・テスト・NuGet パッケージ化
    ├── pages.yml                     master へ push されたら GitHub Pages に公開
    └── update-data.yml               毎週月曜にデータを再生成してコミット
```

サーバーは存在しません。計算は Web ではブラウザ内、ボットでは FaraBotModerator のプロセス内で行います。
ボットは `Core` を参照し、データは GitHub Pages 上の CSV（週 1 回自動更新）を取得してローカルにキャッシュします。

## データ

| データセット | 内容 | 元データ |
|---|---|---|
| `Gen8` | ソード・シールド（鎧の孤島・冠の雪原含む） | PokeAPI |
| `Gen9` | スカーレット・バイオレット（DLC 含む） | PokeAPI + ポケモン HOME ランクバトル使用率 |
| `Champions` | ポケモンチャンピオンズ（メガシンカ含む） | PokeAPI（`version_group = champions`） |

- ポケモン・技・持ち物・覚える技・タイプ相性は [PokeAPI の CSV ダンプ](https://github.com/PokeAPI/pokeapi/tree/master/data/v2/csv) から生成します。REST API は叩きません（週 1 回のバルク取得のみ）。
- ID は PokeAPI と同じです。フォルム違い（メガリザードンX = 10034、イエッサン(♀) = 10186 など）も PokeAPI の ID をそのまま使います。
- チャンピオンズの収録ポケモンは PokeAPI の **チャンピオンズ図鑑（pokedex 36、231 種）** とそのメガシンカ・性別差・種族値やタイプの違うフォルムです。チャンピオンズ用の技データがまだ無いポケモンは SV の技で補います（`tools/datasets.json` の `learnset_fallback_version_groups`）。図鑑に無いポケモンは表示しません。
- 第9世代の `usage_*.csv` は HOME のランクバトル最新シーズンから生成します。チャンピオンズの使用率は取得元が確定していないため、現状は未設定です（`tools/datasets.json` の `home` に設定を足せば同じスクリプトで取得できます）。
- 手動で更新する場合: `python tools/sync_data.py`（HOME を飛ばすなら `--skip-home`）。

## チャットコマンド `!dmg`

```
!dmg 攻撃側 技 防御側 [オプション...]
```

| 書き方 | 意味 |
|---|---|
| `A252` `C252` `S4` | 攻撃側の努力値 |
| `H252` `B4` `D252` | 防御側の努力値 |
| `いじっぱり` などの性格 | 上昇補正が攻撃・特攻・素早さなら攻撃側、防御・特防なら防御側 |
| `こだわりメガネ` などの持ち物 | とつげきチョッキ・しんかのきせきは防御側、それ以外は攻撃側 |
| `攻:` / `防:` | 側を明示（例: `防:ずぶとい` `防:たべのこし` `防:+1`） |
| `+1`〜`+6` / `-1`〜`-6` | 攻撃側の攻撃ランク |
| `急所` `ダブル` `シングル` `Lv100` `無振り` | そのまま |
| `ちからもち` などの特性 | そのポケモンが持てる側に付く。両方持てる／持てないなら防御的な特性は防御側 |
| `テラス` / `テラスほのお` / `ほのおテラス` | 攻撃側のテラスタル（`テラス` だけなら技タイプ）。`防:テラスみず` で防御側 |
| `晴れ` `雨` `砂` `雪` | 天候 |
| `エレキ` `グラス` `サイコ` `ミスト` | フィールド |
| `champions` `SV` `剣盾` | データセットの切り替え |

指定しなかった項目は「攻撃側は攻撃技に応じて A or C に 252、防御側は H252、性格・持ち物・特性は使用率 1 位（無ければ第 1 特性）」で埋め、結果の括弧内に明記します。
名前はカタカナ・ひらがな・前方一致・略称（`メガリザX`、`ガブ` など）で解決し、曖昧なときは候補を返します。

ブラウザの「チャットコマンド」ページで同じ文字列を試せます。ターミナルなら:

```bash
dotnet run --project src/FaraPokemonAssistance.Cli -- champions イエッサン♂ ワイドフォース メガリザードンX
```

## FaraBotModerator への組み込み

[docs/bot-integration.md](docs/bot-integration.md) を参照してください。要点:

1. `FaraBotModerator.csproj` から `FaraPokemonAssistance.Core` を参照する（隣に clone して `ProjectReference`、または CI の成果物 `.nupkg`）。
2. 起動時に `DataCatalog` を 1 つ作る。データ元は GitHub Pages の URL + ローカルキャッシュ。
3. `OnMessageReceived` で `!dmg` を見つけたら `DamageCommand.ExecuteAsync` の結果をそのまま `SendMessage`。

## 開発

```bash
dotnet build FaraPokemonAssistance.sln
dotnet test tests/FaraPokemonAssistance.Core.Tests
dotnet run --project src/FaraPokemonAssistance.Web      # http://localhost:5xxx
```

### 計算で考慮しているもの

- 実数値（種族値・個体値・努力値・性格 1.1/0.9 倍）、ランク補正、急所（スナイパー対応）、タイプ一致、タイプ相性、ダブルの複数対象 0.75 倍、五捨五超入
- 持ち物: こだわり系・いのちのたま・たつじんのおび・タイプ強化・プレート・ちからのハチマキ・ものしりメガネ・パンチグローブ・とつげきチョッキ・しんかのきせき・でんきだま・ふといホネ・ふうせん・ブーストエナジー
- テラスタル: 防御タイプの置き換え、タイプ一致 1.5/2.0 倍（てきおうりょく 2.0/2.25 倍）、威力 60 保証
- 天候: 晴れ・雨のほのお/みず補正、砂嵐のいわ特防 1.5 倍、雪のこおり防御 1.5 倍
- フィールド: エレキ・グラス・サイコの 1.3 倍、ミストのドラゴン半減、グラスのじしん系半減、ワイドフォース（1.5 倍＋全体化）、ライジングボルト、サイコブレイド
- 特性: `src/FaraPokemonAssistance.Core/Battle/DamageCalculator.cs` の `AbilityEffects` と各 `*Modifier` を参照。
  スキン系、てきおうりょく、ちからもち/ヨガパワー、はりきり、ごりむちゅう、テクニシャン、ちからずく、てつのこぶし、がんじょうあご、メガランチャー、かたいツメ、パンクロック、すなのちから、はがねつかい/はがねのせいしん、トランジスタ、りゅうのあぎと、いわはこび、すいほう、サンパワー、ひひいろのこどう、ハドロンエンジン、こだいかっせい/クォークチャージ、いろめがね、ブレインフォース、スナイパー、かたやぶり系、
  防御側: ふゆう・ちょすい・よびみず・かんそうはだ・もらいび・こんがりボディ・ちくでん・ひらいしん・でんきエンジン・そうしょく・ぼうおん・ぼうだん・ふしぎなまもり・だっぴ…の無効化、あついしぼう、たいねつ、もふもふ、きよめのしお、ファーコート、くさのけがわ、マルチスケイル/ファントムガード（満タン想定）、フィルター/ハードロック/プリズムアーマー、こおりのりんぷん、てんねん
- 乱数 16 通りをすべて計算し、確定数（乱数 N 発の確率は畳み込みで厳密に算出）

### まだ無いもの

やけど、壁、HP や状態に依存する特性（もうか・こんじょう 等）、連続技、きのみ・回復を考慮した確定数。

## GitHub Pages の初回設定

リポジトリの **Settings → Pages → Build and deployment → Source** を **GitHub Actions** にしてください。
以後は `master` への push で `pages.yml` が自動デプロイします。

## ライセンス

MIT License。ポケモンのデータは [PokeAPI](https://pokeapi.co/)（BSD-3）および株式会社ポケモンの公開情報に基づきます。
