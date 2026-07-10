param(
    [string]$Checkout = (Join-Path $PSScriptRoot "..\third_party\osu")
)

$ErrorActionPreference = "Stop"
$Checkout = [IO.Path]::GetFullPath($Checkout)

if (-not (Test-Path (Join-Path $Checkout ".git"))) {
    git clone --depth 1 --branch master https://github.com/ppy/osu.git $Checkout
} else {
    git -C $Checkout fetch --depth 1 origin master
    git -C $Checkout checkout --detach origin/master
}

$commit = (git -C $Checkout rev-parse HEAD).Trim()
$program = Join-Path $PSScriptRoot "..\replay_viewer\Program.cs"
$content = Get-Content -Raw $program
$content = [regex]::Replace(
    $content,
    'public const string LazerCommit = "[0-9a-f]{40}";',
    "public const string LazerCommit = `"$commit`";"
)
Set-Content -Path $program -Value $content -Encoding utf8
Write-Output $commit
