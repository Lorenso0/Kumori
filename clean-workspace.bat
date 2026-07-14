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
  "$candidatePaths = @(Get-ChildItem -LiteralPath $root -Directory -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -in @('bin','obj','TestResults') } | ForEach-Object { $_.FullName });" ^
  "$candidatePaths += @('.vs','dist','artifacts') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Container };" ^
  "$safePaths = @($candidatePaths | ForEach-Object {" ^
  "  $resolved = [IO.Path]::GetFullPath($_).TrimEnd([IO.Path]::DirectorySeparatorChar);" ^
  "  if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw ('Refusing path outside workspace: ' + $resolved) };" ^
  "  $resolved" ^
  "} | Sort-Object Length, @{ Expression = { $_ } } -Unique);" ^
  "$targets = New-Object System.Collections.Generic.List[string];" ^
  "foreach ($path in $safePaths) {" ^
  "  $nested = $false;" ^
  "  foreach ($parent in $targets) {" ^
  "    if ($path.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { $nested = $true; break }" ^
  "  };" ^
  "  if (-not $nested) { $targets.Add($path) }" ^
  "};" ^
  "$failures = New-Object System.Collections.Generic.List[string];" ^
  "foreach ($target in $targets) {" ^
  "  if (-not (Test-Path -LiteralPath $target)) { continue };" ^
  "  Write-Host ('Removing ' + $target);" ^
  "  try { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop }" ^
  "  catch { $failures.Add($target + ' -- ' + $_.Exception.Message); Write-Warning ('Could not completely remove ' + $target) }" ^
  "};" ^
  "$after = (Get-ChildItem -LiteralPath $root -File -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum;" ^
  "$reclaimed = [Math]::Max([double]0, [double]($before - $after));" ^
  "Write-Host '';" ^
  "Write-Host ('Cleanup complete. Reclaimed approximately {0:N2} GB.' -f ($reclaimed / 1GB));" ^
  "if ($failures.Count -gt 0) {" ^
  "  Write-Host '';" ^
  "  Write-Warning ('Cleanup left ' + $failures.Count + ' locked or inaccessible target(s):');" ^
  "  $failures | ForEach-Object { Write-Host ('  ' + $_) };" ^
  "  exit 2" ^
  "}"

if errorlevel 1 (
    echo.
    echo Cleanup completed with locked leftovers. Close Kumori, Visual Studio, and any running build or test processes, then run it once more.
    exit /b 1
)

exit /b 0
