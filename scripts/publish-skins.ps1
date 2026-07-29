[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Validate', 'Stage', 'Publish')]
    [string]$Mode,
    [string]$ExtrasRoot = (Join-Path $env:APPDATA 'Kumori\skins\Extras\osu'),
    [string]$CatalogRepository = 'Lorenso0/Kumori-Extras',
    [string]$MinimumKumoriVersion = '0.6.0',
    [string]$SigningKeyPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workRoot = Join-Path $repositoryRoot 'artifacts\skin-extras-publish'
$stageRoot = Join-Path $workRoot 'stage'
$catalogCheckout = Join-Path $workRoot 'Kumori-Extras'
$publisherProject = Join-Path $repositoryRoot 'tools\Kumori.ExtrasPublisher\Kumori.ExtrasPublisher.csproj'
$repositoryTemplate = Join-Path $repositoryRoot 'extras-repository-template'

function Invoke-Checked {
    param([scriptblock]$Command, [string]$Failure)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw $Failure }
}

function Get-GitHubCli {
    $command = Get-Command gh -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $portable = Join-Path $workRoot 'tools\gh\bin\gh.exe'
    if (Test-Path -LiteralPath $portable) { return $portable }
    throw 'GitHub CLI is unavailable. Run SKINS.md so Codex can bootstrap and verify the portable CLI.'
}

function Get-NextReleaseTag {
    param([string]$Gh)
    $prefix = 'catalog-' + (Get-Date -Format 'yyyy.MM.dd') + '.'
    try {
        $releases = & $Gh release list --repo $CatalogRepository --limit 100 `
            --json tagName,isDraft 2>$null | ConvertFrom-Json
    } catch {
        return $prefix + '1'
    }
    if ($LASTEXITCODE -ne 0) { return $prefix + '1' }
    $draftSequences = @(foreach ($release in $releases) {
        $match = [regex]::Match(
            [string]$release.tagName,
            '^' + [regex]::Escape($prefix) + '(\d+)$')
        if ($release.isDraft -and $match.Success) {
            [int]$match.Groups[1].Value
        }
    })
    if ($draftSequences.Count -gt 0) {
        return ($prefix + [int](($draftSequences | Measure-Object -Maximum).Maximum))
    }
    $sequences = @(foreach ($release in $releases) {
        $match = [regex]::Match(
            [string]$release.tagName,
            '^' + [regex]::Escape($prefix) + '(\d+)$')
        if ($match.Success) {
            [int]$match.Groups[1].Value
        }
    })
    $next = if ($sequences.Count -eq 0) {
        1
    } else {
        [int](($sequences | Measure-Object -Maximum).Maximum) + 1
    }
    return ($prefix + $next)
}

function Test-PublishedCatalogAssets {
    param(
        [string]$Gh,
        [string]$StatePath
    )

    if (-not (Test-Path -LiteralPath $StatePath)) { return $false }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    $expectedByRelease = @{}
    foreach ($item in @($state.packs)) {
        if ($item.withdrawnAtUtc) { continue }
        $packAssets = @($item.pack.package)
        if ($item.pack.preview) { $packAssets += $item.pack.preview }
        foreach ($asset in $packAssets) {
            if (-not $asset) { continue }
            $tag = [string]$asset.releaseTag
            if (-not $expectedByRelease.ContainsKey($tag)) {
                $expectedByRelease[$tag] = [Collections.Generic.HashSet[string]]::new(
                    [StringComparer]::Ordinal)
            }
            [void]$expectedByRelease[$tag].Add([string]$asset.assetName)
        }
    }

    foreach ($tag in @($expectedByRelease.Keys)) {
        try {
            $releaseJson = (
                & $Gh release view $tag --repo $CatalogRepository `
                    --json isDraft,assets 2>$null | Out-String).Trim()
            $release = $releaseJson | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or $release.isDraft) {
                Write-Warning "Catalog release '$tag' is missing or unpublished."
                return $false
            }
            $published = [Collections.Generic.HashSet[string]]::new(
                [StringComparer]::Ordinal)
            foreach ($asset in @($release.assets)) {
                [void]$published.Add([string]$asset.name)
            }
            foreach ($name in $expectedByRelease[$tag]) {
                if (-not $published.Contains($name)) {
                    Write-Warning "Catalog asset '$name' is missing from '$tag'."
                    return $false
                }
            }
        } catch {
            Write-Warning "Catalog release '$tag' could not be verified."
            return $false
        }
    }
    return $true
}

New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$gh = $null
if ($Mode -eq 'Publish') { $gh = Get-GitHubCli }

$releaseTag = if ($Mode -eq 'Publish') {
    $tagOutput = @(Get-NextReleaseTag -Gh $gh)
    [string]$tagOutput[-1]
} else {
    'catalog-' + (Get-Date -Format 'yyyy.MM.dd') + '.validate'
}

if (Test-Path -LiteralPath $catalogCheckout) {
    $ownedPaths = @('catalog-v1.json', 'state/catalog-state.json', 'state/publication-summary.json')
    $unexpected = @(git -C $catalogCheckout status --porcelain | Where-Object {
        $line = $_.Substring(3).Replace('\', '/')
        $ownedPaths -notcontains $line
    })
    if ($unexpected.Count -gt 0) {
        throw 'The ignored catalog checkout has unrelated changes; refusing to overwrite them.'
    }
    git -C $catalogCheckout restore --source=HEAD -- $ownedPaths 2>$null
    Invoke-Checked { git -C $catalogCheckout fetch --prune origin } 'Could not fetch the Extras catalog repository.'
    Invoke-Checked { git -C $catalogCheckout checkout main } 'Could not select the Extras catalog main branch.'
    Invoke-Checked { git -C $catalogCheckout pull --ff-only origin main } 'Could not update the Extras catalog checkout.'
} elseif ($Mode -ne 'Publish') {
    $publicClone = "https://github.com/$CatalogRepository.git"
    Invoke-Checked { git clone $publicClone $catalogCheckout } 'Could not clone the public Extras catalog state.'
} else {
    $repositoryExists = $true
    try {
        & $gh repo view $CatalogRepository *> $null
        if ($LASTEXITCODE -ne 0) { $repositoryExists = $false }
    } catch {
        $repositoryExists = $false
    }
    if ($repositoryExists) {
        Invoke-Checked { & $gh repo clone $CatalogRepository $catalogCheckout } 'Could not clone the Extras catalog repository.'
    } else {
        $repositoryName = $CatalogRepository.Split('/')[-1]
        Push-Location $workRoot
        try {
            Invoke-Checked {
                & $gh repo create $CatalogRepository --public --description 'Signed, complete Extras catalog for Kumori.' --clone
            } 'Could not create the public Extras catalog repository.'
        } finally {
            Pop-Location
        }
        if (-not (Test-Path -LiteralPath $catalogCheckout)) {
            throw "GitHub CLI did not create the expected checkout: $catalogCheckout"
        }
        Get-ChildItem -LiteralPath $repositoryTemplate -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $catalogCheckout -Recurse -Force
        }
        Invoke-Checked { git -C $catalogCheckout add --all } 'Could not stage the repository bootstrap.'
        Invoke-Checked { git -C $catalogCheckout commit -m 'Bootstrap signed Extras catalog' } 'Could not commit the repository bootstrap.'
        Invoke-Checked { git -C $catalogCheckout push -u origin HEAD:main } 'Could not publish the repository bootstrap.'
        $environmentPolicy = '{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}'
        Invoke-Checked {
            $environmentPolicy | & $gh api --method PUT "repos/$CatalogRepository/environments/catalog-publication" --input - *> $null
        } 'Could not create the protected catalog-publication environment.'
        Invoke-Checked {
            & $gh api --method POST "repos/$CatalogRepository/environments/catalog-publication/deployment-branch-policies" -f name=main -f type=branch *> $null
        } 'Could not restrict catalog publication to the main branch.'
        $branchProtection = '{"required_status_checks":{"strict":true,"contexts":["validate"]},"enforce_admins":false,"required_pull_request_reviews":null,"restrictions":null,"allow_force_pushes":false,"allow_deletions":false}'
        Invoke-Checked {
            $branchProtection | & $gh api --method PUT "repos/$CatalogRepository/branches/main/protection" --input - *> $null
        } 'Could not protect the Extras catalog main branch.'
    }
    if ($SigningKeyPath) {
        $resolvedKey = (Resolve-Path -LiteralPath $SigningKeyPath).Path
        Invoke-Checked {
            Get-Content -LiteralPath $resolvedKey -Raw | & $gh secret set KUMORI_EXTRAS_SIGNING_KEY_PEM --repo $CatalogRepository
        } 'Could not configure the protected catalog signing secret.'
    }
}

$statePath = Join-Path $catalogCheckout 'state\catalog-state.json'
if (-not (Test-Path -LiteralPath $statePath)) {
    $statePath = Join-Path $workRoot 'empty-catalog-state.json'
    if (-not (Test-Path -LiteralPath $statePath)) {
        [IO.File]::WriteAllText(
            $statePath,
            '{"schemaVersion":1,"packs":[]}',
            [Text.UTF8Encoding]::new($false))
    }
}

if (Test-Path -LiteralPath $stageRoot) {
    $resolvedWork = (Resolve-Path $workRoot).Path
    $resolvedStage = (Resolve-Path $stageRoot).Path
    if (-not $resolvedStage.StartsWith($resolvedWork, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a staging directory outside the publisher work root.'
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

$publishedAssetsHealthy = $true
if ($Mode -eq 'Publish') {
    $publishedAssetsHealthy = [bool](
        Test-PublishedCatalogAssets -Gh $gh -StatePath $statePath)
}
[string]$publisherMode = if ($Mode -eq 'Validate') {
    'Validate'
} elseif (-not $publishedAssetsHealthy) {
    Write-Warning 'Published catalog assets are incomplete; rebuilding the entire active catalog.'
    'Republish'
} else {
    'Stage'
}
Invoke-Checked {
    dotnet run --project $publisherProject -c Release --no-launch-profile -- `
        --mode $publisherMode `
        --extras-root $ExtrasRoot `
        --state $statePath `
        --output $stageRoot `
        --release-tag $releaseTag `
        --minimum-kumori-version $MinimumKumoriVersion
} 'The Extras publisher rejected the local library or catalog state.'

$summaryPath = Join-Path $stageRoot 'publication-summary.json'
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
if (-not $summary.changed) {
    Write-Host 'Verified no-op: the public catalog already matches the complete local Extras library.'
    exit 0
}
if ($Mode -ne 'Publish') {
    Write-Host "Staged $($summary.additions) additions, $($summary.revisions) revisions, $($summary.metadataChanges) metadata changes, $($summary.withdrawals) withdrawals, and $($summary.republished) republished packs."
    exit 0
}

Invoke-Checked { & $gh auth status } 'GitHub CLI authentication is not valid.'
$branch = 'catalog/' + $releaseTag
Invoke-Checked { git -C $catalogCheckout checkout -B $branch main } 'Could not create the catalog-state branch.'
Copy-Item -LiteralPath (Join-Path $stageRoot 'catalog-state.json') -Destination (Join-Path $catalogCheckout 'state\catalog-state.json') -Force
Copy-Item -LiteralPath (Join-Path $stageRoot 'catalog-v1.json') -Destination (Join-Path $catalogCheckout 'catalog-v1.json') -Force
Copy-Item -LiteralPath $summaryPath -Destination (Join-Path $catalogCheckout 'state\publication-summary.json') -Force

foreach ($release in $summary.releases) {
    $releaseExists = $true
    try {
        $releaseState = & $gh release view $release.tag --repo $CatalogRepository --json isDraft,assets | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { $releaseExists = $false }
    } catch {
        $releaseExists = $false
    }
    if (-not $releaseExists) {
        Invoke-Checked {
            & $gh release create $release.tag --repo $CatalogRepository --draft --title $release.tag --notes 'Immutable Kumori Extras catalog shard.'
        } "Could not create draft release $($release.tag)."
        $uploadedNames = @()
    } elseif (-not $releaseState.isDraft) {
        throw "Release $($release.tag) is already published and cannot be changed."
    } else {
        $uploadedNames = @($releaseState.assets | ForEach-Object { $_.name })
    }
    $paths = @($release.assets | ForEach-Object { Join-Path (Join-Path $stageRoot 'assets') $_ })
    $paths = @($paths | Where-Object { $uploadedNames -notcontains (Split-Path -Leaf $_) })
    for ($offset = 0; $offset -lt $paths.Count; $offset += 10) {
        $last = [Math]::Min($offset + 9, $paths.Count - 1)
        $batch = @($paths[$offset..$last])
        $uploaded = $false
        for ($attempt = 1; $attempt -le 5 -and -not $uploaded; $attempt++) {
            try {
                & $gh release upload $release.tag --repo $CatalogRepository @batch
                $uploaded = $LASTEXITCODE -eq 0
            } catch {
                $uploaded = $false
            }
            if (-not $uploaded) {
                if ($attempt -eq 5) { throw "Could not upload $($release.tag) after five retries." }
                Start-Sleep -Seconds (30 * $attempt)
            }
        }
        Start-Sleep -Seconds 10
    }
}

Invoke-Checked { git -C $catalogCheckout add -- catalog-v1.json state/catalog-state.json state/publication-summary.json } 'Could not stage catalog state.'
Invoke-Checked { git -C $catalogCheckout commit -m "Publish $releaseTag catalog state" } 'Could not commit catalog state.'
$remoteBranch = git -C $catalogCheckout ls-remote --heads origin "refs/heads/$branch"
if ($remoteBranch) {
    Invoke-Checked { git -C $catalogCheckout push --force-with-lease -u origin $branch } 'Could not safely update the resumable catalog-state branch.'
} else {
    Invoke-Checked { git -C $catalogCheckout push -u origin $branch } 'Could not push catalog state.'
}
$prUrl = & $gh pr create --repo $CatalogRepository --base main --head $branch --title "Publish $releaseTag" --body 'Automated complete Extras catalog publication.'
if ($LASTEXITCODE -ne 0) {
    $prUrl = & $gh pr list --repo $CatalogRepository --head $branch --state open --json url --jq '.[0].url'
    if ($LASTEXITCODE -ne 0 -or -not $prUrl) { throw 'Could not create or recover the catalog-state pull request.' }
}
$checksStarted = $false
for ($attempt = 1; $attempt -le 12 -and -not $checksStarted; $attempt++) {
    try {
        & $gh pr checks $prUrl --watch --fail-fast
        $checksStarted = $LASTEXITCODE -eq 0
    } catch {
        $checksStarted = $false
    }
    if (-not $checksStarted) { Start-Sleep -Seconds 5 }
}
if (-not $checksStarted) { throw 'Catalog-state validation failed or did not start.' }
Invoke-Checked { & $gh pr merge $prUrl --squash --delete-branch } 'Could not merge the catalog-state pull request.'

$tagsJson = (@($summary.releases | ForEach-Object { $_.tag }) -join ',')
Invoke-Checked {
    & $gh workflow run publish.yml --repo $CatalogRepository -f release_tags=$tagsJson -f final_tag=$releaseTag
} 'Could not dispatch the protected signing workflow.'

Start-Sleep -Seconds 3
$runId = & $gh run list --repo $CatalogRepository --workflow publish.yml --limit 1 --json databaseId --jq '.[0].databaseId'
if ($LASTEXITCODE -ne 0 -or -not $runId) { throw 'Could not find the signing workflow run.' }
Invoke-Checked { & $gh run watch $runId --repo $CatalogRepository --exit-status } 'The signing workflow failed.'

$catalog = Get-Content -LiteralPath (Join-Path $stageRoot 'catalog-v1.json') -Raw | ConvertFrom-Json
$expectedAssets = @{}
foreach ($pack in $catalog.packs) {
    $expectedAssets[$pack.package.assetName] = $pack.package.sha256
    if ($pack.preview) { $expectedAssets[$pack.preview.assetName] = $pack.preview.sha256 }
}
foreach ($release in $summary.releases) {
    $verifyDirectory = Join-Path $stageRoot ('verify-' + $release.tag)
    New-Item -ItemType Directory -Path $verifyDirectory -Force | Out-Null
    Invoke-Checked {
        & $gh release download $release.tag --repo $CatalogRepository --dir $verifyDirectory
    } "Could not redownload public release $($release.tag)."
    foreach ($asset in $release.assets) {
        $download = Join-Path $verifyDirectory $asset
        if (-not (Test-Path -LiteralPath $download)) {
            throw "Public verification did not produce $asset from $($release.tag)."
        }
        $actual = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expectedAssets[$asset]) {
            throw "Public SHA-256 verification failed for $asset."
        }
    }
}

$finalVerify = Join-Path $stageRoot ('verify-' + $releaseTag)
$publicCatalog = Join-Path $finalVerify 'catalog-v1.json'
$publicSignature = Join-Path $finalVerify 'catalog-v1.sig'
$publicChecksum = (Get-Content -LiteralPath (Join-Path $finalVerify 'catalog-v1.json.sha256') -Raw).Trim()
$actualCatalogChecksum = (Get-FileHash -LiteralPath $publicCatalog -Algorithm SHA256).Hash.ToLowerInvariant()
if ($publicChecksum -ne $actualCatalogChecksum) { throw 'The public catalog checksum is invalid.' }
Invoke-Checked {
    dotnet run --project $publisherProject -c Release --no-launch-profile -- `
        --verify-catalog $publicCatalog --verify-signature $publicSignature
} 'The published catalog signature could not be verified with Kumori''s embedded public key.'

Write-Host "Published $releaseTag through $prUrl and Actions run $runId."
