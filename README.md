# FaraPokemonAssistance

ポケモンダメージ計算を行うC#/.NET Blazor APIプロジェクト

## 機能

- CSVファイルベースのデータ管理（データベース不要）
- 持ち物効果の考慮
- タイプ相性（弱点・効果なし・いまひとつ）の計算
- ポケモンの種族値・個体値・努力値を考慮した実数値計算
- 世代別データ対応（Gen1-9）
- RESTful API エンドポイント

## 技術スタック

- C# / .NET 8.0
- ASP.NET Core Web API
- Blazor Server（管理画面用）
- CSV データファイル

## セットアップ手順

### 前提条件

- .NET 8.0 SDK
- Git

### インストール

1. リポジトリをクローン
```bash
git clone https://github.com/fara1991/fara-pokemon-assistance.git
cd fara-pokemon-assistance
```

2. 依存関係の復元
```bash
dotnet restore
```

3. プロジェクトのビルド
```bash
dotnet build
```

4. アプリケーションの実行
```bash
dotnet run
```

5. ブラウザで以下のURLにアクセス
- API Swagger: `https://localhost:5001/swagger`
- Blazor管理画面: `https://localhost:5001`

## API エンドポイント

### ダメージ計算

```http
POST /api/damage/calculate
Content-Type: application/json

{
  "attackerPokemonId": 1,
  "defenderPokemonId": 2,
  "moveId": 1,
  "generation": 9,
  "attacker": {
    "level": 50,
    "ivs": { "hp": 31, "attack": 31, "defense": 31, "spAttack": 31, "spDefense": 31, "speed": 31 },
    "evs": { "hp": 0, "attack": 252, "defense": 0, "spAttack": 0, "spDefense": 0, "speed": 252 },
    "nature": "Adamant",
    "itemId": 1
  },
  "defender": {
    "level": 50,
    "ivs": { "hp": 31, "attack": 31, "defense": 31, "spAttack": 31, "spDefense": 31, "speed": 31 },
    "evs": { "hp": 252, "attack": 0, "defense": 252, "spAttack": 0, "spDefense": 0, "speed": 0 },
    "nature": "Bold",
    "itemId": 2
  }
}
```

### ポケモン一覧取得

```http
GET /api/pokemon?generation=9
```

### 技一覧取得

```http
GET /api/moves?generation=9
```

### アイテム一覧取得

```http
GET /api/items?generation=9
```

## データ構造

### CSVファイル構成

- `Data/Gen{X}/pokemon.csv` - ポケモンの基本情報・種族値
- `Data/Gen{X}/moves.csv` - 技の情報
- `Data/Gen{X}/items.csv` - アイテム情報
- `Data/Gen{X}/type_effectiveness.csv` - タイプ相性表
- `Data/natures.csv` - 性格補正（全世代共通）

### ダメージ計算式

第9世代の計算式を基準とし、世代に応じて調整：

```
ダメージ = ((((レベル × 2 ÷ 5 + 2) × 威力 × 攻撃 ÷ 防御) ÷ 50) + 2) × 補正
```

補正要素：
- タイプ相性
- タイプ一致ボーナス
- 急所
- 乱数（0.85~1.00）
- アイテム効果
- その他の効果

## プロジェクト構造

```
fara-pokemon-assistance/
├── Controllers/         # API コントローラー
├── Models/             # データモデル
├── Services/           # ビジネスロジック
├── Data/               # CSV データファイル
│   ├── Gen1/
│   ├── Gen2/
│   └── ...
├── Components/         # Blazor コンポーネント
└── Pages/              # Blazor ページ
```

## 開発

### 新しい世代の追加

1. `Data/Gen{X}/` フォルダを作成
2. 必要なCSVファイルを配置
3. `GenerationService` を更新

### カスタム計算式の追加

`Services/DamageCalculationService.cs` を編集してください。

## ライセンス

MIT License

## 貢献

Pull Request や Issue の報告を歓迎します。
GitHub Actions により、Issue作成時に自動的に機能実装のPRが作成されます。
