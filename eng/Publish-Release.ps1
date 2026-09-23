# SPDX-FileCopyrightText: 2026 Amos Anderson
# SPDX-License-Identifier: GPL-2.0-only

param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][string]$AssetDirectory
)

$ErrorActionPreference = 'Stop'
if ($Tag -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$') {
    throw 'Invalid release tag.'
}
$version = $Tag.Substring(1)

function Invoke-GitHub {
    param([string[]]$Arguments)
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed: $($Arguments[0]) $($Arguments[1])" }
    $output
}

# Fail before any remote mutation if a build asset is missing or unexpected.
$expected = @(
    "a2utils-$version-win-x64.zip",
    "a2utils-$version-linux-x64.tar.gz",
    "a2utils-$version-osx-arm64.tar.gz",
    "A2Utils.Tool.$version.nupkg"
)
$assetRoot = (Resolve-Path -LiteralPath $AssetDirectory).Path
$files = @(Get-ChildItem -LiteralPath $assetRoot -File | Where-Object Name -ne 'SHA256SUMS.txt')
if ($files.Count -ne $expected.Count -or (Compare-Object ($files.Name | Sort-Object) ($expected | Sort-Object))) {
    throw 'The release must contain exactly the three platform archives and the versioned tool package.'
}
if (@($files | Where-Object Length -eq 0).Count -ne 0) { throw 'Release assets must not be empty.' }
$checksumPath = Join-Path $assetRoot 'SHA256SUMS.txt'
$checksums = foreach ($file in $files | Sort-Object Name) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
[System.IO.File]::WriteAllText($checksumPath, ($checksums -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
$assets = @($files.FullName) + @($checksumPath)

$repositoryInfo = Invoke-GitHub @('repo', 'view', $Repository, '--json', 'isPrivate') | ConvertFrom-Json
if (-not $repositoryInfo.isPrivate) { throw 'This release workflow requires a private repository.' }
$remoteCommit = Invoke-GitHub @('api', "repos/$Repository/commits/$Tag", '--jq', '.sha')
if ($remoteCommit -ne $Commit) { throw 'The remote tag changed after validation; refusing to publish different binaries.' }

$existingOutput = & gh release view $Tag --repo $Repository --json isDraft,assets 2>&1
if ($LASTEXITCODE -eq 0) {
    $existing = $existingOutput | ConvertFrom-Json
    if (-not $existing.isDraft) { throw 'This release is already published. Use a new tag for changed binaries.' }
    $unexpected = @($existing.assets | Where-Object name -notin ($expected + 'SHA256SUMS.txt'))
    if ($unexpected.Count -ne 0) { throw 'The existing draft has unrelated assets; resolve them before retrying.' }
}
elseif (($existingOutput | Out-String) -notmatch 'release not found|HTTP 404') {
    throw "Cannot check for an existing release: $existingOutput"
}
else {
    $arguments = @('release', 'create', $Tag, '--repo', $Repository, '--verify-tag', '--target', $Commit,
        '--title', "A2Utils $version", '--generate-notes', '--draft')
    if ($version.Contains('-')) { $arguments += '--prerelease' }
    Invoke-GitHub $arguments
}

# A failed upload leaves a draft; rerunning the failed job repairs its assets.
Invoke-GitHub (@('release', 'upload', $Tag, '--repo', $Repository, '--clobber') + $assets)
$uploaded = Invoke-GitHub @('release', 'view', $Tag, '--repo', $Repository, '--json', 'assets') | ConvertFrom-Json
foreach ($asset in $assets) {
    $file = Get-Item -LiteralPath $asset
    $match = @($uploaded.assets | Where-Object name -eq $file.Name)
    if ($match.Count -ne 1 -or $match[0].size -ne $file.Length) {
        throw "Release upload verification failed for $($file.Name); the release remains a draft."
    }
}
$prerelease = $version.Contains('-').ToString().ToLowerInvariant()
Invoke-GitHub @('release', 'edit', $Tag, '--repo', $Repository, '--verify-tag', '--draft=false', "--prerelease=$prerelease")
