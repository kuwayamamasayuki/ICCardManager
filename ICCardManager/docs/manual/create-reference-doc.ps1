# Word リファレンスドキュメント作成スクリプト
# pandocの--reference-docオプションで使用するテンプレートを作成
#
# このスクリプトは以下の設定を持つリファレンスドキュメントを生成します:
#   - 余白: やや狭い (上下左右 1.27cm / 0.5インチ)
#   - ページ番号: フッター中央に表示
#   - 用紙サイズ: A4
#   - テーブルスタイル: 黒・単線・0.5ptの罫線（Issue #600: 印刷時に罫線が消えないようにする）
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

    # PageNumbers.Addを使用してページ番号を中央下に配置
    # wdAlignPageNumberCenter = 1, wdAlignPageNumberLeft = 0, wdAlignPageNumberRight = 2
    $Footer.PageNumbers.Add(1, $true) | Out-Null  # 第1引数: 配置(1=中央), 第2引数: 最初のページにも表示

    Write-Host "  ページ番号: フッター中央" -ForegroundColor Gray

    # サンプルテキストを追加（pandocがスタイルを認識するため）
    Write-Host "[設定] サンプルスタイルを追加中..." -ForegroundColor Yellow
    $Doc.Content.Text = "Sample Document for Pandoc Reference`r`n"

    # Issue #600: テーブルスタイルの設定（印刷時に罫線が表示されるようにする）
    # pandocは reference.docx 内の "Table" という名前のテーブルスタイルを参照して出力する。
    # "Table" スタイルが存在しない場合、罫線なしのデフォルトが適用され印刷時に消える。
    # 参考: https://github.com/jgm/pandoc/issues/3275
    Write-Host "[設定] テーブルスタイルを設定中..." -ForegroundColor Yellow

    # 罫線パラメータ（黒・単線・0.5pt）
    # wdLineStyleSingle = 1, wdLineWidth050pt = 4
    # 外枠: wdBorderTop=-1, wdBorderLeft=-2, wdBorderBottom=-3, wdBorderRight=-4
    # 内側: wdBorderHorizontal=-5, wdBorderVertical=-6
    $LineStyle = 1  # wdLineStyleSingle（単線）
    $LineWidth = 4  # wdLineWidth050pt（0.5pt）

    # "Table" テーブルスタイルを作成（pandocが参照する名前）
    # wdStyleTypeTable = 3
    try {
        $TableStyle = $Doc.Styles.Add("Table", 3)
        foreach ($BorderId in @(-1, -2, -3, -4, -5, -6)) {
            $Border = $TableStyle.Table.Borders.Item($BorderId)
            $Border.LineStyle = $LineStyle
            $Border.LineWidth = $LineWidth
            $Border.Color = 0  # 黒（wdColorBlack）
        }
        Write-Host "  テーブルスタイル 'Table': 黒・単線・0.5pt（全罫線）" -ForegroundColor Gray
    }
    catch {
        Write-Host "  警告: テーブルスタイル 'Table' の作成をスキップ（convert-to-docx.ps1の後処理で代替）" -ForegroundColor Yellow
    }

    # ドキュメント末尾にカーソルを移動してサンプルテーブルを挿入
    $Range = $Doc.Content
    $Range.Collapse(0)  # wdCollapseEnd = 0
    $Range.InsertParagraphAfter()
    $Range = $Doc.Content
    $Range.Collapse(0)

    # サンプルテーブルを作成（2行×3列）
    $Table = $Doc.Tables.Add($Range, 2, 3)

    # サンプルテーブルにもインライン罫線を設定（視覚的な確認用）
    foreach ($BorderId in @(-1, -2, -3, -4, -5, -6)) {
        $Border = $Table.Borders.Item($BorderId)
        $Border.LineStyle = $LineStyle
        $Border.LineWidth = $LineWidth
        $Border.Color = 0  # 黒（wdColorBlack）
    }

    # サンプルデータ
    $Table.Cell(1, 1).Range.Text = "Header 1"
    $Table.Cell(1, 2).Range.Text = "Header 2"
    $Table.Cell(1, 3).Range.Text = "Header 3"
    $Table.Cell(2, 1).Range.Text = "Data 1"
    $Table.Cell(2, 2).Range.Text = "Data 2"
    $Table.Cell(2, 3).Range.Text = "Data 3"

    Write-Host "  サンプルテーブル: 黒・単線・0.5pt（全罫線）" -ForegroundColor Gray

    # 保存
    Write-Host "[保存] ファイルを保存中..." -ForegroundColor Yellow

    # 既存ファイルがあれば削除
    if (Test-Path $OutputPath) {
        Remove-Item $OutputPath -Force
    }

    # wdFormatDocumentDefault = 16 (docx形式)
    # 注意: COM Interopでは[ref]キャストではなく直接値を渡す
    $Doc.SaveAs($OutputPath, 16)
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
