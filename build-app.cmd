@echo off
setlocal
cd /d "%~dp0"
if not defined KUMORI_VERSION set KUMORI_VERSION=0.8.5

REM ============================================================
REM  Kumori WPF app (new .NET solution) build script.
REM  This script is the active .NET build/publish path.
REM
REM  Usage:
REM    build-app.cmd            build Debug + run tests
REM    build-app.cmd run        build then launch the app (skip tests)
REM    build-app.cmd publish    Release publish (self-contained,
REM                             single-file, ReadyToRun) to dist\app
REM ============================================================

echo Ensuring osu! 2026.726.0 source is available...
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\update-lazer.ps1
if errorlevel 1 exit /b %errorlevel%

if /i "%~1"=="publish" goto :publish

REM Resident Debug processes lock shared assemblies. Stop only executables
REM launched from this checkout before every development build; otherwise
REM MSBuild can spend tens of seconds retrying copies before it fails.
set "KUMORI_DEBUG_APP=%CD%\src\Kumori.App\bin\Debug\net10.0-windows10.0.17763.0\Kumori.exe"
set "KUMORI_DEBUG_VIEWER=%CD%\replay_viewer\bin\Debug\net10.0\win-x64\Kumori.ReplayViewer.exe"
powershell -NoProfile -NonInteractive -Command "$targets=@([IO.Path]::GetFullPath($env:KUMORI_DEBUG_APP),[IO.Path]::GetFullPath($env:KUMORI_DEBUG_VIEWER)); function Find-DebugProcesses { @(Get-Process -Name Kumori,Kumori.ReplayViewer -ErrorAction SilentlyContinue | Where-Object { try { $targets -contains [IO.Path]::GetFullPath($_.Path) } catch { $false } }) }; $running=@(Find-DebugProcesses); if ($running.Count -eq 0) { exit 0 }; Write-Host 'Stopping running Debug processes before rebuilding...'; $running | Stop-Process -Force; $deadline=[DateTime]::UtcNow.AddSeconds(10); do { Start-Sleep -Milliseconds 100; $remaining=@(Find-DebugProcesses) } while ($remaining.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline); if ($remaining.Count -gt 0) { Write-Error 'Could not stop the running Debug processes.'; exit 1 }"
if errorlevel 1 (
    echo.
    echo Failed to stop the running Debug processes. Build cancelled.
    exit /b 1
)

if /i "%~1"=="run" (
    set "KUMORI_DEV_RESTORE_REQUIRED="
    if not exist "src\Kumori.App\obj\project.assets.json" set "KUMORI_DEV_RESTORE_REQUIRED=1"
    if not exist "replay_viewer\obj\project.assets.json" set "KUMORI_DEV_RESTORE_REQUIRED=1"
    if defined KUMORI_DEV_RESTORE_REQUIRED (
        echo Restoring development dependencies...
        dotnet restore Kumori.Dev.slnf -p:Version=%KUMORI_VERSION%
        if errorlevel 1 exit /b 1
    )
    dotnet build Kumori.Dev.slnf -c Debug --no-restore -p:Version=%KUMORI_VERSION% -nr:false
) else (
    dotnet build Kumori.sln -c Debug -p:Version=%KUMORI_VERSION% -nr:false
)
if errorlevel 1 exit /b %errorlevel%

if /i not "%~1"=="run" (
    xcopy /D /E /I /Y replay_viewer\bin\Debug\net10.0\win-x64 src\Kumori.App\bin\Debug\net10.0-windows10.0.17763.0\Kumori.ReplayViewer >nul
    if errorlevel 1 exit /b 1
)

if /i "%~1"=="run" (
    echo.
    echo Tests skipped. Launching Kumori...
    "%KUMORI_DEBUG_APP%"
    exit /b
)

dotnet test Kumori.sln -c Debug --no-build -nr:false
if errorlevel 1 exit /b %errorlevel%

echo.
echo Build + tests OK.
exit /b 0

:publish
if exist artifacts\native-tools-release rmdir /S /Q artifacts\native-tools-release
if exist artifacts\Kumori.NativeTools.zip del /Q artifacts\Kumori.NativeTools.zip
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
  -o artifacts\native-tools-release
if errorlevel 1 exit /b %errorlevel%

powershell -NoProfile -NonInteractive -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path 'artifacts\native-tools-release').Path, (Join-Path (Resolve-Path 'artifacts').Path 'Kumori.NativeTools.zip'), [System.IO.Compression.CompressionLevel]::Optimal, $false)"
if errorlevel 1 exit /b %errorlevel%

powershell -NoProfile -NonInteractive -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $archive=[System.IO.Compression.ZipFile]::OpenRead((Resolve-Path 'artifacts\Kumori.NativeTools.zip')); try { foreach ($required in @('Kumori.ReplayViewer.exe','THIRD-PARTY-NOTICES.md','OSU-LICENCE')) { if (-not ($archive.Entries | Where-Object FullName -eq $required)) { throw ('Native-tools bundle is missing ' + $required + '.') } } } finally { $archive.Dispose() }"
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
  -p:NativeToolsBundlePath="%CD%\artifacts\Kumori.NativeTools.zip" ^
  -o artifacts\app-publish
if errorlevel 1 exit /b %errorlevel%

mkdir dist\app
copy /Y artifacts\app-publish\Kumori.exe dist\app\Kumori.exe >nul
if errorlevel 1 exit /b %errorlevel%

echo.
echo Published to dist\app\Kumori.exe
echo Replay Viewer is embedded in Kumori.exe and extracts atomically when required.
exit /b 0
