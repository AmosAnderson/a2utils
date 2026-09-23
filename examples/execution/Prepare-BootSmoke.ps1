# SPDX-FileCopyrightText: 2026 Amos Anderson
# SPDX-License-Identifier: GPL-2.0-only

param(
    [Parameter(Mandatory = $true)][string]$MamePath,
    [Parameter(Mandatory = $true)][string]$RomDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Choose a new output directory.' }
$emulatorPath = (Resolve-Path -LiteralPath $MamePath).Path
$romPath = (Resolve-Path -LiteralPath $RomDirectory).Path
New-Item -ItemType Directory -Path $outputPath | Out-Null
$binary = Join-Path $outputPath 'boot.bin'
dotnet run --project (Join-Path $repository 'src/A2Utils.Cli') -c Release --no-restore -- asm compile (Join-Path $PSScriptRoot 'boot.asm') --to $binary --format raw
if ($LASTEXITCODE -ne 0) { throw 'Assembling the boot program failed.' }
$programBytes = [IO.File]::ReadAllBytes($binary)
if ($programBytes.Length -gt 256) { throw 'The smoke program must fit in one sector.' }
$imageBytes = [byte[]]::new(143360)
[Array]::Copy($programBytes, $imageBytes, $programBytes.Length)
$imagePath = Join-Path $outputPath 'boot.dsk'
[IO.File]::WriteAllBytes($imagePath, $imageBytes)
$spec = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'boot.execution.json') | ConvertFrom-Json
$spec.emulatorPath = $emulatorPath
$spec.romDirectory = $romPath
$spec.diskImage = $imagePath
$spec | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputPath 'boot.execution.json') -Encoding utf8
Write-Output (Join-Path $outputPath 'boot.execution.json')
