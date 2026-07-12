@echo off
setlocal

set "ROOT=%~dp0"
set "AUTO_CONFIRM=0"
if /I "%~1"=="--yes" set "AUTO_CONFIRM=1"

echo Kumori workspace cleanup
echo Root: %ROOT%
echo.
echo This removes regenerable workspace data only:
echo   - all bin and obj directories
echo   - TestResults directories
echo   - root dist and artifacts directories
echo   - the root .vs directory
echo.
echo It does not remove source files or %%APPDATA%%\Kumori.
echo.

if "%AUTO_CONFIRM%"=="0" (
    choice /C YN /N /M "Continue? [Y/N] "
    if errorlevel 2 (
        echo Cleanup cancelled.
        exit /b 0
    )
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop';" ^
  "$root = [IO.Path]::GetFullPath('%ROOT%').TrimEnd([IO.Path]::DirectorySeparatorChar);" ^
  "$before = (Get-ChildItem -LiteralPath $root -File -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum;" ^
  "$targets = @(Get-ChildItem -LiteralPath $root -Directory -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -in @('bin','obj','TestResults') });" ^
  "$targets += @('.vs','dist','artifacts') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | ForEach-Object { Get-Item -LiteralPath $_ };" ^
  "$targets = $targets | Sort-Object { $_.FullName.Length } | Select-Object -Unique;" ^
  "foreach ($target in $targets) {" ^
  "  $resolved = [IO.Path]::GetFullPath($target.FullName);" ^
  "  if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw ('Refusing path outside workspace: ' + $resolved) };" ^
  "  Write-Host ('Removing ' + $resolved);" ^
  "  Remove-Item -LiteralPath $resolved -Recurse -Force;" ^
  "};" ^
  "$after = (Get-ChildItem -LiteralPath $root -File -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum;" ^
  "$reclaimed = [Math]::Max(0, $before - $after);" ^
  "Write-Host '';" ^
  "Write-Host ('Cleanup complete. Reclaimed approximately {0:N2} GB.' -f ($reclaimed / 1GB));"

if errorlevel 1 (
    echo.
    echo Cleanup failed. Close Kumori, Visual Studio, and any running build or test processes, then try again.
    exit /b 1
)

exit /b 0
