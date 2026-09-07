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
#     -Publish          : 撮影は行わず、出力先にある画像を docs\screenshots\ へ上書きコピーする
#                         （先に撮影 → auto\ の画像を見比べる → -Publish、の 2 段階で使う）
#     -SkipBuild        : 本体・UITests の再ビルドをスキップ
#     -Help             : 使い方を表示（未知の引数を渡した場合も使い方を表示して終了する）
#
# 注意:
#   - 本体は Release 構成でビルド・起動する（Debug は仮想タッチパネルが写り込むため）
#   - 撮影中はアプリのウィンドウが前面に出る。マウス・キーボードに触れないこと
#   - 既存の DB（%ProgramData%\ICCardManager\iccard.db）は撮影中だけ退避され、終了後に復元される
#   - 表示スケールは 100% を推奨（docs\screenshots\README.md）

# 位置指定引数を無効化する。有効のままだと `--help` のような未知の引数が最初の文字列パラメータ
# （-OutputDir）に束縛され、その名前のフォルダーが作られてしまう。
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$OutputDir,
    [switch]$Publish,
    [switch]$SkipBuild,
    [Alias("h", "?")]
    [switch]$Help,
    # 上記に一致しなかった引数（--help / -foo / 位置指定の値）をここで受け取り、使い方を表示して終了する
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$UnknownArgs
)

$ErrorActionPreference = "Stop"

function Show-Usage {
    Write-Host @"
使い方: .\tools\take-screenshots-uitest.ps1 [-OutputDir <path>] [-SkipBuild]
        .\tools\take-screenshots-uitest.ps1 -Publish [-OutputDir <path>]

  UI テスト基盤（FlaUI）でアプリを起動し、マニュアル用スクリーンショット 8 枚
  （main / history / card / staff / report / export / settings / system）を自動撮影します。
  Windows ネイティブの PowerShell から実行してください（WSL2 不可）。

オプション:
  -OutputDir <path>  出力先。既定: docs\screenshots\auto（Git 管理外）
  -Publish           撮影は行わず、出力先にある画像を docs\screenshots\ へ上書きコピーする
                     （先に撮影して auto\ の画像を見比べてから実行する 2 段階の運用）
  -SkipBuild         本体・UITests の再ビルドをスキップする
  -Help              この使い方を表示する

詳細: docs\screenshots\README.md
"@
}

if ($Help) {
    Show-Usage
    exit 0
}
if ($UnknownArgs -and $UnknownArgs.Count -gt 0) {
    Write-Host "[ERROR] 想定していない引数です: $($UnknownArgs -join ' ')" -ForegroundColor Red
    Write-Host ""
    Show-Usage
    exit 2
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$projectRoot = Join-Path $repoRoot "ICCardManager"
$mainCsproj = Join-Path $projectRoot "src\ICCardManager\ICCardManager.csproj"
$uiTestsCsproj = Join-Path $projectRoot "tests\ICCardManager.UITests\ICCardManager.UITests.csproj"
$publishedDir = Join-Path $projectRoot "docs\screenshots"
if (-not $OutputDir) { $OutputDir = Join-Path $publishedDir "auto" }
$testFilter = "Category=Screenshot"

# -Publish: 撮影せず、出力先にある画像をそのまま docs\screenshots\ へ上書きする。
# 撮影と同時に上書きすると、見比べる前に既存画像が置き換わってしまうため分けている。
if ($Publish) {
    if (-not (Test-Path $OutputDir)) {
        Write-Host "[ERROR] 出力先が見つかりません: $OutputDir" -ForegroundColor Red
        Write-Host "        先に引数なしで実行して撮影してください。" -ForegroundColor Yellow
        exit 1
    }
    $staged = @(Get-ChildItem -Path $OutputDir -Filter *.png)
    if ($staged.Count -eq 0) {
        Write-Host "[ERROR] 出力先に画像がありません: $OutputDir" -ForegroundColor Red
        Write-Host "        先に引数なしで実行して撮影してください。" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "docs\screenshots\ へ上書きコピーします（$($staged.Count) 枚）..." -ForegroundColor Green
    foreach ($f in $staged) {
        $dest = Join-Path $publishedDir $f.Name
        $mark = if (Test-Path $dest) { "上書き" } else { "新規" }
        Copy-Item -Path $f.FullName -Destination $dest -Force
        Write-Host "   → $($f.Name)（$mark）" -ForegroundColor DarkGray
    }
    Write-Host "git diff で差分を確認してからコミットしてください。" -ForegroundColor Yellow
    exit 0
}

if ($env:WSL_DISTRO_NAME) {
    Write-Host "[ERROR] このスクリプトは Windows ネイティブ PowerShell から実行してください。" -ForegroundColor Red
    exit 2
}

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
if ($testExitCode -eq 0 -and $captured.Count -gt 0) {
    Write-Host "画像を見比べて問題なければ、-Publish で docs\screenshots\ へ上書きしてください。" -ForegroundColor Yellow
}

exit $testExitCode
