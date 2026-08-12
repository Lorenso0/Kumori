[CmdletBinding()]
param(
    [switch]$ForceBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location $workspace

# Keep a double-click (or a second terminal invocation) from starting another
# restore/build while this checkout is already being prepared or is running.
$sha256 = [Security.Cryptography.SHA256]::Create()
try
{
    $workspaceHashBytes = $sha256.ComputeHash(
        [Text.Encoding]::UTF8.GetBytes($workspace))
    $workspaceHash = [BitConverter]::ToString($workspaceHashBytes)
    $workspaceHash = $workspaceHash.Replace("-", "").Substring(0, 16)
}
finally
{
    $sha256.Dispose()
}
$runLockPath = Join-Path ([IO.Path]::GetTempPath()) `
    "kumori-local-run-$workspaceHash.lock"
try
{
    $runLock = [IO.File]::Open(
        $runLockPath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
}
catch [IO.IOException]
{
    Write-Host "Kumori is already building or running from this checkout."
    Write-Host "Close the existing run.bat window or Kumori instance first."
    exit 3
}

# This launcher favours keeping the desktop responsive over maximum compile
# throughput. In particular, do not let solution builds fan out into one C#
# compiler per available project/core.
$serialBuildArguments = @(
    "-m:1",
    "-nr:false",
    "-p:BuildInParallel=false"
)

$version = if ([string]::IsNullOrWhiteSpace($env:KUMORI_VERSION)) {
    [xml]$buildProperties = [IO.File]::ReadAllText(
        (Join-Path $workspace "Directory.Build.props"))
    [string]$buildProperties.Project.PropertyGroup.Version
} else {
    $env:KUMORI_VERSION
}
if ([string]::IsNullOrWhiteSpace($version))
{
    throw "Directory.Build.props does not declare the local Kumori version."
}

$appProject = [IO.Path]::GetFullPath(
    (Join-Path $workspace "src\Kumori.App\Kumori.App.csproj"))
$appExecutable = [IO.Path]::GetFullPath(
    (Join-Path $workspace "src\Kumori.App\bin\Debug\net10.0-windows10.0.17763.0\Kumori.exe"))
$rootProjects = @(
    $appProject,
    [IO.Path]::GetFullPath((Join-Path $workspace "replay_viewer\Kumori.ReplayViewer.csproj")),
    [IO.Path]::GetFullPath((Join-Path $workspace "src\Kumori.SkinStudio\Kumori.SkinStudio.csproj")),
    [IO.Path]::GetFullPath((Join-Path $workspace "src\Kumori.StableFrameBridge\Kumori.StableFrameBridge.csproj"))
)
$sharedInputs = @(
    "Directory.Build.props",
    "Directory.Build.targets",
    "global.json",
    "Kumori.Dev.slnf",
    "scripts\update-lazer.ps1",
    "THIRD-PARTY-NOTICES.md"
) | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $workspace $_)) }
$inputExtensions = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @(
    ".cs", ".xaml", ".csproj", ".props", ".targets", ".resx",
    ".json", ".config", ".png", ".ico", ".manifest", ".xml"))
{
    [void]$inputExtensions.Add($extension)
}

function Get-RelativeChildPath([string]$root, [string]$path)
{
    $prefix = [IO.Path]::GetFullPath($root).TrimEnd("\") + "\"
    $fullPath = [IO.Path]::GetFullPath($path)
    if ($fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))
    {
        return $fullPath.Substring($prefix.Length)
    }
    return $fullPath
}

function Get-ProjectReferences([string]$project)
{
    [xml]$document = [IO.File]::ReadAllText($project)
    $directory = [IO.Path]::GetDirectoryName($project)
    foreach ($node in $document.SelectNodes("//ProjectReference"))
    {
        $include = [string]$node.Include
        if ([string]::IsNullOrWhiteSpace($include))
        {
            continue
        }
        $referenced = [IO.Path]::GetFullPath((Join-Path $directory $include))
        if ([IO.File]::Exists($referenced))
        {
            $referenced
        }
    }
}

$references = @{}
$pending = [Collections.Generic.Queue[string]]::new()
foreach ($rootProject in $rootProjects)
{
    $pending.Enqueue($rootProject)
}
while ($pending.Count -gt 0)
{
    $project = $pending.Dequeue()
    if ($references.ContainsKey($project))
    {
        continue
    }
    $projectReferences = @(Get-ProjectReferences $project)
    $references[$project] = $projectReferences
    foreach ($referenced in $projectReferences)
    {
        if (-not $references.ContainsKey($referenced))
        {
            $pending.Enqueue($referenced)
        }
    }
}

# Kumori.App builds the x86 bridge through an MSBuild target rather than a
# ProjectReference, so make that dependency explicit for incremental planning.
$bridgeProject = $rootProjects[3]
$references[$appProject] = @($references[$appProject]) + $bridgeProject

function Get-StampPath([string]$project)
{
    Join-Path ([IO.Path]::GetDirectoryName($project)) "obj\Debug\.kumori-local-run.stamp"
}

function Get-ProjectFingerprint([string]$project)
{
    $entries = [Collections.Generic.List[string]]::new()
    $entries.Add("version|$version")
    foreach ($sharedInput in $sharedInputs)
    {
        if ([IO.File]::Exists($sharedInput))
        {
            $info = [IO.FileInfo]::new($sharedInput)
            $relative = Get-RelativeChildPath $workspace $sharedInput
            $entries.Add(
                "shared|$relative|$($info.Length)|$($info.LastWriteTimeUtc.Ticks)")
        }
    }

    $projectDirectory = [IO.Path]::GetDirectoryName($project)
    foreach ($file in [IO.Directory]::EnumerateFiles(
        $projectDirectory,
        "*",
        [IO.SearchOption]::AllDirectories))
    {
        if ($file.IndexOf("\bin\", [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $file.IndexOf("\obj\", [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $file.EndsWith("_wpftmp.csproj", [StringComparison]::OrdinalIgnoreCase))
        {
            continue
        }
        if (-not $inputExtensions.Contains([IO.Path]::GetExtension($file)))
        {
            continue
        }
        $info = [IO.FileInfo]::new($file)
        $relative = Get-RelativeChildPath $projectDirectory $file
        $entries.Add(
            "project|$relative|$($info.Length)|$($info.LastWriteTimeUtc.Ticks)")
    }
    $entries.Sort([StringComparer]::OrdinalIgnoreCase)
    $payload = [Text.Encoding]::UTF8.GetBytes(
        [string]::Join("`n", $entries))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try
    {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($payload)).Replace("-", "")
    }
    finally
    {
        $sha256.Dispose()
    }
}

$currentFingerprints = @{}
$dirty = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($project in $references.Keys)
{
    $fingerprint = Get-ProjectFingerprint $project
    $currentFingerprints[$project] = $fingerprint
    $stampPath = Get-StampPath $project
    $builtFingerprint = if ([IO.File]::Exists($stampPath)) {
        [IO.File]::ReadAllText($stampPath).Trim()
    } else {
        ""
    }
    if ($ForceBuild -or
        -not [string]::Equals(
            $fingerprint,
            $builtFingerprint,
            [StringComparison]::Ordinal))
    {
        [void]$dirty.Add($project)
    }
}
if (-not [IO.File]::Exists($appExecutable))
{
    foreach ($project in $references.Keys)
    {
        [void]$dirty.Add($project)
    }
}

# Rebuild every project that consumes a changed dependency.
$dependents = @{}
foreach ($project in $references.Keys)
{
    foreach ($referenced in @($references[$project]))
    {
        if (-not $dependents.ContainsKey($referenced))
        {
            $dependents[$referenced] =
                [Collections.Generic.List[string]]::new()
        }
        $dependents[$referenced].Add($project)
    }
}
$dirtyQueue = [Collections.Generic.Queue[string]]::new()
foreach ($project in $dirty)
{
    $dirtyQueue.Enqueue($project)
}
while ($dirtyQueue.Count -gt 0)
{
    $changed = $dirtyQueue.Dequeue()
    if (-not $dependents.ContainsKey($changed))
    {
        continue
    }
    foreach ($dependent in $dependents[$changed])
    {
        if ($dirty.Add($dependent))
        {
            $dirtyQueue.Enqueue($dependent)
        }
    }
}

function Stop-DevelopmentProcesses([switch]$KumoriOnly)
{
    $targets = if ($KumoriOnly) {
        @($appExecutable)
    } else {
        @(
            $appExecutable,
            [IO.Path]::GetFullPath((Join-Path $workspace "replay_viewer\bin\Debug\net10.0\win-x64\Kumori.ReplayViewer.exe")),
            [IO.Path]::GetFullPath((Join-Path $workspace "src\Kumori.SkinStudio\bin\Debug\net10.0\win-x64\Kumori.SkinStudio.exe"))
        )
    }
    $processNames = if ($KumoriOnly) {
        @("Kumori")
    } else {
        @("Kumori", "Kumori.ReplayViewer", "Kumori.SkinStudio")
    }
    function Find-DevelopmentProcesses
    {
        @(Get-Process -Name $processNames `
            -ErrorAction SilentlyContinue | Where-Object {
                try {
                    $targets -contains [IO.Path]::GetFullPath($_.Path)
                } catch {
                    $false
                }
            })
    }

    $running = @(Find-DevelopmentProcesses)
    if ($running.Count -eq 0)
    {
        return
    }
    Write-Host "Stopping this checkout's running development processes..."
    $running | Stop-Process -Force
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do
    {
        Start-Sleep -Milliseconds 100
        $remaining = @(Find-DevelopmentProcesses)
    }
    while ($remaining.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($remaining.Count -gt 0)
    {
        throw "Could not stop every running development process."
    }
}

function Set-BuildStamp([string]$project)
{
    $stampPath = Get-StampPath $project
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($stampPath)) |
        Out-Null
    [IO.File]::WriteAllText($stampPath, $currentFingerprints[$project])
}

if ($dirty.Count -gt 0)
{
    Write-Host "Source changes detected. Preparing an incremental Debug build..."
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $workspace "scripts\update-lazer.ps1")
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
    Stop-DevelopmentProcesses

    $restoreRequired = $false
    foreach ($project in $references.Keys)
    {
        $assets = Join-Path ([IO.Path]::GetDirectoryName($project)) `
            "obj\project.assets.json"
        if (-not [IO.File]::Exists($assets))
        {
            $restoreRequired = $true
            break
        }
    }
    if ($restoreRequired)
    {
        Write-Host "Restoring development dependencies once..."
        & dotnet restore Kumori.Dev.slnf --disable-parallel `
            "-p:Version=$version" @serialBuildArguments
        if ($LASTEXITCODE -ne 0)
        {
            exit $LASTEXITCODE
        }
    }

    if ($ForceBuild -or $dirty.Count -eq $references.Count)
    {
        Write-Host "Building the complete development graph..."
        & dotnet build Kumori.Dev.slnf -c Debug --no-restore `
            "-p:Version=$version" -v:minimal @serialBuildArguments
        if ($LASTEXITCODE -ne 0)
        {
            exit $LASTEXITCODE
        }
        foreach ($project in $references.Keys)
        {
            Set-BuildStamp $project
        }
    }
    else
    {
        $visited = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        $buildOrder = [Collections.Generic.List[string]]::new()
        function Add-InBuildOrder([string]$project)
        {
            if (-not $dirty.Contains($project) -or -not $visited.Add($project))
            {
                return
            }
            foreach ($referenced in @($references[$project]))
            {
                Add-InBuildOrder $referenced
            }
            $buildOrder.Add($project)
        }
        foreach ($rootProject in $rootProjects)
        {
            Add-InBuildOrder $rootProject
        }
        foreach ($project in $dirty)
        {
            Add-InBuildOrder $project
        }

        foreach ($project in $buildOrder)
        {
            $relative = Get-RelativeChildPath $workspace $project
            Write-Host "Building $relative..."
            & dotnet build $project -c Debug --no-restore --no-dependencies `
                "-p:Version=$version" "-p:BuildProjectReferences=false" `
                -v:minimal @serialBuildArguments
            if ($LASTEXITCODE -ne 0)
            {
                exit $LASTEXITCODE
            }
            Set-BuildStamp $project
        }
    }
}
else
{
    Write-Host "Debug build is current."
}

if (-not [IO.File]::Exists($appExecutable))
{
    throw "The Debug application was not produced at $appExecutable."
}
if (-not $NoLaunch)
{
    # A previous Debug instance can be alive in the tray with no visible
    # window. Restart only this checkout's Kumori process so run.bat always
    # produces a visible app and never interferes with installed builds.
    Stop-DevelopmentProcesses -KumoriOnly
    Write-Host "Launching Kumori. This window will stay attached until Kumori closes."
    $appProcess = Start-Process -FilePath $appExecutable `
        -WorkingDirectory $workspace -PassThru
    $appExitCode = 0
    try
    {
        $appProcess.WaitForExit()
        $appExitCode = $appProcess.ExitCode
    }
    finally
    {
        $appProcess.Dispose()
    }
    exit $appExitCode
}
