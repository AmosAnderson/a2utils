# SPDX-FileCopyrightText: 2026 Amos Anderson
# SPDX-License-Identifier: GPL-2.0-only

# Exercise real packaging/archive logic with a synthetic dotnet publisher.
# No restore, compilation, or network access is performed by this test.
$ErrorActionPreference = 'Stop'
$testRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../artifacts/packaging-tests/$([guid]::NewGuid().ToString('N'))"))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'eng') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Package.ps1') -Destination (Join-Path $testRoot 'eng/Package.ps1')
$packageScript = Join-Path $testRoot 'eng/Package.ps1'
$packagingFixture = [pscustomobject]@{ Checks = 0; FailPublish = $false; MissingNotices = $false; PublishPaths = [System.Collections.Generic.List[string]]::new() }

function Assert-Packaging {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $packagingFixture.Checks++
}

function Assert-PackageFailure {
    param([scriptblock]$Action, [string]$Message)
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $packagingFixture.Checks++
        return
    }
    throw "Expected packaging failure: $Message"
}

function dotnet {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $global:LASTEXITCODE = 0
    if ($Arguments[0] -ne 'publish') { return }
    $output = $Arguments[([array]::IndexOf($Arguments, '-o') + 1)]
    $runtime = $Arguments[([array]::IndexOf($Arguments, '-r') + 1)]
    Assert-Packaging (@(Get-ChildItem -LiteralPath $output -Force).Count -eq 0) 'Publishing must start with an empty directory.'
    Assert-Packaging (-not $packagingFixture.PublishPaths.Contains($output)) 'Each package must use a new publish directory.'
    $packagingFixture.PublishPaths.Add($output)
    Set-Content -LiteralPath (Join-Path $output 'a2.exe') -Value 'current executable'
    if ($packagingFixture.FailPublish) { $global:LASTEXITCODE = 1; return }
    @{ libraries = @{ "runtimepack.Microsoft.NETCore.App.Runtime.$runtime/1.2.3" = @{} } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'a2.deps.json')
    $packages = Join-Path $testRoot 'packages'
    $runtimePackage = Join-Path $packages "microsoft.netcore.app.runtime.$runtime/1.2.3"
    New-Item -ItemType Directory -Path $runtimePackage -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Value 'runtime license'
    Set-Content -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Value 'runtime notices'
    $assets = Join-Path $testRoot 'src/A2Utils.Cli/obj'
    New-Item -ItemType Directory -Path $assets -Force | Out-Null
    $folders = if ($packagingFixture.MissingNotices) { @{} } else { @{ $packages = @{} } }
    @{ packageFolders = $folders } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $assets 'project.assets.json')
}

foreach ($runtime in @('win-x64', 'osx-arm64')) {
    $published = Join-Path $testRoot "artifacts/publish/$runtime"
    New-Item -ItemType Directory -Path $published -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $published 'removed.dll') -Value 'old dependency'
    $extension = if ($runtime.StartsWith('win-')) { '.zip' } else { '.tar.gz' }
    $archive = Join-Path $testRoot "artifacts/a2utils-1.2.3-$runtime$extension"
    Set-Content -LiteralPath $archive -Value 'previous archive'
    & $packageScript -Runtime $runtime -Version '1.2.3' -SkipTests | Out-Null
    Assert-Packaging (-not (Test-Path -LiteralPath (Join-Path $published 'removed.dll'))) 'Removed dependencies survived in published output.'
    $extracted = Join-Path $testRoot "extracted-$runtime"
    New-Item -ItemType Directory -Path $extracted | Out-Null
    if ($extension -eq '.zip') { Expand-Archive -LiteralPath $archive -DestinationPath $extracted }
    else {
        & tar -xzf $archive -C $extracted
        if ($LASTEXITCODE -ne 0) { throw 'Fixture archive extraction failed.' }
    }
    Assert-Packaging (-not (Test-Path -LiteralPath (Join-Path $extracted 'removed.dll'))) 'Archive contains a stale dependency.'
    Assert-Packaging ((Get-Content -LiteralPath (Join-Path $extracted 'a2.exe') -Raw).Trim() -eq 'current executable') 'Archive does not contain the current executable.'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $extracted 'notices/dotnet/LICENSE.TXT')) 'Runtime notices were omitted.'

    $previousHash = (Get-FileHash -LiteralPath $archive).Hash
    Set-Content -LiteralPath (Join-Path $published 'preserve.txt') -Value 'keep until success'
    $packagingFixture.FailPublish = $true
    Assert-PackageFailure { & $packageScript -Runtime $runtime -Version '1.2.3' -SkipTests } 'dotnet failed'
    $packagingFixture.FailPublish = $false
    $packagingFixture.MissingNotices = $true
    Assert-PackageFailure { & $packageScript -Runtime $runtime -Version '1.2.3' -SkipTests } 'Unable to locate runtime license notices'
    $packagingFixture.MissingNotices = $false
    Assert-Packaging ((Get-FileHash -LiteralPath $archive).Hash -eq $previousHash) 'A failed package replaced the archive.'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $published 'preserve.txt')) 'A failed package replaced the existing output.'
    Assert-Packaging (@(Get-ChildItem -LiteralPath (Join-Path $testRoot 'artifacts/publish') -Filter '.a2-package-*' -Force).Count -eq 0) 'Staging directories were not cleaned.'

    # Force the final archive replacement to fail after the new directory moved.
    [System.IO.File]::Move($archive, "$archive.saved")
    New-Item -ItemType Directory -Path $archive | Out-Null
    Assert-PackageFailure { & $packageScript -Runtime $runtime -Version '1.2.3' -SkipTests } ''
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $published 'preserve.txt')) 'Archive commit failure did not restore the prior publish directory.'
    Remove-Item -LiteralPath $archive
    [System.IO.File]::Move("$archive.saved", $archive)
    Assert-Packaging ((Get-FileHash -LiteralPath $archive).Hash -eq $previousHash) 'Archive commit failure changed the prior archive.'

    & $packageScript -Runtime $runtime -Version '1.2.3' -SkipTests | Out-Null
    Assert-Packaging (-not (Test-Path -LiteralPath (Join-Path $published 'preserve.txt'))) 'Retry retained old files.'
}

Write-Host "Packaging: $($packagingFixture.Checks) offline checks passed."
