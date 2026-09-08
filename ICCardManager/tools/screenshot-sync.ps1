# 変更されたソースから、撮り直しが必要なマニュアル用スクリーンショットを導出する
# Issue #2021
#
# 用途:
#   docs\screenshots\screenshot-sources.json（画像 → その画面を構成するソースの対応表）を読み、
#   変更ファイルの集合に一致する画像を列挙する。
#   - take-screenshots-uitest.ps1 -Changed が「何を撮り直すか」を決めるのに使う
#   - .claude/hooks/check-doc-sync.sh（Stop フック）と CI（screenshot-sync-check.yml）が
#     「画面に影響する変更なのに画像が更新されていない」ことを検出するのに使う（-Verify）
#
# 変更ファイルの集合の取り方（いずれか 1 つ）:
#   -Base <ref>       : git で <ref> との merge-base から HEAD・作業ツリー・未追跡ファイルまでの差分を取る（既定）
#   -Files <a> <b> …  : 引数で渡す（テスト・他スクリプトからの利用）
#   -FilesFromStdin   : 標準入力から改行区切りで読む（フックからの利用）
#   パスはリポジトリのルートからの相対パス。区切りは '/' でも '\' でもよい
#
# 出力:
#   既定   : 影響を受ける画像を 1 行 1 件（name<TAB>pass<TAB>updated|not-updated<TAB>理由）。影響なしなら何も出さない
#   -Json  : { affected: [{name, pass, filter, reasons}], notUpdated: [name], passes: {…}, changedFiles: N }
#
# 終了コード:
#   0 = 影響なし、または（-Verify 時）影響を受けた画像がすべて変更集合に含まれている
#   2 = 使い方・対応表の誤り
#   3 = -Verify で、影響を受けたのに変更集合に含まれていない画像がある

[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Mapping,
    # git を実行するリポジトリのルート。既定はこのスクリプトの 2 つ上（<repo>/ICCardManager/tools/）。
    # テストが一時リポジトリで git 経路を検証するために差し替えられるようにしている
    [string]$RepoRoot,
    [string]$Base,
    # powershell.exe -File 経由では `-Files a b c` の b c が残余引数へ落ちる（[string[]] へは a しか束縛されない）ため、
    # 残余引数（$RemainingFiles）を -Files の続きとして合流させる。`-` で始まる要素は未知の引数として使い方エラーにする
    [string[]]$Files,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingFiles,
    [switch]$FilesFromStdin,
    [switch]$Verify,
    [switch]$Json,
    [Alias("h", "?")]
    [switch]$Help
)

$ErrorActionPreference = "Stop"
# 日本語のファイル名・理由を UTF-8 で出す（呼び出し側が UTF-8 で読む前提。フックは bash、テストは StandardOutputEncoding=UTF8）
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding = [System.Text.Encoding]::UTF8

function Show-Usage {
    Write-Host @"
使い方: .\tools\screenshot-sync.ps1 [-Base <ref>] [-Verify] [-Json]
        .\tools\screenshot-sync.ps1 -Files <path>... [-Verify] [-Json]
        .\tools\screenshot-sync.ps1 -FilesFromStdin [-Verify] [-Json]

  変更されたソースから、撮り直しが必要なマニュアル用スクリーンショットを導出します。

オプション:
  -Mapping <path>   対応表。既定: docs\screenshots\screenshot-sources.json
  -RepoRoot <path>  git を実行するリポジトリのルート。既定: このスクリプトの 2 つ上
  -Base <ref>       比較元の git 参照。既定: origin/main（無ければ main）
  -Files <path>...  変更ファイルを引数で渡す（git を使わない）
  -FilesFromStdin   変更ファイルを標準入力から改行区切りで読む
  -Verify           影響を受けた画像が変更集合に無ければ終了コード 3
  -Json             JSON で出力する
  -Help             この使い方を表示する
"@
}

function Fail([string]$message, [int]$code = 2) {
    [Console]::Error.WriteLine("[ERROR] $message")
    exit $code
}

if ($Help) { Show-Usage; exit 0 }
$filesGiven = $PSBoundParameters.ContainsKey('Files')
# @($null) は 1 要素の配列になるため、未指定のパラメータ由来の null を除いてから合流させる
$Files = @(@($Files) + @($RemainingFiles) | Where-Object { $null -ne $_ })
# -Files 無しで届いた位置指定の値と、`-` で始まる値（未知のスイッチ）は使い方エラー
$unknownArgs = if ($filesGiven) { @($Files | Where-Object { $_ -and $_.StartsWith('-') }) } else { @($Files) }
if ($unknownArgs.Count -gt 0) {
    [Console]::Error.WriteLine("[ERROR] 想定していない引数です: $($unknownArgs -join ' ')")
    Show-Usage
    exit 2
}

$sourceModes = @($filesGiven, $FilesFromStdin.IsPresent, $PSBoundParameters.ContainsKey('Base')) | Where-Object { $_ }
if ($sourceModes.Count -gt 1) {
    Fail "-Base / -Files / -FilesFromStdin は同時に指定できません。"
}

if ($RepoRoot) {
    if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) { Fail "リポジトリのルートが見つかりません: $RepoRoot" }
    $repoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
} else {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}
if (-not $Mapping) {
    $Mapping = Join-Path $repoRoot "ICCardManager\docs\screenshots\screenshot-sources.json"
}
if (-not (Test-Path -LiteralPath $Mapping)) {
    Fail "対応表が見つかりません: $Mapping"
}

# ---- 対応表の読み込みと検証 ----
try {
    $map = Get-Content -LiteralPath $Mapping -Raw -Encoding UTF8 | ConvertFrom-Json
} catch {
    Fail "対応表を JSON として読めません: $Mapping`n$($_.Exception.Message)"
}
foreach ($key in @("publishedDir", "common", "passes", "screenshots")) {
    if (-not ($map.PSObject.Properties.Name -contains $key)) { Fail "対応表に '$key' がありません: $Mapping" }
}
$passNames = @($map.passes.PSObject.Properties.Name)
foreach ($shot in $map.screenshots) {
    foreach ($key in @("name", "pass", "filter", "sources")) {
        if (-not ($shot.PSObject.Properties.Name -contains $key)) { Fail "対応表の画像に '$key' がありません: $($shot | ConvertTo-Json -Compress)" }
    }
    if ($passNames -notcontains $shot.pass) {
        Fail "対応表の画像 '$($shot.name)' が未定義のパス '$($shot.pass)' を参照しています（定義済み: $($passNames -join ', ')）"
    }
}

# ---- 変更ファイル集合 ----
function Normalize-Path([string]$p) {
    $n = $p.Trim().Replace('\', '/')
    # git status の rename 表記（old -> new）は new 側だけを見る
    if ($n -match ' -> ') { $n = ($n -split ' -> ')[-1] }
    return $n.TrimStart('/')
}

# git を実行して stdout の行だけを返す。
# Windows PowerShell は $ErrorActionPreference = "Stop" のとき、ネイティブコマンドの stderr（git の CRLF 警告など）を
# 終了エラーに変えるため、stderr を破棄する間だけ Continue にする。
# -c core.quotepath=false: 日本語ファイル名を octal escape せず UTF-8 のまま出す
function Invoke-Git([string[]]$gitArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $lines = & git -c core.quotepath=false -c core.safecrlf=false @gitArgs 2>$null
        return @{ ExitCode = $LASTEXITCODE; Lines = @($lines | Where-Object { $_ -is [string] }) }
    } finally {
        $ErrorActionPreference = $previous
    }
}

function Get-ChangedFilesFromGit([string]$ref) {
    Push-Location $repoRoot
    try {
        if (-not $ref) {
            $ref = "origin/main"
            if ((Invoke-Git @("rev-parse", "--verify", "--quiet", "$ref^{commit}")).ExitCode -ne 0) { $ref = "main" }
        }
        $mergeBase = Invoke-Git @("merge-base", $ref, "HEAD")
        if ($mergeBase.ExitCode -ne 0 -or $mergeBase.Lines.Count -eq 0) {
            Fail "git merge-base $ref HEAD に失敗しました。-Base に存在する参照を指定してください。"
        }
        # merge-base からの差分は「コミット済み＋ステージ済み＋未ステージ」を 1 回で取れる。未追跡は別途足す
        $diff = Invoke-Git @("diff", "--name-only", $mergeBase.Lines[0])
        if ($diff.ExitCode -ne 0) { Fail "git diff に失敗しました。" }
        $untracked = Invoke-Git @("ls-files", "--others", "--exclude-standard")
        return @($diff.Lines) + @($untracked.Lines)
    } finally {
        Pop-Location
    }
}

if ($filesGiven) {
    $rawFiles = @($Files)
} elseif ($FilesFromStdin) {
    $rawFiles = @()
    while (($line = [Console]::In.ReadLine()) -ne $null) { $rawFiles += $line }
} else {
    $rawFiles = @(Get-ChangedFilesFromGit $Base)
}
$changed = @($rawFiles | Where-Object { $_ -and $_.Trim() } | ForEach-Object { Normalize-Path $_ } | Sort-Object -Unique)
$changedSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($c in $changed) { [void]$changedSet.Add($c) }

# ---- glob 照合 ----
# '*' は 1 階層（'/' を跨がない）、'**' は複数階層。それ以外の文字はリテラル
function ConvertTo-GlobRegex([string]$glob) {
    $g = Normalize-Path $glob
    $escaped = [regex]::Escape($g)
    $pattern = $escaped.Replace('\*\*/', '(?:.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
    return New-Object System.Text.RegularExpressions.Regex ("^" + $pattern + "$"), ([System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

function Get-MatchedFiles([string[]]$globs) {
    $hits = @()
    foreach ($glob in $globs) {
        $re = ConvertTo-GlobRegex $glob
        foreach ($c in $changed) {
            if ($re.IsMatch($c)) { $hits += $c }
        }
    }
    return @($hits | Sort-Object -Unique)
}

$commonHits = Get-MatchedFiles @($map.common)

$affected = @()
$notUpdated = @()
$publishedDir = (Normalize-Path $map.publishedDir).TrimEnd('/')
foreach ($shot in $map.screenshots) {
    $reasons = @($commonHits) + @(Get-MatchedFiles @($shot.sources))
    $reasons = @($reasons | Sort-Object -Unique)
    if ($reasons.Count -eq 0) { continue }
    $updated = $changedSet.Contains("$publishedDir/$($shot.name)")
    $affected += [pscustomobject]@{
        name    = $shot.name
        pass    = $shot.pass
        filter  = $shot.filter
        updated = $updated
        reasons = $reasons
    }
    if (-not $updated) { $notUpdated += $shot.name }
}

# ---- 出力 ----
if ($Json) {
    $out = [ordered]@{
        changedFiles = $changed.Count
        affected     = @($affected | ForEach-Object { [ordered]@{ name = $_.name; pass = $_.pass; filter = $_.filter; updated = $_.updated; reasons = @($_.reasons) } })
        notUpdated   = @($notUpdated)
        passes       = $map.passes
    }
    # ConvertTo-Json は要素 1 個の配列をスカラーへ潰すことがあるため、Depth を十分に取り、配列は @() で包んでから渡す
    [Console]::Out.Write(($out | ConvertTo-Json -Depth 6))
    [Console]::Out.WriteLine()
} else {
    foreach ($a in $affected) {
        $state = if ($a.updated) { "updated" } else { "not-updated" }
        [Console]::Out.WriteLine(($a.name, $a.pass, $state, ($a.reasons -join ',')) -join "`t")
    }
}

if ($Verify -and $notUpdated.Count -gt 0) { exit 3 }
exit 0
