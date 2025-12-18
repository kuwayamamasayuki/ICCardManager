@echo off
chcp 65001 > nul
setlocal

echo ======================================
echo  ユーザーマニュアル Word変換
echo ======================================
echo.

set "SCRIPT_DIR=%~dp0"
set "INPUT_FILE=%SCRIPT_DIR%ユーザーマニュアル.md"
set "OUTPUT_FILE=%SCRIPT_DIR%ユーザーマニュアル.docx"

:: pandocの確認
where pandoc >nul 2>&1
if errorlevel 1 (
    echo エラー: pandocが見つかりません。
    echo.
    echo pandocをインストールしてください:
    echo   方法1: winget install pandoc
    echo   方法2: https://pandoc.org/installing.html からダウンロード
    echo.
    pause
    exit /b 1
)

:: 入力ファイルの確認
if not exist "%INPUT_FILE%" (
    echo エラー: 入力ファイルが見つかりません
    echo   %INPUT_FILE%
    pause
    exit /b 1
)

echo 変換中...
pandoc "%INPUT_FILE%" -o "%OUTPUT_FILE%" --from markdown --to docx --toc --toc-depth=2 --metadata title="交通系ICカード管理システム ユーザーマニュアル" --metadata author="システム管理者" --metadata lang=ja-JP

if errorlevel 1 (
    echo エラー: 変換に失敗しました。
    pause
    exit /b 1
)

echo.
echo ======================================
echo  変換完了!
echo ======================================
echo.
echo 出力ファイル: %OUTPUT_FILE%
echo.

set /p OPEN_FILE="ファイルを開きますか？ (Y/N): "
if /i "%OPEN_FILE%"=="Y" (
    start "" "%OUTPUT_FILE%"
)
