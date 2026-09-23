# SPDX-FileCopyrightText: 2026 Amos Anderson
# SPDX-License-Identifier: GPL-2.0-only

# Exercises real Git tag/branch relationships and a simulated GitHub CLI.
# This script never connects to GitHub or publishes a release.
$ErrorActionPreference = 'Stop'
$getReleaseInfo = Join-Path $PSScriptRoot 'Get-ReleaseInfo.ps1'
$publishRelease = Join-Path $PSScriptRoot 'Publish-Release.ps1'
$testRoot = Join-Path $PSScriptRoot "../artifacts/release-tests/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$testRoot = (Resolve-Path -LiteralPath $testRoot).Path
$releaseTestFixture = [pscustomobject]@{ Checks = 0; Events = $null; State = $null }

function Assert-Release {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $releaseTestFixture.Checks++
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Message)
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $releaseTestFixture.Checks++
        return
    }
    throw "Expected rejection: $Message"
}

function Invoke-TestGit {
    param([string[]]$Arguments)
    & git -c commit.gpgsign=false @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Git fixture command failed: $Arguments" }
}

Push-Location $testRoot
try {
    Invoke-TestGit @('init', '--quiet', '--initial-branch=main')
    Invoke-TestGit @('config', 'user.name', 'Release Tests')
    Invoke-TestGit @('config', 'user.email', 'release-tests@example.invalid')
    Invoke-TestGit @('commit', '--quiet', '--allow-empty', '-m', 'Main commit')
    Invoke-TestGit @('tag', '-a', 'v1.2.3', '-m', 'Annotated release')
    Invoke-TestGit @('tag', 'v1.2.3-rc.1')
    Invoke-TestGit @('update-ref', 'refs/remotes/origin/main', 'HEAD')
    $release = & $getReleaseInfo -Tag 'v1.2.3'
    Assert-Release ($release.Version -eq '1.2.3') 'Annotated release version was not extracted.'
    $commit = $release.Commit
    $preview = & $getReleaseInfo -Tag 'v1.2.3-rc.1'
    Assert-Release ($preview.Version -eq '1.2.3-rc.1') 'Lightweight prerelease was rejected.'
    foreach ($tag in @('1.2.3', 'V1.2.3', 'v1.2', 'v01.2.3', 'v1.2.3-01', 'v1.2.3+meta', 'v1.2.3/other')) {
        Assert-Rejected { & $getReleaseInfo -Tag $tag } 'Release tags must be'
    }
    Invoke-TestGit @('checkout', '--quiet', '-b', 'feature')
    Invoke-TestGit @('commit', '--quiet', '--allow-empty', '-m', 'Unmerged feature')
    Invoke-TestGit @('tag', 'v2.0.0')
    Assert-Rejected { & $getReleaseInfo -Tag 'v2.0.0' } 'must point to a commit on main'
    Assert-Rejected { & $getReleaseInfo -Tag 'v1.2.3' } 'checkout must match'
    Invoke-TestGit @('checkout', '--quiet', 'main')
    Invoke-TestGit @('commit', '--quiet', '--allow-empty', '-m', 'Later main commit')
    Invoke-TestGit @('update-ref', 'refs/remotes/origin/main', 'HEAD')
    Invoke-TestGit @('checkout', '--quiet', '--detach', 'v1.2.3')
    $ancestor = & $getReleaseInfo -Tag 'v1.2.3'
    Assert-Release ($ancestor.Commit -eq $commit) 'A release from main history was rejected.'
}
finally { Pop-Location }

$assetRoot = Join-Path $testRoot 'assets'
New-Item -ItemType Directory -Path $assetRoot | Out-Null
function New-ReleaseAssets {
    param([string]$Version, [string]$Directory)
    foreach ($name in @("a2utils-$Version-win-x64.zip", "a2utils-$Version-linux-x64.tar.gz",
        "a2utils-$Version-osx-arm64.tar.gz", "A2Utils.Tool.$Version.nupkg")) {
        Set-Content -LiteralPath (Join-Path $Directory $name) -Value "Synthetic release asset: $name"
    }
}
New-ReleaseAssets '1.2.3' $assetRoot

function Reset-GitHubFixture {
    $releaseTestFixture.Events = [System.Collections.Generic.List[string]]::new()
    $releaseTestFixture.State = @{
        Private = $true
        Commit = $commit
        Exists = $false
        Draft = $true
        Assets = @()
        FailUpload = $false
        TruncateUpload = $false
        LookupError = $false
    }
}

# Function precedence intercepts every gh invocation in the publishing script.
function gh {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $global:LASTEXITCODE = 0
    $operation = "$($Arguments[0]) $($Arguments[1])"
    $releaseTestFixture.Events.Add($operation)
    switch -Wildcard ($operation) {
        'repo view' { @{ isPrivate = $releaseTestFixture.State.Private } | ConvertTo-Json -Compress }
        'api *' { $releaseTestFixture.State.Commit }
        'release view' {
            if ($releaseTestFixture.State.LookupError) {
                $global:LASTEXITCODE = 1
                'HTTP 403: access denied'
            }
            elseif (-not $releaseTestFixture.State.Exists) {
                $global:LASTEXITCODE = 1
                'release not found'
            }
            else {
                @{ isDraft = $releaseTestFixture.State.Draft; assets = @($releaseTestFixture.State.Assets) } | ConvertTo-Json -Depth 5 -Compress
            }
        }
        'release create' {
            Assert-Release ($Arguments -contains '--draft') 'A new release must start as a draft.'
            Assert-Release ($Arguments -contains '--verify-tag') 'Release creation must not create a tag.'
            $releaseTestFixture.State.Exists = $true
            $releaseTestFixture.State.CreateArguments = $Arguments
        }
        'release upload' {
            if ($releaseTestFixture.State.FailUpload) {
                $global:LASTEXITCODE = 1
                return
            }
            $paths = $Arguments[([array]::IndexOf($Arguments, '--clobber') + 1)..($Arguments.Count - 1)]
            $releaseTestFixture.State.Assets = @($paths | ForEach-Object {
                $file = Get-Item -LiteralPath $_
                @{ name = $file.Name; size = $(if ($releaseTestFixture.State.TruncateUpload) { 1 } else { $file.Length }) }
            })
        }
        'release edit' {
            Assert-Release ($Arguments -contains '--draft=false') 'Final edit must publish the draft.'
            $releaseTestFixture.State.Draft = $false
            $releaseTestFixture.State.EditArguments = $Arguments
        }
        default { throw "Unexpected GitHub operation: $Arguments" }
    }
}

$publishArguments = @{ Tag = 'v1.2.3'; Commit = $commit; Repository = 'example/private'; AssetDirectory = $assetRoot }
Reset-GitHubFixture
& $publishRelease @publishArguments | Out-Null
Assert-Release (-not $releaseTestFixture.State.Draft) 'Successful uploads did not publish.'
Assert-Release ($releaseTestFixture.State.Assets.Count -eq 5) 'The release must upload four packages and checksums.'
Assert-Release ($releaseTestFixture.State.EditArguments -contains '--prerelease=false') 'Stable tag was marked prerelease.'
$lines = @(Get-Content -LiteralPath (Join-Path $assetRoot 'SHA256SUMS.txt'))
Assert-Release ($lines.Count -eq 4) 'Checksum manifest does not cover every package.'
foreach ($line in $lines) {
    $hash, $name = $line -split '  ', 2
    $actual = (Get-FileHash -LiteralPath (Join-Path $assetRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Release ($hash -ceq $actual) "Incorrect checksum for $name"
}
Assert-Rejected { & $publishRelease @publishArguments } 'already published'
Assert-Release (@($releaseTestFixture.Events | Where-Object { $_ -eq 'release upload' }).Count -eq 1) 'A published release was overwritten.'

Reset-GitHubFixture
$releaseTestFixture.State.FailUpload = $true
Assert-Rejected { & $publishRelease @publishArguments } 'GitHub CLI failed: release upload'
Assert-Release ($releaseTestFixture.State.Draft -and $releaseTestFixture.Events -notcontains 'release edit') 'Failed upload published a release.'
$releaseTestFixture.State.FailUpload = $false
& $publishRelease @publishArguments | Out-Null
Assert-Release (@($releaseTestFixture.Events | Where-Object { $_ -eq 'release create' }).Count -eq 1) 'Retry created another draft.'
Assert-Release (-not $releaseTestFixture.State.Draft) 'Retry did not finish the draft.'

Reset-GitHubFixture
$releaseTestFixture.State.TruncateUpload = $true
Assert-Rejected { & $publishRelease @publishArguments } 'upload verification failed'
Assert-Release ($releaseTestFixture.State.Draft -and $releaseTestFixture.Events -notcontains 'release edit') 'Incomplete upload published a release.'
Reset-GitHubFixture
$releaseTestFixture.State.Private = $false
Assert-Rejected { & $publishRelease @publishArguments } 'requires a private repository'
Assert-Release ($releaseTestFixture.Events -notcontains 'release create') 'A public release was created.'
Reset-GitHubFixture
$releaseTestFixture.State.Commit = '0' * 40
Assert-Rejected { & $publishRelease @publishArguments } 'remote tag changed'
Assert-Release ($releaseTestFixture.Events -notcontains 'release create') 'A moved tag was published.'
Reset-GitHubFixture
$releaseTestFixture.State.LookupError = $true
Assert-Rejected { & $publishRelease @publishArguments } 'Cannot check for an existing release'
Assert-Release ($releaseTestFixture.Events -notcontains 'release create') 'A lookup failure was treated as a missing release.'

$missingRoot = Join-Path $testRoot 'missing-assets'
New-Item -ItemType Directory -Path $missingRoot | Out-Null
Reset-GitHubFixture
Assert-Rejected { & $publishRelease -Tag 'v1.2.3' -Commit $commit -Repository 'example/private' -AssetDirectory $missingRoot } 'exactly the three platform archives'
Assert-Release ($releaseTestFixture.Events.Count -eq 0) 'Missing assets were not rejected before GitHub access.'

$previewRoot = Join-Path $testRoot 'preview-assets'
New-Item -ItemType Directory -Path $previewRoot | Out-Null
New-ReleaseAssets '1.2.3-rc.1' $previewRoot
Reset-GitHubFixture
& $publishRelease -Tag 'v1.2.3-rc.1' -Commit $commit -Repository 'example/private' -AssetDirectory $previewRoot | Out-Null
Assert-Release ($releaseTestFixture.State.CreateArguments -contains '--prerelease') 'Preview draft was not marked prerelease.'
Assert-Release ($releaseTestFixture.State.EditArguments -contains '--prerelease=true') 'Preview was published as stable.'

Write-Host "Release automation: $($releaseTestFixture.Checks) checks passed. No GitHub requests were made."
