# SPDX-FileCopyrightText: 2026 Amos Anderson
# SPDX-License-Identifier: GPL-2.0-only

param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$MainRef = 'refs/remotes/origin/main'
)

$ErrorActionPreference = 'Stop'
# NuGet-compatible SemVer, with no build metadata or leading-zero numeric identifiers.
$number = '(0|[1-9][0-9]*)'
$identifier = '(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
if ($Tag -cnotmatch "\Av$number\.$number\.$number(-$identifier(\.$identifier)*)?\z") {
    throw 'Release tags must be vMAJOR.MINOR.PATCH, optionally followed by a prerelease such as -rc.1.'
}

$commit = & git rev-parse --verify "refs/tags/$Tag^{commit}"
if ($LASTEXITCODE -ne 0) { throw "Cannot resolve release tag $Tag" }
$headCommit = & git rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0 -or $headCommit -ne $commit) {
    throw 'The checkout must match the release tag commit.'
}
& git merge-base --is-ancestor $commit $MainRef
if ($LASTEXITCODE -ne 0) {
    throw "Release tag $Tag must point to a commit on main ($MainRef)."
}

[pscustomobject]@{
    Version = $Tag.Substring(1)
    Commit = $commit
}
