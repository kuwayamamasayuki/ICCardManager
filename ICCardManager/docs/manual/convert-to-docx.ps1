# マニュアル Word変換スクリプト
# 使用方法:
#   .\convert-to-docx.ps1              # 全マニュアルを変換（更新があるもののみ）
#   .\convert-to-docx.ps1 -Force       # 全マニュアルを強制変換
#   .\convert-to-docx.ps1 -Target user # ユーザーマニュアルのみ変換
#   .\convert-to-docx.ps1 -NoMermaid   # Mermaidフィルターを使用しない
# 前提条件:
#   1. pandocがインストールされていること
#      インストール: winget install pandoc または https://pandoc.org/installing.html
#   2. Mermaid図をレンダリングする場合、mermaid-filterが必要
#      インストール: npm install -g mermaid-filter
#   3. ページ番号・余白を設定する場合、リファレンスドキュメントが必要
#      作成: .\create-reference-doc.ps1 を実行

param(
    [ValidateSet("all", "user", "user-summary", "admin", "dev")]
    [string]$Target = "all",
    [switch]$Force,
    [switch]$NoMermaid
)

$ErrorActionPreference = "Stop"

# スクリプトのディレクトリを取得
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# マニュアル定義
$Manuals = @(
    @{
        Name = "ユーザーマニュアル"
        Key = "user"
        Input = "ユーザーマニュアル.md"
        Output = "ユーザーマニュアル.docx"
        Title = "交通系ICカード管理システム ユーザーマニュアル"
    },
    @{
        Name = "ユーザーマニュアル概要版"
        Key = "user-summary"
        Input = "ユーザーマニュアル概要版.md"
        Output = "ユーザーマニュアル概要版.docx"
        Title = "交通系ICカード管理システム 操作ガイド（概要版）"
    },
    @{
        Name = "管理者マニュアル"
        Key = "admin"
        Input = "管理者マニュアル.md"
        Output = "管理者マニュアル.docx"
        Title = "交通系ICカード管理システム 管理者マニュアル"
    },
    @{
        Name = "開発者ガイド"
        Key = "dev"
        Input = "開発者ガイド.md"
        Output = "開発者ガイド.docx"
        Title = "交通系ICカード管理システム 開発者ガイド"
    }
)

# 対象マニュアルをフィルタ
if ($Target -ne "all") {
    $Manuals = $Manuals | Where-Object { $_.Key -eq $Target }
}

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " マニュアル Word変換" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# pandocの確認
Write-Host "[準備] pandocの確認..." -ForegroundColor Yellow
$PandocPath = Get-Command pandoc -ErrorAction SilentlyContinue

if (-not $PandocPath) {
    Write-Host "エラー: pandocが見つかりません。" -ForegroundColor Red
    Write-Host ""
    Write-Host "pandocをインストールしてください:" -ForegroundColor Yellow
    Write-Host "  方法1: winget install pandoc" -ForegroundColor White
    Write-Host "  方法2: https://pandoc.org/installing.html からダウンロード" -ForegroundColor White
    Write-Host ""
    exit 1
}
Write-Host "  pandoc: $($PandocPath.Source)" -ForegroundColor Green

# リファレンスドキュメントの確認
$ReferenceDocPath = Join-Path $ScriptDir "reference.docx"
$UseReferenceDoc = Test-Path $ReferenceDocPath
if ($UseReferenceDoc) {
    Write-Host "  reference.docx: 使用する（ページ番号・余白を適用）" -ForegroundColor Green
} else {
    Write-Host "  警告: reference.docx が見つかりません。ページ番号・余白はデフォルトになります。" -ForegroundColor Yellow
    Write-Host "  作成: .\create-reference-doc.ps1 を実行してください。" -ForegroundColor Gray
}

# mermaid-filterの確認
$UseMermaidFilter = $false
if (-not $NoMermaid) {
    Write-Host "[準備] mermaid-filterの確認..." -ForegroundColor Yellow
    $MermaidFilterPath = Get-Command mermaid-filter.cmd -ErrorAction SilentlyContinue

    if ($MermaidFilterPath) {
        $UseMermaidFilter = $true
        Write-Host "  mermaid-filter: $($MermaidFilterPath.Source)" -ForegroundColor Green
    } else {
        Write-Host "  警告: mermaid-filterが見つかりません。Mermaid図はテキストのまま出力されます。" -ForegroundColor Yellow
        Write-Host "  インストール: npm install -g mermaid-filter" -ForegroundColor Gray
    }
} else {
    Write-Host "[準備] mermaid-filter: スキップ (-NoMermaid指定)" -ForegroundColor Gray
}
Write-Host ""

# 変換結果の追跡
$ConvertedCount = 0
$SkippedCount = 0
$ErrorCount = 0

# 各マニュアルを処理
foreach ($Manual in $Manuals) {
    $InputPath = Join-Path $ScriptDir $Manual.Input
    $OutputPath = Join-Path $ScriptDir $Manual.Output

    Write-Host "--------------------------------------" -ForegroundColor Gray
    Write-Host "[$($Manual.Name)]" -ForegroundColor Cyan

    # 入力ファイルの確認
    if (-not (Test-Path $InputPath)) {
        Write-Host "  スキップ: 入力ファイルが見つかりません" -ForegroundColor Yellow
        Write-Host "    $InputPath" -ForegroundColor Gray
        $SkippedCount++
        continue
    }

    # 更新チェック（-Forceでない場合）
    if (-not $Force -and (Test-Path $OutputPath)) {
        $InputInfo = Get-Item $InputPath
        $OutputInfo = Get-Item $OutputPath

        if ($OutputInfo.LastWriteTime -ge $InputInfo.LastWriteTime) {
            Write-Host "  スキップ: 変更なし（.docxが最新）" -ForegroundColor Gray
            Write-Host "    .md:   $($InputInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor Gray
            Write-Host "    .docx: $($OutputInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor Gray
            $SkippedCount++
            continue
        }
    }

    # 変換実行
    if ($UseMermaidFilter) {
        Write-Host "  変換中（Mermaidフィルター有効）..." -ForegroundColor Yellow
    } else {
        Write-Host "  変換中..." -ForegroundColor Yellow
    }

    # Issue #454対応: --tocを削除（Markdownファイルに目次が既にあるため、重複を防ぐ）
    $PandocArgs = @(
        $InputPath,
        "-o", $OutputPath,
        "--from", "markdown",
        "--to", "docx",
        "--metadata", "title=$($Manual.Title)",
        "--metadata", "author=システム管理者",
        "--metadata", "lang=ja-JP"
    )

    # リファレンスドキュメントが存在する場合、使用する（ページ番号・余白を適用）
    if ($UseReferenceDoc) {
        $PandocArgs += @("--reference-doc", $ReferenceDocPath)
    }

    # mermaid-filterが有効な場合、フィルターを追加
    if ($UseMermaidFilter) {
        $PandocArgs += @("-F", "mermaid-filter.cmd")
    }

    try {
        & pandoc $PandocArgs
        if ($LASTEXITCODE -ne 0) {
            throw "pandocがエラーコード $LASTEXITCODE を返しました"
        }

        $FileInfo = Get-Item $OutputPath
        Write-Host "  完了: $($Manual.Output)" -ForegroundColor Green
        Write-Host "    サイズ: $([math]::Round($FileInfo.Length / 1KB, 2)) KB" -ForegroundColor Gray
        $ConvertedCount++
    }
    catch {
        Write-Host "  エラー: 変換に失敗しました" -ForegroundColor Red
        Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        $ErrorCount++
    }
}

# 結果サマリ
Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " 完了!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  変換: $ConvertedCount 件" -ForegroundColor $(if ($ConvertedCount -gt 0) { "Green" } else { "Gray" })
Write-Host "  スキップ: $SkippedCount 件" -ForegroundColor Gray
if ($ErrorCount -gt 0) {
    Write-Host "  エラー: $ErrorCount 件" -ForegroundColor Red
}
Write-Host ""

# エラーがあった場合は終了コード1
if ($ErrorCount -gt 0) {
    exit 1
}
