# Word リファレンスドキュメント作成スクリプト
# pandocの--reference-docオプションで使用するテンプレートを作成
#
# このスクリプトは以下の設定を持つリファレンスドキュメントを生成します:
#   - 余白: やや狭い (上下左右 1.27cm / 0.5インチ)
#   - ページ番号: フッター中央に表示
#   - 用紙サイズ: A4
#
# 使用方法:
#   .\create-reference-doc.ps1
#
# 前提条件:
#   Microsoft Word がインストールされていること

$ErrorActionPreference = "Stop"

# スクリプトのディレクトリを取得
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$OutputPath = Join-Path $ScriptDir "reference.docx"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " リファレンスドキュメント作成" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# Wordアプリケーションを起動
Write-Host "[準備] Microsoft Word を起動中..." -ForegroundColor Yellow
try {
    $Word = New-Object -ComObject Word.Application
    $Word.Visible = $false
}
catch {
    Write-Host "エラー: Microsoft Word が見つかりません。" -ForegroundColor Red
    Write-Host "Word がインストールされていることを確認してください。" -ForegroundColor Yellow
    exit 1
}

try {
    # 新規ドキュメント作成
    Write-Host "[作成] 新規ドキュメントを作成中..." -ForegroundColor Yellow
    $Doc = $Word.Documents.Add()

    # ページ設定
    Write-Host "[設定] ページ設定を適用中..." -ForegroundColor Yellow
    $PageSetup = $Doc.PageSetup

    # 用紙サイズ: A4
    $PageSetup.PaperSize = 7  # wdPaperA4

    # 余白: やや狭い (1.27cm = 36ポイント)
    # Word の「やや狭い」は上下左右 1.27cm (0.5インチ)
    $NarrowMargin = 36  # ポイント (1.27cm ≈ 36pt)
    $PageSetup.TopMargin = $NarrowMargin
    $PageSetup.BottomMargin = $NarrowMargin
    $PageSetup.LeftMargin = $NarrowMargin
    $PageSetup.RightMargin = $NarrowMargin

    Write-Host "  余白: 上下左右 1.27cm (やや狭い)" -ForegroundColor Gray

    # フッターにページ番号を追加
    Write-Host "[設定] ページ番号を追加中..." -ForegroundColor Yellow

    # フッターセクションを取得
    $Section = $Doc.Sections.Item(1)
    $Footer = $Section.Footers.Item(1)  # wdHeaderFooterPrimary = 1

    # フッターにページ番号フィールドを挿入（中央揃え）
    $Footer.Range.ParagraphFormat.Alignment = 1  # wdAlignParagraphCenter
    $Footer.Range.Fields.Add($Footer.Range, -1, "PAGE", $false)  # wdFieldPage = 33, but -1 for auto

    Write-Host "  ページ番号: フッター中央" -ForegroundColor Gray

    # サンプルテキストを追加（pandocがスタイルを認識するため）
    Write-Host "[設定] サンプルスタイルを追加中..." -ForegroundColor Yellow
    $Doc.Content.Text = "Sample Document for Pandoc Reference`r`n"

    # 保存
    Write-Host "[保存] ファイルを保存中..." -ForegroundColor Yellow

    # 既存ファイルがあれば削除
    if (Test-Path $OutputPath) {
        Remove-Item $OutputPath -Force
    }

    # wdFormatDocumentDefault = 16 (docx形式)
    $Doc.SaveAs2([ref]$OutputPath, [ref]16)
    $Doc.Close()

    Write-Host ""
    Write-Host "======================================" -ForegroundColor Cyan
    Write-Host " 完了!" -ForegroundColor Green
    Write-Host "======================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "リファレンスドキュメントを作成しました:" -ForegroundColor Green
    Write-Host "  $OutputPath" -ForegroundColor White
    Write-Host ""
    Write-Host "次のステップ:" -ForegroundColor Yellow
    Write-Host "  1. convert-to-docx.ps1 を実行してマニュアルを変換" -ForegroundColor White
    Write-Host "  2. 必要に応じて reference.docx をWordで開いてスタイルを調整" -ForegroundColor White
}
catch {
    Write-Host ""
    Write-Host "エラーが発生しました:" -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    # Wordを終了
    if ($Word) {
        $Word.Quit()
        [System.Runtime.Interopservices.Marshal]::ReleaseComObject($Word) | Out-Null
    }
}
