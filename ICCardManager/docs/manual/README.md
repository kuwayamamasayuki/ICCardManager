# マニュアル

このディレクトリには、交通系ICカード管理システムのマニュアル一式が含まれています。

## マニュアル一覧

| ファイル | 対象 | 説明 |
|----------|------|------|
| [ユーザーマニュアル.md](ユーザーマニュアル.md) | 一般ユーザー | ICカードの貸出・返却操作 |
| [管理者マニュアル.md](管理者マニュアル.md) | システム管理者 | 職員・カード管理、バックアップ、設定 |
| [開発者ガイド.md](開発者ガイド.md) | 開発者 | システム構成、コーディング規約、セキュリティ |

## ファイル構成

| ファイル | 説明 |
|----------|------|
| `ユーザーマニュアル.md` | ユーザーマニュアル本体（Markdown形式） |
| `管理者マニュアル.md` | 管理者マニュアル本体（Markdown形式） |
| `開発者ガイド.md` | 開発者ガイド本体（Markdown形式） |
| `convert-to-docx.ps1` | Word形式への変換スクリプト（PowerShell） |
| `convert-to-docx.bat` | Word形式への変換スクリプト（バッチファイル） |

## Word形式への変換

マニュアルはMarkdown形式で管理されています。Word形式が必要な場合は、以下の手順で変換してください。

### 前提条件

[pandoc](https://pandoc.org/) がインストールされている必要があります。

```powershell
# wingetでインストール
winget install pandoc

# または、公式サイトからダウンロード
# https://pandoc.org/installing.html
```

### 変換手順

```powershell
# 全マニュアルを変換（更新があるもののみ）
.\convert-to-docx.ps1

# 全マニュアルを強制変換
.\convert-to-docx.ps1 -Force

# 特定のマニュアルのみ変換
.\convert-to-docx.ps1 -Target user   # ユーザーマニュアル
.\convert-to-docx.ps1 -Target admin  # 管理者マニュアル
.\convert-to-docx.ps1 -Target dev    # 開発者ガイド
```

バッチファイルを使用する場合:

```batch
rem 全マニュアルを変換（更新があるもののみ）
convert-to-docx.bat

rem 全マニュアルを強制変換
convert-to-docx.bat /force

rem 特定のマニュアルのみ変換
convert-to-docx.bat user   # ユーザーマニュアル
convert-to-docx.bat admin  # 管理者マニュアル
convert-to-docx.bat dev    # 開発者ガイド
```

### 出力ファイル

変換後、以下のファイルが同じディレクトリに生成されます（更新があるもののみ）:

- `ユーザーマニュアル.docx`
- `管理者マニュアル.docx`
- `開発者ガイド.docx`

> **注意**: `.md` ファイルより `.docx` ファイルの方が新しい場合は、変換がスキップされます。強制的に変換する場合は `-Force` オプションを使用してください。

## マニュアルの更新

マニュアルの内容を更新する場合は、対応する `.md` ファイルを編集してください。
Word形式が必要な場合は、編集後に変換スクリプトを実行して再生成してください。

## 注意事項

- `.docx` ファイルはGitの管理対象外です
- マニュアルの原本は `.md` ファイルです
- Word形式は配布用に都度生成してください
