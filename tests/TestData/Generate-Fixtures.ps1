# SPDX-FileCopyrightText: 2026 Amos Anderson
# SPDX-License-Identifier: GPL-2.0-only

$ErrorActionPreference = 'Stop'

# Construct all sectors directly. This generator deliberately has no disk-engine dependency.
$image = [byte[]]::new(35 * 16 * 256)
for ($track = 3; $track -lt 35; $track++) {
    if ($track -eq 17) { continue }
    for ($sector = 0; $sector -lt 16; $sector++) {
        $offset = ($track * 16 + $sector) * 256
        for ($index = 0; $index -lt 256; $index++) { $image[$offset + $index] = 0x5a }
        $image[$offset] = [byte]$track
        $image[$offset + 1] = [byte]$sector
    }
}

$vtoc = 17 * 16 * 256
$image[$vtoc] = 4
$image[$vtoc + 1] = 17
$image[$vtoc + 2] = 15
$image[$vtoc + 3] = 3
$image[$vtoc + 6] = 42
$image[$vtoc + 0x27] = 122
$image[$vtoc + 0x30] = 18
$image[$vtoc + 0x31] = 1
$image[$vtoc + 0x34] = 35
$image[$vtoc + 0x35] = 16
$image[$vtoc + 0x37] = 1
for ($track = 3; $track -lt 35; $track++) {
    if ($track -eq 17) { continue }
    $image[$vtoc + 0x38 + $track * 4] = 0xff
    $image[$vtoc + 0x39 + $track * 4] = 0xff
}
# Track 18 sectors 12-15 belong to our two files.
$image[$vtoc + 0x38 + 18 * 4] = 0x0f

for ($sector = 15; $sector -gt 1; $sector--) {
    $catalog = (17 * 16 + $sector) * 256
    $image[$catalog + 1] = 17
    $image[$catalog + 2] = [byte]($sector - 1)
}

$catalog = (17 * 16 + 15) * 256
$files = @(
    @{ Name = 'HELLO.BIN'; Type = 0x84; List = 15; Data = 14 },
    @{ Name = 'README'; Type = 0x00; List = 13; Data = 12 }
)
for ($fileIndex = 0; $fileIndex -lt $files.Count; $fileIndex++) {
    $file = $files[$fileIndex]
    $entry = $catalog + 0x0b + $fileIndex * 35
    $image[$entry] = 18
    $image[$entry + 1] = [byte]$file.List
    $image[$entry + 2] = [byte]$file.Type
    for ($index = 0; $index -lt 30; $index++) { $image[$entry + 3 + $index] = 0xa0 }
    for ($index = 0; $index -lt $file.Name.Length; $index++) {
        $image[$entry + 3 + $index] = [byte]([int][char]$file.Name[$index] -bor 0x80)
    }
    $image[$entry + 33] = 2
    $list = (18 * 16 + $file.List) * 256
    [Array]::Clear($image, $list, 256)
    $image[$list + 12] = 18
    $image[$list + 13] = [byte]$file.Data
}

$binary = (18 * 16 + 14) * 256
for ($index = 0; $index -lt 256; $index++) { $image[$binary + $index] = 0xcc }
# B header: load address $2000, logical length 5; binary content is not text-converted.
([byte[]](0x00, 0x20, 0x05, 0x00, 0x00, 0x7f, 0x80, 0xff, 0x0d)).CopyTo($image, $binary)
$text = (18 * 16 + 12) * 256
[Array]::Clear($image, $text, 256)
$message = "A2UTILS`r"
for ($index = 0; $index -lt $message.Length; $index++) {
    $image[$text + $index] = [byte]([int][char]$message[$index] -bor 0x80)
}
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'independent-dos33.do'), $image)

# For each ProDOS-ordered sector, select this DOS-ordered sector on the same track.
$dosForProDos = @(0, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 15)
$proDosOrder = [byte[]]::new($image.Length)
for ($track = 0; $track -lt 35; $track++) {
    for ($sector = 0; $sector -lt 16; $sector++) {
        [Array]::Copy($image, ($track * 16 + $dosForProDos[$sector]) * 256,
            $proDosOrder, ($track * 16 + $sector) * 256, 256)
    }
}
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'independent-dos33.po'), $proDosOrder)

Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $PSScriptRoot 'independent-dos33.do'),
    (Join-Path $PSScriptRoot 'independent-dos33.po') | Format-Table Hash, Path
