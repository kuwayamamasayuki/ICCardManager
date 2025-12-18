@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

echo ======================================
echo  ICCardManager インストーラービルド
echo ======================================
echo.

set "SCRIPT_DIR=%~dp0"
set "PROJECT_ROOT=%SCRIPT_DIR%.."
set "SRC_DIR=%PROJECT_ROOT%\src\ICCardManager"
set "PUBLISH_DIR=%PROJECT_ROOT%\publish"
set "OUTPUT_DIR=%SCRIPT_DIR%output"

:: Inno Setup のパスを探す
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" (
    set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
) else if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" (
    set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
)

if "%ISCC%"=="" (
    echo エラー: Inno Setup が見つかりません。
    echo Inno Setup 6 をインストールしてください: https://jrsoftware.org/isinfo.php
    pause
    exit /b 1
)

echo [1/3] アプリケーションのビルド...
pushd "%SRC_DIR%"
dotnet publish -c Release -r win-x64 --self-contained true -o "%PUBLISH_DIR%" -v q
if errorlevel 1 (
    echo エラー: ビルドに失敗しました。
    popd
    pause
    exit /b 1
)
popd
echo   ビルド完了

echo.
echo [2/3] ファイルの確認...
if not exist "%PUBLISH_DIR%\ICCardManager.exe" (
    echo エラー: ICCardManager.exe が見つかりません。
    pause
    exit /b 1
)
echo   実行ファイル: OK

echo.
echo [3/3] インストーラーの作成...
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
"%ISCC%" "%SCRIPT_DIR%ICCardManager.iss"
if errorlevel 1 (
    echo エラー: インストーラーの作成に失敗しました。
    pause
    exit /b 1
)

echo.
echo ======================================
echo  ビルド完了!
echo ======================================
echo.
echo インストーラーは %OUTPUT_DIR% に作成されました。
echo.
pause
