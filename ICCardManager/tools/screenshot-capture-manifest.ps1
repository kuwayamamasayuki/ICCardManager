# 直近の撮影で「どの画像が実際に作られたか」を記録・検証する
# Issue #2095
#
# 用途:
#   take-screenshots-uitest.ps1 の撮影側と -Publish 側は別プロセスで走るため、
#   「auto\ にファイルが存在するか」しか共有できていなかった。auto\ は .gitignore 対象で
#   前回の実行の成果物が残り続けるので、撮影が漏れた画像は前回の残骸がそのまま再公開される。
#   ここでは「存在」ではなく「今回の撮影で作られたか」を、撮影側が書くマニフェスト 1 か所で判定する。
#
# モード（いずれか 1 つを指定する）:
#   -Clear    撮影の前に、対象画像と失敗時の切り分け用画像（<name>_FAILED.png）を出力先から消す。
#             消した後は「存在＝今回作られた」が成立する。マニフェストも消す（途中で中断したときに
#             前回の記録が残らないようにする）
#   -Write    撮影の後に、対象のうち実際に作られたものを captured、作られなかったものを notCaptured として
#             マニフェスト（<OutputDir>\.screenshot-manifest.json）へ書く
#   -Verify   公開の前に、マニフェストを読んで対象がすべて今回の撮影で作られたかを確かめる
#
# 出力:
#   -Json を付けると結果を JSON で標準出力へ出す（呼び出し側が件数・名前を使えるようにする）
#
# 終了コード:
#   0 = 正常（-Verify では対象がすべて今回の撮影で作られている）
#   2 = 使い方・引数の誤り
#   3 = -Verify で、今回の撮影で作られていない対象がある（マニフェストが無い場合を含む）

[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$OutputDir,
    # powershell.exe -File 経由では `-Targets a b c` の b c が残余引数へ落ちる（[string[]] へは a しか束縛されない）ため、
    # 残余引数を -Targets の続きとして合流させる（screenshot-sync.ps1 と同じ作法）
    [string[]]$Targets,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingTargets,
    [switch]$Clear,
    [switch]$Write,
    [switch]$Verify,
    # -Write のときに記録する撮影開始時刻（ISO 8601）。省略時は現在時刻
    [string]$StartedAt,
    [switch]$Json,
    [Alias("h", "?")]
    [switch]$Help
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# マニフェストのファイル名。先頭のドットで一覧から目立たせない。*.png ではないので
# -Publish の Get-ChildItem -Filter *.png には拾われない
$ManifestFileName = ".screenshot-manifest.json"

function Show-Usage {
    Write-Host @"
使い方: .\tools\screenshot-capture-manifest.ps1 -OutputDir <path> -Targets <name.png>... (-Clear | -Write [-StartedAt <iso>] | -Verify) [-Json]

  直近の撮影で「どの画像が実際に作られたか」を記録・検証します（Issue #2095）。

モード:
  -Clear     撮影前に対象画像と <name>_FAILED.png、マニフェストを出力先から消す
  -Write     撮影後にマニフェストを書く（captured / notCaptured）
  -Verify    公開前にマニフェストを読み、対象がすべて今回の撮影で作られたかを確かめる

オプション:
  -OutputDir <path>     撮影の出力先（既定の運用では docs\screenshots\auto）
  -Targets <name>...    対象の画像名。対応表（screenshot-sources.json）由来の名前だけを渡すこと
  -StartedAt <iso>      -Write が記録する撮影開始時刻。省略時は現在時刻
  -Json                 結果を JSON で標準出力へ出す
  -Help                 この使い方を表示する
"@
}

function Fail([string]$message, [int]$code = 2) {
    [Console]::Error.WriteLine("[ERROR] $message")
    exit $code
}

if ($Help) { Show-Usage; exit 0 }

# @($null) は 1 要素の配列になるため、未指定のパラメータ由来の null を除いてから合流させる
$Targets = @(@($Targets) + @($RemainingTargets) | Where-Object { $null -ne $_ -and $_ -ne "" })

$modes = @($Clear.IsPresent, $Write.IsPresent, $Verify.IsPresent) | Where-Object { $_ }
if ($modes.Count -ne 1) {
    [Console]::Error.WriteLine("[ERROR] -Clear / -Write / -Verify のいずれか 1 つを指定してください。")
    Show-Usage
    exit 2
}
if (-not $OutputDir) { Fail "-OutputDir を指定してください。" }
if ($Targets.Count -eq 0) { Fail "-Targets に対象の画像名を 1 つ以上指定してください。" }

# 対象名は「対応表に載っている画像名」しか受け付けない。-Clear がファイルを消す以上、
# パス区切りや親ディレクトリ参照を含む値、拡張子の違う値を通すと、利用者が別用途で置いた
# ファイルまで消し得る（Issue #2095 の修正方針）。判定はモードによらず入口で行う
foreach ($t in $Targets) {
    if ($t -notmatch '^[A-Za-z0-9_.\-]+\.png$') {
        Fail "対象の画像名が不正です: $t （対応表に載っている '<name>.png' の形だけを指定できます）"
    }
    if ($t -match '^\.') {
        Fail "対象の画像名が不正です: $t （ドットで始まる名前は指定できません）"
    }
}
$Targets = @($Targets | Select-Object -Unique)

if (-not (Test-Path -LiteralPath $OutputDir -PathType Container)) {
    if ($Clear) {
        # 撮影前の掃除は、出力先がまだ無くても「消すものが無い」だけで成功でよい
        if ($Json) { @{ removed = @() } | ConvertTo-Json -Compress }
        exit 0
    }
    Fail "出力先が見つかりません: $OutputDir"
}
$outputDirFull = (Resolve-Path -LiteralPath $OutputDir).Path
$manifestPath = Join-Path $outputDirFull $ManifestFileName

# ---- -Clear: 撮影前に対象の残骸を消す ----
if ($Clear) {
    $removed = @()
    foreach ($t in $Targets) {
        $failed = [System.IO.Path]::GetFileNameWithoutExtension($t) + "_FAILED.png"
        foreach ($name in @($t, $failed)) {
            $path = Join-Path $outputDirFull $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Remove-Item -LiteralPath $path -Force
                $removed += $name
            }
        }
    }
    # 途中で中断したときに前回のマニフェストが「今回の記録」として読まれないよう、先に消す
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        Remove-Item -LiteralPath $manifestPath -Force
        $removed += $ManifestFileName
    }
    if ($Json) { @{ removed = @($removed) } | ConvertTo-Json -Compress }
    exit 0
}

# ---- -Write: 撮影後に「今回作られたもの」を記録する ----
if ($Write) {
    $captured = @($Targets | Where-Object { Test-Path -LiteralPath (Join-Path $outputDirFull $_) -PathType Leaf })
    $notCaptured = @($Targets | Where-Object { $captured -notcontains $_ })
    $startedAtValue = if ($StartedAt) { $StartedAt } else { (Get-Date).ToString("o") }
    $manifest = [ordered]@{
        startedAt   = $startedAtValue
        completedAt = (Get-Date).ToString("o")
        targets     = @($Targets)
        captured    = @($captured)
        notCaptured = @($notCaptured)
    }
    # ConvertTo-Json は既定で深さ 2 までしか展開しないが、ここは 1 階層なので既定で足りる
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    if ($Json) { $manifest | ConvertTo-Json -Compress }
    exit 0
}

# ---- -Verify: 公開前に「今回の撮影で作られたか」を確かめる ----
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    [Console]::Error.WriteLine("[ERROR] 直近の撮影の記録がありません: $manifestPath")
    [Console]::Error.WriteLine("        出力先に画像が残っていても、今回の撮影で作られたものとは限りません。先に撮影してください。")
    if ($Json) {
        @{ manifestFound = $false; captured = @(); notCaptured = @($Targets | ForEach-Object { @{ name = $_; reason = "no-manifest" } }) } | ConvertTo-Json -Compress -Depth 4
    }
    exit 3
}

try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
} catch {
    Fail "直近の撮影の記録を JSON として読めません: $manifestPath`n$($_.Exception.Message)" 3
}
$captured = @($manifest.captured)
# マニフェストに captured として載っていても、その後にファイルが消されていれば公開できない
$verified = @($Targets | Where-Object { $captured -contains $_ -and (Test-Path -LiteralPath (Join-Path $outputDirFull $_) -PathType Leaf) })
$notCaptured = @()
foreach ($t in $Targets) {
    if ($verified -contains $t) { continue }
    $reason = if ($captured -contains $t) { "missing-file" } elseif (@($manifest.targets) -contains $t) { "not-captured" } else { "not-targeted" }
    $notCaptured += @{ name = $t; reason = $reason }
}

if ($Json) {
    @{
        manifestFound = $true
        startedAt     = $manifest.startedAt
        captured      = @($verified)
        notCaptured   = @($notCaptured)
    } | ConvertTo-Json -Compress -Depth 4
}
if ($notCaptured.Count -gt 0) {
    foreach ($n in $notCaptured) {
        $text = switch ($n.reason) {
            "not-captured" { "直近の撮影の対象だったが作られなかった（テストがスキップまたは失敗した可能性があります）" }
            "not-targeted" { "直近の撮影の対象ではない（出力先にあるのは前回以前の画像です）" }
            default        { "直近の撮影では作られたが、その後ファイルが無くなっている" }
        }
        [Console]::Error.WriteLine("[ERROR] $($n.name): $text")
    }
    exit 3
}
exit 0
