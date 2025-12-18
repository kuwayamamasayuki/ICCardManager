# テストデータ追加スクリプト（PowerShell）
# 印刷プレビューのページネーション確認用
#
# 使い方:
# 1. PowerShellを開く
# 2. このスクリプトのあるディレクトリに移動
# 3. .\AddTestData.ps1 を実行

$ErrorActionPreference = "Stop"

# データベースパス（LocalAppData を使用）
$dbPath = "$env:LOCALAPPDATA\ICCardManager\iccard.db"
$sqlFile = Join-Path $PSScriptRoot "add_test_ledger_data.sql"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 印刷プレビュー テストデータ追加ツール" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# データベースの存在確認
if (-not (Test-Path $dbPath)) {
    Write-Host "[ERROR] データベースが見つかりません: $dbPath" -ForegroundColor Red
    Write-Host ""
    Write-Host "アプリケーションを一度起動してデータベースを初期化してください。" -ForegroundColor Yellow
    exit 1
}

Write-Host "[INFO] データベース: $dbPath" -ForegroundColor Green
Write-Host "[INFO] SQLファイル: $sqlFile" -ForegroundColor Green
Write-Host ""

# SQLiteの存在確認
$sqlite3 = $null
$possiblePaths = @(
    "sqlite3",
    "C:\Program Files\SQLite\sqlite3.exe",
    "C:\sqlite\sqlite3.exe",
    "$env:LOCALAPPDATA\Programs\Python\Python*\Scripts\sqlite3.exe"
)

foreach ($path in $possiblePaths) {
    if (Get-Command $path -ErrorAction SilentlyContinue) {
        $sqlite3 = $path
        break
    }
}

if (-not $sqlite3) {
    Write-Host "[WARN] sqlite3 が見つかりません。" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "以下のいずれかの方法でSQLite3をインストールしてください:" -ForegroundColor Yellow
    Write-Host "  1. https://www.sqlite.org/download.html からダウンロード" -ForegroundColor Yellow
    Write-Host "  2. winget install SQLite.SQLite" -ForegroundColor Yellow
    Write-Host "  3. choco install sqlite" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "または、DB Browser for SQLite等のGUIツールでSQLを実行してください。" -ForegroundColor Yellow
    Write-Host "SQLファイル: $sqlFile" -ForegroundColor Cyan
    exit 1
}

Write-Host "[INFO] sqlite3: $sqlite3" -ForegroundColor Green
Write-Host ""

# 確認
Write-Host "テストデータを追加します。続行しますか？ (Y/N): " -NoNewline -ForegroundColor Yellow
$confirm = Read-Host
if ($confirm -ne "Y" -and $confirm -ne "y") {
    Write-Host "キャンセルしました。" -ForegroundColor Red
    exit 0
}

# SQLの実行
Write-Host ""
Write-Host "[INFO] SQLを実行中..." -ForegroundColor Cyan

try {
    # SQLファイルの内容を読み込んで直接実行（パスの問題を回避）
    $sqlContent = Get-Content -Path $sqlFile -Raw -Encoding UTF8

    # 一時ファイルに書き出し（ASCII安全なパスを使用）
    $tempSqlFile = Join-Path $env:TEMP "iccard_test_data.sql"
    $sqlContent | Out-File -FilePath $tempSqlFile -Encoding UTF8

    # パスをフォワードスラッシュに変換（sqlite3のエスケープ問題を回避）
    $tempSqlFileForSqlite = $tempSqlFile -replace '\\', '/'
    $dbPathForSqlite = $dbPath -replace '\\', '/'

    Write-Host "[DEBUG] Temp SQL: $tempSqlFileForSqlite" -ForegroundColor Gray
    Write-Host "[DEBUG] DB Path: $dbPathForSqlite" -ForegroundColor Gray

    # sqlite3で実行（stdinにSQLを渡す方式）
    $result = Get-Content -Path $tempSqlFile -Raw | & $sqlite3 $dbPathForSqlite 2>&1
    Write-Host $result

    # 一時ファイルを削除
    Remove-Item -Path $tempSqlFile -ErrorAction SilentlyContinue

    Write-Host ""
    Write-Host "[SUCCESS] テストデータの追加が完了しました！" -ForegroundColor Green
    Write-Host ""
    Write-Host "アプリを起動して、印刷プレビューを確認してください。" -ForegroundColor Cyan
    Write-Host "テストカード: はやかけん TEST-001" -ForegroundColor Cyan
}
catch {
    Write-Host "[ERROR] SQLの実行に失敗しました: $_" -ForegroundColor Red
    exit 1
}
