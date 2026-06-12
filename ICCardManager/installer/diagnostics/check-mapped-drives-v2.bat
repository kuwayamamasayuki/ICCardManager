@echo off
REM ============================================================
REM Mapped Drive Detection Diagnostic Script v2 (Issue #1584)
REM
REM HOW TO USE:
REM   1. Double-click this file. DO NOT run as administrator.
REM   2. mapped_drives_diag_v2.txt is saved on your Desktop
REM      and opens automatically in Notepad.
REM   3. Read section [15] for the two additional steps requested
REM      (installer log capture and elevated cmd net use test).
REM   4. Send mapped_drives_diag_v2.txt and the materials from
REM      section [15] back to the developer.
REM ============================================================

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-mapped-drives-v2.ps1"

echo.
echo Done. See mapped_drives_diag_v2.txt on your Desktop.
echo.
pause
