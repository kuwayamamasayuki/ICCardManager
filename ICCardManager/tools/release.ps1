# リリース一括実行スクリプト
# 使用方法: pwsh.exe -File tools/release.ps1 -Version 1.25.0 [-DryRun] [-Force]
#
# バージョンバンプ → PR作成 → CIチェック待ち → マージ → タグ → ビルド → GitHub Release を一括実行

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$DryRun,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BumpScript = Join-Path $ScriptDir "bump-version.ps1"
$PublishScript = Join-Path $ScriptDir "publish-release.ps1"

# ─────────────────────────────────────────────────

Write-Host ""
Write-Host "╔══════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  ICCardManager Release v${Version}        " -ForegroundColor Cyan -NoNewline
Write-Host "║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════╝" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host "  [DryRunモード] 変更は行いません`n" -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────
# Phase 1: バージョンバンプ + PR + マージ
# ─────────────────────────────────────────────────

Write-Host "`n━━━ Phase 1/2: バージョンバンプ + PR + マージ ━━━" -ForegroundColor Magenta

$bumpArgs = @("-File", $BumpScript, "-NewVersion", $Version)
if ($DryRun) {
    $bumpArgs += "-DryRun"
}

& pwsh.exe @bumpArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n✗ Phase 1 が失敗しました。中止します。" -ForegroundColor Red
    exit 1
}

if ($DryRun) {
    Write-Host "`n[DryRun] Phase 2 はスキップします。" -ForegroundColor Yellow
    Write-Host "実行する場合: pwsh.exe -File tools/release.ps1 -Version ${Version}" -ForegroundColor Gray
    exit 0
}

# ─────────────────────────────────────────────────
# Phase 2: タグ + ビルド + GitHub Release
# ─────────────────────────────────────────────────

Write-Host "`n━━━ Phase 2/2: タグ + ビルド + GitHub Release ━━━" -ForegroundColor Magenta

$publishArgs = @("-File", $PublishScript, "-Version", $Version)
if ($Force) {
    $publishArgs += "-Force"
}

& pwsh.exe @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n✗ Phase 2 が失敗しました。" -ForegroundColor Red
    Write-Host "  リカバリ: pwsh.exe -File tools/publish-release.ps1 -Version ${Version} [-SkipTag] [-SkipBuild]" -ForegroundColor Yellow
    exit 1
}

# ─────────────────────────────────────────────────

Write-Host "`n╔══════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  リリース v${Version} 完了！               " -ForegroundColor Green -NoNewline
Write-Host "║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
