# Pokémon Battle Assistant

ポケモン対戦アシスタント（Pokémon Battle Assistant）。ダメージ計算・素早さ比較・育成済みポケモンとチームの管理・タイプ相性を、**ブラウザ**（Cloudflare Workers）と **Twitch チャットのコマンド**（FaraBotModerator）の両方から使えるようにするプロジェクトです。

- Web: https://pokemon.fara-labs.com/
- チャット: `!dmg イエッサン♂ ワイドフォース メガリザードンX` → `イエッサン(C252 C↑A↓ こだわりメガネ) ワイドフォース → メガリザードンX(H252 A↑C↓ HP185): 61.6〜72.9% (114〜135) 確定2発 [効果抜群(2倍)]` のような 1 行が返ります。

## 構成

```
fara-pokemon-battle-assistant/
├── src/
│   ├── FaraPokemonBattleAssistant.Core/   計算ロジック・CSV 読み込み・名前解決・登録/チーム・チャットコマンド（クラスライブラリ）
│   ├── FaraPokemonBattleAssistant.Web/    Blazor WebAssembly（Cloudflare Workers に公開）
│   │   └── wwwroot/data/             ★ データ CSV。Web もボットもこのファイルを読む
│   └── FaraPokemonBattleAssistant.Cli/    コマンドをターミナルで試す / ボット組み込み前の動作確認用
├── tests/FaraPokemonBattleAssistant.Core.Tests/
├── tools/
│   ├── sync_data.py                  公開データとポケモン HOME からデータを再生成するスクリプト
│   ├── datasets.json                 データセット定義（世代・バージョングループ・HOME 設定）
│   ├── item_effects.csv              持ち物の効果（手入力）
│   └── ability_names_ja.csv          PokeAPI に日本語名がまだ無い特性の補完表（手入力。sync_data.py が適用、英語のまま残ると警告）
└── .github/workflows/
    ├── build.yml                     ビルド・テスト・NuGet パッケージ化
    ├── deploy.yml                    master へ push されたら Cloudflare Workers（本番）に公開
    ├── deploy-develop.yml            develop へ push されたら確認用サイトに公開
    └── update-data.yml               毎週月曜にデータを再生成して develop にコミット（変更があれば deploy-develop.yml で確認用に公開。本番へは develop → master の PR で反映）
```

サーバーは存在しません。計算は Web ではブラウザ内、ボットでは FaraBotModerator のプロセス内で行います。
ボットは `Core` を参照し、データは公開サイト上の CSV（週 1 回自動更新）を取得してローカルにキャッシュします。

## データ

| データセット | 内容 | 元データ |
|---|---|---|
| `Gen1`・`Gen2` | 赤・緑〜金・銀・クリスタル。その世代の時点のタイプ・種族値・特性・技・タイプ相性。**参照のみ**（ポケモン一覧・タイプ相性表。計算機・素早さ比較・登録・チャットの計算は非対応） | 公開データ（`*_past.csv`・`move_changelog.csv`） |
| `Gen3`〜`Gen7` | ルビー・サファイア〜サン・ムーン・USUM。上と同じくその世代の時点のデータで、計算機・素早さ比較にも対応（第6〜7世代はメガシンカ、第7世代は Z 技） | 同上 |
| `Gen8` | ソード・シールド（鎧の孤島・冠の雪原含む）。ダイマックス対応 | 公開データ |
| `Gen9` | スカーレット・バイオレット（DLC 含む）。テラスタル対応 | 公開データ + ポケモン HOME ランクバトル使用率 |
| `Champions` | ポケモンチャンピオンズ（メガシンカ・メガストーン含む） | 公開データ（`version_group = champions`） |

- ポケモン・技・持ち物・覚える技・タイプ相性は [公開されている CSV ダンプ](https://github.com/PokeAPI/pokeapi/tree/master/data/v2/csv) から生成します。REST API は叩きません（週 1 回のバルク取得のみ）。
- ID は元データと同じです。フォルム違い（メガリザードンX = 10034、イエッサン(♀) = 10186 など）も元データの ID をそのまま使います。性別でフォルムが分かれる種族は基本フォルムにも `(♂)` を付けます（`イエッサン(♂)`。チャットでは `イエッサン` だけでも ♂ になります）。
- チャンピオンズの収録ポケモンは **チャンピオンズ図鑑（pokedex 36、231 種）** とそのメガシンカ・性別差・種族値やタイプの違うフォルムです。チャンピオンズ用の技データがまだ無いポケモンは SV の技で補い、SV にも居ないポケモン（ガラルのバリヤード等）は剣盾 → USUM の順に、そのポケモンのデータがある最初の世代の技で補います（`tools/datasets.json` の `learnset_fallback_version_groups` / `learnset_fallback_older_version_groups`）。技データが空のポケモンは作りません（空だと「全技を覚える」扱いになるため）。`python tools/verify_learnsets.py` で PokeAPI の CSV と突き合わせて検証できます。図鑑に無いポケモンは表示しません。
- 第9世代の `usage_*.csv` は HOME のランクバトル最新シーズンから生成します。チャンピオンズの使用率は取得元が確定していないため、現状は未設定です（`tools/datasets.json` の `home` に設定を足せば同じスクリプトで取得できます）。
- 第1〜7世代（`tools/datasets.json` の `historical: true`）は、元データの `pokemon_types_past` / `pokemon_stats_past` / `pokemon_abilities_past` / `type_efficacy_past` / `move_changelog` で、その世代の時点の値にします。第1世代の「とくしゅ」は特攻・特防の両方に入れ、第3世代までの技は技のタイプで物理・特殊を決めます。特性は第3世代から、隠れ特性は第5世代からです。メガシンカ・リージョンフォームなどは、そのゲームまでに登場していたものだけを入れます。
- `datasets.json` の `calc: "Reference"` のゲームは参照のみです（計算機などでは案内を出します）。
- 第3〜7世代のダメージ計算（`LegacyDamageCalculator`）は、その世代の補正の順番・丸め（第3〜4世代は補正ごとの切り捨て・タイプ一致の位置、急所 2 倍 / 1.5 倍、スキン系 1.3 倍、フィールド 1.5 倍、まひ 1/4、やけど 1/8 など）で行います。公開されているダメージ計算ツール（@smogon/calc）の結果と、`tools/calc_vectors` で作った条件（`tests/.../Data/calc_vectors.json`。第3〜7世代に加え、ソード・シールド / スカーレット・バイオレット / チャンピオンズの条件も含む）で一致を確かめています（第3世代のウェザーボールは、天候があると威力ではなくダメージが 2 倍になり、変わったタイプで物理・特殊も決まります。天候がないときはツールが特殊技として計算しますが、ゲームではノーマルタイプの物理技なので、その条件だけ比較から除いています）。ちからずく・すてみの対象の技は `move_lists.js` で作る `Data/move_lists.json` で全部の技を照合します。今のゲームの計算を意図して変えたときは `Data/calc_snapshot.txt` を作り直し、変わったケースを確認します。
- 手動で更新する場合: `python tools/sync_data.py`（HOME を飛ばすなら `--skip-home`）。

## チャットコマンド

接頭辞でデータセットを選びます: **`!pokech`** = ポケモンチャンピオンズ、**`!pokesv`** = スカーレット・バイオレット、**`!pokess`** = ソード・シールド（`!poke` はチャンピオンズ扱い）。
`!poke help` で使えるコマンドの一覧が返ります。登録・削除・使用チームの変更は配信者本人とモデレーターだけに許可する想定です（`PokeCommandOptions.AllowMutations`）。
登録データはボットなら PC 上の JSON、ブラウザなら localStorage に保存され、データセットごとに独立しています。
サイトの「データ連携」ページ（`/data-sync`。ポケモン登録は `/my-pokemons`、チーム登録は `/my-teams`。以前の `/sync`・`/pokemon`・`/roster`・`/team`・`/my-pokemon` などは新しい URL に転送）でアクセスキーを作ると、登録データをクラウドに保存し、同じアクセスキーを入れた別のブラウザ・端末やボットが同じデータを読み書きできます（連携中はクラウドだけが正で、ブラウザにはアクセスキーだけを保存します。ボット側は `RemoteRosterStore`。[docs/bot-integration.md](docs/bot-integration.md)）。

### 育成済みポケモンの登録（`-p` / `--pokemon`）

対象はオプションで指定します。ポケモンは `-p`（長い形は `--pokemon`）、チームは `-t`（`--team`）で、サブコマンドの直後に置きます。長い形は短い形と同じ意味です（例は下）。`add pokemon` のような単語の形は使えません（`add` だけを打つと、`-p` か `-t` を付けるよう案内します）。全角スペースや全角の英数字・記号（`ｄｍｇ`・`－ｐ`・`＋１` など）は半角として読み取ります（`PokeCommand` / `DamageCommand` の入口で `CommandText.Normalize`）。

例（短い形の直後に、同じ意味の長い形を並べています）:

```
!pokech add -p ガブリアス 4 32 0 0 0 30 ようき
!pokech add --pokemon ガブリアス 4 32 0 0 0 30 ようき
!pokech ls -p
!pokech ls --pokemon
!pokech ls -p ガブリアス
!pokech use -t 1
!pokech use --team 1
```

| コマンド | 内容 |
|---|---|
| `add -p 名前 H A B C D S 性格 [特性]` | 登録。チャンピオンズはポイント（各 0〜32・合計 66、1 pt = 実数値 +1）、SV/剣盾は努力値（各 0〜252・合計 510） |
| `ls -p [名前]` | 一覧（登録 ID・ポケモン・性格）。名前を付けるとそのポケモンだけ |
| `detail -p ID` | 詳細（性格・振り・特性・実数値・所属チーム） |
| `rm -p ID` | 削除（チームからも外れる） |

### チーム（`-t` / `--team`）

| コマンド | 内容 |
|---|---|
| `add -t 番号 ID [持ち物]` | チームに追加（6 体まで）。同じ ID を再指定すると持ち物を更新 |
| `rm -t 番号 名前` / `rm -t 番号` | チームから外す / チーム削除。登録の削除などで 0 体になったチームは自動で削除 |
| `use -t 番号` | 使用チームにする。以降の dmg / calc / faster はこのチームの構成（努力値・性格・特性・持ち物）で計算 |
| `unuse -t` | 使用チームを解除する |
| `ls -t` / `detail -t 番号` | チーム一覧 / 詳細 |

### 計算・ヘルプ

| コマンド | 内容 |
|---|---|
| `dmg 攻撃側 技±ランク 防御側 [オプション]` | 通常と急所のダメージ・確定数。使用チームにいるポケモンはその構成（持ち物は括弧内に表示）。相手の特性が不明で特性によって結果が変わる場合は「特性別:」で特性ごとに表示。例: `!pokech dmg ガブリアス じしん+1 リザードン` |
| `calc 攻撃側 技±ランク 防御側 受けたダメージ` | 受けた（与えた）ダメージから、相手の防御側の努力値と性格補正を推定。`93` なら実数、`45%` なら割合（H も推定）。相手の特性で結果が変わる場合は特性ごとに表示 |
| `faster 自分 相手 [+1 晴れ]` | 素早さ比較。自分は使用チームの実数値（スカーフ・ランク・天候特性反映）、相手は 無振り / 準速 / 最速 / スカーフ最速 |
| `help` | 使えるコマンドの一覧 |

`dmg` のオプション（順不同）:

| 書き方 | 意味 |
|---|---|
| `A252` `C252` `S4` / `H252` `B4` `D252` | 攻撃側 / 防御側の努力値（チャンピオンズはポイント） |
| 性格名、持ち物名、特性名 | 持てる側・自然な側に付く。`攻:` / `防:` で明示（例: `防:ずぶとい` `防:持ち物なし`） |
| `+1`〜`+6` / `-1`〜`-6` | 攻撃側の攻撃ランク（技名に直接 `じしん+2` とも書ける） |
| `急所` `ダブル` `シングル` `無振り` `持ち物なし` | そのまま |
| `壁` | 防御側の場にリフレクター / ひかりのかべ（オーロラベール）。0.5 倍、ダブルは 2732/4096。急所・すりぬけは無視 |
| `HP50%` / `攻:HP30%` | 防御側 / 攻撃側の残り HP。防御側は確定数の開始 HP とマルチスケイル、攻撃側はもうか等のピンチ特性に使う |
| `テラス` / `テラスほのお` / `防:テラスみず` | テラスタル（SV） |
| `ダイマ` / `防:ダイマ` | ダイマックス（剣盾）。HP 2 倍、技はダイマックス技の威力 |
| `晴れ` `雨` `砂嵐` `雪` / `エレキフィールド` `グラスフィールド` `サイコフィールド` `ミストフィールド` | 天候 / フィールド（略称の `サイコ` なども可） |
| `やけど` `まひ`（既定は攻撃側）/ `どく` `もうどく` `ねむり` `こおり`（既定は防御側） | 状態異常。やけどは物理 0.5 倍（こんじょう・からげんきは除外）、どく系は毎ターンの定数ダメージを確定数に織り込む（ポイズンヒール・マジックガードは除く）。からげんき・たたりめ・ベノムショック・きつけ・めざましビンタ・どくぼうそう・ねつぼうそうなども反映。`faster` では `まひ` が相手、`自:まひ` が自分 |

指定しなかった項目は「攻撃側は攻撃技に応じて A or C に全振り、防御側は H 全振り、性格・持ち物・特性は使用率 1 位（無ければ第 1 特性）」で埋め、結果の括弧内に明記します。
名前はカタカナ・ひらがな・前方一致・略称（`メガリザX`、`ガブ` など）で解決し、曖昧なときは候補を返します。

ブラウザの「チャットコマンド」ページで同じ文字列を試せます。ターミナルなら:

```bash
dotnet run --project src/FaraPokemonBattleAssistant.Cli -- "!pokech dmg イエッサン♂ ワイドフォース メガリザードンX サイコフィールド"
```

## FaraBotModerator への組み込み

[docs/bot-integration.md](docs/bot-integration.md) を参照してください。要点:

1. `FaraBotModerator.csproj` から `FaraPokemonBattleAssistant.Core` を参照する（隣に clone して `ProjectReference`、または CI の成果物 `.nupkg`）。
2. 起動時に `DataCatalog` を 1 つ作る。データ元は公開サイトの URL（`https://pokemon.fara-labs.com/data/`）+ ローカルキャッシュ。
3. `OnMessageReceived` で `!pokech` / `!pokesv` / `!pokess` を見つけたら `PokeCommand.ExecuteAsync` の結果をそのまま `SendMessage`。登録・削除は配信者本人とモデレーターだけに許可する。

## 開発

```bash
dotnet build FaraPokemonBattleAssistant.sln
dotnet test tests/FaraPokemonBattleAssistant.Core.Tests
dotnet run --project src/FaraPokemonBattleAssistant.Web      # https://localhost:52017
npm ci && npm run preview                                # Cloudflare と同じ動きで http://localhost:8787
npm run check                                            # dotnet publish + wrangler deploy --dry-run（本番・develop）
npm run test:worker                                      # データ連携 API（worker/index.js）を wrangler dev で確認
```

### 計算で考慮しているもの

- 実数値（種族値・個体値・努力値・性格 1.1/0.9 倍）、ランク補正、急所（スナイパー対応）、タイプ一致、タイプ相性、ダブルの複数対象 0.75 倍、五捨五超入
- 持ち物: こだわり系・いのちのたま・たつじんのおび・タイプ強化・プレート・ちからのハチマキ・ものしりメガネ・パンチグローブ・とつげきチョッキ・しんかのきせき・でんきだま・ふといホネ・ふうせん・ブーストエナジー
- テラスタル: 防御タイプの置き換え、タイプ一致 1.5/2.0 倍（てきおうりょく 2.0/2.25 倍）、威力 60 保証
- ダイマックス: HP 2 倍、ダイマックス技の威力表（かくとう・どくは低めの表）
- サイコショック・サイコブレイク・しんぴのつるぎは相手の「防御」で計算
- 天候: 晴れ・雨のほのお/みず補正、砂嵐のいわ特防 1.5 倍、雪のこおり防御 1.5 倍
- フィールド: エレキ・グラス・サイコの 1.3 倍、ミストのドラゴン半減、グラスのじしん系半減、ワイドフォース（1.5 倍＋全体化）、ライジングボルト、サイコブレイド
- 特性: `src/FaraPokemonBattleAssistant.Core/Battle/DamageCalculator.cs` の `AbilityEffects` と各 `*Modifier` を参照。
  スキン系、てきおうりょく、ちからもち/ヨガパワー、はりきり、ごりむちゅう、テクニシャン、ちからずく、てつのこぶし、がんじょうあご、メガランチャー、かたいツメ、パンクロック、すなのちから、はがねつかい/はがねのせいしん、トランジスタ、りゅうのあぎと、いわはこび、すいほう、サンパワー、ひひいろのこどう、ハドロンエンジン、こだいかっせい/クォークチャージ、いろめがね、ブレインフォース、スナイパー、かたやぶり系、
  防御側: ふゆう・ちょすい・よびみず・かんそうはだ・もらいび・こんがりボディ・ちくでん・ひらいしん・でんきエンジン・そうしょく・ぼうおん・ぼうだん・ふしぎなまもり・だっぴ…の無効化、あついしぼう、たいねつ、もふもふ、きよめのしお、ファーコート、くさのけがわ、マルチスケイル/ファントムガード（満タン想定）、フィルター/ハードロック/プリズムアーマー、こおりのりんぷん、てんねん
- 壁（リフレクター / ひかりのかべ / オーロラベール）
- HP に依存する特性: もうか・しんりょく・げきりゅう・むしのしらせ（HP 1/3 以下）、マルチスケイル / ファントムガード（満タン時の 1 発目のみ）
- 半減きのみ（1 発目のみ 0.5 倍、ホズのみはノーマル技）
- 乱数 16 通りをすべて計算し、確定数（乱数 N 発の確率は残り HP の分布を 1 発ずつ進めて厳密に算出）。どく・もうどく・やけど・くろいヘドロの定数ダメージ、オボンのみ・オレンのみ・フィラのみ等の回復きのみ、たべのこし・くろいヘドロ・グラスフィールドの回復を含む

### まだ無いもの

威力が状況で変わる技（ジャイロボール・はたきおとす等）の個別処理、連続技の回数指定。

## 公開（Cloudflare Workers）

`master` への push（PR のマージ含む）で `deploy.yml` が https://pokemon.fara-labs.com/ に公開します（Worker `fara-pokemon`。設定は `wrangler.jsonc`）。サイト本体は静的アセットで、`/api/*` だけ Worker スクリプト（`worker/index.js`）を通ります。
手動で公開し直すときは **Actions → Deploy to Cloudflare Workers → Run workflow**。

`develop` への push（PR のマージ含む）では `deploy-develop.yml` が確認用の https://pokemon-dev.fara-labs.com/ に公開します（Worker `fara-pokemon-dev`。検索エンジンには載らないよう `X-Robots-Tag: noindex` を付けています）。本番には影響しません。
確認用サイトは Cloudflare Access で自分だけが見られるようにしています（Zero Trust → Access → Applications。許可したメールアドレスに届くコードでログイン）。

初回だけ、リポジトリの **Settings → Secrets and variables → Actions** に次を登録してください（値は fara-portfolio-site と同じものを使えます）。

| タブ | 名前 | 値 |
|---|---|---|
| Secrets | `CLOUDFLARE_API_TOKEN` | Cloudflare の API トークン（テンプレート Edit Cloudflare Workers、ゾーン `fara-labs.com`） |
| Variables | `CLOUDFLARE_ACCOUNT_ID` | Cloudflare のアカウント ID |

### 登録データの連携 API

登録ポケモン・チームを、アクセスキーで読み書きするための API です（`worker/index.js`）。サイトの「データ連携」ページの「API で使うには」にも同じ説明があります。サイトも API の使い手の 1 つで、ボット（FaraBotModerator の `RemoteRosterStore`）はその一例です。
アクセスキーごとに SQLite ストレージの Durable Object（`RosterSlot`）を 1 つ持ち、登録データ（`RosterState` の JSON）を 1 つの文書として保存しています。API はその一部（ポケモン / チーム）を読み書きします。本番と確認用は Worker が別なので、データも別々です。

登録データはゲーム（データセットのキー: `Champions`・`Gen9` など。大文字小文字は区別しない）ごとに分かれているので、本文の `dataSets` はゲームのキーごとのオブジェクトです。
ポケモン・チームの中身は `RegisteredPokemon` / `Team` の JSON（`Id`・`PokemonName`・`Slots`・`EntryId` など先頭が大文字の項目）をそのまま使います。

| メソッド | 内容 |
|---|---|
| `GET /api/my-pokemons` | `200 {"revision": n, "dataSets": {"Champions": {"nextId": n, "pokemons": [...]}, ...}}`（データが無ければ `"dataSets": {}`） |
| `PUT /api/my-pokemons` | 本文 `{"baseRevision": n, "dataSets": {"Champions": {"pokemons": [...], "nextId": n}}}`（`nextId` は省略可）。書いたゲームのポケモンだけを置き換え、**同じ処理の中で**、消えたポケモンをそのゲームのすべてのチームから外し、0 体になったチームを消し、使用中のチームが消えたら使用中を解除します。成功で `200 {"revision": n+1}` |
| `GET /api/my-teams` | `200 {"revision": n, "dataSets": {"Champions": {"teams": [...], "activeTeam": n または null}, ...}}` |
| `PUT /api/my-teams` | 本文 `{"baseRevision": n, "dataSets": {"Champions": {"teams": [...], "activeTeam": n または null}}}`。書いたゲームのチームと使用中チームだけを置き換えます。登録に無いポケモン・7 体以上・0 体のチーム・同じチーム番号や同じポケモンの重複・無いチームを使用中にする、は `400`（`message` に理由）で、何も保存しません。成功で `200 {"revision": n+1}` |
| `DELETE /api/access-key` | このアクセスキーを使えなくし、その置き場を消す（キーの作り直し用）。`204`。以後そのキーはすべての API で `410` |

- `revision` は登録データ全体で 1 つです（どちらの PUT でも増えます）。`baseRevision` が現在と違えば `409 {"revision": 現在, "dataSets": そのリソースの現在の内容}` なので、読み直してからやり直してください。両方を読むときは 2 回の GET の `revision` が同じことを確かめ、違えば読み直します
- 書かなかったゲームは変わりません。登録 ID は使い回さず、`nextId` は「保存していた値・送った `nextId`・一番大きい `Id` + 1」の最大になります
- 本文と保存後の登録データ全体の上限は 256 KB で、超えると `413`
- 認証は `Authorization: Bearer <アクセスキー>`。アクセスキーは 256 bit の乱数を base64url（パディングなし・43 文字）にしたもので、形式が違えば `401`。キーはクライアントが作るもので、作成・登録の API はありません（新しいキーの置き場は空で、`revision` は 0）
- キーの作り直し・データ連携の開始では、新しいキーの空の置き場に `PUT /api/my-pokemons`（`baseRevision` 0）→ `PUT /api/my-teams`（`baseRevision` 1）の順で移してから新しいキーに切り替え、古いキーには `DELETE /api/access-key` を呼びます。新しいキーは移し終わるまで誰も使わないので、途中の状態は見えません（途中で失敗したら新しいキーは使わずに捨てます）
- サーバーはキーを保存せず、`SHA-256(キー)` を Durable Object の名前に使います。キーはログ・URL・エラーメッセージに出しません
- すべての応答に `Cache-Control: no-store`。CORS ヘッダーは付けません（サイトは同じオリジン、ボットはブラウザではないため）
- 以前の `/api/roster` はありません（`404`）
- 確認用サイト（Cloudflare Access の内側）では、`/api/my-pokemons`・`/api/my-teams`・`/api/access-key` を Access の **Bypass** にしないとボットから使えません（キーで認証するので Access は不要）

Cloudflare の DNS に `pokemon` / `pokemon-dev` のレコード（GitHub Pages 時代の CNAME など）が残っていると、カスタムドメインの作成で失敗します。削除してから再実行してください。

## ライセンス

MIT License。ポケモンのデータは [公開データベース](https://pokeapi.co/)（BSD-3）および株式会社ポケモンの公開情報に基づきます。
