# ICCardManager インストーラービルドスクリプト
# 使用方法: .\build-installer.ps1 [-SkipBuild] [-Version "1.0.0"]
# バージョンを指定しない場合、csprojファイルから自動的に読み取ります

param(
    [switch]$SkipBuild,
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

# パスの設定
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
$SrcDir = Join-Path $ProjectRoot "src\ICCardManager"
$PublishDir = Join-Path $ProjectRoot "publish"
$InstallerScript = Join-Path $ScriptDir "ICCardManager.iss"
$OutputDir = Join-Path $ScriptDir "output"
$CsprojPath = Join-Path $SrcDir "ICCardManager.csproj"

# バージョンが指定されていない場合、csprojから読み取る
if ([string]::IsNullOrEmpty($Version)) {
    if (Test-Path $CsprojPath) {
        [xml]$csproj = Get-Content $CsprojPath
        $Version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        if ([string]::IsNullOrEmpty($Version)) {
            $Version = "1.0.0"
            Write-Host "警告: csprojからバージョンを読み取れませんでした。デフォルト値を使用します: $Version" -ForegroundColor Yellow
        }
    } else {
        $Version = "1.0.0"
        Write-Host "警告: csprojファイルが見つかりません。デフォルト値を使用します: $Version" -ForegroundColor Yellow
    }
}

# Inno Setup のパス（標準インストール場所）
$InnoSetupPaths = @(
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "D:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "D:\Program Files\Inno Setup 6\ISCC.exe"
)

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " ICCardManager インストーラービルド" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "バージョン: $Version" -ForegroundColor Cyan
Write-Host ""

# Step 1: Inno Setup の確認
Write-Host "[1/9] Inno Setup の確認..." -ForegroundColor Yellow
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

# Step 2: アイコンの生成
Write-Host ""
Write-Host "[2/9] アイコンの生成..." -ForegroundColor Yellow
$IconScript = Join-Path $ScriptDir "generate-icon.ps1"
$IconPath = Join-Path $ScriptDir "app.ico"

if (Test-Path $IconScript) {
    & $IconScript
    if (Test-Path $IconPath) {
        Write-Host "  アイコン生成完了: $IconPath" -ForegroundColor Green
    } else {
        Write-Host "警告: アイコンの生成に失敗しました。" -ForegroundColor Yellow
    }
} else {
    Write-Host "警告: アイコン生成スクリプトが見つかりません: $IconScript" -ForegroundColor Yellow
}

# Step 3: アプリケーションのビルド
if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "[3/9] アプリケーションのビルド..." -ForegroundColor Yellow

    Push-Location $SrcDir
    try {
        # obj/binフォルダを削除してクリーンな状態にする
        if (Test-Path "obj") { Remove-Item -Recurse -Force "obj" }
        if (Test-Path "bin") { Remove-Item -Recurse -Force "bin" }

        # 復元
        dotnet restore -v q
        if ($LASTEXITCODE -ne 0) { throw "復元に失敗しました" }

        # 発行（.NET Framework 4.8 x86）
        dotnet publish -c Release -o $PublishDir -v q
        if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました" }

        Write-Host "  ビルド完了: $PublishDir" -ForegroundColor Green

        # PDBファイルを削除（リリースビルドでは不要）
        $PdbFiles = Get-ChildItem $PublishDir -Filter "*.pdb" -ErrorAction SilentlyContinue
        if ($PdbFiles) {
            $PdbFiles | Remove-Item -Force
            Write-Host "  PDBファイル: $($PdbFiles.Count) 個削除" -ForegroundColor Green
        }
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host ""
    Write-Host "[3/9] ビルドをスキップ（既存のpublishを使用）..." -ForegroundColor Yellow
}

# Step 4: デバッグツールのビルド
if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "[4/9] デバッグツールのビルド..." -ForegroundColor Yellow

    $DebugToolDir = Join-Path $ProjectRoot "tools\DebugDataViewer"
    $DebugToolPublishDir = Join-Path $PublishDir "Tools"

    Push-Location $DebugToolDir
    try {
        # obj/binフォルダを削除してクリーンな状態にする
        if (Test-Path "obj") { Remove-Item -Recurse -Force "obj" }
        if (Test-Path "bin") { Remove-Item -Recurse -Force "bin" }

        # 復元
        dotnet restore -v q
        if ($LASTEXITCODE -ne 0) { throw "復元に失敗しました" }

        # 発行（.NET Framework 4.8 x86）
        dotnet publish -c Release -o $DebugToolPublishDir -v q
        if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました" }

        Write-Host "  DebugDataViewer ビルド完了: $DebugToolPublishDir" -ForegroundColor Green

        # PDBファイルを削除（リリースビルドでは不要）
        $PdbFiles = Get-ChildItem $DebugToolPublishDir -Filter "*.pdb" -ErrorAction SilentlyContinue
        if ($PdbFiles) {
            $PdbFiles | Remove-Item -Force
            Write-Host "  PDBファイル: $($PdbFiles.Count) 個削除" -ForegroundColor Green
        }

        # ICCardManager.exe をToolsフォルダにコピー（依存関係として必要）
        $MainAppExe = Join-Path $PublishDir "ICCardManager.exe"
        if (Test-Path $MainAppExe) {
            Copy-Item $MainAppExe -Destination $DebugToolPublishDir -Force
            Write-Host "  ICCardManager.exe をToolsフォルダにコピー" -ForegroundColor Green
        } else {
            Write-Host "  警告: ICCardManager.exe が見つかりません。先にメインアプリをビルドしてください。" -ForegroundColor Yellow
        }

        # SQLite.Interop.dll (x86/x64) をToolsフォルダにコピー（Issue #466対応）
        # System.Data.SQLite.Coreパッケージはネイティブのx86/x64 DLLを必要とする
        $SqliteX86Source = Join-Path $DebugToolPublishDir "x86"
        if (-not (Test-Path $SqliteX86Source)) {
            # dotnet publish で出力されない場合、bin/Releaseからコピー
            $SqliteX86Source = Join-Path $DebugToolDir "bin\Release\net48\x86"
        }
        if (Test-Path $SqliteX86Source) {
            $SqliteX86Dest = Join-Path $DebugToolPublishDir "x86"
            if (-not (Test-Path $SqliteX86Dest)) { New-Item -ItemType Directory -Path $SqliteX86Dest -Force | Out-Null }
            Copy-Item -Path "$SqliteX86Source\*" -Destination $SqliteX86Dest -Force -ErrorAction SilentlyContinue
            Write-Host "  SQLite.Interop.dll: x86 フォルダをコピー" -ForegroundColor Green
        } else {
            Write-Host "  警告: x86フォルダが見つかりません。SQLiteが動作しない可能性があります。" -ForegroundColor Yellow
        }

        $SqliteX64Source = Join-Path $DebugToolPublishDir "x64"
        if (-not (Test-Path $SqliteX64Source)) {
            # dotnet publish で出力されない場合、bin/Releaseからコピー
            $SqliteX64Source = Join-Path $DebugToolDir "bin\Release\net48\x64"
        }
        if (Test-Path $SqliteX64Source) {
            $SqliteX64Dest = Join-Path $DebugToolPublishDir "x64"
            if (-not (Test-Path $SqliteX64Dest)) { New-Item -ItemType Directory -Path $SqliteX64Dest -Force | Out-Null }
            Copy-Item -Path "$SqliteX64Source\*" -Destination $SqliteX64Dest -Force -ErrorAction SilentlyContinue
            Write-Host "  SQLite.Interop.dll: x64 フォルダをコピー" -ForegroundColor Green
        }
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host ""
    Write-Host "[4/9] デバッグツールのビルドをスキップ..." -ForegroundColor Yellow
}

# Step 5: リソースファイルのコピー
Write-Host ""
Write-Host "[5/9] リソースファイルのコピー..." -ForegroundColor Yellow

$SoundsSource = Join-Path $SrcDir "Resources\Sounds"
$SoundsDest = Join-Path $PublishDir "Resources\Sounds"
$TemplatesSource = Join-Path $SrcDir "Resources\Templates"
$TemplatesDest = Join-Path $PublishDir "Resources\Templates"

# Soundsフォルダのコピー
if (Test-Path $SoundsSource) {
    if (-not (Test-Path $SoundsDest)) { New-Item -ItemType Directory -Path $SoundsDest -Force | Out-Null }
    Copy-Item -Path "$SoundsSource\*" -Destination $SoundsDest -Force -ErrorAction SilentlyContinue
    $SoundCount = (Get-ChildItem $SoundsDest -Filter "*.wav" -ErrorAction SilentlyContinue).Count
    Write-Host "  サウンドファイル: $SoundCount 個コピー" -ForegroundColor Green
}

# Templatesフォルダのコピー
if (Test-Path $TemplatesSource) {
    if (-not (Test-Path $TemplatesDest)) { New-Item -ItemType Directory -Path $TemplatesDest -Force | Out-Null }
    Copy-Item -Path "$TemplatesSource\*" -Destination $TemplatesDest -Force -ErrorAction SilentlyContinue
    $TemplateCount = (Get-ChildItem $TemplatesDest -Filter "*.xlsx" -ErrorAction SilentlyContinue).Count
    Write-Host "  テンプレートファイル: $TemplateCount 個コピー" -ForegroundColor Green
}

# SQLite.Interop.dll のコピー（x86ネイティブDLL）
$SqliteInteropSource = Join-Path $SrcDir "bin\Release\net48\x86"
$SqliteInteropDest = Join-Path $PublishDir "x86"
if (Test-Path $SqliteInteropSource) {
    if (-not (Test-Path $SqliteInteropDest)) { New-Item -ItemType Directory -Path $SqliteInteropDest -Force | Out-Null }
    Copy-Item -Path "$SqliteInteropSource\*" -Destination $SqliteInteropDest -Force -ErrorAction SilentlyContinue
    Write-Host "  SQLite.Interop.dll: x86 フォルダにコピー" -ForegroundColor Green
}

# Step 6: 発行ファイルの確認
Write-Host ""
Write-Host "[6/9] 発行ファイルの確認..." -ForegroundColor Yellow

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

# デバッグツールの確認
$DebugToolExe = Join-Path $PublishDir "Tools\DebugDataViewer.exe"
if (Test-Path $DebugToolExe) {
    $DebugToolInfo = Get-Item $DebugToolExe
    Write-Host "  デバッグツール: $($DebugToolInfo.Name) ($([math]::Round($DebugToolInfo.Length / 1KB, 2)) KB)" -ForegroundColor Green
} else {
    Write-Host "  デバッグツール: 見つかりません（-SkipBuildの場合はビルドが必要です）" -ForegroundColor Yellow
}

# Step 7: マニュアルの変換（Issue #480）
Write-Host ""
Write-Host "[7/9] マニュアルの変換..." -ForegroundColor Yellow

$ManualDir = Join-Path $ProjectRoot "docs\manual"
$ConvertScript = Join-Path $ManualDir "convert-to-docx.ps1"

# pandocがインストールされているか確認
$PandocPath = Get-Command pandoc -ErrorAction SilentlyContinue
if (-not $PandocPath) {
    Write-Host "  警告: pandocがインストールされていません。マニュアル変換をスキップします。" -ForegroundColor Yellow
    Write-Host "  インストール: winget install pandoc または https://pandoc.org/installing.html" -ForegroundColor Yellow
} elseif (-not (Test-Path $ConvertScript)) {
    Write-Host "  警告: 変換スクリプトが見つかりません: $ConvertScript" -ForegroundColor Yellow
} else {
    # 変換スクリプトを実行（更新が必要なマニュアルのみ変換）
    Push-Location $ManualDir
    try {
        & $ConvertScript -NoMermaid
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  マニュアル変換完了" -ForegroundColor Green
        } else {
            Write-Host "  警告: マニュアル変換でエラーが発生しました（ビルドは続行）" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "  警告: マニュアル変換中に例外が発生しました: $_" -ForegroundColor Yellow
        Write-Host "  ビルドは続行します" -ForegroundColor Yellow
    }
    finally {
        Pop-Location
    }
}

# Step 8: インストーラーの作成
Write-Host ""
Write-Host "[8/9] インストーラーの作成..." -ForegroundColor Yellow

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
