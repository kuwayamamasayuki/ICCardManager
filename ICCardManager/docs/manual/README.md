# ユーザーマニュアル

このディレクトリには、交通系ICカード管理システムの一般ユーザー向けマニュアルが含まれています。

## ファイル構成

| ファイル | 説明 |
|----------|------|
| `ユーザーマニュアル.md` | マニュアル本体（Markdown形式） |
| `ユーザーマニュアル.docx` | マニュアル本体（Word形式）※要生成 |
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
# PowerShellスクリプトを使用
.\convert-to-docx.ps1

# または、バッチファイルを使用
.\convert-to-docx.bat
```

### 出力ファイル

変換後、`ユーザーマニュアル.docx` が同じディレクトリに生成されます。

## マニュアルの更新

マニュアルの内容を更新する場合は、`ユーザーマニュアル.md` を編集してください。
Word形式が必要な場合は、編集後に変換スクリプトを実行して再生成してください。

## 注意事項

- `.docx` ファイルはGitの管理対象外です
- マニュアルの原本は `.md` ファイルです
- Word形式は配布用に都度生成してください
