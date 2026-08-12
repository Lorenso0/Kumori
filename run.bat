@echo off
setlocal
cd /d "%~dp0"

set "KUMORI_RUN_OPTIONS="
if /i "%~1"=="rebuild" set "KUMORI_RUN_OPTIONS=-ForceBuild"
if /i "%~1"=="--rebuild" set "KUMORI_RUN_OPTIONS=-ForceBuild"
if not "%~1"=="" if not defined KUMORI_RUN_OPTIONS (
    echo Usage: run.bat [rebuild]
    exit /b 2
)

powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass ^
    -File "%~dp0scripts\run-local.ps1" %KUMORI_RUN_OPTIONS%
exit /b %errorlevel%
