# マニュアル用スクリーンショット自動撮影スクリプト
# Issue #2016 / #2021
#
# 用途:
#   UI テスト基盤（FlaUI）でアプリを起動し、マニュアルが参照する画面を自動撮影する。
#   対話式の TakeScreenshots.ps1 と違い、画面を開く操作も自動で行う。
#   撮影対象と、各画像がどのパス（起動構成・テストフィルタ）で撮られるかは
#   docs\screenshots\screenshot-sources.json（対応表）が唯一の定義。
#   （職員証・交通系ICカードのタッチを要する staff_recognized / lend / return / busstop も
#     Debug パスで撮影する。Issue #2019。帳票プレビュー・インストーラーなど、対応表に定義の
#     無い画面だけが引き続き TakeScreenshots.ps1 の対象）
#
# 使い方:
#   PowerShell（Windows ネイティブ）で実行する:
#     cd <repo>\ICCardManager
#     .\tools\take-screenshots-uitest.ps1              # 全画像を撮影
#     .\tools\take-screenshots-uitest.ps1 -Changed     # 画面に影響する変更があった画像だけ撮影（Issue #2021）
#     .\tools\take-screenshots-uitest.ps1 -Publish     # 撮影せず、auto\ の画像を docs\screenshots\ へ上書き
#     .\tools\take-screenshots-uitest.ps1 -Changed -Publish   # 影響を受けた画像だけ上書き
#
#   オプション:
#     -Changed          : 対応表（screenshot-sources.json）と git の差分から、撮り直しが必要な画像だけを対象にする
#     -Base <ref>       : -Changed の比較元（既定: origin/main。無ければ main）
#     -OutputDir <path> : 出力先（既定: docs\screenshots\auto。.gitignore 対象）
#     -Publish          : 撮影は行わず、直近の撮影で作られた画像を docs\screenshots\ へ上書きコピーする
#                         （先に撮影 → auto\ の画像を見比べる → -Publish、の 2 段階で使う）
#     -SkipBuild        : 本体・UITests の再ビルドをスキップ
#     -Help             : 使い方を表示（未知の引数を渡した場合も使い方を表示して終了する）
#
# 注意:
#   - 本体は対応表の passes に書かれた構成でビルド・起動する。Release パス（#2016）と、タッチを要する画面の
#     Debug パス（#2019。仮想タッチが Debug 限定。ICCARDMANAGER_SCREENSHOT_MODE=1 で操作パネルを透明にする）
#   - 撮影中はアプリのウィンドウが前面に出る。マウス・キーボードに触れないこと
#   - 既存の DB（%ProgramData%\ICCardManager\iccard.db）は撮影中だけ退避され、終了後に復元される
#   - 表示スケールは 100% を推奨（docs\screenshots\README.md）
#   - 撮影の前に対象画像を出力先から消し、撮影後に「今回作られた画像」をマニフェスト
#     （auto\.screenshot-manifest.json）へ記録する。-Publish はこれを見て、撮影が漏れた画像の
#     残骸が再公開されるのを防ぐ（Issue #2095。判定は screenshot-capture-manifest.ps1 の 1 か所）

# 位置指定引数を無効化する。有効のままだと `--help` のような未知の引数が最初の文字列パラメータ
# （-OutputDir）に束縛され、その名前のフォルダーが作られてしまう。
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$OutputDir,
    [switch]$Changed,
    [string]$Base,
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
使い方: .\tools\take-screenshots-uitest.ps1 [-Changed [-Base <ref>]] [-OutputDir <path>] [-SkipBuild]
        .\tools\take-screenshots-uitest.ps1 -Publish [-Changed [-Base <ref>]] [-OutputDir <path>]

  UI テスト基盤（FlaUI）でアプリを起動し、マニュアル用スクリーンショットを自動撮影します。
  対象と撮り方は docs\screenshots\screenshot-sources.json で定義します。
  Windows ネイティブの PowerShell から実行してください（WSL2 不可）。

オプション:
  -Changed           画面に影響するソースの変更（git の差分）から、撮り直しが必要な画像だけを対象にする
  -Base <ref>        -Changed の比較元。既定: origin/main（無ければ main）
  -OutputDir <path>  出力先。既定: docs\screenshots\auto（Git 管理外）
  -Publish           撮影は行わず、直近の撮影で作られた画像を docs\screenshots\ へ上書きコピーする
                     （先に撮影して auto\ の画像を見比べてから実行する 2 段階の運用。-Changed と併用すると対象の画像だけ）
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
if ($PSBoundParameters.ContainsKey('Base') -and -not $Changed) {
    Write-Host "[ERROR] -Base は -Changed と一緒に指定してください。" -ForegroundColor Red
    exit 2
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$projectRoot = Join-Path $repoRoot "ICCardManager"
$mainCsproj = Join-Path $projectRoot "src\ICCardManager\ICCardManager.csproj"
$uiTestsCsproj = Join-Path $projectRoot "tests\ICCardManager.UITests\ICCardManager.UITests.csproj"
$publishedDir = Join-Path $projectRoot "docs\screenshots"
$mappingPath = Join-Path $publishedDir "screenshot-sources.json"
$syncScript = Join-Path $PSScriptRoot "screenshot-sync.ps1"
$manifestScript = Join-Path $PSScriptRoot "screenshot-capture-manifest.ps1"
if (-not $OutputDir) { $OutputDir = Join-Path $publishedDir "auto" }

# 撮影と -Publish は別プロセスなので、「今回の撮影で作られたか」はマニフェスト 1 か所で判定する（Issue #2095）。
# 子スクリプトは日本語を UTF-8 で出す。native の stderr で例外にしないよう ErrorActionPreference を落として呼ぶ
function Invoke-ManifestScript {
    param([string[]]$ScriptArgs)

    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $allArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $manifestScript, "-OutputDir", $OutputDir) + $ScriptArgs
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $out = & powershell.exe @allArgs
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEap
    }
    return @{ ExitCode = $code; Output = ($out -join "`n") }
}

# ---- 対応表を読む（撮影対象とパスの定義。撮り直しの判定も同じ表で行う） ----
if (-not (Test-Path $mappingPath)) {
    Write-Host "[ERROR] 対応表が見つかりません: $mappingPath" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $manifestScript)) {
    Write-Host "[ERROR] 撮影記録スクリプトが見つかりません: $manifestScript" -ForegroundColor Red
    exit 1
}
$mapping = Get-Content -LiteralPath $mappingPath -Raw -Encoding UTF8 | ConvertFrom-Json
$allShots = @($mapping.screenshots)

# ---- 撮影対象を決める ----
# -Changed: screenshot-sync.ps1 に git の差分と対応表を突き合わせてもらい、影響を受けた画像だけを対象にする
if ($Changed) {
    $syncArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $syncScript, "-Json")
    if ($Base) { $syncArgs += @("-Base", $Base) }
    # 子スクリプトは UTF-8 で出力する（日本語のパスが理由に入っても文字化けさせない）
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $syncOutput = & powershell.exe @syncArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] 撮り直しの判定に失敗しました（screenshot-sync.ps1 ExitCode: $LASTEXITCODE）" -ForegroundColor Red
        exit 1
    }
    $sync = ($syncOutput -join "`n") | ConvertFrom-Json
    $targets = @($sync.affected)
    $baseLabel = if ($Base) { $Base } else { "origin/main" }
    if ($targets.Count -eq 0) {
        Write-Host "画面に影響する変更はありません（比較元: $baseLabel、変更ファイル $($sync.changedFiles) 件）。撮り直しは不要です。" -ForegroundColor Green
        exit 0
    }
    Write-Host "画面に影響する変更があります（比較元: $baseLabel）。撮り直す画像: $($targets.Count) 枚" -ForegroundColor Yellow
    foreach ($t in $targets) {
        Write-Host "   $($t.name) ← $($t.reasons -join ', ')" -ForegroundColor DarkGray
    }
    Write-Host ""
} else {
    $targets = $allShots
}
$targetNames = @($targets | ForEach-Object { $_.name })

# -Publish: 撮影せず、出力先にある画像をそのまま docs\screenshots\ へ上書きする。
# 撮影と同時に上書きすると、見比べる前に既存画像が置き換わってしまうため分けている。
if ($Publish) {
    if (-not (Test-Path $OutputDir)) {
        Write-Host "[ERROR] 出力先が見つかりません: $OutputDir" -ForegroundColor Red
        Write-Host "        先に引数なしで実行して撮影してください。" -ForegroundColor Yellow
        exit 1
    }
    # 対応表に載っている画像だけを差し替える。無条件に *.png をコピーすると、
    # 失敗時の切り分け用に残る history_FAILED.png のような成果物まで docs\screenshots\ へ公開され、
    # そのままコミットされ得る（-Changed の有無によらず効かせる。コードレビューで検出）
    $staged = @(Get-ChildItem -Path $OutputDir -Filter *.png | Where-Object { $targetNames -contains $_.Name })
    # 「出力先にあるか」は「今回の撮影で作られたか」ではない。auto\ は .gitignore 対象で前回の実行の成果物が
    # 残り続けるため、撮影が漏れた画像は残骸がそのまま再公開され、内容が前回と同じなら git diff にも現れない
    # （Issue #2095）。判定はマニフェスト 1 か所へ寄せる（-Changed の有無によらず効かせる）
    $verify = Invoke-ManifestScript (@("-Verify") + @("-Targets") + $targetNames)
    if ($verify.ExitCode -ne 0) {
        Write-Host "[ERROR] 今回の撮影で作られていない画像を公開しようとしています（上の行が対象）。" -ForegroundColor Red
        Write-Host "        出力先に残る前回以前の画像は公開しません。" -ForegroundColor Yellow
        $captureHint = if ($Changed) { ".\tools\take-screenshots-uitest.ps1 -Changed" } else { ".\tools\take-screenshots-uitest.ps1" }
        Write-Host "        先に撮影してください: $captureHint" -ForegroundColor Yellow
        exit 1
    }
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
Write-Host " Issue #2016 / #2021" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

foreach ($p in @($mainCsproj, $uiTestsCsproj)) {
    if (-not (Test-Path $p)) {
        Write-Host "[ERROR] プロジェクトが見つかりません: $p" -ForegroundColor Red
        exit 1
    }
}

# ---- 対象をパスごとにまとめる（パスの定義は対応表の passes） ----
# 全撮影ではパスのフィルタだけ、-Changed では「パスのフィルタ AND（画像ごとのフィルタの OR）」で絞る
$passes = @()
foreach ($passName in @($mapping.passes.PSObject.Properties.Name)) {
    $def = $mapping.passes.$passName
    $shots = @($targets | Where-Object { $_.pass -eq $passName })
    if ($shots.Count -eq 0) { continue }
    $filter = "($($def.filter))"
    if ($Changed) {
        $filter += "&(" + (($shots | ForEach-Object { $_.filter }) -join "|") + ")"
    }
    $passEnv = @{}
    if ($def.env) { foreach ($p in $def.env.PSObject.Properties) { $passEnv[$p.Name] = [string]$p.Value } }
    $passes += @{ Name = $passName; Configuration = $def.configuration; Filter = $filter; Env = $passEnv; Shots = $shots }
}
if ($passes.Count -eq 0) {
    Write-Host "[ERROR] 撮影対象が対応表のどのパスにも属していません。" -ForegroundColor Red
    exit 1
}

if (-not $SkipBuild) {
    $configs = @($passes | ForEach-Object { $_.Configuration } | Sort-Object -Unique)
    $total = $configs.Count + 1
    $step = 1
    foreach ($cfg in $configs) {
        Write-Host "[$step/$total] 本体を $cfg でビルド中..." -ForegroundColor Green
        dotnet build $mainCsproj --configuration $cfg
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[ERROR] 本体（$cfg）のビルドに失敗しました（ExitCode: $LASTEXITCODE）" -ForegroundColor Red
            exit 1
        }
        $step++
    }
    Write-Host "[$step/$total] UITests をビルド中..." -ForegroundColor Green
    dotnet build $uiTestsCsproj
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] UITests のビルドに失敗しました（ExitCode: $LASTEXITCODE）" -ForegroundColor Red
        exit 1
    }
    Write-Host ""
} else {
    Write-Host "ビルドをスキップ（-SkipBuild 指定）" -ForegroundColor Yellow
    Write-Host ""
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# 撮影の前に対象の残骸（前回の画像・前回の _FAILED.png・前回のマニフェスト）を消す。
# こうして初めて「出力先にある＝今回撮れた」が成立し、撮影漏れが完了報告と -Publish の両方に現れる（Issue #2095）
$clear = Invoke-ManifestScript (@("-Clear") + @("-Targets") + $targetNames)
if ($clear.ExitCode -ne 0) {
    Write-Host "[ERROR] 出力先の前回分を消せませんでした（ExitCode: $($clear.ExitCode)）" -ForegroundColor Red
    exit 1
}
$startedAt = (Get-Date).ToString("o")

Write-Host "撮影中..." -ForegroundColor Green
Write-Host "       出力先: $OutputDir" -ForegroundColor DarkGray
Write-Host "       撮影中はマウス・キーボードに触れないでください。" -ForegroundColor Yellow
Write-Host ""

$testExitCode = 0
foreach ($pass in $passes) {
    Write-Host "── $($pass.Name) パス（$($pass.Shots.Count) 枚、--filter $($pass.Filter)）──" -ForegroundColor Cyan
    # 撮影テストを有効化し、本体の起動構成を指定する（AppFixture / ScreenshotHelper が参照する）
    $env:ICCARDMANAGER_SCREENSHOT = "1"
    $env:ICCARDMANAGER_UITEST_CONFIGURATION = $pass.Configuration
    $env:ICCARDMANAGER_SCREENSHOT_DIR = $OutputDir
    foreach ($kv in $pass.Env.GetEnumerator()) { Set-Item -Path "Env:\$($kv.Key)" -Value $kv.Value }
    try {
        dotnet test $uiTestsCsproj `
            --no-build `
            --filter $pass.Filter `
            --logger "console;verbosity=normal"
        if ($LASTEXITCODE -ne 0) { $testExitCode = $LASTEXITCODE }
    } finally {
        Remove-Item Env:\ICCARDMANAGER_SCREENSHOT -ErrorAction SilentlyContinue
        Remove-Item Env:\ICCARDMANAGER_UITEST_CONFIGURATION -ErrorAction SilentlyContinue
        Remove-Item Env:\ICCARDMANAGER_SCREENSHOT_DIR -ErrorAction SilentlyContinue
        foreach ($kv in $pass.Env.GetEnumerator()) { Remove-Item -Path "Env:\$($kv.Key)" -ErrorAction SilentlyContinue }
    }
    Write-Host ""
}

Write-Host ""
# 撮影の前に対象を消してあるので、ここで存在するものは今回の実行で作られたものだけ。
# 結果はマニフェストへ書き、別プロセスの -Publish が同じ根拠で判定できるようにする（Issue #2095）
$write = Invoke-ManifestScript (@("-Write", "-StartedAt", $startedAt, "-Json") + @("-Targets") + $targetNames)
if ($write.ExitCode -ne 0) {
    Write-Host "[ERROR] 撮影結果を記録できませんでした（ExitCode: $($write.ExitCode)）" -ForegroundColor Red
    exit 1
}
$manifest = $write.Output | ConvertFrom-Json
$captured = @($manifest.captured)
$notCaptured = @($manifest.notCaptured)

Write-Host "========================================" -ForegroundColor Cyan
# 「対象と一致したか」ではなく「今回の実行で作られたか」で数える。xUnit の Skip.If はテスト成功なので
# dotnet test の ExitCode だけでは「撮れなかった」と「撮る必要が無かった」が同じ結果へ畳まれる（Issue #2095）
if ($notCaptured.Count -eq 0 -and $testExitCode -eq 0) {
    Write-Host " ✓ 撮影完了: 今回の実行で $($captured.Count) 枚を撮影（対象 $($targetNames.Count) 枚）" -ForegroundColor Green
    foreach ($n in $captured) { Write-Host "   $n" -ForegroundColor DarkGray }
} else {
    if ($testExitCode -eq 0) {
        Write-Host " ! 撮影されなかった画像があります: $($notCaptured.Count) 枚（今回の実行で撮影 $($captured.Count) 枚 / 対象 $($targetNames.Count) 枚）" -ForegroundColor Yellow
    } else {
        Write-Host " ✗ 撮影に失敗したものがあります（ExitCode: $testExitCode）" -ForegroundColor Red
        Write-Host "   テスト出力を確認してください。DB は自動で復元されています。" -ForegroundColor Yellow
        Write-Host "   今回の実行で撮影 $($captured.Count) 枚 / 対象 $($targetNames.Count) 枚" -ForegroundColor Yellow
    }
    foreach ($n in $notCaptured) { Write-Host "   × $n" -ForegroundColor Yellow }
    if ($notCaptured.Count -gt 0) {
        Write-Host "   撮影テストがスキップされた（リーダー未接続など、その環境では撮れない画像）か、失敗した可能性があります。" -ForegroundColor Yellow
        Write-Host "   これらは -Publish の対象に入れられません（前回の画像をそのまま公開しないため）。" -ForegroundColor Yellow
    }
}
Write-Host "========================================" -ForegroundColor Cyan
if ($testExitCode -eq 0 -and $notCaptured.Count -eq 0 -and $captured.Count -gt 0) {
    $publishHint = if ($Changed) { "-Changed -Publish" } else { "-Publish" }
    Write-Host "画像を見比べて問題なければ、$publishHint で docs\screenshots\ へ上書きしてください。" -ForegroundColor Yellow
}

exit $testExitCode
