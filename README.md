# A2Utils

C#/.NET 10 command-line utilities for Apple II and Apple IIe disk images and
programs. This development preview supports DOS 3.3 and ProDOS through a pinned
CiderPress II DiskArc engine, plus native assembly and Applesoft BASIC tools.

## Build and run

Install the SDK selected by `global.json`, then run from this directory:

```sh
dotnet restore A2Utils.slnx --locked-mode
dotnet build A2Utils.slnx -c Release --no-restore
dotnet test A2Utils.slnx -c Release --no-restore
dotnet run --project src/A2Utils.Cli -- disk --help
dotnet run --project src/A2Utils.Cli -- disk ls tests/TestData/independent-dos33.do
```

`a2` is the installed executable name. The examples below assume installation
from a locally built package:

```sh
dotnet pack src/A2Utils.Cli -c Release -o artifacts/packages
dotnet tool install A2Utils.Tool --version 0.3.0-dev --add-source artifacts/packages --tool-path artifacts/tools
```

Run `artifacts/tools/a2` (or `artifacts/tools/a2.exe` on Windows), or add that
directory to your PATH. No global tool installation is required. To upgrade an
existing preview, use `dotnet tool update` with the same options.

## Compile and decompile programs

```sh
a2 asm compile examples/hello.asm --to hello.bin
a2 asm decompile hello.bin --origin 0x2000 --to hello.dis.asm
a2 basic compile examples/hello.bas --to hello.basbin
a2 basic decompile hello.basbin --to hello.list.bas
```

`asm compile` assembles 6502 source; `asm decompile` produces assembly that can
be reassembled to identical bytes. Use `--cpu 65c02` for enhanced Apple IIe/IIc
instructions, or `--cpu w65c02` for newer WDC extensions. Original source names,
comments, and the distinction between code and data cannot be recovered.
Aliases `assemble` and `disassemble` are also available.

`basic compile` tokenizes numbered Applesoft BASIC source; `basic decompile`
produces a readable listing. Aliases are `tokenize` and `detokenize`.
This is Applesoft's interpreted program format; it does not produce machine
code or validate every BASIC expression. Integer BASIC is not supported.

Output defaults to raw payload bytes, ready for `disk add` or `disk replace`.
Use `--format dos` for host files with DOS load/length headers. An assembly
source needs `.org` or `--origin`; raw machine-code input needs `--origin`.
BASIC defaults to `$0801`. Existing output files require `--overwrite`.

```sh
a2 disk add work.do hello.bin --name HELLO --type B --load-address 0x2000 --in-place
a2 disk add work.do hello.basbin --name DEMO --type A --in-place
a2 asm decompile HELLO --from-image work.do --to hello.asm
a2 basic decompile DEMO --from-image work.do --to demo.bas
```

See [the program tools guide](docs/programs.md) for assembly syntax, BASIC
rules, ProDOS examples, CPU compatibility, and format limits.

## Supported disk operations

| Command | Behavior |
| --- | --- |
| `disk info IMAGE` | Container, layout, volume, capacity, and diagnostics |
| `disk ls IMAGE [PATH]` | Catalog; `--recursive` includes ProDOS subdirectories |
| `disk extract IMAGE [PATH] --to DIR` | Stored bytes and restoration manifest in a new directory |
| `disk export IMAGE PATH --to HOSTFILE` | Logical payload; `--format text` converts Apple text to UTF-8 |
| `disk verify IMAGE` | Read-only structural checks |
| `disk create OUTPUT --fs dos33` | Format a 140 KiB data disk |
| `disk add IMAGE HOSTFILE --name NAME --type TYPE` | Import a payload with explicit metadata |
| `disk replace IMAGE PATH HOSTFILE` | Replace payload while retaining file metadata |
| `disk import IMAGE HOSTDIRECTORY` | Import a directory's contents atomically; `--recursive` includes subdirectories |
| `disk copy IMAGE SOURCE DESTINATION` | Copy to a new image path; `--from SOURCEIMAGE` copies between images |
| `disk move IMAGE SOURCE DESTINATION` | Move to a new path within the same image |
| `disk delete IMAGE PATH` | Delete; nonempty directories require `--recursive` |
| `disk rename IMAGE PATH NEWNAME` | Rename within the current directory |
| `disk mkdir IMAGE PATH` | Create a ProDOS directory; `--parents` creates missing ancestors |
| `disk attr IMAGE PATH` | Read metadata; `--lock`, `--unlock`, `--type`, `--aux-type` update it |
| `disk convert INPUT OUTPUT --container raw --order prodos` | Convert storage layout/container |

File mutations require `--output NEWIMAGE` or `--in-place`. Existing output
files require `--overwrite`. In-place mode creates a unique `.bak` file.

```sh
a2 disk info games.dsk --json
a2 disk extract games.dsk --to extracted
a2 disk create work.do --fs dos33 --volume-number 42
a2 disk add work.do hello.bin --name HELLO --type B --load-address 0x2000 --output updated.do
a2 disk create work.po --fs prodos --size 800k --volume-name WORK
a2 disk mkdir work.po PROGRAMS --in-place
a2 disk add work.po hello.bin --name PROGRAMS/HELLO --type BIN --aux-type 0x2000 --in-place
a2 disk add work.do --manifest extracted/a2-manifest.json --output restored.do
a2 disk convert updated.do updated.2mg --container 2mg --order prodos
```

Raw `.do`, `.po`, `.dsk`, and single-volume `.hdv` images and sector-data
`.2mg`/`.2img` containers are supported. DOS 3.3 requires standard 140 KiB
geometry. ProDOS supports 280–65,535 blocks (512 bytes each); an existing
65,536-block image with an unused trailing block is also preserved. Use
`--size 140k`, `--size 800k`, or an exact `--blocks` count when creating disks.
`k` means KiB and `m` means MiB. Larger images require ProDOS block order.

Images with ambiguous layouts require `--input-order dos|prodos` and,
when needed, `--input-fs dos33|prodos`. Extensions are hints. Converting an
image does not convert its filesystem. Converting 2IMG to raw requires
`--allow-metadata-loss`, since raw images cannot store the container header.

## Text editing and file transfers

`export` writes logical file content, removing DOS binary/BASIC headers where
applicable. DOS S/R/AA/BB types lack a reliable EOF, so their exported payload
can include sector padding. Its default `--format binary` leaves payload bytes
unchanged. Explicit `--format text` requires a TXT file, removes Apple high bits, and converts Apple
line endings to UTF-8 LF. Text import accepts UTF-8 (with an optional BOM),
printable ASCII, tabs, and line endings; other characters are rejected.
It writes CR line endings and DOS high bits where appropriate.

```sh
a2 disk export games.do HELLO --to hello.bin
a2 disk export games.do README --to readme.txt --format text
a2 disk add work.do readme.txt --name README --format text --in-place
a2 disk replace work.do README readme.txt --format text --in-place
a2 disk mkdir work.po DOCS/NOTES --parents --in-place
a2 disk import work.po ./notes --to DOCS/NOTES --recursive --format text --in-place
a2 disk copy work.po DOCS ARCHIVE --recursive --in-place
a2 disk copy target.do HELLO HELLO --from games.do --in-place
a2 disk move work.po ARCHIVE/NOTES OLDNOTES --in-place
```

Text `add` and `import` default to TXT; binary imports require `--type`.
Text `replace` requires an existing TXT file. Use the `basic` commands for
tokenized BASIC conversion. Export requires `--overwrite` to replace an existing host file.

Copy destinations are exact new paths with existing parent directories. Copying
a directory requires `--recursive`; copying between images requires matching
filesystems (DOS to DOS or ProDOS to ProDOS). Copy preserves supported metadata
and contents; move retains file-data allocation within the original image.

Directory import uses exact host names, rejects collisions, and rolls back the
entire operation if any file fails. `--to` defaults to the image root and must
already exist. DOS accepts flat directories; ProDOS supports recursive trees.
Keep output images outside the imported host directory. Links and special files
are rejected. On Linux and macOS, directory import uses `/usr/bin/stat` to
check regular files before reading them.

## Preservation and scripting

Extraction preserves stored bytes, including DOS load/length headers and sector
slack. It creates numbered host filenames ending in `.a2raw`; the versioned
`a2-manifest.json` maps them to original paths, metadata, SHA-256 hashes, and
allocated data extents. Restore with `disk add --manifest` into the same
filesystem type. This preserves supported file content and metadata, while
physical allocation may differ. In ProDOS copies and manifest restores,
allocated all-zero blocks may become sparse holes without changing logical
bytes or metadata. Ordinary `add HOSTFILE` takes payload bytes; text conversion
requires explicit `--format text`.

Results go to stdout; diagnostics go to stderr. `--json` emits an envelope
with `schemaVersion: 1`. Errors have stable codes and appear on stderr.
`--quiet` suppresses normal text and `--verbose` adds diagnostic detail.
Exit codes: 0 success, 1 unexpected failure, 2 usage, 3 unsupported or ambiguous
format, 4 corruption, 5 host I/O, 6 refused/cancelled operation.

Writes use a temporary sibling file, reopen and verify it, then replace the
destination. Source-change checks and backups protect ordinary local workflows;
atomic compare-and-swap against hostile concurrent writers and network/power-loss
durability are not promised. Extraction refuses existing directories and paths
through symbolic links or junctions. Back up irreplaceable source media.

## Development status

Local Windows tests cover independently constructed disk images, sector-order
vectors, DOS/ProDOS allocation boundaries, sparse restore, failed transactions,
and CLI workflows. The repeatable DiskArc evaluation is documented in
`docs/decisions/0001-disk-engine.md`. Run formatting with:

```sh
dotnet format A2Utils.slnx --verify-no-changes --no-restore --exclude third_party
```

The CI workflow targets Windows, Linux, and macOS. `eng/Package.ps1` produces
a local tool package and a self-contained runtime download. Cross-platform CI
execution and emulator catalog/load checks must pass before a stable release.
New disks are formatted data volumes without boot code. NIB/WOZ, DOS 3.2,
partitions, hybrid writes, extended/forked-file operations, Integer BASIC conversion,
and repair are outside this preview's supported scope.

See `PLAN.md` for milestone status and `AGENTS.md` for contributor guidance.
Third-party source and license notices are under `third_party/CiderPress2`;
the engine revision is pinned and its source hashes are recorded there.
The license for original A2Utils code remains to be selected before public
distribution; these development packages are for local evaluation.
