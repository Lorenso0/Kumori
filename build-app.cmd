@echo off
setlocal
cd /d "%~dp0"

REM ============================================================
REM  Kumori WPF app (new .NET solution) build script.
REM  This script is the active .NET build/publish path.
REM
REM  Usage:
REM    build-app.cmd            build Debug + run tests
REM    build-app.cmd run        build then launch the app
REM    build-app.cmd publish    Release publish (self-contained,
REM                             single-file, ReadyToRun) to dist\app
REM ============================================================

if /i "%~1"=="publish" goto :publish

dotnet publish replay_viewer\Kumori.ReplayViewer.csproj -c Debug -r win-x64 ^
  --self-contained false ^
  -o replay_viewer\bin\Debug\net8.0\win-x64
if errorlevel 1 exit /b %errorlevel%

dotnet build Kumori.sln -c Debug
if errorlevel 1 exit /b %errorlevel%

xcopy /E /I /Y replay_viewer\bin\Debug\net8.0\win-x64 src\Kumori.App\bin\Debug\net8.0-windows\Kumori.ReplayViewer >nul
if errorlevel 1 exit /b %errorlevel%

dotnet test Kumori.sln -c Debug --no-build
if errorlevel 1 exit /b %errorlevel%

if /i "%~1"=="run" (
    dotnet run --project src\Kumori.App -c Debug --no-build
    exit /b %errorlevel%
)

echo.
echo Build + tests OK.
exit /b 0

:publish
dotnet publish src\Kumori.App\Kumori.App.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:PublishReadyToRun=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o dist\app
if errorlevel 1 exit /b %errorlevel%

echo.
echo Published to dist\app\Kumori.exe
echo Bundled replay viewer: dist\app\Kumori.ReplayViewer\Kumori.ReplayViewer.exe
exit /b 0
