# SPDX-FileCopyrightText: 2026 Amos Anderson
# SPDX-License-Identifier: MIT

param(
    [string]$Runtime = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Runtime -notin @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) {
    throw "Unsupported packaging runtime: $Runtime"
}
$versionArguments = @()
$archiveName = "a2utils-$Runtime"
if ($PSBoundParameters.ContainsKey('Version')) {
    $versionPattern = '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?\z'
    if ($Version -cnotmatch $versionPattern) {
        throw 'Version must be a semantic version such as 0.3.0 or 0.3.0-rc.1, without a v prefix or build metadata.'
    }
    $versionArguments = @("-p:Version=$Version")
    $archiveName = "a2utils-$Version-$Runtime"
}

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

$stagingRoot = $null
$keepStaging = $false
Push-Location $projectRoot
try {
    Invoke-DotNet (@('restore', 'A2Utils.slnx', '--locked-mode', '--configfile', 'NuGet.Config') + $versionArguments)
    if (-not $SkipTests) {
        Invoke-DotNet (@('test', 'A2Utils.slnx', '-c', 'Release', '--no-restore') + $versionArguments)
    }
    Invoke-DotNet (@('pack', 'src/A2Utils.Cli', '-c', 'Release', '--no-restore', '-o', 'artifacts/packages') + $versionArguments)
    # dotnet publish leaves unrelated files in its output directory. Always start
    # with a new staging tree so removed dependencies cannot enter a later archive.
    $stagingRoot = Join-Path $projectRoot "artifacts/publish/.a2-package-$([guid]::NewGuid().ToString('N'))"
    $publishPath = Join-Path $stagingRoot 'output'
    New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
    Invoke-DotNet (@('publish', 'src/A2Utils.Cli', '-c', 'Release', '-r', $Runtime,
        '--self-contained', 'true', '-p:PackAsTool=false', '-o', $publishPath) + $versionArguments)
    $dependencies = Get-Content -LiteralPath (Join-Path $publishPath 'a2.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $runtimePrefix = "runtimepack.Microsoft.NETCore.App.Runtime.$Runtime/"
    $runtimeLibrary = @($dependencies.libraries.Keys | Where-Object { $_.StartsWith($runtimePrefix) })
    if ($runtimeLibrary.Count -ne 1) { throw 'Unable to identify the packaged runtime version' }
    $runtimeVersion = $runtimeLibrary[0].Substring($runtimePrefix.Length)
    $assets = Get-Content -LiteralPath 'src/A2Utils.Cli/obj/project.assets.json' -Raw | ConvertFrom-Json -AsHashtable
    $runtimeNotices = Join-Path $publishPath 'notices/dotnet'
    New-Item -ItemType Directory -Path $runtimeNotices -Force | Out-Null
    $copiedNotices = $false
    foreach ($packageRoot in $assets.packageFolders.Keys) {
        $runtimePackage = Join-Path $packageRoot "microsoft.netcore.app.runtime.$Runtime/$runtimeVersion"
        if (Test-Path -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT')) {
            Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination $runtimeNotices
            Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination $runtimeNotices
            $copiedNotices = $true
            break
        }
    }
    if (-not $copiedNotices) { throw 'Unable to locate runtime license notices' }
    if ($Runtime.StartsWith('win-')) {
        $archivePath = Join-Path $projectRoot "artifacts/$archiveName.zip"
        $stagedArchive = Join-Path $stagingRoot 'package.zip'
        Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $stagedArchive
    }
    else {
        $archivePath = Join-Path $projectRoot "artifacts/$archiveName.tar.gz"
        $stagedArchive = Join-Path $stagingRoot 'package.tar.gz'
        & tar -czf $stagedArchive -C $publishPath .
        if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed' }
    }
    $publishedPath = Join-Path $projectRoot "artifacts/publish/$Runtime"
    $previousPath = Join-Path $stagingRoot 'previous'
    $movedPrevious = $false
    $movedOutput = $false
    try {
        if ([System.IO.Directory]::Exists($publishedPath)) {
            [System.IO.Directory]::Move($publishedPath, $previousPath)
            $movedPrevious = $true
        }
        [System.IO.Directory]::Move($publishPath, $publishedPath)
        $movedOutput = $true
        [System.IO.File]::Move($stagedArchive, $archivePath, $true)
    }
    catch {
        try {
            if ($movedOutput) { [System.IO.Directory]::Move($publishedPath, $publishPath) }
            if ($movedPrevious) { [System.IO.Directory]::Move($previousPath, $publishedPath) }
        }
        catch {
            $keepStaging = $true
            throw "Unable to restore the previous publish directory; retained staging at ${stagingRoot}: $_"
        }
        throw
    }
    Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
}
finally {
    if (-not $keepStaging -and $stagingRoot -and (Test-Path -LiteralPath $stagingRoot)) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    Pop-Location
}
