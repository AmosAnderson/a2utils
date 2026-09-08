# Supported image combinations

[Documentation home](../../README.md) · [Disk workflows](../disk-images.md) · [Command reference](../cli-reference.md)

The application distinguishes image container, payload order, filesystem, and
geometry. Filename suffixes do not determine the filesystem.

## Filesystems, geometry, and containers

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

One block is 512 bytes. A standard 140 KiB disk has 280 blocks, represented as
35 tracks of 16 sectors of 256 bytes. Larger contiguous payloads use ProDOS
block order; they cannot be converted to DOS sector order. Conversion supports
payloads of 280–65,536 complete blocks independently of whether a filesystem is
recognized. File operations still require a supported filesystem and geometry.

Common raw suffixes are `.do`, `.po`, `.dsk`, and `.hdv`; `.2mg` and `.2img`
name 2IMG wrappers. The CLI option spelling is `--container 2mg`.
Raw suffixes do not determine filesystem identity or override conflicting data.
A 140 KiB DOS filesystem can use either DOS or ProDOS payload order, as shown
by the [independent fixtures](../../tests/TestData/README.md).

2IMG conversion accepts container versions 0/1 with sector-data format 0
(DOS order) or 1 (ProDOS order). Header payload ranges, block counts, and
comment/creator-data ranges must be valid and nonoverlapping. A supplied input
order must agree with the header.

## Conversion and preservation

2IMG-to-2IMG conversion preserves header extensions, comments, creator data,
gaps, and trailing bytes while changing required layout fields. Conversion
to raw always requires `--allow-metadata-loss`, because the header and any
nonpayload bytes are discarded. Raw-to-2IMG creates a new header. Container
conversion retains the original filesystem and geometry; it does not copy DOS
files into a ProDOS volume, resize a volume, or add boot code.

Extraction stores DOS raw file bytes and ProDOS data-fork bytes plus a versioned
manifest. DOS headers and trailing sector bytes are retained. ProDOS file EOF
defines the extracted byte count. Filesystem allocation locations and unused
whole-disk space are not part of file extraction; retain the original image
when a complete disk-level copy is needed.

`disk export` writes the logical payload, removing DOS binary/BASIC headers
where applicable. DOS S/R/AA/BB files lack a reliable EOF, so their payload can
include sector padding. `--format text` explicitly converts TXT content to
UTF-8. `disk copy` requires matching source and destination filesystems.
ProDOS copies and manifest restores may turn allocated all-zero blocks into
sparse holes; logical bytes, EOF, and supported metadata remain preserved.

File manifests retain file and directory metadata, not volume identity,
boot sectors, wrapper metadata, or complete free space. See the
[manifest reference](../disk-images.md#extract-and-restore-a-manifest) for its
fields and restore requirements. Program binary headers and tokenized BASIC
are described separately in [program tools](../programs.md).

## Operation boundaries

| Image condition | Available behavior |
| --- | --- |
| Recognized undamaged DOS/ProDOS | Inspection, verification, file reads, and supported staged writes, subject to access flags. |
| Unknown filesystem in supported contiguous layout | Limited `info`; explicit-order container conversion. |
| Ambiguous raw order/filesystem | Explicit input overrides are required for the interpretation. |
| 2IMG container write protection | Inspection/read operations; file mutation refused. |
| Hybrid/embedded filesystem or damaged allocation | Writes refused; read/inspection results depend on the supported structure that can be opened. |
| Resource-fork files | Forked-file operations refused; a volume containing them is not eligible for file mutations. |

NIB/WOZ, 13-sector DOS, DiskCopy, partitions, hybrid writes, and forked-file
operations are unsupported. Preserve such images for inspection with a tool
that explicitly supports their structures. `verify` does not repair damage.
Newly formatted images are data volumes without operating-system files or
boot code. Successful structural verification is not a bootability or program
execution test.

Format documentation and primary-reference links are included with the pinned
engine under [DiskArc](../../third_party/CiderPress2/DiskArc). The adapter choice
is documented in the [disk engine decision](../decisions/0001-disk-engine.md);
completed checks and remaining release limits are recorded in
[validation status](../VALIDATION.md).
