# ICCardManager インストーラービルドスクリプト
# 使用方法: .\build-installer.ps1 [-SkipBuild] [-Version "1.0.0"]

param(
    [switch]$SkipBuild,
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

# パスの設定
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
$SrcDir = Join-Path $ProjectRoot "src\ICCardManager"
$PublishDir = Join-Path $ProjectRoot "publish"
$InstallerScript = Join-Path $ScriptDir "ICCardManager.iss"
$OutputDir = Join-Path $ScriptDir "output"

# Inno Setup のパス（標準インストール場所）
$InnoSetupPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " ICCardManager インストーラービルド" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Inno Setup の確認
Write-Host "[1/4] Inno Setup の確認..." -ForegroundColor Yellow
$IsccPath = $null
foreach ($path in $InnoSetupPaths) {
    if (Test-Path $path) {
        $IsccPath = $path
        break
    }
}

if (-not $IsccPath) {
    Write-Host "エラー: Inno Setup が見つかりません。" -ForegroundColor Red
    Write-Host "Inno Setup 6 をインストールしてください: https://jrsoftware.org/isinfo.php" -ForegroundColor Red
    exit 1
}
Write-Host "  Inno Setup: $IsccPath" -ForegroundColor Green

# Step 2: アプリケーションのビルド
if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "[2/4] アプリケーションのビルド..." -ForegroundColor Yellow

    Push-Location $SrcDir
    try {
        # クリーンビルド
        dotnet clean -c Release -v q
        if ($LASTEXITCODE -ne 0) { throw "クリーンに失敗しました" }

        # 発行
        dotnet publish -c Release -r win-x64 --self-contained true -o $PublishDir -v q
        if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました" }

        Write-Host "  ビルド完了: $PublishDir" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host ""
    Write-Host "[2/4] ビルドをスキップ（既存のpublishを使用）..." -ForegroundColor Yellow
}

# Step 3: 発行ファイルの確認
Write-Host ""
Write-Host "[3/4] 発行ファイルの確認..." -ForegroundColor Yellow

$ExePath = Join-Path $PublishDir "ICCardManager.exe"
if (-not (Test-Path $ExePath)) {
    Write-Host "エラー: $ExePath が見つかりません。" -ForegroundColor Red
    Write-Host "先にアプリケーションをビルドしてください。" -ForegroundColor Red
    exit 1
}

$FileInfo = Get-Item $ExePath
Write-Host "  実行ファイル: $($FileInfo.Name) ($([math]::Round($FileInfo.Length / 1MB, 2)) MB)" -ForegroundColor Green

# Resourcesフォルダの確認
$ResourcesDir = Join-Path $PublishDir "Resources"
if (Test-Path $ResourcesDir) {
    $SoundFiles = Get-ChildItem (Join-Path $ResourcesDir "Sounds") -Filter "*.wav" -ErrorAction SilentlyContinue
    $TemplateFiles = Get-ChildItem (Join-Path $ResourcesDir "Templates") -Filter "*.xlsx" -ErrorAction SilentlyContinue
    Write-Host "  サウンドファイル: $($SoundFiles.Count) 個" -ForegroundColor Green
    Write-Host "  テンプレートファイル: $($TemplateFiles.Count) 個" -ForegroundColor Green
}

# Step 4: インストーラーの作成
Write-Host ""
Write-Host "[4/4] インストーラーの作成..." -ForegroundColor Yellow

# 出力ディレクトリの作成
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

# バージョンを指定してInno Setupを実行
$IsccArgs = @(
    "/DMyAppVersion=$Version",
    $InstallerScript
)

& $IsccPath $IsccArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "エラー: インストーラーの作成に失敗しました。" -ForegroundColor Red
    exit 1
}

# 結果の表示
Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " ビルド完了!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan

$InstallerFile = Get-ChildItem $OutputDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($InstallerFile) {
    Write-Host ""
    Write-Host "インストーラー: $($InstallerFile.FullName)" -ForegroundColor Green
    Write-Host "サイズ: $([math]::Round($InstallerFile.Length / 1MB, 2)) MB" -ForegroundColor Green
}
