@echo off
REM ============================================================
REM Mapped Drive Detection Diagnostic Script (Issue #1584)
REM
REM HOW TO USE:
REM   1. Double-click this file. DO NOT run as administrator.
REM   2. mapped_drives_diag.txt is saved on your Desktop
REM      and opens automatically in Notepad.
REM   3. Send that txt file to the developer.
REM ============================================================

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-mapped-drives.ps1"

echo.
echo Done. See mapped_drives_diag.txt on your Desktop.
echo.
pause
