# タグ付け + ビルド + GitHub Release公開スクリプト
# 使用方法: pwsh.exe -File tools/publish-release.ps1 -Version 1.25.0 [-SkipBuild] [-SkipTag] [-Force]

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipBuild,
    [switch]$SkipTag,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# パスの設定
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
$CsprojPath = Join-Path $ProjectRoot "src\ICCardManager\ICCardManager.csproj"
$ChangelogPath = Join-Path $ProjectRoot "CHANGELOG.md"
$InstallerScript = Join-Path $ProjectRoot "installer\build-installer.ps1"
$InstallerOutput = Join-Path $ProjectRoot "installer\output\ICCardManager_Setup_${Version}.exe"

$TagName = "v${Version}"

# ─────────────────────────────────────────────────
# ユーティリティ関数
# ─────────────────────────────────────────────────

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "  ✓ $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "  ! $Message" -ForegroundColor Yellow
}

function Write-Fail {
    param([string]$Message)
    Write-Host "  ✗ $Message" -ForegroundColor Red
}

function Get-ChangelogSection {
    param([string]$Path, [string]$Ver)
    $content = Get-Content $Path -Encoding UTF8
    $section = @()
    $capturing = $false

    foreach ($line in $content) {
        if ($line -match "^### v${Ver}\b") {
            $capturing = $true
            continue
        }
        if ($capturing -and $line -match '^### v\d+\.\d+\.\d+') {
            break
        }
        if ($capturing) {
            $section += $line
        }
    }

    # 前後の空行を除去
    $text = ($section -join "`n").Trim()
    return $text
}

# ─────────────────────────────────────────────────
# 1. 前提チェック
# ─────────────────────────────────────────────────

Write-Step "前提条件チェック"

# git / gh コマンド存在チェック
foreach ($cmd in @("git", "gh")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Write-Fail "$cmd コマンドが見つかりません"
        exit 1
    }
}
Write-Success "git / gh コマンド確認済み"

# mainブランチ上か
$currentBranch = git -C $ProjectRoot rev-parse --abbrev-ref HEAD
if ($currentBranch -ne "main") {
    Write-Fail "mainブランチ上で実行してください（現在: $currentBranch）"
    exit 1
}
Write-Success "mainブランチ上"

# csprojのバージョンが一致するか
[xml]$csproj = Get-Content $CsprojPath
$csprojVersion = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($csprojVersion -ne $Version) {
    Write-Fail "csprojのバージョン（${csprojVersion}）が指定バージョン（${Version}）と一致しません"
    Write-Host "  bump-version.ps1 のPRがマージ済みか確認してください" -ForegroundColor Yellow
    exit 1
}
Write-Success "csprojバージョン一致: ${Version}"

# CHANGELOGにセクションが存在するか
$changelogContent = Get-Content $ChangelogPath -Raw -Encoding UTF8
if ($changelogContent -notmatch "### v${Version}\b") {
    Write-Fail "CHANGELOG.md に v${Version} のセクションが見つかりません"
    exit 1
}
Write-Success "CHANGELOG.md にセクション確認済み"

# ─────────────────────────────────────────────────
# 2. git pull で最新化
# ─────────────────────────────────────────────────

Write-Step "最新化"
git -C $ProjectRoot pull origin main
Write-Success "git pull 完了"

# ─────────────────────────────────────────────────
# 3. タグ作成・プッシュ
# ─────────────────────────────────────────────────

if (-not $SkipTag) {
    Write-Step "タグ作成"

    # 既存タグチェック（冪等性）
    $existingTag = git -C $ProjectRoot tag -l $TagName
    if ($existingTag) {
        Write-Warn "タグ ${TagName} は既に存在します。スキップします"
    } else {
        # 確認プロンプト
        if (-not $Force) {
            Write-Host "  タグ ${TagName} を作成してプッシュします。よろしいですか？ (y/N): " -NoNewline -ForegroundColor Yellow
            $answer = Read-Host
            if ($answer -ne 'y' -and $answer -ne 'Y') {
                Write-Host "  中止しました" -ForegroundColor Yellow
                exit 0
            }
        }

        git -C $ProjectRoot tag $TagName
        git -C $ProjectRoot push origin $TagName
        Write-Success "タグ ${TagName} を作成・プッシュしました"
        Write-Host "  → release.yml GitHub Action が起動します" -ForegroundColor Gray
    }
} else {
    Write-Warn "タグ作成をスキップ（-SkipTag）"
}

# ─────────────────────────────────────────────────
# 4. インストーラービルド
# ─────────────────────────────────────────────────

if (-not $SkipBuild) {
    Write-Step "インストーラービルド"

    if (-not (Test-Path $InstallerScript)) {
        Write-Fail "build-installer.ps1 が見つかりません: $InstallerScript"
        exit 1
    }

    & $InstallerScript -Version $Version
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "インストーラービルドに失敗しました"
        exit 1
    }

    if (-not (Test-Path $InstallerOutput)) {
        Write-Fail "インストーラーが見つかりません: $InstallerOutput"
        exit 1
    }

    Write-Success "インストーラービルド完了: $InstallerOutput"
} else {
    Write-Warn "インストーラービルドをスキップ（-SkipBuild）"

    if (-not (Test-Path $InstallerOutput)) {
        Write-Fail "インストーラーが見つかりません: $InstallerOutput"
        Write-Host "  -SkipBuild を外すか、先にビルドしてください" -ForegroundColor Yellow
        exit 1
    }
    Write-Success "既存インストーラー確認: $InstallerOutput"
}

# ─────────────────────────────────────────────────
# 5. GitHub Release更新
# ─────────────────────────────────────────────────

Write-Step "GitHub Release更新"

# GitHub Actionのリリース作成を待機（最大3分）
$maxWaitSeconds = 180
$waitInterval = 15
$elapsed = 0

Write-Host "  GitHub Actionのリリース作成を待機中..." -ForegroundColor Gray

while ($elapsed -lt $maxWaitSeconds) {
    $releaseExists = gh release view $TagName 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Success "GitHub Release ${TagName} を検出"
        break
    }

    if ($elapsed -eq 0) {
        Write-Host "  リリースが見つかりません。待機します（最大${maxWaitSeconds}秒）..." -ForegroundColor Gray
    }

    Start-Sleep -Seconds $waitInterval
    $elapsed += $waitInterval
    Write-Host "  ... ${elapsed}秒経過" -ForegroundColor Gray
}

if ($elapsed -ge $maxWaitSeconds) {
    Write-Warn "GitHub Releaseが${maxWaitSeconds}秒以内に作成されませんでした"
    Write-Host "  手動で確認してください: gh release view ${TagName}" -ForegroundColor Yellow
    Write-Host "  リリースが作成されたら、以下で再実行できます:" -ForegroundColor Yellow
    Write-Host "  pwsh.exe -File tools/publish-release.ps1 -Version ${Version} -SkipTag -SkipBuild" -ForegroundColor Yellow
    exit 1
}

# CHANGELOGから該当バージョンのセクションを抽出
$releaseNotes = Get-ChangelogSection -Path $ChangelogPath -Ver $Version

if ([string]::IsNullOrWhiteSpace($releaseNotes)) {
    Write-Warn "CHANGELOGからリリースノートを抽出できませんでした"
    $releaseNotes = "v${Version} リリース"
}

# リリースノートのヘッダーを追加
$fullReleaseNotes = @"
## ICCardManager v${Version}

${releaseNotes}

### 動作環境
- Windows 10/11 (32-bit/64-bit)
- Sony PaSoRi (RC-S380等)
- .NET Runtime: 不要（self-contained）

### インストール方法
インストーラー（``ICCardManager_Setup_${Version}.exe``）を実行してください。
"@

# gh release edit でリリースノートを更新
$fullReleaseNotes | gh release edit $TagName --notes-file -
if ($LASTEXITCODE -ne 0) {
    Write-Fail "リリースノートの更新に失敗しました"
    exit 1
}
Write-Success "リリースノート更新完了"

# インストーラーexeをアップロード（--clobber で上書き対応）
gh release upload $TagName $InstallerOutput --clobber
if ($LASTEXITCODE -ne 0) {
    Write-Fail "インストーラーのアップロードに失敗しました"
    exit 1
}
Write-Success "インストーラーアップロード完了"

# ─────────────────────────────────────────────────
# 6. 結果出力
# ─────────────────────────────────────────────────

Write-Step "リリース完了"

$repoUrl = gh repo view --json url -q ".url" 2>$null
if ($repoUrl) {
    $releaseUrl = "${repoUrl}/releases/tag/${TagName}"
    Write-Host "`n  リリースURL: $releaseUrl" -ForegroundColor Green
} else {
    Write-Host "`n  確認: gh release view ${TagName}" -ForegroundColor Green
}

Write-Host ""
