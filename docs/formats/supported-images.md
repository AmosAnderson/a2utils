# Supported image combinations

The application distinguishes image container, payload order, filesystem, and
geometry. Filename suffixes do not determine the filesystem.

| Filesystem | Payload size | Orders | Containers |
| --- | --- | --- | --- |
| DOS 3.3 | 143,360 bytes | DOS or ProDOS | Raw, sector-data 2IMG |
| ProDOS | 143,360 bytes | DOS or ProDOS | Raw, sector-data 2IMG |
| ProDOS | 280–65,535 512-byte blocks | ProDOS for larger volumes | Raw, sector-data 2IMG |

An existing 65,536-block ProDOS host image may contain an unused trailing
block; conversion and staged mutations preserve it. Creation is limited to
65,535 blocks. Unknown filesystems permit metadata inspection and explicit-order
container conversion, but no file operations. Ambiguous raw layouts require an
override rather than guessing.

2IMG-to-2IMG conversion preserves header extensions, comments, creator data,
gaps, and trailing bytes while changing required layout fields. Conversion
to raw requires explicit metadata-loss consent. Container conversion retains
the original filesystem; it does not copy DOS files into a ProDOS volume.

Extraction stores DOS raw file bytes and ProDOS data-fork bytes plus a versioned
manifest. DOS headers and trailing sector bytes are retained. ProDOS file EOF
defines the extracted byte count. Filesystem allocation locations and unused
whole-disk space are not part of file extraction; retain the original image
when a complete disk-level copy is needed.

`disk export` writes the logical payload, removing DOS binary/BASIC headers
where applicable. DOS S/R/AA/BB files lack a reliable EOF, so their payload can
include sector padding. `--format text` explicitly converts TXT content to
UTF-8. `disk copy` requires matching source and destination filesystems. ProDOS copies and manifest
restores may turn allocated all-zero blocks into sparse holes; logical bytes,
EOF, and supported metadata remain preserved.

NIB/WOZ, 13-sector DOS, DiskCopy, partitions, hybrid writes, and forked-file
operations are unsupported. Preserve such images for inspection with a tool
that explicitly supports their structures.

Format documentation and primary-reference links are included with the pinned
engine under `third_party/CiderPress2/DiskArc`. The adapter decision is in
`docs/decisions/0001-disk-engine.md` and actual verification is recorded in
`docs/VALIDATION.md`.
