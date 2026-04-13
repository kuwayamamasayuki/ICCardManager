<#
.SYNOPSIS
    設計書Markdownファイルを .docx / .pdf に変換する

.DESCRIPTION
    ICCardManager/docs/design/ 配下のMarkdownファイルを、Mermaid図を含めて
    Word (.docx) および PDF 形式に変換します。

    初回実行時に mermaid-cli を自動インストールします（要 Node.js）。
    生成ファイルは docs/design/output/ に出力されます（git管理対象外）。

    必要なツール:
      - Pandoc  : https://pandoc.org/installing.html
      - Node.js : https://nodejs.org/ (Mermaid図の変換に使用)
      - Chrome  : PDF変換に使用（-Format docx の場合は不要）

.PARAMETER Format
    出力形式を指定します。
      all  : docx と pdf の両方を出力（デフォルト）
      docx : Word形式のみ
      pdf  : PDF形式のみ

.PARAMETER File
    特定のファイルのみ変換する場合にファイル名を指定します（例: "01_*.md"）。
    省略時は docs/design/ 配下のすべての .md を変換します（README.md を除く）。

.EXAMPLE
    .\docs\design\convert-docs.ps1
    .\docs\design\convert-docs.ps1 -Format docx
    .\docs\design\convert-docs.ps1 -File "01_*.md"
    .\docs\design\convert-docs.ps1 -Format pdf -File "06_シーケンス図.md"
#>
param(
    [ValidateSet("all", "docx", "pdf")]
    [string]$Format = "all",

    [string]$File = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ============================================================
# パス設定（スクリプトは docs/design/ に配置されている前提）
# ============================================================
$DesignDir   = $PSScriptRoot
$OutputDir   = Join-Path $DesignDir "output"
$TempDir     = Join-Path $OutputDir ".temp"
$ImageDir    = Join-Path $TempDir "images"
$MmdcDir     = Join-Path $OutputDir ".mermaid-cli"

# ============================================================
# ユーティリティ
# ============================================================
function Write-Step {
    param([int]$Step, [int]$Total, [string]$Message)
    Write-Host "[$Step/$Total] $Message" -ForegroundColor Cyan
}

function Write-Detail {
    param([string]$Message, [string]$Color = "DarkGray")
    Write-Host "       $Message" -ForegroundColor $Color
}

function Write-FileResult {
    param([string]$FileName, [bool]$Success, [string]$Error = "")
    if ($Success) {
        Write-Host "       -> $FileName" -ForegroundColor Green
    }
    else {
        Write-Host "       -> $FileName [失敗] $Error" -ForegroundColor Red
    }
}

# ============================================================
# 前提条件チェック
# ============================================================
function Assert-Prerequisites {
    $errors = @()

    if (-not (Get-Command pandoc -ErrorAction SilentlyContinue)) {
        $errors += "pandoc が見つかりません (https://pandoc.org/installing.html)"
    }

    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        $errors += "Node.js が見つかりません (https://nodejs.org/)"
    }

    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        $errors += "npm が見つかりません (Node.js に同梱)"
    }

    # Chrome（PDF生成時のみ必要）
    if ($Format -in @("all", "pdf")) {
        $script:ChromePath = Find-Chrome
        if (-not $script:ChromePath) {
            $errors += "Google Chrome が見つかりません (PDF変換に必要。-Format docx なら不要)"
        }
    }

    if ($errors.Count -gt 0) {
        Write-Host ""
        Write-Host "[エラー] 必要なツールが不足しています:" -ForegroundColor Red
        $errors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        Write-Host ""
        exit 1
    }
}

function Find-Chrome {
    $candidates = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
    )
    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

# ============================================================
# mermaid-cli セットアップ（初回のみインストール）
# ============================================================
function Initialize-MermaidCli {
    $script:MmdcExe = Join-Path $MmdcDir "node_modules\.bin\mmdc.cmd"

    if (Test-Path $script:MmdcExe) {
        Write-Detail "mermaid-cli: キャッシュ済み"
        return
    }

    Write-Detail "mermaid-cli をインストールしています（初回のみ）..." "Yellow"
    New-Item -ItemType Directory -Path $MmdcDir -Force | Out-Null

    Push-Location $MmdcDir
    try {
        # package.json が必要
        if (-not (Test-Path (Join-Path $MmdcDir "package.json"))) {
            '{"private":true}' | Out-File -FilePath (Join-Path $MmdcDir "package.json") -Encoding utf8
        }
        & npm install "@mermaid-js/mermaid-cli" --save 2>&1 | Out-Null
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path $script:MmdcExe)) {
        throw "mermaid-cli のインストールに失敗しました。npm install の出力を確認してください。"
    }
    Write-Detail "mermaid-cli: インストール完了" "Green"
}

# ============================================================
# Puppeteer設定ファイル生成（Chrome再利用でChromiumダウンロード回避）
# ============================================================
function New-PuppeteerConfig {
    param([string]$Path)

    $chrome = Find-Chrome
    if ($chrome) {
        $escaped = $chrome.Replace('\', '\\')
        @"
{
  "executablePath": "$escaped",
  "args": ["--no-sandbox", "--disable-setuid-sandbox"]
}
"@ | Out-File -FilePath $Path -Encoding utf8 -NoNewline
    }
    else {
        "{}" | Out-File -FilePath $Path -Encoding utf8 -NoNewline
    }
}

# ============================================================
# Mermaid コードブロック → PNG 変換
# ============================================================
function Convert-MermaidToImages {
    param(
        [string]$InputFile,
        [string]$ProcessedFile,
        [string]$PuppeteerConfigPath
    )

    $lines = Get-Content $InputFile -Encoding UTF8
    $output    = [System.Collections.Generic.List[string]]::new()
    $buffer    = [System.Collections.Generic.List[string]]::new()
    $inMermaid = $false
    $index     = 0
    $baseName  = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
    $converted = 0
    $failed    = 0

    foreach ($line in $lines) {
        # Mermaid ブロック開始
        if (-not $inMermaid -and $line -match '^\s*```mermaid') {
            $inMermaid = $true
            $buffer.Clear()
            continue
        }

        # Mermaid ブロック終了 → 画像に変換
        if ($inMermaid -and $line -match '^\s*```\s*$') {
            $inMermaid = $false
            $index++

            $mmdFile = Join-Path $ImageDir "${baseName}_${index}.mmd"
            $pngFile = Join-Path $ImageDir "${baseName}_${index}.png"

            # .mmd ファイルに書き出し
            [System.IO.File]::WriteAllText($mmdFile, ($buffer -join "`n"), [System.Text.Encoding]::UTF8)

            # mmdc で PNG にレンダリング
            try {
                $mmArgs = @("-i", $mmdFile, "-o", $pngFile, "-b", "white", "-w", "800", "-s", "2")
                if ($PuppeteerConfigPath -and (Test-Path $PuppeteerConfigPath)) {
                    $mmArgs += @("--puppeteerConfigFile", $PuppeteerConfigPath)
                }
                & $script:MmdcExe @mmArgs 2>&1 | Out-Null

                if (Test-Path $pngFile) {
                    # 絶対パスで画像参照（pandoc --embed-resources で埋め込まれる）
                    $output.Add("")
                    $output.Add("![]($($pngFile.Replace('\','/')))")
                    $output.Add("")
                    $converted++
                }
                else { throw "PNGファイルが生成されませんでした" }
            }
            catch {
                Write-Warning "  Mermaid図 #${index} の変換に失敗: $_"
                # フォールバック: コードブロックのまま残す
                $output.Add('```')
                $buffer | ForEach-Object { $output.Add($_) }
                $output.Add('```')
                $failed++
            }
            continue
        }

        if ($inMermaid) { $buffer.Add($line) }
        else            { $output.Add($line) }
    }

    [System.IO.File]::WriteAllText($ProcessedFile, ($output -join "`n"), [System.Text.Encoding]::UTF8)

    return @{ Converted = $converted; Failed = $failed }
}

# ============================================================
# CSS テンプレート（HTML → PDF 用）
# ============================================================
function New-CssFile {
    param([string]$Path)

    @'
@charset "UTF-8";
@page {
    size: A4;
    margin: 15mm 20mm;
}
body {
    font-family: "Yu Gothic Medium", "游ゴシック Medium", "Meiryo", "メイリオ", sans-serif;
    font-size: 10.5pt;
    line-height: 1.8;
    color: #333;
    max-width: 780px;
    margin: 0 auto;
    padding: 0 20px;
}
h1 {
    font-size: 20pt;
    border-bottom: 3px solid #2c3e50;
    padding-bottom: 8px;
    margin-top: 40px;
    page-break-before: always;
}
h1:first-of-type { page-break-before: avoid; }
h2 {
    font-size: 16pt;
    border-bottom: 1px solid #bdc3c7;
    padding-bottom: 4px;
    margin-top: 30px;
}
h3 { font-size: 13pt; margin-top: 24px; }
h4 { font-size: 11pt; margin-top: 20px; }
table {
    border-collapse: collapse;
    width: 100%;
    margin: 16px 0;
    font-size: 9.5pt;
    page-break-inside: auto;
}
thead { background-color: #2c3e50; color: white; }
th { padding: 8px 12px; text-align: left; font-weight: bold; }
td { padding: 6px 12px; border: 1px solid #ddd; }
tr:nth-child(even) { background-color: #f8f9fa; }
tr { page-break-inside: avoid; }
code {
    font-family: "Cascadia Code", "Consolas", "MS Gothic", monospace;
    background-color: #f4f4f4;
    padding: 2px 6px;
    border-radius: 3px;
    font-size: 9pt;
}
pre {
    background-color: #f8f8f8;
    border: 1px solid #e0e0e0;
    border-radius: 4px;
    padding: 12px 16px;
    overflow-x: auto;
    font-size: 8.5pt;
    line-height: 1.5;
    page-break-inside: avoid;
}
pre code { background-color: transparent; padding: 0; }
img {
    max-width: 100%;
    height: auto;
    display: block;
    margin: 16px auto;
}
blockquote {
    border-left: 4px solid #3498db;
    margin: 16px 0;
    padding: 8px 16px;
    background-color: #eef6fc;
    color: #2c3e50;
}
strong { color: #2c3e50; }
@media print {
    body { margin: 0; padding: 0; max-width: none; }
    h1, h2, h3 { page-break-after: avoid; }
    table, figure, img { page-break-inside: avoid; }
    pre { white-space: pre-wrap; word-wrap: break-word; }
}
'@ | Out-File -FilePath $Path -Encoding utf8 -NoNewline
}

# ============================================================
# Chrome ヘッドレスで HTML → PDF 変換
# ============================================================
function Convert-HtmlToPdf {
    param(
        [string]$HtmlFile,
        [string]$PdfFile
    )

    $htmlUri = "file:///$($HtmlFile.Replace('\', '/'))"
    $arguments = "--headless=new --disable-gpu --no-sandbox --disable-software-rasterizer --run-all-compositor-stages-before-draw `"--print-to-pdf=$PdfFile`" `"$htmlUri`""

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:ChromePath
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardOutput.ReadToEnd() | Out-Null
    $proc.StandardError.ReadToEnd() | Out-Null
    $proc.WaitForExit()

    if (-not (Test-Path $PdfFile) -or (Get-Item $PdfFile).Length -eq 0) {
        throw "Chrome がPDFを生成できませんでした (exit code: $($proc.ExitCode))"
    }
}

# ============================================================
# メイン処理
# ============================================================
$totalSteps = 5
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  設計書 Markdown -> docx/pdf 変換" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# --- Step 1: 前提条件 ---
Write-Step 1 $totalSteps "前提条件の確認"
Assert-Prerequisites
Write-Detail "pandoc, Node.js, npm: OK" "Green"
if ($script:ChromePath) {
    Write-Detail "Chrome: OK" "Green"
}

# --- ディレクトリ作成 ---
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
New-Item -ItemType Directory -Path $TempDir   -Force | Out-Null
New-Item -ItemType Directory -Path $ImageDir  -Force | Out-Null

# --- 対象ファイル取得 ---
if ($File) {
    $mdFiles = @(Get-ChildItem -Path $DesignDir -Filter $File |
                 Where-Object { $_.Extension -eq ".md" } |
                 Sort-Object Name)
    if ($mdFiles.Count -eq 0) {
        Write-Host ""
        Write-Host "[エラー] '$File' に一致するファイルがありません: $DesignDir" -ForegroundColor Red
        exit 1
    }
}
else {
    $mdFiles = @(Get-ChildItem -Path $DesignDir -Filter "*.md" |
                 Where-Object { $_.Name -ne "README.md" } |
                 Sort-Object Name)
}

Write-Host ""
Write-Detail "対象: $($mdFiles.Count) ファイル ($Format)"
$mdFiles | ForEach-Object { Write-Detail "  - $($_.Name)" }

# --- Step 2: mermaid-cli セットアップ ---
Write-Host ""
Write-Step 2 $totalSteps "mermaid-cli のセットアップ"
Initialize-MermaidCli

$puppeteerConfig = Join-Path $TempDir "puppeteer-config.json"
New-PuppeteerConfig -Path $puppeteerConfig

# --- Step 3: テンプレート準備 ---
Write-Host ""
Write-Step 3 $totalSteps "テンプレート準備"
$cssFile = Join-Path $TempDir "design-doc.css"
New-CssFile -Path $cssFile
Write-Detail "CSS テンプレート生成完了"

# --- Step 4: ファイル変換 ---
Write-Host ""
Write-Step 4 $totalSteps "ファイル変換"
Write-Host ""

$results = @{
    DocxSuccess = 0; DocxFail = 0
    PdfSuccess  = 0; PdfFail  = 0
    MermaidConverted = 0; MermaidFailed = 0
}

$fileIndex = 0
foreach ($mdFile in $mdFiles) {
    $fileIndex++
    $name = $mdFile.BaseName

    Write-Host "  [$fileIndex/$($mdFiles.Count)] $($mdFile.Name)" -ForegroundColor White

    # --- Mermaid 前処理 ---
    $processedMd = Join-Path $TempDir "$($name)_processed.md"
    $mermaidResult = Convert-MermaidToImages `
        -InputFile $mdFile.FullName `
        -ProcessedFile $processedMd `
        -PuppeteerConfigPath $puppeteerConfig

    $results.MermaidConverted += $mermaidResult.Converted
    $results.MermaidFailed    += $mermaidResult.Failed

    if ($mermaidResult.Converted -gt 0 -or $mermaidResult.Failed -gt 0) {
        Write-Detail "Mermaid図: $($mermaidResult.Converted) 件変換$(if ($mermaidResult.Failed -gt 0) { ", $($mermaidResult.Failed) 件失敗" })"
    }

    # --- docx 変換 ---
    if ($Format -in @("all", "docx")) {
        $docxFile = Join-Path $OutputDir "$name.docx"
        try {
            & pandoc $processedMd `
                -f markdown `
                -t docx `
                -o $docxFile `
                --resource-path="$DesignDir;$ImageDir" `
                --wrap=none 2>&1 |
                ForEach-Object {
                    if ($_ -is [System.Management.Automation.ErrorRecord]) { throw $_ }
                }
            Write-FileResult "$name.docx" $true
            $results.DocxSuccess++
        }
        catch {
            Write-FileResult "$name.docx" $false $_
            $results.DocxFail++
        }
    }

    # --- PDF 変換 (Markdown → HTML → Chrome headless → PDF) ---
    if ($Format -in @("all", "pdf")) {
        $htmlFile = Join-Path $TempDir "$name.html"
        $pdfFile  = Join-Path $OutputDir "$name.pdf"
        try {
            # Step A: Markdown → 自己完結型 HTML
            & pandoc $processedMd `
                -f markdown `
                -t html5 `
                -o $htmlFile `
                --standalone `
                --embed-resources `
                --resource-path="$DesignDir;$ImageDir" `
                --css="$cssFile" `
                --metadata title="$name" `
                --wrap=none 2>&1 |
                ForEach-Object {
                    if ($_ -is [System.Management.Automation.ErrorRecord]) { throw $_ }
                }

            # Step B: HTML → PDF (Chrome headless)
            Convert-HtmlToPdf -HtmlFile $htmlFile -PdfFile $pdfFile

            Write-FileResult "$name.pdf" $true
            $results.PdfSuccess++
        }
        catch {
            Write-FileResult "$name.pdf" $false $_
            $results.PdfFail++
        }
    }
}

# --- Step 5: クリーンアップ ---
Write-Host ""
Write-Step 5 $totalSteps "一時ファイルのクリーンアップ"
if (Test-Path $TempDir) {
    Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Detail "完了"

# --- サマリー ---
$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  変換結果サマリー" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

if ($Format -in @("all", "docx")) {
    $color = if ($results.DocxFail -eq 0) { "Green" } else { "Yellow" }
    Write-Host "  docx    : 成功 $($results.DocxSuccess) / 失敗 $($results.DocxFail)" -ForegroundColor $color
}
if ($Format -in @("all", "pdf")) {
    $color = if ($results.PdfFail -eq 0) { "Green" } else { "Yellow" }
    Write-Host "  pdf     : 成功 $($results.PdfSuccess) / 失敗 $($results.PdfFail)" -ForegroundColor $color
}
if ($results.MermaidConverted -gt 0 -or $results.MermaidFailed -gt 0) {
    $color = if ($results.MermaidFailed -eq 0) { "Green" } else { "Yellow" }
    Write-Host "  Mermaid : 変換 $($results.MermaidConverted) / 失敗 $($results.MermaidFailed)" -ForegroundColor $color
}

Write-Host ""
Write-Host "  出力先  : $OutputDir" -ForegroundColor Cyan
Write-Host "  所要時間: $($elapsed.ToString('mm\:ss'))" -ForegroundColor DarkGray
Write-Host ""
