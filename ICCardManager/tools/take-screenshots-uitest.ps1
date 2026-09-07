# マニュアル用スクリーンショット自動撮影スクリプト
# Issue #2016
#
# 用途:
#   UI テスト基盤（FlaUI）でアプリを起動し、マニュアルが参照する画面を自動撮影する。
#   対話式の TakeScreenshots.ps1 と違い、画面を開く操作も自動で行う。
#   第 1 段階の対象（8 枚）: main / history / card / staff / report / export / settings / system
#   （職員証・交通系ICカードのタッチを要する staff_recognized / lend / return は対象外。
#     それらは引き続き TakeScreenshots.ps1 で撮影する）
#
# 使い方:
#   PowerShell（Windows ネイティブ）で実行する:
#     cd <repo>\ICCardManager
#     .\tools\take-screenshots-uitest.ps1
#
#   オプション:
#     -OutputDir <path> : 出力先（既定: docs\screenshots\auto。.gitignore 対象）
#     -Publish          : 撮影後に docs\screenshots\ へ上書きコピーする（見比べてから使うこと）
#     -SkipBuild        : 本体・UITests の再ビルドをスキップ
#
# 注意:
#   - 本体は Release 構成でビルド・起動する（Debug は仮想タッチパネルが写り込むため）
#   - 撮影中はアプリのウィンドウが前面に出る。マウス・キーボードに触れないこと
#   - 既存の DB（%ProgramData%\ICCardManager\iccard.db）は撮影中だけ退避され、終了後に復元される
#   - 表示スケールは 100% を推奨（docs\screenshots\README.md）

[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$Publish,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

if ($env:WSL_DISTRO_NAME) {
    Write-Host "[ERROR] このスクリプトは Windows ネイティブ PowerShell から実行してください。" -ForegroundColor Red
    exit 2
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$projectRoot = Join-Path $repoRoot "ICCardManager"
$mainCsproj = Join-Path $projectRoot "src\ICCardManager\ICCardManager.csproj"
$uiTestsCsproj = Join-Path $projectRoot "tests\ICCardManager.UITests\ICCardManager.UITests.csproj"
$publishedDir = Join-Path $projectRoot "docs\screenshots"
if (-not $OutputDir) { $OutputDir = Join-Path $publishedDir "auto" }
$testFilter = "Category=Screenshot"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " マニュアル用スクリーンショット自動撮影" -ForegroundColor Cyan
Write-Host " Issue #2016" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

foreach ($p in @($mainCsproj, $uiTestsCsproj)) {
    if (-not (Test-Path $p)) {
        Write-Host "[ERROR] プロジェクトが見つかりません: $p" -ForegroundColor Red
        exit 1
    }
}

if (-not $SkipBuild) {
    Write-Host "[1/3] 本体を Release でビルド中..." -ForegroundColor Green
    dotnet build $mainCsproj --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] 本体のビルドに失敗しました（ExitCode: $LASTEXITCODE）" -ForegroundColor Red
        exit 1
    }
    Write-Host "[2/3] UITests をビルド中..." -ForegroundColor Green
    dotnet build $uiTestsCsproj
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] UITests のビルドに失敗しました（ExitCode: $LASTEXITCODE）" -ForegroundColor Red
        exit 1
    }
    Write-Host ""
} else {
    Write-Host "[1/3][2/3] ビルドをスキップ（-SkipBuild 指定）" -ForegroundColor Yellow
    Write-Host ""
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "[3/3] 撮影中..." -ForegroundColor Green
Write-Host "       出力先: $OutputDir" -ForegroundColor DarkGray
Write-Host "       撮影中はマウス・キーボードに触れないでください。" -ForegroundColor Yellow
Write-Host ""

# 撮影テストを有効化し、本体を Release で起動させる（AppFixture / ScreenshotHelper が参照する）
$env:ICCARDMANAGER_SCREENSHOT = "1"
$env:ICCARDMANAGER_UITEST_CONFIGURATION = "Release"
$env:ICCARDMANAGER_SCREENSHOT_DIR = $OutputDir
try {
    dotnet test $uiTestsCsproj `
        --no-build `
        --filter $testFilter `
        --logger "console;verbosity=normal"
    $testExitCode = $LASTEXITCODE
} finally {
    Remove-Item Env:\ICCARDMANAGER_SCREENSHOT -ErrorAction SilentlyContinue
    Remove-Item Env:\ICCARDMANAGER_UITEST_CONFIGURATION -ErrorAction SilentlyContinue
    Remove-Item Env:\ICCARDMANAGER_SCREENSHOT_DIR -ErrorAction SilentlyContinue
}

Write-Host ""
$captured = @(Get-ChildItem -Path $OutputDir -Filter *.png -ErrorAction SilentlyContinue)
Write-Host "========================================" -ForegroundColor Cyan
if ($testExitCode -eq 0) {
    Write-Host " ✓ 撮影完了: $($captured.Count) 枚" -ForegroundColor Green
    foreach ($f in $captured) { Write-Host "   $($f.Name)" -ForegroundColor DarkGray }
} else {
    Write-Host " ✗ 撮影に失敗したものがあります（ExitCode: $testExitCode）" -ForegroundColor Red
    Write-Host "   テスト出力を確認してください。DB は自動で復元されています。" -ForegroundColor Yellow
}
Write-Host "========================================" -ForegroundColor Cyan

if ($Publish -and $testExitCode -eq 0 -and $captured.Count -gt 0) {
    Write-Host ""
    Write-Host "docs\screenshots\ へ上書きコピーします..." -ForegroundColor Green
    foreach ($f in $captured) {
        Copy-Item -Path $f.FullName -Destination (Join-Path $publishedDir $f.Name) -Force
        Write-Host "   → $($f.Name)" -ForegroundColor DarkGray
    }
    Write-Host "git diff で差分を確認してからコミットしてください。" -ForegroundColor Yellow
}

exit $testExitCode
