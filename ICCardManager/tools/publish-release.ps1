# タグ付け + ビルド + GitHub Release公開スクリプト
# 使用方法: pwsh.exe -File tools/publish-release.ps1 -Version 1.25.1 [-SkipBuild] [-SkipTag] [-Force] [-ReleaseWaitSeconds 900]

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipBuild,
    [switch]$SkipTag,
    [switch]$Force,

    # release.yml が GitHub Release を作るのを待つ最大秒数。ワークフローは 3〜6 分かかる（Issue #2246）
    [ValidateRange(0, 7200)]
    [int]$ReleaseWaitSeconds = 900
)

$ErrorActionPreference = "Stop"

# UTF-8出力を強制（日本語文字化け防止）
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# パスの設定
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
$CsprojPath = Join-Path $ProjectRoot "src\ICCardManager\ICCardManager.csproj"
$ChangelogPath = Join-Path $ProjectRoot "CHANGELOG.md"
$InstallerScript = Join-Path $ProjectRoot "installer\build-installer.ps1"
$InstallerOutput = Join-Path $ProjectRoot "installer\output\ICCardManager_Setup_${Version}.exe"

$TagName = "v${Version}"

# リリースノートの組み立てと照合（Issue #2246）
. (Join-Path $ScriptDir "release-notes.ps1")

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

# gh コマンドのWSL2対応ラッパー
$script:UseWslGh = $false

function Initialize-GhCommand {
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        return
    }
    $null = & wsl.exe which gh 2>$null
    if ($LASTEXITCODE -eq 0) {
        $script:UseWslGh = $true
    } else {
        Write-Fail "gh コマンドが見つかりません（Windows/WSL両方で未検出）"
        exit 1
    }
}

function Invoke-Gh {
    if ($script:UseWslGh) {
        & wsl.exe gh @args
    } else {
        & gh @args
    }
}

# Windowsパス → WSLパス変換（wsl.exe gh にファイルパスを渡す場合に必要）
# wsl.exe wslpath は日本語パスでエンコーディング問題が起きるため、文字列変換で対応
function ConvertTo-WslPath {
    param([string]$WindowsPath)
    if ($script:UseWslGh) {
        # D:\OneDrive\交通系\... → /mnt/d/OneDrive/交通系/...
        $wslPath = $WindowsPath -replace '\\', '/'
        if ($wslPath -match '^([A-Za-z]):(.*)$') {
            $drive = $Matches[1].ToLower()
            $rest = $Matches[2]
            $wslPath = "/mnt/${drive}${rest}"
        }
        return $wslPath
    }
    return $WindowsPath
}

# ─────────────────────────────────────────────────
# 1. 前提チェック
# ─────────────────────────────────────────────────

Write-Step "前提条件チェック"

# git コマンド存在チェック
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Fail "git コマンドが見つかりません"
    exit 1
}

# gh コマンド検出（WSL2フォールバック付き）
Initialize-GhCommand
if ($script:UseWslGh) {
    Write-Success "git 確認済み / gh 確認済み（WSL経由）"
} else {
    Write-Success "git / gh コマンド確認済み"
}

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

# リリースノートを組み立てられるか（上限を超えて見出しに絞っても収まらない等）を、タグを打つ前に確かめる。
# 組み立ての失敗は CHANGELOG の形の問題なので、タグ・ビルド・Release 作成の待機を済ませてから気付いても手戻りになる
try {
    $null = New-ReleaseNotesBody -Version $Version -Section (Get-ChangelogSection -Path $ChangelogPath -Ver $Version) `
        -ChangelogUrl (Get-ChangelogUrlAtTag -RepoUrl "https://github.com/owner/repository-name-placeholder" -TagName $TagName)
} catch {
    Write-Fail "$($_.Exception.Message)"
    Write-Host "  CHANGELOG.md を直して main へ反映してから、もう一度実行してください" -ForegroundColor Yellow
    exit 1
}
Write-Success "リリースノートを組み立てられることを確認済み"

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

# GitHub Actionのリリース作成を待機
$maxWaitSeconds = $ReleaseWaitSeconds
$waitInterval = 15
$elapsed = 0
$releaseFound = $false

Write-Host "  GitHub Actionのリリース作成を待機中..." -ForegroundColor Gray

while ($true) {
    $null = Invoke-Gh release view $TagName 2>$null
    if ($LASTEXITCODE -eq 0) {
        $releaseFound = $true
        Write-Success "GitHub Release ${TagName} を検出"
        break
    }
    if ($elapsed -ge $maxWaitSeconds) {
        break
    }

    if ($elapsed -eq 0) {
        Write-Host "  リリースが見つかりません。待機します（最大${maxWaitSeconds}秒）..." -ForegroundColor Gray
    }

    Start-Sleep -Seconds $waitInterval
    $elapsed += $waitInterval
    Write-Host "  ... ${elapsed}秒経過" -ForegroundColor Gray
}

if (-not $releaseFound) {
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
}

$repoUrl = Invoke-Gh repo view --json url -q ".url" 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoUrl)) {
    Write-Fail "リポジトリの URL を取得できませんでした（gh repo view）"
    Write-Host "  gh auth status で認証を確認し、-SkipTag -SkipBuild を付けて再実行してください" -ForegroundColor Yellow
    exit 1
}
$repoUrl = "$repoUrl".Trim()
$changelogUrl = Get-ChangelogUrlAtTag -RepoUrl $repoUrl -TagName $TagName

# 本文が上限（125,000 文字）を超える場合は、各項目の見出し行だけに絞り CHANGELOG へのリンクを添える
try {
    $fullReleaseNotes = New-ReleaseNotesBody -Version $Version -Section $releaseNotes -ChangelogUrl $changelogUrl
} catch {
    Write-Fail "$($_.Exception.Message)"
    Write-Host "  CHANGELOG.md を直して main へ反映してから、-SkipTag -SkipBuild を付けて再実行してください" -ForegroundColor Yellow
    exit 1
}
$bodyLength = Measure-ReleaseBodyLength $fullReleaseNotes
if (-not [string]::IsNullOrWhiteSpace($releaseNotes) -and -not $fullReleaseNotes.Contains($releaseNotes)) {
    $omitted = ""
    if ($fullReleaseNotes -match '(?m)^- ほか (\d+) 件の項目') {
        $omitted = "。見出しだけでも収まらないため、$($Matches[1]) 件の項目を省きます"
    }
    Write-Warn "CHANGELOG のセクションが上限（${ReleaseBodyMaxLength} 文字）を超えるため、各項目の見出しだけを載せます（${bodyLength} 文字${omitted}）"
}

# 本文は stdin ではなく一時ファイルで渡す。pwsh → wsl.exe → gh の stdin では本文が届かず、
# gh は空の本文を正常に処理して終了コード 0 を返していた（Issue #2246）
$notesFile = Join-Path $ProjectRoot "installer\output\release-notes-${Version}.md"
Write-ReleaseNotesFile -Path $notesFile -Body $fullReleaseNotes

Invoke-Gh release edit $TagName --notes-file (ConvertTo-WslPath $notesFile)
if ($LASTEXITCODE -ne 0) {
    Write-Fail "リリースノートの更新に失敗しました（本文: $notesFile）"
    Write-Host "  gh auth status で認証を確認し、-SkipTag -SkipBuild を付けて再実行してください" -ForegroundColor Yellow
    exit 1
}

# 終了コードは成功の根拠にしない。書き込んだ本文を読み戻し、長さが送った本文と一致することを確かめる
$viewOutput = Invoke-Gh release view $TagName --json body
if ($LASTEXITCODE -ne 0) {
    Write-Fail "リリースノートを読み戻せませんでした（gh release view）"
    Write-Host "  本文が設定されたかを確かめ、-SkipTag -SkipBuild を付けて再実行してください（本文: $notesFile）" -ForegroundColor Yellow
    exit 1
}
try {
    $actualBody = (($viewOutput -join "`n") | ConvertFrom-Json).body
} catch {
    # 読み戻した内容を解析できないときは、本文を確かめられなかったものとして照合の失敗に合流させる
    $actualBody = $null
}
if (-not (Test-ReleaseBodyMatches -Expected $fullReleaseNotes -Actual $actualBody)) {
    $actualLength = Measure-ReleaseBodyLength $actualBody
    Write-Fail "リリースノートが正しく設定されていません（送った本文: ${bodyLength} 文字／読み戻した本文: ${actualLength} 文字）"
    Write-Host "  本文は $notesFile に残しています。-SkipTag -SkipBuild を付けて再実行してください" -ForegroundColor Yellow
    exit 1
}
Remove-Item -LiteralPath $notesFile -ErrorAction SilentlyContinue
Write-Success "リリースノート更新完了（${bodyLength} 文字。読み戻して確認済み）"

# インストーラーexeをアップロード（--clobber で上書き対応）
Invoke-Gh release upload $TagName (ConvertTo-WslPath $InstallerOutput) --clobber
if ($LASTEXITCODE -ne 0) {
    Write-Fail "インストーラーのアップロードに失敗しました"
    exit 1
}
Write-Success "インストーラーアップロード完了"

# ─────────────────────────────────────────────────
# 6. 結果出力
# ─────────────────────────────────────────────────

Write-Step "リリース完了"

# $repoUrl はリリースノートの組み立て前に取得済み（取得できなければそこで止まっている）
Write-Host "`n  リリースURL: ${repoUrl}/releases/tag/${TagName}" -ForegroundColor Green

Write-Host ""
