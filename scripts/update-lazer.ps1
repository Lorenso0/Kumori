param(
    [string]$Checkout = (Join-Path $PSScriptRoot "..\third_party\osu")
)

$ErrorActionPreference = "Stop"
$Checkout = [IO.Path]::GetFullPath($Checkout)
$PinnedCommit = "acf314f4ed7eccdd1de42bff81ffef97621125c9"

if (-not (Test-Path (Join-Path $Checkout ".git"))) {
    git init $Checkout
    git -C $Checkout remote add origin https://github.com/ppy/osu.git
}

$commit = ""
$head = git -C $Checkout rev-parse --verify --quiet 'HEAD^{commit}' 2>$null
if ($LASTEXITCODE -eq 0) {
    $commit = ([string]$head).Trim()
}
if (-not [string]::Equals($commit, $PinnedCommit, [StringComparison]::OrdinalIgnoreCase)) {
    git -C $Checkout fetch --depth 1 origin $PinnedCommit
    git -C $Checkout checkout --detach $PinnedCommit
    $commit = (git -C $Checkout rev-parse HEAD).Trim()
}

if (-not [string]::Equals($commit, $PinnedCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Could not check out the required osu!lazer revision $PinnedCommit."
}

Write-Output "Using pinned osu!lazer revision $commit"
