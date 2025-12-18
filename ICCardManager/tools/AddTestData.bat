@echo off
chcp 65001 > nul
echo ========================================
echo  印刷プレビュー テストデータ追加ツール
echo ========================================
echo.

REM Pythonで実行
python "%~dp0add_test_data.py"

echo.
pause
