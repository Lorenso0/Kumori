@echo off
setlocal
cd /d "%~dp0"
if not defined KUMORI_VERSION set KUMORI_VERSION=0.4.7

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

REM Re-running the launcher while this Debug build is resident in the tray
REM cannot rebuild its locked output files. Activate that instance instead;
REM developers who need a rebuild can exit it from the tray and run again.
set "KUMORI_DEBUG_APP=%CD%\src\Kumori.App\bin\Debug\net10.0-windows10.0.17763.0\Kumori.exe"
if /i "%~1"=="run" if exist "%KUMORI_DEBUG_APP%" (
    powershell -NoProfile -NonInteractive -Command "$target=[IO.Path]::GetFullPath($env:KUMORI_DEBUG_APP); $running=Get-Process -Name Kumori -ErrorAction SilentlyContinue | Where-Object { try { [IO.Path]::GetFullPath($_.Path) -eq $target } catch { $false } }; if ($running) { exit 0 }; exit 1"
    if not errorlevel 1 (
        echo.
        echo Kumori is already running. Activating the existing Debug instance.
        echo Exit Kumori from its tray icon first if you need to rebuild changes.
        start "" "%KUMORI_DEBUG_APP%"
        exit /b 0
    )
)

dotnet publish replay_viewer\Kumori.ReplayViewer.csproj -c Debug -r win-x64 -p:Version=%KUMORI_VERSION% ^
  --self-contained false ^
  -o replay_viewer\bin\Debug\net10.0\win-x64
if errorlevel 1 exit /b %errorlevel%

dotnet build Kumori.sln -c Debug
if errorlevel 1 exit /b %errorlevel%

xcopy /E /I /Y replay_viewer\bin\Debug\net10.0\win-x64 src\Kumori.App\bin\Debug\net10.0-windows10.0.17763.0\Kumori.ReplayViewer >nul
if errorlevel 1 exit /b %errorlevel%

dotnet test Kumori.sln -c Debug --no-build
if errorlevel 1 exit /b %errorlevel%

if /i "%~1"=="run" (
    dotnet run --project src\Kumori.App\Kumori.App.csproj -c Debug --no-build
    exit /b
)

echo.
echo Build + tests OK.
exit /b 0

:publish
if exist artifacts\viewer-release rmdir /S /Q artifacts\viewer-release
if exist artifacts\Kumori.ReplayViewer.zip del /Q artifacts\Kumori.ReplayViewer.zip
if exist artifacts\app-publish rmdir /S /Q artifacts\app-publish
if exist dist\app rmdir /S /Q dist\app

REM Kumori.StableFrameBridge is built by an MSBuild target rather than a normal
REM ProjectReference. Restore the full solution explicitly so a publish made
REM immediately after clean-workspace.bat has its win-x86 assets file.
dotnet restore Kumori.sln -p:Version=%KUMORI_VERSION%
if errorlevel 1 exit /b %errorlevel%

dotnet publish replay_viewer\Kumori.ReplayViewer.csproj -c Release -r win-x64 -p:Version=%KUMORI_VERSION% ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -p:PublishReadyToRun=false ^
  -o artifacts\viewer-release
if errorlevel 1 exit /b %errorlevel%

powershell -NoProfile -NonInteractive -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path 'artifacts\viewer-release').Path, (Join-Path (Resolve-Path 'artifacts').Path 'Kumori.ReplayViewer.zip'), [System.IO.Compression.CompressionLevel]::Optimal, $false)"
if errorlevel 1 exit /b %errorlevel%

powershell -NoProfile -NonInteractive -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $archive=[System.IO.Compression.ZipFile]::OpenRead((Resolve-Path 'artifacts\Kumori.ReplayViewer.zip')); try { if (-not ($archive.Entries | Where-Object FullName -eq 'Kumori.ReplayViewer.exe')) { throw 'Replay viewer bundle is missing Kumori.ReplayViewer.exe.' } } finally { $archive.Dispose() }"
if errorlevel 1 exit /b %errorlevel%

dotnet publish src\Kumori.App\Kumori.App.csproj -c Release -r win-x64 -p:Version=%KUMORI_VERSION% ^
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
