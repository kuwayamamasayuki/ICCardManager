# バージョンバンプ + PR作成 + 自動マージスクリプト
# 使用方法: pwsh.exe -File tools/bump-version.ps1 -NewVersion 1.25.0 [-DryRun] [-NoMerge]

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$NewVersion,

    [switch]$DryRun,
    [switch]$NoMerge
)

$ErrorActionPreference = "Stop"

# パスの設定
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
$CsprojPath = Join-Path $ProjectRoot "src\ICCardManager\ICCardManager.csproj"
$ReadmePath = Join-Path $ProjectRoot "README.md"
$ChangelogPath = Join-Path $ProjectRoot "CHANGELOG.md"
$UserManualPath = Join-Path $ProjectRoot "docs\manual\ユーザーマニュアル.md"
$AdminManualPath = Join-Path $ProjectRoot "docs\manual\管理者マニュアル.md"

$Today = Get-Date -Format "yyyy-MM-dd"
$TodayJp = Get-Date -Format "yyyy年M月"

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
# pwsh.exe（Windowsプロセス）からはWSL側の /usr/bin/gh が見えないため、
# Windows側に gh が無ければ wsl.exe 経由で呼び出す
$script:UseWslGh = $false

function Initialize-GhCommand {
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        return  # Windows側にghあり
    }
    # WSL側のghを確認
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

# 作業ツリーがcleanか
$gitStatus = git -C $ProjectRoot status --porcelain
if ($gitStatus) {
    Write-Fail "作業ツリーにコミットされていない変更があります"
    git -C $ProjectRoot status --short
    exit 1
}
Write-Success "作業ツリーはクリーン"

# mainブランチ上か
$currentBranch = git -C $ProjectRoot rev-parse --abbrev-ref HEAD
if ($currentBranch -ne "main") {
    Write-Fail "mainブランチ上で実行してください（現在: $currentBranch）"
    exit 1
}
Write-Success "mainブランチ上"

# origin/mainと同期済みか
git -C $ProjectRoot fetch origin main --quiet 2>$null
$localHead = git -C $ProjectRoot rev-parse HEAD
$remoteHead = git -C $ProjectRoot rev-parse origin/main
if ($localHead -ne $remoteHead) {
    Write-Fail "ローカルのmainがorigin/mainと同期していません。git pullを実行してください"
    exit 1
}
Write-Success "origin/main と同期済み"

# ─────────────────────────────────────────────────
# 2. 前回タグ取得 & CHANGELOG自動生成
# ─────────────────────────────────────────────────

Write-Step "CHANGELOG自動生成"

$lastTag = git -C $ProjectRoot describe --tags --abbrev=0 2>$null
if (-not $lastTag) {
    Write-Warn "過去のタグが見つかりません。全コミットを対象にします"
    $logRange = "HEAD"
} else {
    Write-Success "前回タグ: $lastTag"
    $logRange = "$lastTag..HEAD"
}

# git log 取得
$commits = git -C $ProjectRoot log $logRange --oneline --no-merges

if (-not $commits) {
    Write-Fail "前回タグ以降のコミットがありません"
    exit 1
}

# Conventional commit prefix → 日本語カテゴリ
$categoryMap = [ordered]@{
    'feat'     = '新機能'
    'fix'      = 'バグ修正'
    'refactor' = 'リファクタリング'
    'docs'     = 'ドキュメント'
    'test'     = 'テスト'
    'perf'     = 'パフォーマンス'
}

# コミットを分類
$categorized = @{}
foreach ($key in $categoryMap.Keys) {
    $categorized[$key] = @()
}

foreach ($line in $commits) {
    # "abcdef0 type: message (#123)" or "abcdef0 type(scope): message (#123)"
    if ($line -match '^\w+\s+(feat|fix|refactor|docs|test|perf)(?:\([^)]*\))?:\s*(.+)$') {
        $type = $Matches[1]
        $message = $Matches[2].Trim()

        # issue番号を抽出（最初の #NNN）
        $issueRef = ""
        if ($message -match '#(\d+)') {
            $issueRef = "（#$($Matches[1])）"
            # メッセージからPRの (#NNN) を除去（末尾のもの）
            $message = $message -replace '\s*\(#\d+\)\s*$', ''
        }

        $entry = "- ${message}${issueRef}"
        $categorized[$type] += $entry
    }
    # chore: は除外、マッチしないものも除外
}

# CHANGELOGセクション生成
$changelogSection = "### v${NewVersion} (${Today})`n"

$hasEntries = $false
foreach ($key in $categoryMap.Keys) {
    if ($categorized[$key].Count -gt 0) {
        $hasEntries = $true
        $categoryName = $categoryMap[$key]
        $changelogSection += "`n**${categoryName}**`n"
        foreach ($entry in $categorized[$key]) {
            $changelogSection += "${entry}`n"
        }
    }
}

if (-not $hasEntries) {
    Write-Warn "分類可能なコミットがありません。手動でCHANGELOGを編集してください"
    $changelogSection += "`n（コミットメッセージからエントリを自動生成できませんでした）`n"
}

Write-Host "`n--- 生成されたCHANGELOGセクション ---" -ForegroundColor Magenta
Write-Host $changelogSection
Write-Host "--- ここまで ---`n" -ForegroundColor Magenta

# ─────────────────────────────────────────────────
# 3. 5ファイル更新
# ─────────────────────────────────────────────────

Write-Step "ファイル更新"

# 3a. csproj — Version / FileVersion / AssemblyVersion
$csprojContent = Get-Content $CsprojPath -Raw -Encoding UTF8
$csprojContent = $csprojContent -replace '<Version>[^<]+</Version>', "<Version>${NewVersion}</Version>"
$csprojContent = $csprojContent -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>${NewVersion}.0</FileVersion>"
$csprojContent = $csprojContent -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>${NewVersion}.0</AssemblyVersion>"
Write-Success "ICCardManager.csproj: ${NewVersion}"

# 3b. README.md — 最新バージョン行
$readmeContent = Get-Content $ReadmePath -Raw -Encoding UTF8
$readmeContent = $readmeContent -replace '最新バージョン: \*\*v[^*]+\*\* \([^)]+\)', "最新バージョン: **v${NewVersion}** (${Today})"
Write-Success "README.md: v${NewVersion} (${Today})"

# 3c. CHANGELOG.md — 「# 更新履歴」の直後にセクション挿入
$changelogContent = Get-Content $ChangelogPath -Raw -Encoding UTF8
$changelogContent = $changelogContent -replace '(# 更新履歴\r?\n)', "`$1`n${changelogSection}`n"
Write-Success "CHANGELOG.md: セクション挿入"

# 3d. ユーザーマニュアル — バージョン + 最終更新日
$userManualContent = Get-Content $UserManualPath -Raw -Encoding UTF8
$userManualContent = $userManualContent -replace '\*\*バージョン\*\*: [^\r\n]+', "**バージョン**: ${NewVersion}"
$userManualContent = $userManualContent -replace '\*\*最終更新日\*\*: [^\r\n]+', "**最終更新日**: ${TodayJp}"
Write-Success "ユーザーマニュアル.md: ${NewVersion}"

# 3e. 管理者マニュアル — バージョン + 最終更新日
$adminManualContent = Get-Content $AdminManualPath -Raw -Encoding UTF8
$adminManualContent = $adminManualContent -replace '\*\*バージョン\*\*: [^\r\n]+', "**バージョン**: ${NewVersion}"
$adminManualContent = $adminManualContent -replace '\*\*最終更新日\*\*: [^\r\n]+', "**最終更新日**: ${TodayJp}"
Write-Success "管理者マニュアル.md: ${NewVersion}"

# ─────────────────────────────────────────────────
# 4. DryRun: 差分表示して終了
# ─────────────────────────────────────────────────

if ($DryRun) {
    Write-Step "DryRunモード: 変更をプレビュー中（ファイルは変更しません）"

    # 一時的にファイルを書き出して差分表示
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "bump-version-preview"
    if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    $files = @(
        @{ Path = $CsprojPath;      Content = $csprojContent },
        @{ Path = $ReadmePath;      Content = $readmeContent },
        @{ Path = $ChangelogPath;   Content = $changelogContent },
        @{ Path = $UserManualPath;  Content = $userManualContent },
        @{ Path = $AdminManualPath; Content = $adminManualContent }
    )

    foreach ($file in $files) {
        $relativePath = [System.IO.Path]::GetRelativePath($ProjectRoot, $file.Path)
        $tempFile = Join-Path $tempDir (Split-Path -Leaf $file.Path)
        [System.IO.File]::WriteAllText($tempFile, $file.Content, [System.Text.Encoding]::UTF8)
        Write-Host "`n--- $relativePath ---" -ForegroundColor Yellow
        git diff --no-index -- $file.Path $tempFile 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  （変更なし）"
        }
    }

    Remove-Item $tempDir -Recurse -Force
    Write-Host "`n[DryRun] 変更は適用されませんでした。" -ForegroundColor Yellow
    exit 0
}

# ─────────────────────────────────────────────────
# 5. ファイル書き出し
# ─────────────────────────────────────────────────

Write-Step "ファイル書き出し"

# BOM無しUTF-8で書き出し
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($CsprojPath, $csprojContent, $utf8NoBom)
[System.IO.File]::WriteAllText($ReadmePath, $readmeContent, $utf8NoBom)
[System.IO.File]::WriteAllText($ChangelogPath, $changelogContent, $utf8NoBom)
[System.IO.File]::WriteAllText($UserManualPath, $userManualContent, $utf8NoBom)
[System.IO.File]::WriteAllText($AdminManualPath, $adminManualContent, $utf8NoBom)
Write-Success "5ファイルを更新しました"

# ─────────────────────────────────────────────────
# 6. ブランチ作成・コミット・プッシュ・PR作成
# ─────────────────────────────────────────────────

Write-Step "Git操作"

$branchName = "chore/bump-version-${NewVersion}"

git -C $ProjectRoot checkout -b $branchName
Write-Success "ブランチ作成: $branchName"

$filesToAdd = @(
    $CsprojPath,
    $ReadmePath,
    $ChangelogPath,
    $UserManualPath,
    $AdminManualPath
)
foreach ($f in $filesToAdd) {
    git -C $ProjectRoot add $f
}

$commitMessage = "chore: バージョンを v${NewVersion} に更新"
git -C $ProjectRoot commit -m $commitMessage
Write-Success "コミット: $commitMessage"

git -C $ProjectRoot push -u origin $branchName
Write-Success "プッシュ完了"

Write-Step "PR作成"

$prBody = @"
## Summary
- バージョンを v${NewVersion} に更新
- CHANGELOG.md に v${NewVersion} セクションを追加

## 更新ファイル
- ``src/ICCardManager/ICCardManager.csproj``
- ``README.md``
- ``CHANGELOG.md``
- ``docs/manual/ユーザーマニュアル.md``
- ``docs/manual/管理者マニュアル.md``
"@

$prUrl = Invoke-Gh pr create `
    --title $commitMessage `
    --body $prBody `
    --base main `
    --head $branchName

Write-Host ""
Write-Success "PR作成完了: $prUrl"

# ─────────────────────────────────────────────────
# 7. CIチェック待ち + 自動マージ
# ─────────────────────────────────────────────────

if ($NoMerge) {
    Write-Warn "自動マージをスキップ（-NoMerge）"
    Write-Host "`n次のステップ:" -ForegroundColor Cyan
    Write-Host "  1. GitHubでPRをレビュー・マージ"
    Write-Host "  2. pwsh.exe -File tools/publish-release.ps1 -Version ${NewVersion}"
} else {
    Write-Step "CIチェック待ち + 自動マージ"

    # PRのチェック完了を待機（最大5分）
    $maxWaitSeconds = 300
    $waitInterval = 15
    $elapsed = 0
    $checksReady = $false

    Write-Host "  CIチェックの完了を待機中..." -ForegroundColor Gray

    while ($elapsed -lt $maxWaitSeconds) {
        # チェックのステータスを取得
        $checksOutput = Invoke-Gh pr checks $branchName --repo (git -C $ProjectRoot remote get-url origin) 2>&1
        $checksExitCode = $LASTEXITCODE

        if ($checksExitCode -eq 0) {
            # すべてのチェックがpass
            $checksReady = $true
            break
        }

        # チェックがまだ存在しない or 実行中かを判定
        $checksStr = $checksOutput | Out-String
        if ($checksStr -match 'fail|FAIL') {
            Write-Fail "CIチェックが失敗しました"
            Write-Host $checksStr -ForegroundColor Red
            Write-Host "`n手動で修正後、以下でマージしてください:" -ForegroundColor Yellow
            Write-Host "  gh pr merge $branchName --squash --delete-branch" -ForegroundColor Yellow
            exit 1
        }

        Start-Sleep -Seconds $waitInterval
        $elapsed += $waitInterval
        Write-Host "  ... ${elapsed}秒経過" -ForegroundColor Gray
    }

    if (-not $checksReady) {
        # チェックが無い場合（ブランチ保護ルールなし）も含む — 直接マージを試みる
        Write-Warn "CIチェックが${maxWaitSeconds}秒以内に完了しませんでした。マージを試みます..."
    }

    # squashマージ + ブランチ削除
    Invoke-Gh pr merge $branchName --squash --delete-branch
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "PRのマージに失敗しました"
        Write-Host "  手動でマージしてください: gh pr merge $branchName --squash --delete-branch" -ForegroundColor Yellow
        exit 1
    }

    Write-Success "PRをマージしました"

    # mainに戻って最新化
    git -C $ProjectRoot checkout main
    git -C $ProjectRoot pull origin main
    Write-Success "mainブランチを最新化しました"

    Write-Host "`n次のステップ:" -ForegroundColor Cyan
    Write-Host "  pwsh.exe -File tools/publish-release.ps1 -Version ${NewVersion}"
}
