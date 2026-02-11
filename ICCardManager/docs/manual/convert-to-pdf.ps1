# マニュアル PDF変換スクリプト（Word経由）
# 使用方法:
#   .\convert-to-pdf.ps1              # 全マニュアルを変換（更新があるもののみ）
#   .\convert-to-pdf.ps1 -Force       # 全マニュアルを強制変換
#   .\convert-to-pdf.ps1 -Target user # ユーザーマニュアルのみ変換
# 前提条件:
#   1. Microsoft Word がインストールされていること（Microsoft 365 等）
#   2. .docx ファイルが生成済みであること（.\convert-to-docx.ps1 を先に実行）
# 処理フロー:
#   .md → .docx（convert-to-docx.ps1）→ .pdf（本スクリプト / Word COM）

param(
    [ValidateSet("all", "user", "user-summary", "admin", "dev")]
    [string]$Target = "all",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# スクリプトのディレクトリを取得
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# マニュアル定義（入力は.docx、出力は.pdf）
$Manuals = @(
    @{
        Name = "ユーザーマニュアル"
        Key = "user"
        Input = "ユーザーマニュアル.docx"
        Output = "ユーザーマニュアル.pdf"
    },
    @{
        Name = "ユーザーマニュアル概要版"
        Key = "user-summary"
        # 概要版は修正版docxが配布対象
        Input = "ユーザーマニュアル概要版（修正版）.docx"
        Output = "ユーザーマニュアル概要版.pdf"
    },
    @{
        Name = "管理者マニュアル"
        Key = "admin"
        Input = "管理者マニュアル.docx"
        Output = "管理者マニュアル.pdf"
    },
    @{
        Name = "開発者ガイド"
        Key = "dev"
        Input = "開発者ガイド.docx"
        Output = "開発者ガイド.pdf"
    }
)

# 対象マニュアルをフィルタ
if ($Target -ne "all") {
    $Manuals = $Manuals | Where-Object { $_.Key -eq $Target }
}

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " マニュアル PDF変換（Word経由）" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# Word COMオブジェクトの作成
Write-Host "[準備] Microsoft Wordの確認..." -ForegroundColor Yellow
$Word = $null
try {
    $Word = New-Object -ComObject Word.Application
    $Word.Visible = $false
    $Word.DisplayAlerts = 0  # wdAlertsNone
    Write-Host "  Word: $($Word.Version)" -ForegroundColor Green
}
catch {
    Write-Host "エラー: Microsoft Wordが見つかりません。" -ForegroundColor Red
    Write-Host ""
    Write-Host "Microsoft Word（Microsoft 365等）がインストールされている必要があります。" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

Write-Host ""

# 変換結果の追跡
$ConvertedCount = 0
$SkippedCount = 0
$ErrorCount = 0

# ExportAsFixedFormat の定数
$wdExportFormatPDF = 17
$wdExportOptimizeForPrint = 0

try {
    # 各マニュアルを処理
    foreach ($Manual in $Manuals) {
        $InputPath = Join-Path $ScriptDir $Manual.Input
        $OutputPath = Join-Path $ScriptDir $Manual.Output

        Write-Host "--------------------------------------" -ForegroundColor Gray
        Write-Host "[$($Manual.Name)]" -ForegroundColor Cyan

        # 入力ファイル（.docx）の確認
        if (-not (Test-Path $InputPath)) {
            Write-Host "  スキップ: .docxファイルが見つかりません" -ForegroundColor Yellow
            Write-Host "    $InputPath" -ForegroundColor Gray
            Write-Host "    → 先に .\convert-to-docx.ps1 を実行してください" -ForegroundColor Gray
            $SkippedCount++
            continue
        }

        # 更新チェック（-Forceでない場合）
        if (-not $Force -and (Test-Path $OutputPath)) {
            $InputInfo = Get-Item $InputPath
            $OutputInfo = Get-Item $OutputPath

            if ($OutputInfo.LastWriteTime -ge $InputInfo.LastWriteTime) {
                Write-Host "  スキップ: 変更なし（.pdfが最新）" -ForegroundColor Gray
                Write-Host "    .docx: $($InputInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor Gray
                Write-Host "    .pdf:  $($OutputInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor Gray
                $SkippedCount++
                continue
            }
        }

        Write-Host "  変換中..." -ForegroundColor Yellow

        try {
            # Wordで.docxを開く
            $Doc = $Word.Documents.Open($InputPath, $false, $true)  # ReadOnly=true

            # ExportAsFixedFormat でPDF出力（SaveAsではなくこちらがPDF出力の正式API）
            $Doc.ExportAsFixedFormat(
                $OutputPath,
                $wdExportFormatPDF,
                $false,                   # OpenAfterExport
                $wdExportOptimizeForPrint  # OptimizeFor
            )
            $Doc.Close($false)  # 保存せずに閉じる

            $FileInfo = Get-Item $OutputPath
            Write-Host "  完了: $($Manual.Output)" -ForegroundColor Green
            Write-Host "    サイズ: $([math]::Round($FileInfo.Length / 1KB, 2)) KB" -ForegroundColor Gray
            $ConvertedCount++
        }
        catch {
            Write-Host "  エラー: 変換に失敗しました" -ForegroundColor Red
            Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
            $ErrorCount++
        }
    }
}
finally {
    # Wordを終了（必ず実行）
    if ($Word) {
        Write-Host ""
        Write-Host "[後処理] Wordを終了しています..." -ForegroundColor Gray
        $Word.Quit()
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($Word) | Out-Null
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

# 結果サマリ
Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " 完了!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  変換: $ConvertedCount 件" -ForegroundColor $(if ($ConvertedCount -gt 0) { "Green" } else { "Gray" })
Write-Host "  スキップ: $SkippedCount 件" -ForegroundColor Gray
if ($ErrorCount -gt 0) {
    Write-Host "  エラー: $ErrorCount 件" -ForegroundColor Red
}
Write-Host ""

# エラーがあった場合は終了コード1
if ($ErrorCount -gt 0) {
    exit 1
}
