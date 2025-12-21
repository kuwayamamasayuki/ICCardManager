# ユーザーマニュアル Word変換スクリプト
# 使用方法: .\convert-to-docx.ps1
# 前提条件: pandocがインストールされていること
#   インストール: winget install pandoc または https://pandoc.org/installing.html

param(
    [string]$InputFile = "ユーザーマニュアル.md",
    [string]$OutputFile = "ユーザーマニュアル.docx"
)

$ErrorActionPreference = "Stop"

# スクリプトのディレクトリを取得
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$InputPath = Join-Path $ScriptDir $InputFile
$OutputPath = Join-Path $ScriptDir $OutputFile

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " ユーザーマニュアル Word変換" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# pandocの確認
Write-Host "[1/3] pandocの確認..." -ForegroundColor Yellow
$PandocPath = Get-Command pandoc -ErrorAction SilentlyContinue

if (-not $PandocPath) {
    Write-Host "エラー: pandocが見つかりません。" -ForegroundColor Red
    Write-Host ""
    Write-Host "pandocをインストールしてください:" -ForegroundColor Yellow
    Write-Host "  方法1: winget install pandoc" -ForegroundColor White
    Write-Host "  方法2: https://pandoc.org/installing.html からダウンロード" -ForegroundColor White
    Write-Host ""
    exit 1
}
Write-Host "  pandoc: $($PandocPath.Source)" -ForegroundColor Green

# 入力ファイルの確認
Write-Host ""
Write-Host "[2/3] 入力ファイルの確認..." -ForegroundColor Yellow
if (-not (Test-Path $InputPath)) {
    Write-Host "エラー: 入力ファイルが見つかりません: $InputPath" -ForegroundColor Red
    exit 1
}
Write-Host "  入力: $InputPath" -ForegroundColor Green

# 変換実行
Write-Host ""
Write-Host "[3/3] Word形式に変換中..." -ForegroundColor Yellow

# pandocオプション
# --reference-doc: カスタムテンプレートを使用する場合に指定
# --toc: 目次を生成
# --toc-depth: 目次の深さ
$PandocArgs = @(
    $InputPath,
    "-o", $OutputPath,
    "--from", "markdown",
    "--to", "docx",
    "--toc",
    "--toc-depth=2",
    "--metadata", "title=交通系ICカード管理システム ユーザーマニュアル",
    "--metadata", "author=システム管理者",
    "--metadata", "lang=ja-JP"
)

& pandoc $PandocArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "エラー: 変換に失敗しました。" -ForegroundColor Red
    exit 1
}

# 結果の表示
Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " 変換完了!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan

if (Test-Path $OutputPath) {
    $FileInfo = Get-Item $OutputPath
    Write-Host ""
    Write-Host "出力ファイル: $OutputPath" -ForegroundColor Green
    Write-Host "ファイルサイズ: $([math]::Round($FileInfo.Length / 1KB, 2)) KB" -ForegroundColor Green
    Write-Host ""

    # ファイルを開くか確認
    $OpenFile = Read-Host "ファイルを開きますか？ (Y/N)"
    if ($OpenFile -eq "Y" -or $OpenFile -eq "y") {
        Start-Process $OutputPath
    }
}
