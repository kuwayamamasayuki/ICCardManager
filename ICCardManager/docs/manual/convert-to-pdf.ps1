# マニュアル PDF変換スクリプト（Word経由）
# 使用方法:
#   .\convert-to-pdf.ps1                      # 全マニュアルを変換（更新があるもののみ）
#   .\convert-to-pdf.ps1 -Force               # 全マニュアルを強制変換
#   .\convert-to-pdf.ps1 -Target user         # ユーザーマニュアルのみ変換
#   .\convert-to-pdf.ps1 -Target user-summary # ユーザーマニュアル概要版のみ変換
#   .\convert-to-pdf.ps1 -Target it           # IT担当者ガイドのみ変換
# 前提条件:
#   1. Microsoft Word がインストールされていること（Microsoft 365 等）
#   2. .docx ファイルが生成済みであること（.\convert-to-docx.ps1 を先に実行）
# 処理フロー:
#   .md → .docx（convert-to-docx.ps1）→ .pdf（本スクリプト / Word COM）
# 終了コード:
#   0 = すべて変換またはスキップ（.pdf が最新）
#   1 = 変換元の .docx が無い・Word が無い・変換に失敗した、のいずれかがあった
#
# 本ファイルは日本語を含むため BOM 付き UTF-8 で保存する（Windows PowerShell 5.1 は BOM の無い .ps1 を
# システムの既定コードページで読み、構文解析に失敗する。Issue #2241）。

param(
    [ValidateSet("all", "intro", "user", "user-summary", "admin", "quickstart", "it", "dev")]
    [string]$Target = "all",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# スクリプトのディレクトリを取得
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# マニュアル定義（対象一覧は manual-targets.ps1 に一元化。入力の .docx は docx 側の出力と同じ名前になる。Issue #2241）
. (Join-Path $ScriptDir "manual-targets.ps1")
$Manuals = @(Get-ManualTargets)

# 対象マニュアルをフィルタ
if ($Target -ne "all") {
    $Manuals = @($Manuals | Where-Object { $_.Key -eq $Target })
}

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " マニュアル PDF変換（Word経由）" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# 変換結果の追跡
$ConvertedCount = 0
$SkippedCount = 0
$ErrorCount = 0

# 1. 変換元の確認と変換対象の決定（Word を起動する前に行う）
#    変換元の .docx が無いことはスキップではなくエラーとして数える。スキップ扱いだった頃は、
#    入力のファイル名が食い違っていても終了コード 0 で「完了」と表示され、PDF が作られないことに
#    誰も気付かなかった（Issue #2241）。
$Pending = @()
foreach ($Manual in $Manuals) {
    $InputPath = Join-Path $ScriptDir $Manual.Docx
    $OutputPath = Join-Path $ScriptDir $Manual.Pdf

    Write-Host "--------------------------------------" -ForegroundColor Gray
    Write-Host "[$($Manual.Name)]" -ForegroundColor Cyan

    if (-not (Test-Path $InputPath)) {
        Write-Host "  エラー: 変換元の .docx ファイルが見つかりません" -ForegroundColor Red
        Write-Host "    $InputPath" -ForegroundColor Gray
        Write-Host "    → 先に .\convert-to-docx.ps1 -Target $($Manual.Key) を実行してください" -ForegroundColor Gray
        $ErrorCount++
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

    Write-Host "  変換対象: $($Manual.Docx) → $($Manual.Pdf)" -ForegroundColor Yellow
    $Pending += $Manual
}

# 2. 変換（対象があるときだけ Word を起動する）
if ($Pending.Count -gt 0) {
    Write-Host ""
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

    # ExportAsFixedFormat の定数
    $wdExportFormatPDF = 17
    $wdExportOptimizeForPrint = 0

    try {
        foreach ($Manual in $Pending) {
            $InputPath = Join-Path $ScriptDir $Manual.Docx
            $OutputPath = Join-Path $ScriptDir $Manual.Pdf

            Write-Host "--------------------------------------" -ForegroundColor Gray
            Write-Host "[$($Manual.Name)]" -ForegroundColor Cyan
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
                Write-Host "  完了: $($Manual.Pdf)" -ForegroundColor Green
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
}

# 結果サマリ
Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
if ($ErrorCount -gt 0) {
    Write-Host " 完了（エラーあり）" -ForegroundColor Red
} else {
    Write-Host " 完了!" -ForegroundColor Green
}
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
