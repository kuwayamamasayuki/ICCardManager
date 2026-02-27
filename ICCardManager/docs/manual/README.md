# マニュアル

このディレクトリには、交通系ICカード管理システム：ピッすいのマニュアル一式が含まれています。

## マニュアル一覧

| ファイル | 対象 | 説明 |
|----------|------|------|
| [はじめに.md](はじめに.md) | 全員 | マニュアルの案内（どのマニュアルを読むべきか） |
| [ユーザーマニュアル.md](ユーザーマニュアル.md) | 一般ユーザー | ICカードの貸出・返却操作（詳細版） |
| [ユーザーマニュアル概要版.md](ユーザーマニュアル概要版.md) | 一般ユーザー | ICカードの貸出・返却操作（2ページの概要版） |
| [管理者マニュアル.md](管理者マニュアル.md) | システム管理者 | 職員・カード管理、バックアップ、設定 |
| [開発者ガイド.md](開発者ガイド.md) | 開発者 | システム構成、コーディング規約、セキュリティ |

## ファイル構成

| ファイル | 説明 |
|----------|------|
| `はじめに.md` | はじめに（マニュアルの案内） |
| `ユーザーマニュアル.md` | ユーザーマニュアル本体（Markdown形式） |
| `管理者マニュアル.md` | 管理者マニュアル本体（Markdown形式） |
| `開発者ガイド.md` | 開発者ガイド本体（Markdown形式） |
| `convert-to-docx.ps1` | Word形式への変換スクリプト（PowerShell） |
| `convert-to-docx.bat` | Word形式への変換スクリプト（バッチファイル） |
| `convert-to-pdf.ps1` | PDF形式への変換スクリプト（PowerShell） |

## Word形式への変換

マニュアルはMarkdown形式で管理されています。Word形式が必要な場合は、以下の手順で変換してください。

### 前提条件

#### 必須: pandoc

[pandoc](https://pandoc.org/) がインストールされている必要があります。

```powershell
# wingetでインストール
winget install pandoc

# または、公式サイトからダウンロード
# https://pandoc.org/installing.html
```

#### オプション: mermaid-filter（Mermaid図のレンダリング用）

開発者ガイドなどに含まれるMermaid図を画像としてレンダリングするには、[mermaid-filter](https://github.com/raghur/mermaid-filter) が必要です。

```powershell
# Node.jsがインストールされている環境で
npm install -g mermaid-filter
```

> **注意**: mermaid-filterがインストールされていない場合でも変換は可能ですが、Mermaid図はコードブロックのまま出力されます。

### 変換手順

```powershell
# 全マニュアルを変換（更新があるもののみ、Mermaid図もレンダリング）
.\convert-to-docx.ps1

# 全マニュアルを強制変換
.\convert-to-docx.ps1 -Force

# 特定のマニュアルのみ変換
.\convert-to-docx.ps1 -Target intro  # はじめに
.\convert-to-docx.ps1 -Target user   # ユーザーマニュアル
.\convert-to-docx.ps1 -Target admin  # 管理者マニュアル
.\convert-to-docx.ps1 -Target dev    # 開発者ガイド

# Mermaidフィルターを使用しない（高速変換）
.\convert-to-docx.ps1 -NoMermaid
```

バッチファイルを使用する場合:

```batch
rem 全マニュアルを変換（更新があるもののみ、Mermaid図もレンダリング）
convert-to-docx.bat

rem 全マニュアルを強制変換
convert-to-docx.bat /force

rem 特定のマニュアルのみ変換
convert-to-docx.bat intro  rem はじめに
convert-to-docx.bat user   rem ユーザーマニュアル
convert-to-docx.bat admin  rem 管理者マニュアル
convert-to-docx.bat dev    rem 開発者ガイド

rem Mermaidフィルターを使用しない（高速変換）
convert-to-docx.bat /nomermaid
```

### 出力ファイル

変換後、以下のファイルが同じディレクトリに生成されます（更新があるもののみ）:

- `はじめに.docx`
- `ユーザーマニュアル.docx`
- `管理者マニュアル.docx`
- `開発者ガイド.docx`

> **注意**: `.md` ファイルより `.docx` ファイルの方が新しい場合は、変換がスキップされます。強制的に変換する場合は `-Force` オプションを使用してください。

## PDF形式への変換

マニュアルをPDF形式で出力することもできます。`.docx` → `.pdf` の変換を Microsoft Word 経由で行います。

### 追加の前提条件

#### 必須: Microsoft Word

PDF出力には Microsoft Word（Microsoft 365 等）が必要です。Word の COM オートメーションで `.docx` を PDF に変換します。

#### 必須: .docx ファイル

PDF変換の入力は `.docx` ファイルです。先に `.\convert-to-docx.ps1` を実行して `.docx` を生成してください。

### PDF変換手順

```powershell
# 1. まず .docx を生成（未生成の場合）
.\convert-to-docx.ps1

# 2. .docx → .pdf に変換
.\convert-to-pdf.ps1

# 全マニュアルを強制変換
.\convert-to-pdf.ps1 -Force

# 特定のマニュアルのみ変換
.\convert-to-pdf.ps1 -Target intro  # はじめに
.\convert-to-pdf.ps1 -Target user   # ユーザーマニュアル
.\convert-to-pdf.ps1 -Target admin  # 管理者マニュアル
.\convert-to-pdf.ps1 -Target dev    # 開発者ガイド
```

### PDF出力ファイル

変換後、以下のファイルが同じディレクトリに生成されます：

- `はじめに.pdf`
- `ユーザーマニュアル.pdf`
- `ユーザーマニュアル概要版.pdf`
- `管理者マニュアル.pdf`
- `開発者ガイド.pdf`

## マニュアルの更新

マニュアルの内容を更新する場合は、対応する `.md` ファイルを編集してください。
Word形式が必要な場合は、編集後に変換スクリプトを実行して再生成してください。

## 注意事項

- `.docx` / `.pdf` ファイルはGitの管理対象外です
- マニュアルの原本は `.md` ファイルです
- Word形式・PDF形式は配布用に都度生成してください
- 開発者ガイドのMermaid図をレンダリングするには `mermaid-filter` が必要です
