# ICCardManager - 交通系ICカード管理システム

複数の交通系ICカード（はやかけん、nimoca、SUGOCA等）を複数職員でシェア利用する際の出納記録を管理するWindowsデスクトップアプリケーションです。

![メイン画面](docs/screenshots/main.png)

## 主な機能

| 機能 | 説明 |
|------|------|
| 🎫 **貸出・返却管理** | 職員証タッチ → ICカードタッチで簡単操作 |
| 📊 **物品出納簿自動生成** | 月次帳票をExcel形式で出力 |
| 🔍 **利用履歴照会** | 過去の利用記録を検索・閲覧 |
| ⚡ **30秒ルール** | 誤操作時に30秒以内の再タッチで取消可能 |
| 🚌 **バス利用対応** | バス停名の手入力・オートコンプリート |
| ♿ **アクセシビリティ** | 音・色・アイコン・テキストの多重通知 |

### スクリーンショット

<details>
<summary>貸出・返却画面</summary>

| 貸出時 | 返却時 |
|--------|--------|
| ![貸出](docs/screenshots/lend.png) | ![返却](docs/screenshots/return.png) |

</details>

<details>
<summary>各種管理画面</summary>

| 履歴照会 | カード管理 | 職員管理 |
|----------|------------|----------|
| ![履歴](docs/screenshots/history.png) | ![カード](docs/screenshots/card.png) | ![職員](docs/screenshots/staff.png) |

</details>

## 動作環境

### ハードウェア要件

| 項目 | 要件 |
|------|------|
| OS | Windows 10/11 (64-bit) |
| CPU | x64プロセッサ |
| メモリ | 4GB以上 |
| ストレージ | 100MB以上の空き容量 |
| ICカードリーダー | Sony PaSoRi (RC-S380等) |

### ソフトウェア要件

| 項目 | 要件 |
|------|------|
| .NET Framework | 4.8（通常Windows 10/11にプリインストール済み） |
| PaSoRiドライバ | [ソニー公式サイト](https://www.sony.co.jp/Products/felica/consumer/support/download/)よりダウンロード |
| Excel | 出力ファイルの閲覧に必要 |

> **Note**: インターネット非接続環境で動作します。クラウドサービスは利用しません。

## セットアップ

### インストール手順

1. 配布されたZIPファイルを任意のフォルダに展開
   ```
   推奨: C:\ICCardManager\ または D:\ICCardManager\
   ```

2. フォルダ構成を確認
   ```
   ICCardManager/
   ├── ICCardManager.exe      # メインアプリケーション
   └── Resources/
       ├── Sounds/            # 通知音
       └── Templates/         # 帳票テンプレート
   ```

3. `ICCardManager.exe` を実行

### 初回起動時の注意

- **Windows SmartScreen警告**: 「詳細情報」→「実行」をクリック
- **データベース**: `iccard.db` が自動作成されます

### 初期設定

1. **職員の登録**: 「職員管理」から職員証を登録
2. **ICカードの登録**: 「カード管理」からICカードを登録
3. **残額警告の設定**: 「設定」から警告閾値を設定（デフォルト: 1,000円）

## 使い方

### 基本的な操作フロー

```
┌─────────────┐     ┌─────────────┐     ┌─────────────┐
│  職員証     │ ──> │  ICカード   │ ──> │  処理完了   │
│  タッチ     │     │  タッチ     │     │  (貸出/返却)│
└─────────────┘     └─────────────┘     └─────────────┘
     ↑                                        │
     └────────────────────────────────────────┘
                    60秒でタイムアウト
```

### 貸出・返却の判定

| 状態 | 操作結果 |
|------|----------|
| カードが未貸出 | → 貸出処理 |
| カードが貸出中 | → 返却処理（利用履歴を記録） |

### 30秒ルール（誤操作防止）

30秒以内に同じカードを再タッチすると、直前の操作を取り消せます。

| 直前の操作 | 再タッチ結果 |
|------------|--------------|
| 貸出 | → 貸出取消（返却） |
| 返却 | → 返却取消（再貸出） |

### 画面説明

| 画面 | 機能 |
|------|------|
| メイン画面 | 貸出・返却操作、状態表示 |
| 履歴照会 | 期間・カード・職員で絞り込み検索 |
| カード管理 | ICカードの登録・編集・削除 |
| 職員管理 | 職員の登録・編集・削除 |
| 帳票出力 | 月次物品出納簿の生成 |
| 設定 | 文字サイズ、残額警告、バックアップ設定 |

### 月次帳票（物品出納簿）

1. 「帳票出力」ボタンをクリック
2. 対象月とカードを選択
3. 「出力」ボタンでExcelファイルを生成

## 開発者向け情報

### 技術スタック

| カテゴリ | 技術 | バージョン |
|----------|------|------------|
| 言語 | C# | 10 |
| フレームワーク | .NET Framework | 4.8 |
| UI | WPF | - |
| アーキテクチャ | MVVM | - |
| データベース | SQLite | 3.x |
| ICカード | PC/SC API | - |
| Excel出力 | ClosedXML | 0.102.x |
| MVVMツールキット | CommunityToolkit.Mvvm | 8.x |

### ディレクトリ構成

```
ICCardManager/
├── src/
│   └── ICCardManager/
│       ├── Views/           # XAML (View層)
│       ├── ViewModels/      # ViewModel層
│       ├── Models/          # エンティティ
│       ├── Services/        # ビジネスロジック
│       ├── Data/            # データアクセス層
│       └── Resources/       # リソースファイル
├── tests/
│   └── ICCardManager.Tests/ # 単体テスト
└── docs/                    # 設計書
```

### ビルドコマンド

```bash
# 開発ビルド
dotnet build

# リリースビルド（配布用）
dotnet publish src/ICCardManager/ICCardManager.csproj -c Release

# 出力先: src/ICCardManager/bin/Release/net48/publish/
```

### インストーラーのビルド

配布用インストーラーを作成できます。

#### 前提条件

- [Inno Setup 6](https://jrsoftware.org/isinfo.php) がインストールされていること

#### ビルド手順

```powershell
# PowerShellスクリプトを使用（推奨）
cd installer
.\build-installer.ps1

# または、バッチファイルを使用
.\build-installer.bat

# ビルド済みのpublishフォルダを使用する場合
.\build-installer.ps1 -SkipBuild

# バージョンを指定する場合
.\build-installer.ps1 -Version "1.1.0"
```

#### 出力ファイル

```
installer/output/ICCardManager_Setup_1.0.0.exe
```

### テスト実行

```bash
# 全テスト実行
dotnet test

# 詳細ログ付き
dotnet test --logger "console;verbosity=detailed"

# 特定のテストクラスのみ
dotnet test --filter "FullyQualifiedName~SummaryGeneratorTests"
```

### 開発用設定

開発時の注意事項は [CLAUDE.md](CLAUDE.md) を参照してください。

## ドキュメント

| ドキュメント | 説明 |
|--------------|------|
| [設計書一式](docs/design/) | システム設計書（概要、DB、画面、機能、クラス、シーケンス図） |
| [ユーザーマニュアル](docs/manual/ユーザーマニュアル.md) | 一般ユーザー向け操作マニュアル |
| [管理者マニュアル](docs/manual/管理者マニュアル.md) | システム管理者向けマニュアル |
| [開発者ガイド](docs/manual/開発者ガイド.md) | 開発・運用・保守向けガイド |
| [配布手順書](docs/DISTRIBUTION.md) | ビルド・配布手順 |

## トラブルシューティング

<details>
<summary>ICカードリーダーが認識されない</summary>

1. PaSoRiドライバが正しくインストールされているか確認
2. デバイスマネージャーでICカードリーダーが認識されているか確認
3. 他のICカード関連アプリ（FeliCaポートソフトウェア等）を終了

</details>

<details>
<summary>アプリケーションが起動しない</summary>

1. `Resources` フォルダが `ICCardManager.exe` と同じ階層にあるか確認
2. アンチウイルスソフトの除外設定に追加
3. 右クリック → 「管理者として実行」を試行

</details>

<details>
<summary>帳票出力でエラーが発生する</summary>

1. `Resources/Templates/物品出納簿テンプレート.xlsx` が存在するか確認
2. 出力先フォルダに書き込み権限があるか確認

</details>

## ライセンス

Private - All Rights Reserved

## 更新履歴

| バージョン | 日付 | 内容 |
|------------|------|------|
| 1.0.7 | 2026-01-31 | アンインストール時のデータ削除オプション追加、インストーラーバージョン自動同期 |
| 1.0.6 | 2026-01-31 | ビルド時の警告を解消 |
| 1.0.5 | 2026-01-31 | デフォルトサイズですべてのボタンが表示されるように調整 |
| 1.0.4 | 2026-01-31 | マイグレーションテストの修正 |
| 1.0.3 | 2026-01-31 | 履歴の表示順を物品出納簿に合わせて古い順に変更 |
| 1.0.2 | 2026-01-31 | 残高不足時の特殊処理に対応 |
| 1.0.1 | 2026-01-30 | ポイント還元対応、交通系ICカード払い戻し対応 |
| 1.0.0 | 2024-12 | 初版リリース |
