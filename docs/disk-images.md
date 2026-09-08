# Working with disk images

[Documentation home](../README.md) · [Command reference](cli-reference.md) · [Supported formats](formats/supported-images.md)

This guide covers catalog inspection, payload editing, preservation transfers,
and image conversion. Commands assume `a2` is available and run from the repository
root; see [getting started](getting-started.md). Each workflow states its inputs.
Use new output names on a repeat run, or explicitly choose `--overwrite` for
host files you intend to replace.

## Container, order, and filesystem

Three independent properties describe an image:

| Property | Meaning | Examples |
| --- | --- | --- |
| Container | Host-file wrapper around disk data | Raw sector/block bytes; a 2IMG header and optional metadata |
| Order | How sectors/blocks are arranged in the payload | `dos`, `prodos` |
| Filesystem | Catalog, allocation, and file metadata inside the disk | `dos33`, `prodos` |

Extensions are hints. In particular, the supplied `independent-dos33.po` fixture
uses ProDOS order **and contains DOS 3.3**. `disk convert` changes the first two
properties while retaining the filesystem; it is not a DOS-to-ProDOS file
migration or a resize command. [Supported combinations](formats/supported-images.md)
lists geometry limits and unsupported formats.

## Inspect an existing image

The repository includes original, independently constructed DOS fixtures.
These commands leave them unchanged:

```sh
a2 disk info tests/TestData/independent-dos33.do
a2 disk ls tests/TestData/independent-dos33.do
a2 disk attr tests/TestData/independent-dos33.do HELLO.BIN --json
a2 disk verify tests/TestData/independent-dos33.do
```

The catalog contains locked binary `HELLO.BIN` (5 logical bytes, load address
`0x2000`) and text `README` (8 Apple text bytes). Text catalog columns show type,
`*` for a locked entry, logical length, and image path. Use `--json` to inspect
stored length, storage size, raw name, timestamps, and access flags. These
lengths differ: file payload bytes, stored file bytes, and allocated space are
separate measurements.

`disk ls IMAGE DIRECTORY` lists that directory's immediate children;
`--recursive` includes descendants. `disk ls IMAGE FILE` lists just that file.
`verify` performs structural checks and never repairs an image. A successful
check does not mean a program will run or the disk will boot.

When detection is ambiguous, supply information you know about the image:

```sh
a2 disk info tests/TestData/independent-dos33.po --input-order prodos --input-fs dos33
```

Overrides select an interpretation; they do not transform the file. An override
that conflicts with a 2IMG header is refused. Unknown filesystems support
limited `info` inspection and explicit-order container conversion, not file
operations. `info` opens a read-only session, so JSON `isReadOnly` describes
that session; `imageWriteProtected` separately reports container protection.

## Image paths and names

Image paths belong to the disk filesystem, independently of Windows or Unix
host path rules. There is no current image directory, volume prefix, wildcard
expansion, or recursive search implied by a filename.

| Rule | DOS 3.3 | ProDOS |
| --- | --- | --- |
| Root | Omit an optional path or use `/`. | Omit an optional path or use `/`. |
| Name lookup | Case-sensitive: `README` and `readme` differ. | Case-insensitive; stored display case is preserved where supported. |
| Path separator | Flat catalog strings; `/` within a filename is literal. | `/`, even on Windows; leading/trailing `/` are accepted. |
| Directories | Not available. | Components such as `DOCS/README`; `.` and `..` components are rejected. |
| New ordinary names | Up to 30 characters; start with a letter for compatibility, avoid commas and trailing spaces. | 1–15 letters/digits/periods, beginning with a letter. |

Use uppercase DOS names for compatibility with older software. Existing unusual
DOS names are treated literally, including embedded `/` and `..`; they do not
become host paths during extraction. The adapter also represents some DOS
control characters as visible Unicode control pictures. Preservation extraction
retains the original raw name bytes.

For ProDOS, `PROGRAMS/HELLO` is relative to the volume root; do not include the
volume's name. Use single separators and quote paths containing shell-sensitive
characters. Copy/import do not truncate or sanitize names to make them fit.
Host filenames such as `long_file_name.txt` must be renamed before ProDOS import.
For one file, `disk add --name README` lets you choose an image name independently
of the host filename.

`copy` and `move` require an exact **new destination name**, with an existing
parent. To place `HELLO` in `PROGRAMS`, specify `PROGRAMS/HELLO`; passing
`PROGRAMS` asks to create an entry named `PROGRAMS`. Existing entries are not
merged or overwritten. `rename` accepts a single new name without slashes;
use `move` when changing the parent directory.

## Create a DOS work disk

This workflow exports a known payload from the fixture, creates a fresh data
disk, and adds it to a separate output image:

```sh
a2 disk export tests/TestData/independent-dos33.do HELLO.BIN --to hello-payload.bin
a2 disk create dos-empty.do --fs dos33 --volume-number 42
a2 disk add dos-empty.do hello-payload.bin --name HELLO --type B --load-address 0x2000 --output dos-work.do
a2 disk ls dos-work.do
a2 disk verify dos-work.do
```

The payload is synthetic test data, not an executable example. For an actual
assembly or Applesoft source workflow, use [program tools](programs.md).
DOS creation requires 140 KiB (35 tracks × 16 sectors × 256 bytes). Its default
container is raw, order is DOS, and volume number is 254. The created disk is
formatted without boot code or operating-system files.

### File types and addresses

`disk add` and binary `disk import` require explicit file types. Recognized
aliases are case-insensitive; numeric types use `0x` hexadecimal.

| Content | Accepted aliases | A2Utils type value |
| --- | --- | --- |
| Text | `T`, `TXT` | `0x04` |
| Integer BASIC bytes | `I`, `INT` | `0xfa` |
| Applesoft BASIC bytes | `A`, `BAS` | `0xfc` |
| Binary | `B`, `BIN` | `0x06` |
| DOS S | `S`, `F2` | `0xf2` |
| DOS R | `R`, `REL` | `0xfe` |
| DOS AA | `AA`, `F3` | `0xf3` |
| DOS BB | `BB`, `F4` | `0xf4` |
| ProDOS system file | `SYS` | `0xff` |
| ProDOS untyped file | `NON` | `0x00` |

Values are A2Utils/ProDOS type identifiers, not raw DOS catalog flag bytes. DOS
supports the first eight rows only; ProDOS accepts byte values `0x00`–`0xff`,
except `0x0f` for data-file creation because directories use `mkdir`. Treating a
file as a known type does not compile or validate its program content. Integer
BASIC bytes can be stored, but Integer BASIC source conversion is unsupported.

For DOS binaries, `--load-address` is required on add/import. It is written in
the DOS binary header. For ProDOS, use `--aux-type` to record the load address or
other type-specific auxiliary metadata; its default is 0. Both accept decimal
or `0x` hexadecimal from 0 through 65535. `--load-address` and `--aux-type` cannot
be combined, and `--load-address` is not accepted for text or BASIC imports.

`add` and `replace` take **logical payload bytes**, without DOS load/length
headers. They do not inspect a `.bin` extension or strip a host header. Program
compilation defaults to raw output suitable for these commands. Passing an
`asm compile --format dos` result directly to `disk add` would add its host
header as program data; use raw program output instead.

## Export or extract?

| Need | Command | Result |
| --- | --- | --- |
| Use a file's logical bytes | `disk export ... --to FILE` | One binary payload, without metadata or DOS headers |
| Edit a supported text file | `disk export ... --format text --to FILE` | BOM-free UTF-8 text with LF line endings |
| Preserve files for same-filesystem restoration | `disk extract ... --to NEWDIR` | Stored `.a2raw` files plus metadata and hashes in a manifest |
| Retain every disk byte and original allocation | Keep a complete copy of the original image | File extraction is not a whole-image backup |

DOS binary and BASIC files have header-defined logical lengths. Text length is
derived by the filesystem reader. DOS S/R/AA/BB types lack a reliable EOF, so
logical export can include sector padding. ProDOS logical export follows the
file's EOF. Resource forks are unsupported.

### Edit text explicitly

This independent example starts with the fixture's text file:

```sh
a2 disk export tests/TestData/independent-dos33.do README --to readme.txt --format text
a2 disk create text-work.do --fs dos33
a2 disk add text-work.do readme.txt --name README --format text --in-place
```

The exported file contains `A2UTILS` followed by an LF newline. Edit `readme.txt`
with a UTF-8 editor, then replace the image entry:

```sh
a2 disk replace text-work.do README readme.txt --format text --in-place
a2 disk export text-work.do README --to readme-check.txt --format text
```

Text import accepts valid UTF-8 with an optional BOM, printable ASCII, tabs,
and CR/LF/CRLF newlines. It writes CR line endings and sets high bits for DOS
text, leaving them clear for ProDOS. Export clears high bits and normalizes
newlines to LF. Unsupported Unicode, NUL, DEL, and other controls are refused;
no replacement characters or final newline are silently added. This explicit
normalization is not a byte-preservation operation. Use binary export or
extraction if exact stored text bytes matter.

`--format text` defaults add/import to TXT. Replacement requires the existing
T/TXT type. Tokenized BASIC needs `basic decompile`/`basic compile`, not text
conversion.

### Extract and restore a manifest

```sh
a2 disk extract tests/TestData/independent-dos33.do --to preserved-dos
a2 disk create restore-empty.do --fs dos33 --volume-number 42
a2 disk add restore-empty.do --manifest preserved-dos/a2-manifest.json --output restored.do
a2 disk verify restored.do
```

The extraction directory must not exist; extraction can create missing host
parent directories. Selecting a ProDOS directory includes descendants, and
selecting a nested file includes the necessary ancestor-directory metadata.
No additional recursive flag is needed.

The version-1 JSON manifest has these fields:

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Currently `1`; unsupported versions are refused. |
| `fileSystem` | `dos33` or `prodos`; restore requires the same filesystem type. |
| `entries` | File and directory records. |
| `entries[].entry` | Original `path`, `name`, directory flag, symbolic/numeric type, `auxType`, access/lock state, logical/stored/allocation lengths, raw name, timestamps, and condition/fork flags. |
| `entries[].hostFile` | Sibling `.a2raw` payload filename; `null` for directories. |
| `entries[].sha256` | SHA-256 of the stored payload; `null` for directories. |
| `entries[].dataExtents` | Ordered `{ "offset": ..., "length": ... }` ranges that contain allocated data; offsets/lengths are bytes. |

Raw names are JSON Base64 because the model stores a byte array. Payload names
have a numeric prefix and a sanitized display name, such as
`0001_HELLO.BIN.a2raw`; use `hostFile` rather than deriving the name yourself.
The host directory remains flat even when the image contains subdirectories.
Image paths and metadata live in the manifest.

Keep the manifest and its payload siblings together. Restore checks hashes,
stored lengths, raw names, unique paths, and ordered valid extents. Bytes
outside allocated extents must be zero. Payload paths cannot escape to another
directory or traverse links. Editing a payload without a matching manifest
fails hash validation; use logical export/add/replace for content editing.

Restore creates the recorded paths without merging existing entries; an empty
target image of sufficient capacity is the easiest destination. The manifest
does not store the volume name/number, boot code, original sector placement,
unused whole-disk bytes, or 2IMG wrapper metadata. Choose target volume options
explicitly when creating it.

DOS raw extraction preserves file headers and trailing sector bytes. ProDOS
extraction records data-fork bytes through EOF and sparse extents. Copy and
restore may convert ProDOS allocated all-zero blocks to sparse holes, changing
allocation and `storageSize` while retaining logical bytes, EOF, and supported
file metadata. Retain the original image when original physical allocation
matters.

## ProDOS directories and transfers

Create a larger ProDOS data disk and its directory hierarchy:

```sh
a2 disk create prodos-work.po --fs prodos --size 800k --volume-name WORK
a2 disk mkdir prodos-work.po DOCS/NOTES --parents --in-place
a2 disk ls prodos-work.po --recursive
```

ProDOS creation supports 280–65,535 blocks of 512 bytes. `--size 800k` creates
1,600 blocks; `--blocks 65535` requests the exact maximum. Choose either
`--size` or `--blocks`. Larger images require ProDOS order. `--parents` accepts
already-existing directories and creates missing ancestors, while refusing
file components and inaccessible parents. A plain `mkdir` requires a new leaf
and an existing parent. Supported nesting is bounded at 256 directory levels.

### Import a host directory

Prerequisite: create a host directory `notes` containing only files you intend
to import, such as `README.TXT` and `TOPICS/INDEX.TXT`, with UTF-8 ASCII text.
Each component must fit ProDOS naming rules. Continue using `prodos-work.po`
from the preceding commands:

```sh
a2 disk import prodos-work.po notes --to DOCS/NOTES --recursive --format text --in-place
```

This imports the **contents** of `notes` below `DOCS/NOTES`; it does not create
an extra `notes` directory. The `--to` image directory must exist and defaults
to `/`. Every file receives the same selected type and auxiliary metadata.
For binary import, supply `--type`; DOS binary import also requires one common
`--load-address`. Use individual `add` commands or a preservation manifest when
files need different metadata.

Import plans names and reads/checks every file before writing entries. A name
conflict, unrepresentable name, invalid text, changed input, or disk-full error
prevents the entire staged image from being committed. Existing directories
are conflicts, not merge points. Without `--recursive`, a subdirectory causes
failure rather than being silently skipped; DOS requires a flat host tree
even when the flag is supplied. Hidden files are included and must also have
valid image names.

Keep the destination image outside the host import tree. Links, junctions,
pipes, sockets, and devices are refused. On Linux/macOS, regular-file checks
require `/usr/bin/stat`. Input and transformed byte totals are bounded by the
image capacity, and entry counts/depth are also bounded. Free-space allocation
and catalog overhead can still cause an import smaller than that byte limit
to fail; the transaction leaves the original image intact.

### Copy and move image entries

After populating `DOCS` in the preceding workflow:

```sh
a2 disk copy prodos-work.po DOCS ARCHIVE --recursive --in-place
a2 disk move prodos-work.po ARCHIVE/NOTES OLDNOTES --in-place
a2 disk rename prodos-work.po OLDNOTES SAVEDNOTES --in-place
a2 disk ls prodos-work.po --recursive
```

Directory copying requires `--recursive`; moving a directory includes its
contents without that flag. Copy preserves supported file bytes, names/types,
auxiliary values, access flags, and timestamps, subject to the ProDOS sparse
allocation caveat above. Move changes the catalog location/name without
copying file data or reallocating its data blocks.

For an independent same-filesystem copy example:

```sh
a2 disk create copy-target.do --fs dos33
a2 disk copy copy-target.do HELLO.BIN HELLO.COPY --from tests/TestData/independent-dos33.do --in-place
a2 disk attr copy-target.do HELLO.COPY
```

`IMAGE` is always the receiving image. With `--from`, global input overrides
apply to it; `--source-order` and `--source-fs` select the source interpretation.
Source and destination filesystems must match, although their containers,
orders, and supported capacities may differ. A locked readable source can be
copied; the copy retains its lock. Moving, renaming, replacing, or deleting a
locked entry requires an explicit unlock.

### Attributes and deletion

Continue with `copy-target.do`, whose copied binary is locked:

```sh
a2 disk attr copy-target.do HELLO.COPY --unlock --aux-type 0x4000 --in-place
a2 disk rename copy-target.do HELLO.COPY DEMO --in-place
a2 disk attr copy-target.do DEMO --lock --in-place
```

`attr` with no changes is read-only. To change a DOS binary load address after
import, use its `--aux-type` option; add/import use `--load-address` instead.
Locking clears write/rename/delete access permissions; unlocking enables those
permissions. Parent-directory access also matters. `--overwrite` affects host
output replacement only and never bypasses these permissions. Container write
protection and host read-only attributes are separate from file locks.

Delete a named ProDOS directory and its contents explicitly, for example after
the earlier copy/move workflow:

```sh
a2 disk delete prodos-work.po SAVEDNOTES --recursive --in-place
```

An empty directory needs no recursive flag. Deletion checks child and parent
permissions before removing the tree. Volume-root delete/rename/copy/move and
placing a directory inside itself or a descendant are refused.

## Convert container and order

```sh
a2 disk convert tests/TestData/independent-dos33.do converted.2mg --container 2mg --order prodos
a2 disk info converted.2mg
a2 disk convert converted.2mg roundtrip.do --container raw --order dos --allow-metadata-loss
```

Both outputs still contain DOS 3.3. Conversion keeps payload sectors intact
under the selected ordering and validates the expected output bytes. It does
not alter files, install boot code, resize the image, or translate programs.

Raw-to-2IMG creates a new wrapper. 2IMG-to-2IMG keeps existing header extensions,
comments, creator data, gaps, and trailing bytes, updating necessary layout
fields. 2IMG-to-raw drops the wrapper and all nonpayload bytes and always
requires `--allow-metadata-loss`. A raw input of unknown filesystem needs an
explicit `--input-order` for conversion. Larger images support ProDOS order
only; output `--order dos` is restricted to 140 KiB images.

Conversion requires a separate output path. Existing output files need
`--overwrite`; that mode does not create an in-place backup of the output.

## Write transactions and backups

All CLI image mutations require one destination policy:

- `--output NEWIMAGE`: stage an edited copy and keep the input image unchanged.
- `--in-place`: stage an edited copy, validate it, then replace the input and
  retain a uniquely named sibling backup, `IMAGE.<unique-id>.bak`.

The temporary image is placed beside the destination, reopened for structural
validation, flushed, and committed only after source/destination change checks.
New outputs that appear during staging and changed existing inputs are refused.
Create, conversion, export, and program output also stage and validate before
replacing host files. `--overwrite` must be explicit for an existing separate
output and does not create a backup of that output.

Host output file parent directories must already exist; extraction is the
exception and creates missing parents for its new output directory. The host
needs space for staging and, for in-place work, retained backups. Successful
mutation output reports the backup path, also available as JSON `backupPath`.
Each in-place command produces its own backup; the CLI does not prune them.

To undo an in-place change, use the reported backup as input and write a new
working copy with your host's file-copy command. Do not assume the newest
backup is the original disk: it represents the image immediately before that
particular command.

Writes are refused for damaged/dubious allocation, hybrid or embedded
filesystems, volumes containing unsupported forked files, and protected images.
Write paths reject symbolic links/junctions and input/output aliases. These
checks support ordinary local workflows; they do not promise atomic
compare-and-swap against hostile writers or power-loss/network-filesystem
durability. Keep complete original images for preservation.

See [scripting](scripting.md) for checking every exit status and obtaining backup
paths programmatically, or [troubleshooting](troubleshooting.md) when an
operation is refused. For building the library, testing preservation, or
extending the adapter, see [development](development.md).
