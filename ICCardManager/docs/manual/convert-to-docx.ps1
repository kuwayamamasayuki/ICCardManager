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
    [ValidateSet("all", "intro", "user", "user-summary", "admin", "dev")]
    [string]$Target = "all",
    [switch]$Force,
    [switch]$NoMermaid
)

$ErrorActionPreference = "Stop"

# Issue #600: テーブル罫線の後処理
# pandocが生成するdocxのテーブルには罫線が含まれないことがある。
# --reference-doc のテーブルスタイル継承はpandocバージョンにより挙動が異なるため、
# 生成後にdocx（ZIPアーカイブ）内のXMLを直接編集して罫線を確実に付与する。
function Add-TableBordersToDocx {
    param([string]$DocxPath)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::Open(
        $DocxPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry("word/document.xml")
        if (-not $entry) { return 0 }

        # XMLを読み込み
        $stream = $entry.Open()
        $xml = New-Object System.Xml.XmlDocument
        $xml.PreserveWhitespace = $true
        $xml.Load($stream)
        $stream.Dispose()

        $ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
        $nsm = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
        $nsm.AddNamespace("w", $ns)

        $tableCount = 0
        $tblPrNodes = $xml.SelectNodes("//w:tblPr", $nsm)

        foreach ($tblPr in $tblPrNodes) {
            # 既に罫線定義がある場合はスキップ
            $existingBorders = $tblPr.SelectSingleNode("w:tblBorders", $nsm)
            if ($existingBorders) { continue }

            # tblBordersを作成（黒・単線・0.5pt）
            $tblBorders = $xml.CreateElement("w", "tblBorders", $ns)

            foreach ($side in @("top", "left", "bottom", "right", "insideH", "insideV")) {
                $border = $xml.CreateElement("w", $side, $ns)
                $border.SetAttribute("val", $ns, "single")
                $border.SetAttribute("sz", $ns, "4")      # 0.5pt
                $border.SetAttribute("space", $ns, "0")
                $border.SetAttribute("color", $ns, "000000")  # 黒
                $tblBorders.AppendChild($border) | Out-Null
            }

            # OOXMLスキーマ順序に合わせてtblLayoutの前に挿入
            $tblLayout = $tblPr.SelectSingleNode("w:tblLayout", $nsm)
            if ($tblLayout) {
                $tblPr.InsertBefore($tblBorders, $tblLayout) | Out-Null
            } else {
                $tblPr.AppendChild($tblBorders) | Out-Null
            }
            $tableCount++
        }

        if ($tableCount -gt 0) {
            # 変更をZIPに書き戻す
            $entry.Delete()
            $newEntry = $archive.CreateEntry(
                "word/document.xml", [System.IO.Compression.CompressionLevel]::Optimal)
            $newStream = $newEntry.Open()
            $xml.Save($newStream)
            $newStream.Dispose()
        }

        return $tableCount
    }
    finally {
        $archive.Dispose()
    }
}

# スクリプトのディレクトリを取得
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# マニュアル定義
$Manuals = @(
    @{
        Name = "はじめに"
        Key = "intro"
        Input = "はじめに.md"
        Output = "はじめに.docx"
        Title = "交通系ICカード管理システム はじめに"
    },
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

# PATHに無い場合、一般的なインストール場所を検索
if (-not $PandocPath) {
    $CommonPaths = @(
        "$env:LOCALAPPDATA\Pandoc\pandoc.exe",
        "$env:ProgramFiles\Pandoc\pandoc.exe",
        "${env:ProgramFiles(x86)}\Pandoc\pandoc.exe"
    )
    foreach ($Path in $CommonPaths) {
        if (Test-Path $Path) {
            $PandocPath = Get-Item $Path
            break
        }
    }
}

if (-not $PandocPath) {
    Write-Host "エラー: pandocが見つかりません。" -ForegroundColor Red
    Write-Host ""
    Write-Host "pandocをインストールしてください:" -ForegroundColor Yellow
    Write-Host "  方法1: winget install pandoc" -ForegroundColor White
    Write-Host "  方法2: https://pandoc.org/installing.html からダウンロード" -ForegroundColor White
    Write-Host ""
    exit 1
}

# パスの取得（Get-CommandとGet-Itemで異なるプロパティ名）
$PandocExe = if ($PandocPath.Source) { $PandocPath.Source } else { $PandocPath.FullName }
Write-Host "  pandoc: $PandocExe" -ForegroundColor Green

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
    # --resource-path: 画像などのリソースをMarkdownファイルの場所から相対パスで解決
    $PandocArgs = @(
        $InputPath,
        "-o", $OutputPath,
        "--from", "markdown",
        "--to", "docx",
        "--resource-path", $ScriptDir,
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
        & $PandocExe $PandocArgs
        if ($LASTEXITCODE -ne 0) {
            throw "pandocがエラーコード $LASTEXITCODE を返しました"
        }

        # Issue #600: テーブルに罫線を追加（後処理）
        try {
            $borderCount = Add-TableBordersToDocx -DocxPath $OutputPath
            if ($borderCount -gt 0) {
                Write-Host "    テーブル罫線: ${borderCount}個のテーブルに適用" -ForegroundColor Gray
            }
        }
        catch {
            Write-Host "    警告: テーブル罫線の後処理をスキップしました: $($_.Exception.Message)" -ForegroundColor Yellow
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
