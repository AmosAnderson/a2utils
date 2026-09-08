# A2Utils

C#/.NET 10 command-line utilities for Apple II and Apple IIe disk images.
This working development preview supports DOS 3.3 and ProDOS, using a pinned
CiderPress II DiskArc engine behind an independent library and CLI.

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
dotnet tool install A2Utils.Tool --version 0.1.0-dev --add-source artifacts/packages --tool-path artifacts/tools
```

Run `artifacts/tools/a2` (or `artifacts/tools/a2.exe` on Windows), or add that
directory to your PATH. No global tool installation is required.

## Supported operations

| Command | Behavior |
| --- | --- |
| `disk info IMAGE` | Container, layout, volume, capacity, and diagnostics |
| `disk ls IMAGE [PATH]` | Catalog; `--recursive` includes ProDOS subdirectories |
| `disk extract IMAGE [PATH] --to DIR` | Stored bytes and restoration manifest in a new directory |
| `disk verify IMAGE` | Read-only structural checks |
| `disk create OUTPUT --fs dos33` | Format a 140 KiB data disk |
| `disk add IMAGE HOSTFILE --name NAME --type TYPE` | Import a payload with explicit metadata |
| `disk replace IMAGE PATH HOSTFILE` | Replace payload while retaining file metadata |
| `disk delete IMAGE PATH` | Delete; nonempty directories require `--recursive` |
| `disk rename IMAGE PATH NEWNAME` | Rename within the current directory |
| `disk mkdir IMAGE PATH` | Create a ProDOS directory |
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

## Preservation and scripting

Extraction preserves stored bytes, including DOS load/length headers and sector
slack. It creates numbered host filenames ending in `.a2raw`; the versioned
`a2-manifest.json` maps them to original paths, metadata, SHA-256 hashes, and
allocated data extents. Restore with `disk add --manifest` into the same
filesystem type. This preserves supported file content and metadata, while
physical allocation may differ. Ordinary `add HOSTFILE` takes payload bytes;
text and BASIC conversion is not performed.

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
partitions, hybrid writes, extended/forked-file operations, text conversion,
and repair are outside this preview's supported scope.

See `PLAN.md` for milestone status and `AGENTS.md` for contributor guidance.
Third-party source and license notices are under `third_party/CiderPress2`;
the engine revision is pinned and its source hashes are recorded there.
The license for original A2Utils code remains to be selected before public
distribution; these development packages are for local evaluation.
