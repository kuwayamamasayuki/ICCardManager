@echo off
setlocal

rem Wrapper for convert-to-docx.ps1 (manual conversion: .md -> .docx).
rem Usage:
rem   convert-to-docx.bat                 convert all manuals (only those updated)
rem   convert-to-docx.bat /force          convert all manuals unconditionally
rem   convert-to-docx.bat /nomermaid      do not use mermaid-filter
rem   convert-to-docx.bat user-summary    convert one manual
rem   Target names are the same as -Target of convert-to-docx.ps1:
rem   intro / user / user-summary / admin / quickstart / it / dev
rem
rem The list of manuals lives only in manual-targets.ps1. Do not write a list of
rem manuals or a pandoc command line back into this file (Issue #2241).
rem Keep this file ASCII only: cmd.exe misreads multibyte characters in batch files.

rem Capture the script folder before "shift" (shift also moves %0).
set "SCRIPT_DIR=%~dp0"
set "PS_ARGS="

:parse_args
if "%~1"=="" goto :run
if /i "%~1"=="/force" (set "PS_ARGS=%PS_ARGS% -Force" & goto :next)
if /i "%~1"=="-force" (set "PS_ARGS=%PS_ARGS% -Force" & goto :next)
if /i "%~1"=="/nomermaid" (set "PS_ARGS=%PS_ARGS% -NoMermaid" & goto :next)
if /i "%~1"=="-nomermaid" (set "PS_ARGS=%PS_ARGS% -NoMermaid" & goto :next)
set "PS_ARGS=%PS_ARGS% -Target %~1"
:next
shift
goto :parse_args

:run
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%convert-to-docx.ps1"%PS_ARGS%
exit /b %ERRORLEVEL%
