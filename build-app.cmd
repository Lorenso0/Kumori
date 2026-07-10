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
if exist artifacts\viewer-release rmdir /S /Q artifacts\viewer-release
if exist artifacts\Kumori.ReplayViewer.zip del /Q artifacts\Kumori.ReplayViewer.zip
if exist artifacts\app-publish rmdir /S /Q artifacts\app-publish
if exist dist\app rmdir /S /Q dist\app

dotnet publish replay_viewer\Kumori.ReplayViewer.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -p:PublishReadyToRun=false ^
  -o artifacts\viewer-release
if errorlevel 1 exit /b %errorlevel%

powershell -NoProfile -NonInteractive -Command "Compress-Archive -Path 'artifacts\viewer-release\*' -DestinationPath 'artifacts\Kumori.ReplayViewer.zip' -CompressionLevel Optimal"
if errorlevel 1 exit /b %errorlevel%

dotnet publish src\Kumori.App\Kumori.App.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:PublishReadyToRun=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:IncludeAllContentForSelfExtract=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -p:ReplayViewerBundlePath="%CD%\artifacts\Kumori.ReplayViewer.zip" ^
  -o artifacts\app-publish
if errorlevel 1 exit /b %errorlevel%

mkdir dist\app
copy /Y artifacts\app-publish\Kumori.exe dist\app\Kumori.exe >nul
if errorlevel 1 exit /b %errorlevel%

echo.
echo Published to dist\app\Kumori.exe
echo The replay viewer is embedded in Kumori.exe and extracts to %%APPDATA%%\Kumori\runtime when required.
exit /b 0
