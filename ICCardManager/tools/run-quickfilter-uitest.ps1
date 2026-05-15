# 操作ログクイックフィルタ実描画 FlaUI テスト実行スクリプト
# Issue #1522
#
# 用途:
#   WSL2 環境では二段モーダル取得が不安定のため OperationLogQuickFilterDisplayTests は
#   自動 Skip される（TestConstants.IsRunningOnWsl2）。本スクリプトは Windows ローカル
#   開発機 / CI で当該テストだけを実行するための補助ランナー。
#
# 使い方:
#   PowerShell（Windows ネイティブ）で実行する:
#     cd <repo>\ICCardManager
#     .\tools\run-quickfilter-uitest.ps1
#
#   オプション:
#     -SkipBuild  : メインプロジェクトの再ビルドをスキップ
#     -Verbose    : テスト出力を詳細に表示

[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

# WSL2 検出: テスト側と同じロジック（WSL_DISTRO_NAME 環境変数）
if ($env:WSL_DISTRO_NAME) {
    Write-Host "[ERROR] このスクリプトは Windows ネイティブ PowerShell から実行してください。" -ForegroundColor Red
    Write-Host "        WSL2 内では FlaUI の二段モーダル取得が不安定で、対象テストは Skip されます。" -ForegroundColor Yellow
    Write-Host "        WSL_DISTRO_NAME='$($env:WSL_DISTRO_NAME)'" -ForegroundColor Yellow
    exit 2
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$mainCsproj = Join-Path $repoRoot "ICCardManager\src\ICCardManager\ICCardManager.csproj"
$uiTestsCsproj = Join-Path $repoRoot "ICCardManager\tests\ICCardManager.UITests\ICCardManager.UITests.csproj"
$testFilter = "FullyQualifiedName~OperationLogQuickFilterDisplayTests"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 操作ログクイックフィルタ FlaUI テスト" -ForegroundColor Cyan
Write-Host " Issue #1522 リグレッション検出" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 前提確認
if (-not (Test-Path $mainCsproj)) {
    Write-Host "[ERROR] メインプロジェクトが見つかりません: $mainCsproj" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $uiTestsCsproj)) {
    Write-Host "[ERROR] UITests プロジェクトが見つかりません: $uiTestsCsproj" -ForegroundColor Red
    exit 1
}

# メインプロジェクトのビルド（dotnet run --no-build で起動されるため事前ビルド必須）
if (-not $SkipBuild) {
    Write-Host "[1/2] メインプロジェクトをビルド中..." -ForegroundColor Green
    dotnet build $mainCsproj --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] メインプロジェクトのビルドに失敗しました（ExitCode: $LASTEXITCODE）" -ForegroundColor Red
        exit 1
    }
    Write-Host ""
} else {
    Write-Host "[1/2] ビルドをスキップ（-SkipBuild 指定）" -ForegroundColor Yellow
    Write-Host ""
}

# テスト実行
Write-Host "[2/2] OperationLogQuickFilterDisplayTests を実行中..." -ForegroundColor Green
Write-Host "       フィルタ: $testFilter" -ForegroundColor DarkGray
Write-Host ""

dotnet test $uiTestsCsproj `
    --no-build:$false `
    --filter $testFilter `
    --logger "console;verbosity=normal"

$testExitCode = $LASTEXITCODE

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
if ($testExitCode -eq 0) {
    Write-Host " ✓ テスト合格" -ForegroundColor Green
    Write-Host "   クイックフィルタ 3 ボタンが正しく描画され、" -ForegroundColor Green
    Write-Host "   操作種別 ComboBox と矩形衝突していません。" -ForegroundColor Green
} else {
    Write-Host " ✗ テスト失敗（ExitCode: $testExitCode）" -ForegroundColor Red
    Write-Host "   Issue #1505 のクリップ再発、または PR #1521 の行分離が崩れた可能性。" -ForegroundColor Yellow
    Write-Host "   テスト出力で BoundingRectangle 値を確認してください。" -ForegroundColor Yellow
}
Write-Host "========================================" -ForegroundColor Cyan

exit $testExitCode
