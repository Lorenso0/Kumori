param(
    [string]$Checkout = (Join-Path $PSScriptRoot "..\third_party\osu")
)

$ErrorActionPreference = "Stop"
$Checkout = [IO.Path]::GetFullPath($Checkout)
$PinnedCommit = "5da71008b082d1a77e4bb301dc98886f1f24b895"
$PinnedRelease = "2026.726.0-lazer"
$RendererPatch = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\patches\osu-skin-studio-renderer.patch"))

if (-not (Test-Path (Join-Path $Checkout ".git"))) {
    New-Item -ItemType Directory -Path $Checkout -Force | Out-Null
    git -c "safe.directory=$Checkout" -C $Checkout init
    if ($LASTEXITCODE -ne 0) {
        throw "Could not initialise the osu! source checkout at $Checkout."
    }
    git -c "safe.directory=$Checkout" -C $Checkout remote add origin https://github.com/ppy/osu.git
    if ($LASTEXITCODE -ne 0) {
        throw "Could not configure the osu! source remote at $Checkout."
    }
}

$headOutput = git -c "safe.directory=$Checkout" -C $Checkout rev-parse --verify --quiet 'HEAD^{commit}' 2>$null
$head = if ($LASTEXITCODE -eq 0 -and $null -ne $headOutput) {
    ([string]$headOutput).Trim()
} else {
    ""
}
if (-not [string]::Equals($head, $PinnedCommit, [StringComparison]::OrdinalIgnoreCase)) {
    $changes = @(git -c "safe.directory=$Checkout" -C $Checkout status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect the osu! source checkout at $Checkout."
    }
    if ($changes.Count -gt 0) {
        throw "The osu! source checkout has tracked changes. Preserve or discard them before switching to $PinnedRelease."
    }

    git -c "safe.directory=$Checkout" -C $Checkout fetch --depth 1 origin "refs/tags/$PinnedRelease"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not fetch osu! $PinnedRelease ($PinnedCommit)."
    }
    git -c "safe.directory=$Checkout" -C $Checkout checkout --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "Could not check out osu! $PinnedRelease ($PinnedCommit)."
    }
    $head = ([string](git -c "safe.directory=$Checkout" -C $Checkout rev-parse HEAD)).Trim()
}

if (-not [string]::Equals($head, $PinnedCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The osu! source checkout is $head; expected $PinnedCommit."
}
if (-not (Test-Path (Join-Path $Checkout "osu.Game/osu.Game.csproj"))) {
    throw "The osu! source checkout is incomplete: osu.Game.csproj was not found."
}
if (-not (Test-Path $RendererPatch)) {
    throw "The Skin Studio renderer patch was not found at $RendererPatch."
}

function Test-GitPatch([switch]$Reverse) {
    $arguments = @('-c', "safe.directory=$Checkout", '-C', $Checkout, 'apply')
    if ($Reverse) {
        $arguments += '--reverse'
    }
    $arguments += @('--check', $RendererPatch)

    # Both outcomes are expected here: a reverse check succeeds when the patch
    # is already present, while a forward check succeeds on a clean checkout.
    # Keep native stderr from becoming a terminating PowerShell error.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & git @arguments 2>$null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

if (-not (Test-GitPatch -Reverse)) {
    if (-not (Test-GitPatch)) {
        throw "The Skin Studio renderer patch does not apply cleanly to $PinnedRelease."
    }
    git -c "safe.directory=$Checkout" -C $Checkout apply $RendererPatch
    if ($LASTEXITCODE -ne 0) {
        throw "Could not apply the Skin Studio renderer patch."
    }
}

Write-Output "Using osu! $PinnedRelease ($PinnedCommit) with the Kumori renderer patch"
