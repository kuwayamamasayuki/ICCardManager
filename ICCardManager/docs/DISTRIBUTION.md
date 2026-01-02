# ICCardManager 配布手順書

## 概要

本ドキュメントでは、ICCardManager の配布用パッケージの作成手順と、エンドユーザーへの配布方法について説明します。

## ビルド環境要件

### 開発マシン
- Windows 10/11 (64-bit)
- .NET SDK（.NET Framework 4.8対応版）または Visual Studio 2022
- Visual Studio 2022 または VS Code（任意）

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
| `-c Release` | リリース構成でビルド（最適化有効） |

## 出力ファイル構成

ビルド成功後、以下のファイルが `bin/Release/net48/publish/` に生成されます：

```
publish/
├── ICCardManager.exe          # メイン実行ファイル
├── ICCardManager.pdb          # デバッグシンボル（配布時は任意）
├── *.dll                      # 依存ライブラリ
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

> **注記**: .NET Framework 4.8はWindows 10/11にプリインストールされているため、
> 配布先のPCに追加のランタイムインストールは通常不要です。

## 配布パッケージの作成

### 手順

1. **ビルドの実行**
   ```bash
   dotnet publish -c Release
   ```

2. **出力ディレクトリの確認**
   ```
   bin/Release/net48/publish/
   ```

3. **配布用ZIPの作成**
   - `publish` フォルダの内容をZIPに圧縮
   - ファイル名例: `ICCardManager_v1.0.0.zip`

4. **配布物の構成**
   ```
   ICCardManager_v1.0.0.zip
   ├── ICCardManager.exe
   └── Resources/
       ├── Sounds/
       │   ├── error.wav
       │   ├── lend.wav
       │   ├── return.wav
       │   └── warning.wav
       └── Templates/
           └── 物品出納簿テンプレート.xlsx
   ```

> **注記**: `ICCardManager.pdb` は通常配布には含めません（デバッグ用途のみ）。

## インストール手順（エンドユーザー向け）

### 動作環境

- **OS**: Windows 10/11 (64-bit)
- **ICカードリーダー**: Sony PaSoRi（RC-S380等）
- **.NET Framework**: 4.8（Windows 10/11にプリインストール済み）

### インストール手順

1. 配布されたZIPファイルを任意のフォルダに展開
   - 推奨: `C:\ICCardManager\` または `D:\ICCardManager\`

2. フォルダ構成を確認
   ```
   ICCardManager/
   ├── ICCardManager.exe
   └── Resources/
       ├── Sounds/
       └── Templates/
   ```

3. `ICCardManager.exe` を実行

### 初回起動時の注意

- **Windows SmartScreen警告**: 初回起動時に「WindowsによってPCが保護されました」と表示される場合があります
  - 「詳細情報」→「実行」をクリックして起動

- **データベース**: 初回起動時に自動作成されます
  - 場所: 実行ファイルと同じフォルダ内の `iccard.db`

### ショートカットの作成（任意）

デスクトップやスタートメニューにショートカットを作成する場合：

1. `ICCardManager.exe` を右クリック
2. 「送る」→「デスクトップ（ショートカットを作成）」

## トラブルシューティング

### 起動しない場合

1. **Resourcesフォルダの確認**
   - `ICCardManager.exe` と同じ階層に `Resources` フォルダがあるか確認
   - 音声ファイル（4つのWAVファイル）が存在するか確認

2. **アンチウイルスソフトの確認**
   - 一部のアンチウイルスソフトが誤検知する場合があります
   - 除外設定に追加してください

3. **管理者権限での実行**
   - ICカードリーダーへのアクセスに管理者権限が必要な場合があります
   - 右クリック→「管理者として実行」を試してください

### 帳票出力でエラーが発生する場合

1. **テンプレートファイルの確認**
   - `Resources/Templates/物品出納簿テンプレート.xlsx` が存在するか確認

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

### 更新履歴の管理

リリースごとに以下を記録:
- バージョン番号
- リリース日
- 変更内容の概要
- 既知の問題（あれば）

## セキュリティに関する注意

- データベース（`iccard.db`）には個人情報（職員名、ICカードIDm）が含まれます
- 適切なアクセス制御を行ってください
- バックアップファイルも同様に管理してください

---

*このドキュメントは ICCardManager Issue #30 の一部として作成されました。*
