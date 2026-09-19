# A2Utils

A2Utils is a C#/.NET 10 command-line toolkit for Apple II and Apple IIe disk
images and programs. Use it to inspect and edit DOS 3.3/ProDOS disks, assemble
6502/65C02 source, and convert Applesoft BASIC between listings and tokenized
programs. Build projects into disk images, prepare graphics assets, test pure
6502/65C02 routines in-process, and test full machines through an optional MAME
installation. The reusable core library is independent of console output.

The current version is **0.6.1-dev**, a development preview. The
[GitHub repository](https://github.com/AmosAnderson/a2utils) is private.

The AI programming workflow now includes [setup profiles and starters](docs/setup.md),
[runtime memory checks](docs/runtime-memory.md), [project graphics assets and visual tests](docs/project-assets.md),
[ordered interactions and routine/cycle tests](docs/interactive-testing.md),
[audio assertions](docs/audio-execution.md), [CFFA2 block devices](docs/block-storage-execution.md), and [reusable Apple II routines](examples/runtime/README.md).
Agents can also discover typed command contracts, inspect or import projects,
preflight declarative disk changes, and invoke the CLI through its local MCP
stdio server.

## Download and run

When a release is available, download the package for your computer and
`SHA256SUMS.txt` from
[GitHub Releases](https://github.com/AmosAnderson/a2utils/releases). Access to
the private repository is required.

| Computer | Release asset | Command after extraction |
| --- | --- | --- |
| Windows x64 | `a2utils-VERSION-win-x64.zip` | `.\a2.exe` |
| Linux x64 | `a2utils-VERSION-linux-x64.tar.gz` | `./a2` |
| Apple Silicon macOS | `a2utils-VERSION-osx-arm64.tar.gz` | `./a2` |

These archives are self-contained and do not require a separate .NET install.
They contain the executable, .NET runtime, supporting libraries, and notices at
the archive root. Extract an archive into a new dedicated directory and keep all
of those files together; copying only `a2` or `a2.exe` will not work.

Replace `VERSION` below with the version in the downloaded filename. Verify the
archive against its line in `SHA256SUMS.txt` before running it.

Windows PowerShell:

```powershell
$version = 'VERSION'
$archive = ".\a2utils-$version-win-x64.zip"
$archiveName = Split-Path -Leaf $archive
$actual = (Get-FileHash $archive -Algorithm SHA256).Hash
$expected = ((Select-String -LiteralPath SHA256SUMS.txt -SimpleMatch "  $archiveName").Line -split '\s+')[0]
if ($actual -ne $expected) { throw 'SHA-256 mismatch.' }
Expand-Archive $archive -DestinationPath ".\a2utils-$version"
& ".\a2utils-$version\a2.exe" --version
& ".\a2utils-$version\a2.exe" --help
```

Linux:

```sh
version='VERSION'
grep -F "  a2utils-${version}-linux-x64.tar.gz" SHA256SUMS.txt | sha256sum --check -
mkdir "a2utils-$version"
tar -xzf "a2utils-$version-linux-x64.tar.gz" -C "a2utils-$version"
"./a2utils-$version/a2" --version
"./a2utils-$version/a2" --help
```

Apple Silicon macOS:

```sh
version='VERSION'
grep -F "  a2utils-${version}-osx-arm64.tar.gz" SHA256SUMS.txt | shasum -a 256 --check
mkdir "a2utils-$version"
tar -xzf "a2utils-$version-osx-arm64.tar.gz" -C "a2utils-$version"
"./a2utils-$version/a2" --version
"./a2utils-$version/a2" --help
```

The Windows check stops on a mismatch; the Linux and macOS checks report the
matching archive as `OK`.

The Unix archives preserve execute permission. If another extraction program
does not, run `chmod +x PATH_TO_A2/a2`. The development archives are not
code-signed or notarized, so Windows SmartScreen or macOS Gatekeeper may warn.
Verify the checksum and source before allowing the program; on macOS use Privacy
& Security's **Open Anyway** control if appropriate.

The examples below use `a2` for readability. Invoke it by its extracted path or
add the entire extraction directory to `PATH`; do not move only the executable.
Windows ARM, Linux ARM, and Intel Mac users can instead install the portable
`A2Utils.Tool.VERSION.nupkg`. From its download directory, with the .NET 10 SDK
installed:

```sh
dotnet tool install --global A2Utils.Tool --version VERSION --add-source .
a2 --version
```

Use `dotnet tool update --global A2Utils.Tool --version VERSION --add-source .`
instead when upgrading an existing global installation.

## First commands

Help is available at every command level. Start with read-only inspection of a
copy of your disk image:

```sh
a2 --version
a2 --help
a2 disk --help
a2 disk info "my-disk.dsk"
a2 disk ls "my-disk.dsk"
a2 disk verify "my-disk.dsk"
```

`disk info` identifies the container, sector order, and filesystem. Extensions
are only hints; ambiguous raw images may need justified `--input-order dos` or
`--input-order prodos` and `--input-fs dos33` or `--input-fs prodos` overrides.
`disk verify` checks filesystem structure without repairing or running the disk.
Add `--verbose` for diagnostic detail or `--json` for a stable scripting result.
Quote host paths containing spaces, write command-line hexadecimal values as
`0x2000`, and create output parent directories before commands that write files.
See [troubleshooting](docs/troubleshooting.md) when a diagnostic is unclear.

## Protect your images and outputs

Commands that modify entries or attributes in an existing image require exactly
one destination mode. Prefer `--output NEWIMAGE` initially; it leaves the source
image unchanged. `--in-place` replaces the source only after validation and
creates a uniquely named `.bak` containing its previous bytes. Keep independent
backups of important images.

Host-file outputs refuse to replace existing files unless `--overwrite` is
given. That option does not overwrite entries inside an image or bypass locks;
use `disk replace` for an intentional entry update. Writes are staged and
validated before replacement.

Use `disk export` for a logical payload or readable UTF-8 text. Use
`disk extract` when you need stored bytes plus an `a2-manifest.json` for a later
same-filesystem restore. Neither substitutes for retaining the original whole
image when physical layout and untouched disk bytes matter.

## Common tasks

Use your own image, assembly, and BASIC filenames in these examples:

```sh
a2 disk export "my-disk.dsk" README --format text --to readme.txt
a2 disk extract "my-disk.dsk" --to extracted-disk
a2 asm compile hello.asm --origin 0x2000 --to hello.bin
a2 asm decompile hello.bin --origin 0x2000 --to hello.dis.asm
a2 basic compile hello.bas --to hello.basbin
a2 basic decompile hello.basbin --to hello.list.bas
a2 disk create work.do --fs dos33
a2 disk add work.do hello.bin --name HELLO --type B --load-address 0x2000 --in-place
a2 disk add work.do hello.basbin --name DEMO --type A --in-place
a2 disk verify work.do
a2 disk diff original.do work.do --json
a2 project resolve project.a2.json --json
a2 build project.a2.json --cache artifacts/build-cache --json
```

Commands that write host outputs require new destinations or the appropriate
explicit replacement option; verification, diff, and project resolution are
read-only with respect to requested outputs. An image made by `disk create` is
a formatted data volume without an operating system or boot code. The
assembly example targets address `0x2000`; any `.org` in `hello.asm` must agree.
Follow the [first-project walkthrough](docs/getting-started.md#build-a-disk-with-two-programs)
for this sequence and the equivalent ProDOS metadata step by step.

## Capabilities and boundaries

| Area | Supported in this preview |
| --- | --- |
| Disk images | Raw `.do`, `.po`, `.dsk`, single-volume `.hdv`, and sector-data `.2mg`/`.2img` |
| Filesystems | Standard 140 KiB DOS 3.3; ProDOS volumes of 280–65,535 blocks |
| Disk operations | Inspect, verify, diff, declarative plan/apply, extract/restore, export/import, create, add/replace/delete, copy/move/rename, directories, attributes, and container/order conversion |
| Text | Explicit Apple text ↔ UTF-8 conversion for printable ASCII, tabs, and line endings |
| Assembly | Documented NMOS 6502, Apple-compatible 65C02, and WDC65C02 instructions; labels, expressions, and data directives |
| Applesoft | Full token vocabulary, numbered source, linked program validation, checks, renumbering, symbolic labels, and readable listings |
| Automation | Typed versioned JSON, JSON Schemas, stable diagnostics, capability discovery, cancellation, and a local MCP stdio interface |
| Projects | Reproducible builds, opt-in content-addressed caching, non-writing resolution, image-to-project import, source maps/symbols, target profiles, and original bare-metal DOS boot sectors |
| Execution | Deterministic in-process 6502/65C02 routines plus optional MAME 0.289, selective/parallel suites, graphics-memory assertions, isolated mounts, debugging, text, and screenshots |
| C and ca65 | Optional isolated cc65 integration producing AppleSingle programs |
| Graphics | PNG screens, sprites, fonts, tiles, Applesoft shapes, and double-hires assets |

Machine-code decompilation produces assembly, without recovering original
symbols, comments, or code/data boundaries. BASIC compilation produces
interpreted Applesoft tokens. NIB/WOZ, DOS 3.2, partitions, forked-file
operations, Integer BASIC conversion, and repair are outside the supported
scope. See the [format guide](docs/formats/supported-images.md) before writing an
unfamiliar or ambiguous image.

## Optional integrations

Use `a2 build PROJECT --preflight --json` for a complete disposable build with a
change/capacity plan. Projects with `execution.suite` can run
`a2 build PROJECT --test --artifacts NEW_DIRECTORY --json` to bind tests and
symbolic addresses to the exact built image. See the
[project test example](examples/development/project-tests/README.md).

Disk operations, the native assembler, Applesoft tools, project builder, and
graphics conversion are built in. `a2 cc` and project manifests containing cc65
sources additionally require a separate cc65 installation containing `cl65`.
The `run` and `test` commands default to MAME 0.289, matching ROMs, and supplied
media; none of those emulator files are bundled. A routine specification with
`engine: "cpu"` needs none of them. On Linux and macOS, host program
input and directory import also expect the standard `/usr/bin/stat` utility.

Coding agents can start `a2 mcp serve` as a local stdio MCP server. It exposes
`a2_cli`, `a2_capabilities`, and `a2_schema`; relative paths use the server
process working directory. Keep standard input/output attached to the MCP client.
One tool response is limited to 16 MiB. See the [command reference](docs/cli-reference.md#mcp-server)
and use `a2 capabilities --json` before generating invocations.

## Documentation

| Guide | What you will find |
| --- | --- |
| [Getting started](docs/getting-started.md) | Downloads, requirements, installation, shell setup, and a complete first project |
| [Command reference](docs/cli-reference.md) | Every command, argument, option, alias, and default |
| [Working with disk images](docs/disk-images.md) | Catalogs, file transfers, metadata, manifests, conversion, and backups |
| [Assembly and Applesoft BASIC](docs/programs.md) | Compiler syntax, CPU modes, source examples, program headers, and disk integration |
| [Project builds](docs/projects.md) | Reproducible manifests, inferred load metadata, templates, target profiles, and memory checks |
| [BASIC development](docs/basic-development.md) | Source checking, renumbering, symbolic labels, and source mappings |
| [Automated execution](docs/execution.md) | CPU and MAME run/test specifications, assertions, timeouts, and execution evidence |
| [Runtime debugging](docs/runtime-debugging.md) | Breakpoints, watchpoints, bounded steps, and instruction history |
| [IIe memory](docs/iie-memory.md) and [observations](docs/iie-execution.md) | Banked projects, explicit loaders, physical memory, 80-column text, and MouseText |
| [C and ca65](docs/cc65.md) | Optional isolated cc65 compilation and AppleSingle programs |
| [Graphics](docs/graphics.md) | PNG screen conversion and sprite, font, tile, shape, and double-hires assets |
| [Scripting and JSON](docs/scripting.md) | Output contracts, result fields, exit codes, and PowerShell/Bash examples |
| [Troubleshooting](docs/troubleshooting.md) | Common diagnostics, likely causes, and corrective commands |
| [Supported formats](docs/formats/supported-images.md) | Containers, sector order, filesystem capabilities, geometry, and preservation limits |
| [Development and library use](docs/development.md) | Architecture, C# APIs, tests, dependency policy, and packaging |
| [Core API reference](docs/core-api.md) | Every public Core namespace, type, method, option/result record, and integration contract |
| [Validation record](docs/VALIDATION.md) | Completed checks and remaining release gates |

## Build from source

From a repository checkout, use the .NET SDK selected by
[global.json](global.json) (10.0.400 with patch roll-forward):

```sh
dotnet restore A2Utils.slnx --locked-mode
dotnet build A2Utils.slnx -c Release --no-restore
dotnet run --project src/A2Utils.Cli -c Release --no-build -- disk --help
dotnet run --project src/A2Utils.Cli -c Release --no-build -- disk ls tests/TestData/independent-dos33.do
```

The bundled fixture contains `HELLO.BIN` and `README`, with independently
specified bytes and metadata. It contains no Apple boot code. Its provenance
and hashes are in [tests/TestData/README.md](tests/TestData/README.md). To package
or install the command from source, follow the
[development setup](docs/getting-started.md#install-the-local-command).

Contributors should read [AGENTS.md](AGENTS.md). [PLAN.md](PLAN.md) records the
original proposal, implemented milestones, and future work.

## Preview status and licensing

The [validation record](docs/VALIDATION.md) lists tested formats, packages, and
external tools along with the remaining limits. The standard local suites and
remaining external-machine checks are recorded there; real MAME and OS
interoperability checks require local emulator resources.

Original A2Utils code is licensed under the GNU General Public License, version
2 only (`GPL-2.0-only`); see [LICENSE](LICENSE). Third-party code and dependencies
retain their own licenses, which are included in each archive and summarized in
[THIRD_PARTY.md](THIRD_PARTY.md).

CiderPress2 and the Model Context Protocol SDK are licensed under Apache-2.0,
which the [Apache Software Foundation](https://www.apache.org/licenses/GPL-compatibility)
and [GNU Project](https://www.gnu.org/licenses/license-compatibility.en.html)
identify as incompatible with GPL-2.0-only for a publicly distributed combined
program. The repository and development releases remain private while those
dependencies are replaced, separately licensed, or the project license is
revisited before public binary distribution.
