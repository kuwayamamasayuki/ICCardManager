@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

rem マニュアル Word変換スクリプト
rem 使用方法:
rem   convert-to-docx.bat         全マニュアルを変換（更新があるもののみ）
rem   convert-to-docx.bat /force  全マニュアルを強制変換
rem   convert-to-docx.bat user    ユーザーマニュアルのみ変換
rem   convert-to-docx.bat admin   管理者マニュアルのみ変換
rem   convert-to-docx.bat dev     開発者ガイドのみ変換

set "SCRIPT_DIR=%~dp0"
set "TARGET=all"
set "FORCE=0"
set "CONVERTED=0"
set "SKIPPED=0"
set "ERRORS=0"

rem 引数の解析
if "%~1"=="/force" set "FORCE=1"
if "%~1"=="-force" set "FORCE=1"
if "%~1"=="user" set "TARGET=user"
if "%~1"=="admin" set "TARGET=admin"
if "%~1"=="dev" set "TARGET=dev"

echo ======================================
echo  マニュアル Word変換
echo ======================================
echo.

rem pandocの確認
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

rem ユーザーマニュアル
if "%TARGET%"=="all" call :convert_manual "ユーザーマニュアル" "ユーザーマニュアル.md" "ユーザーマニュアル.docx" "交通系ICカード管理システム ユーザーマニュアル"
if "%TARGET%"=="user" call :convert_manual "ユーザーマニュアル" "ユーザーマニュアル.md" "ユーザーマニュアル.docx" "交通系ICカード管理システム ユーザーマニュアル"

rem 管理者マニュアル
if "%TARGET%"=="all" call :convert_manual "管理者マニュアル" "管理者マニュアル.md" "管理者マニュアル.docx" "交通系ICカード管理システム 管理者マニュアル"
if "%TARGET%"=="admin" call :convert_manual "管理者マニュアル" "管理者マニュアル.md" "管理者マニュアル.docx" "交通系ICカード管理システム 管理者マニュアル"

rem 開発者ガイド
if "%TARGET%"=="all" call :convert_manual "開発者ガイド" "開発者ガイド.md" "開発者ガイド.docx" "交通系ICカード管理システム 開発者ガイド"
if "%TARGET%"=="dev" call :convert_manual "開発者ガイド" "開発者ガイド.md" "開発者ガイド.docx" "交通系ICカード管理システム 開発者ガイド"

rem 結果サマリ
echo.
echo ======================================
echo  完了!
echo ======================================
echo.
echo   変換: %CONVERTED% 件
echo   スキップ: %SKIPPED% 件
if %ERRORS% gtr 0 echo   エラー: %ERRORS% 件
echo.

if %ERRORS% gtr 0 exit /b 1
exit /b 0

rem ========================================
rem サブルーチン: マニュアル変換
rem %1: マニュアル名
rem %2: 入力ファイル名
rem %3: 出力ファイル名
rem %4: タイトル
rem ========================================
:convert_manual
set "MANUAL_NAME=%~1"
set "INPUT_FILE=%SCRIPT_DIR%%~2"
set "OUTPUT_FILE=%SCRIPT_DIR%%~3"
set "TITLE=%~4"

echo --------------------------------------
echo [%MANUAL_NAME%]

rem 入力ファイルの確認
if not exist "%INPUT_FILE%" (
    echo   スキップ: 入力ファイルが見つかりません
    set /a SKIPPED+=1
    goto :eof
)

rem 更新チェック（/forceでない場合）
if %FORCE%==0 (
    if exist "%OUTPUT_FILE%" (
        rem ファイルの更新日時を比較
        for %%I in ("%INPUT_FILE%") do set "INPUT_TIME=%%~tI"
        for %%O in ("%OUTPUT_FILE%") do set "OUTPUT_TIME=%%~tO"

        rem 簡易比較（文字列比較）
        rem 注意: この方法は完全ではないが、多くのケースで動作する
        if "!OUTPUT_TIME!" geq "!INPUT_TIME!" (
            echo   スキップ: 変更なし（.docxが最新）
            set /a SKIPPED+=1
            goto :eof
        )
    )
)

rem 変換実行
echo   変換中...
pandoc "%INPUT_FILE%" -o "%OUTPUT_FILE%" --from markdown --to docx --toc --toc-depth=2 --metadata title="%TITLE%" --metadata author="システム管理者" --metadata lang=ja-JP

if errorlevel 1 (
    echo   エラー: 変換に失敗しました
    set /a ERRORS+=1
    goto :eof
)

echo   完了: %~3
set /a CONVERTED+=1
goto :eof
