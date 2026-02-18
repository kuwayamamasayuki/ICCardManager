# ICCardManager 配布手順書

## 概要

本ドキュメントでは、ICCardManager の配布用インストーラーの作成手順と、エンドユーザーへの配布方法について説明します。

## ビルド環境要件

### 開発マシン
- Windows 10/11 (64-bit)
- .NET SDK（.NET Framework 4.8対応版）または Visual Studio 2022
- [Inno Setup 6](https://jrsoftware.org/isinfo.php)（インストーラー作成用）

### ビルドコマンド

```bash
# プロジェクトディレクトリに移動
cd src/ICCardManager

# リリースビルド（配布用）
dotnet publish -c Release
```

### ビルドオプションの説明

| オプション | 説明 |
|-----------|------|
| `-c Release` | リリース構成でビルド（最適化有効、PDBなし） |

## 出力ファイル構成

ビルド成功後、以下のファイルが `bin/Release/net48/publish/` に生成されます：

```
publish/
├── ICCardManager.exe          # メイン実行ファイル
├── *.dll                      # 依存ライブラリ
├── appsettings.json           # 設定ファイル
├── x86/                       # 32bit ネイティブDLL
│   └── SQLite.Interop.dll
└── Resources/
    ├── Sounds/
    │   ├── error.wav          # エラー音
    │   ├── lend.wav           # 貸出音
    │   ├── return.wav         # 返却音
    │   └── warning.wav        # 警告音
    └── Templates/
        └── 物品出納簿テンプレート.xlsx  # 月次帳票テンプレート
```

### ファイルサイズの目安

| ファイル | サイズ目安 |
|----------|-----------|
| ICCardManager.exe | 約 1 MB |
| 依存DLL合計 | 約 15-20 MB |
| Resources/ 合計 | 約 100 KB |
| インストーラー | 約 10 MB |

> **注記**: .NET Framework 4.8はWindows 10/11にプリインストールされているため、
> 配布先のPCに追加のランタイムインストールは通常不要です。

## インストーラーの作成

### 前提条件

- [Inno Setup 6](https://jrsoftware.org/isinfo.php) がインストールされていること
- Inno Setup のパスが環境変数に登録されていること（通常はインストール時に自動設定）

### 作成手順

#### PowerShellスクリプトを使用（推奨）

```powershell
# installerディレクトリに移動
cd installer

# インストーラーをビルド（アプリケーションのビルドも含む）
.\build-installer.ps1

# または、ビルド済みのpublishフォルダを使用する場合
.\build-installer.ps1 -SkipBuild

# バージョンを明示的に指定する場合
.\build-installer.ps1 -Version "1.0.7"
```

#### バッチファイルを使用

```cmd
cd installer
build-installer.bat
```

### 出力ファイル

```
installer/output/ICCardManager_Setup_1.0.7.exe
```

> **注記**: バージョン番号は `src/ICCardManager/ICCardManager.csproj` の `<Version>` タグから自動取得されます。

### インストーラーの構成内容

インストーラーには以下が含まれます：

| 内容 | 説明 |
|------|------|
| アプリケーション本体 | ICCardManager.exe と依存DLL |
| サウンドファイル | Resources/Sounds/ 配下のWAVファイル |
| テンプレートファイル | Resources/Templates/物品出納簿テンプレート.xlsx |
| ドキュメント | ユーザーマニュアル、管理者マニュアル（md/docx/pdf形式） |
| アイコン | アプリケーションアイコン（app.ico） |

## インストール手順（エンドユーザー向け）

### 動作環境

- **OS**: Windows 10/11 (64-bit)
- **ICカードリーダー**: Sony PaSoRi（RC-S380等）
- **.NET Framework**: 4.8（Windows 10/11にプリインストール済み）
- **ストレージ**: 100MB以上の空き容量

### インストール手順

1. 配布されたインストーラー（`ICCardManager_Setup_x.x.x.exe`）を実行
2. 「WindowsによってPCが保護されました」と表示された場合は「詳細情報」→「実行」をクリック
3. 画面の指示に従ってインストール
4. インストール完了後、デスクトップのショートカットまたはスタートメニューからアプリを起動

### インストール先フォルダ

| 種類 | パス |
|------|------|
| プログラム | `C:\Program Files\ICCardManager\` |
| データベース・設定 | `C:\ProgramData\ICCardManager\` |
| バックアップ | `C:\ProgramData\ICCardManager\backup\` |
| ログ | `C:\ProgramData\ICCardManager\Logs\` |

### 初回起動時の注意

- **データベース**: 初回起動時に `C:\ProgramData\ICCardManager\iccard.db` が自動作成されます

### 作成されるショートカット

インストーラーにより以下のショートカットが自動作成されます：

- **スタートメニュー**: 「交通系ICカード管理システム」グループ
  - アプリケーション本体
  - ドキュメントフォルダ
  - アンインストール
- **デスクトップ**: アプリケーションショートカット（オプション）

## アンインストール

### 手順

1. 「設定」→「アプリ」→「交通系ICカード管理システム」を選択
2. 「アンインストール」をクリック
3. アンインストール完了時にデータ削除オプションが表示されます
   - **すべて削除**: データベース、バックアップ、ログをすべて削除
   - **データのみ残す**: バックアップとログは削除、データベースは残す
   - **何も削除しない**: ユーザーデータをすべて残す

> **注記**: アンインストール後に再インストールする場合、「何も削除しない」を選択すると既存のデータを引き継げます。

## トラブルシューティング

### 起動しない場合

1. **アンチウイルスソフトの確認**
   - 一部のアンチウイルスソフトが誤検知する場合があります
   - 除外設定に `C:\Program Files\ICCardManager\` を追加してください

2. **管理者権限での実行**
   - ICカードリーダーへのアクセスに管理者権限が必要な場合があります
   - 右クリック→「管理者として実行」を試してください

### 帳票出力でエラーが発生する場合

1. **テンプレートファイルの確認**
   - `C:\Program Files\ICCardManager\Resources\Templates\物品出納簿テンプレート.xlsx` が存在するか確認

2. **出力先フォルダのアクセス権限**
   - 出力先フォルダに書き込み権限があるか確認

### ICカードリーダーが認識されない場合

1. **ドライバの確認**
   - Sony PaSoRiの最新ドライバがインストールされているか確認
   - デバイスマネージャーでICカードリーダーが認識されているか確認

2. **他のアプリケーションとの競合**
   - FeliCaポートソフトウェアなど、他のICカード関連アプリを終了

## バージョン管理

### バージョン番号の規則

`vX.Y.Z` 形式を使用:
- **X**: メジャーバージョン（大きな機能追加・変更）
- **Y**: マイナーバージョン（機能追加・改善）
- **Z**: パッチバージョン（バグ修正）

### バージョン管理ファイル

バージョン番号は以下のファイルで一元管理されます：

| ファイル | 用途 |
|----------|------|
| `src/ICCardManager/ICCardManager.csproj` | アプリケーションバージョン（Version, FileVersion, AssemblyVersion） |
| `installer/ICCardManager.iss` | インストーラーバージョン（csprojから自動取得） |

### 更新履歴の管理

リリースごとに以下を記録:
- バージョン番号
- リリース日
- 変更内容の概要
- 既知の問題（あれば）

更新履歴は `README.md` の「更新履歴」セクションに記載します。

## リリース手順

リリースは2つのフェーズに分けて実施します。

### Phase 1: ソース更新（Claude Code / WSL）

以下の作業はClaude Code（WSL環境）から実行できます。

1. `src/ICCardManager/ICCardManager.csproj` の `<Version>`、`<FileVersion>`、`<AssemblyVersion>` を更新
2. `README.md` の「更新履歴」セクションに新バージョンのエントリを追加
3. マニュアル `.md` ファイルの `**バージョン**: X.Y.Z` 行を更新（GitHub閲覧用）
4. コミット・プッシュ → PR → mainにマージ
5. タグ作成・プッシュ: `git tag vX.Y.Z && git push origin vX.Y.Z`
6. `gh release create vX.Y.Z` でリリースノートを作成

### Phase 2: インストーラービルド（Windows PowerShell）

> **重要**: 以下は必ずWindowsネイティブのPowerShellから実行してください。
> WSLから `powershell.exe` を呼び出すと日本語ファイル名が文字化けし、
> マニュアル変換が失敗します。

7. Windows PowerShell を開き、`installer/` ディレクトリに移動
8. `.\build-installer.ps1` を実行（マニュアル変換 + インストーラー作成を自動実行）
9. `gh release upload vX.Y.Z .\output\ICCardManager_Setup_X.Y.Z.exe` でインストーラーをアップロード

> **補足**: `build-installer.ps1` は `.csproj` からバージョンを自動取得し、
> マニュアルの `.docx` に正しいバージョンを注入します（`-Force -Version` オプション）。
> マニュアル `.md` のバージョン文字列が未更新でも、`.docx` には正しいバージョンが反映されます。

## セキュリティに関する注意

- データベース（`iccard.db`）には個人情報（職員名、ICカードIDm）が含まれます
- `C:\ProgramData\ICCardManager\` フォルダへの適切なアクセス制御を行ってください
- バックアップファイルも同様に管理してください

---

*このドキュメントは ICCardManager Issue #30 の一部として作成され、Issue #421 でインストーラー対応に更新されました。*
