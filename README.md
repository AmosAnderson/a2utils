# A2Utils

A2Utils is a C#/.NET 10 command-line toolkit for Apple II and Apple IIe disk
images and programs. Use it to inspect and edit DOS 3.3/ProDOS disks, assemble
6502/65C02 source, and convert Applesoft BASIC between listings and tokenized
programs. The reusable core library is independent of console output.

The current version is **0.3.0-dev**, a development preview. The
[GitHub repository](https://github.com/AmosAnderson/a2utils) is private.

## Documentation

| Guide | What you will find |
| --- | --- |
| [Getting started](docs/getting-started.md) | Requirements, local installation, shell setup, and a complete first project |
| [Command reference](docs/cli-reference.md) | Every command, argument, option, alias, and default |
| [Working with disk images](docs/disk-images.md) | Catalogs, file transfers, metadata, manifests, conversion, and backups |
| [Assembly and Applesoft BASIC](docs/programs.md) | Compiler syntax, CPU modes, source examples, program headers, and disk integration |
| [Scripting and JSON](docs/scripting.md) | Output contracts, result fields, exit codes, and PowerShell/Bash examples |
| [Troubleshooting](docs/troubleshooting.md) | Common diagnostics, likely causes, and corrective commands |
| [Supported formats](docs/formats/supported-images.md) | Containers, sector order, filesystem capabilities, geometry, and preservation limits |
| [Development and library use](docs/development.md) | Architecture, C# APIs, tests, dependency policy, and packaging |
| [Validation record](docs/VALIDATION.md) | Completed checks and remaining release gates |

Contributors should read [AGENTS.md](AGENTS.md). [PLAN.md](PLAN.md) records the
original proposal, implemented milestones, and future work.

## Try it from source

From the repository root, use the SDK selected by [global.json](global.json)
(10.0.400 with patch roll-forward):

```sh
dotnet restore A2Utils.slnx --locked-mode
dotnet build A2Utils.slnx -c Release --no-restore
dotnet run --project src/A2Utils.Cli -c Release --no-build -- disk --help
dotnet run --project src/A2Utils.Cli -c Release --no-build -- disk ls tests/TestData/independent-dos33.do
```

The bundled fixture contains `HELLO.BIN` and `README`, with independently
specified bytes and metadata. It contains no Apple boot code. Its provenance
and hashes are in [tests/TestData/README.md](tests/TestData/README.md).

To create the locally installed `a2` command, follow
[installation and shell setup](docs/getting-started.md#install-the-local-command).
The examples below assume that setup and run from the repository root.

## Common tasks

```sh
a2 disk info tests/TestData/independent-dos33.do --json
a2 disk export tests/TestData/independent-dos33.do README --format text --to readme.txt
a2 asm compile examples/hello.asm --to hello.bin
a2 asm decompile hello.bin --origin 0x2000 --to hello.dis.asm
a2 basic compile examples/hello.bas --to hello.basbin
a2 basic decompile hello.basbin --to hello.list.bas
a2 disk create work.do --fs dos33
a2 disk add work.do hello.bin --name HELLO --type B --load-address 0x2000 --in-place
a2 disk add work.do hello.basbin --name DEMO --type A --in-place
a2 basic decompile DEMO --from-image work.do --to demo.bas
```

These commands create new host files; rerunning them requires new output names
or explicit replacement options. The [first-project walkthrough](docs/getting-started.md#build-a-disk-with-two-programs)
explains each step, including equivalent ProDOS metadata.

## Capabilities and boundaries

| Area | Supported in this preview |
| --- | --- |
| Disk images | Raw `.do`, `.po`, `.dsk`, single-volume `.hdv`, and sector-data `.2mg`/`.2img` |
| Filesystems | Standard 140 KiB DOS 3.3; ProDOS volumes of 280–65,535 blocks |
| Disk operations | Inspect, verify, extract/restore, export, create, add/replace/delete, copy/move/rename, directories, attributes, and container/order conversion |
| Text | Explicit Apple text ↔ UTF-8 conversion for printable ASCII, tabs, and line endings |
| Assembly | Documented NMOS 6502, Apple-compatible 65C02, and WDC65C02 instructions; labels, expressions, and data directives |
| Applesoft | Full token vocabulary, numbered source, linked program validation, and readable listings |
| Automation | Versioned JSON, stable diagnostic codes, cancellation, and explicit write destinations |

Machine-code decompilation produces assembly, without recovering original
symbols, comments, or code/data boundaries. BASIC compilation produces
interpreted Applesoft tokens. Neither tool executes the resulting programs.
NIB/WOZ, DOS 3.2, partitions, forked-file operations, Integer BASIC conversion,
and repair are outside the supported scope.

## Data preservation

Image mutations require either `--output NEWIMAGE` or `--in-place`. In-place
writes create unique backups. Outputs are staged and validated before
replacement; `--overwrite` authorizes replacing a host output file, not an
existing image entry or locked file.

Use `disk export` for usable payloads and `disk extract` for stored bytes plus
a restoration manifest. Container conversion retains the filesystem. The
[disk guide](docs/disk-images.md) explains which bytes and metadata each
operation preserves and where physical allocation may change.

## Development status

The Windows validation record reports **458 passing tests**, including known
opcode/token vectors, independent disk fixtures, failed-write protection, and
packaged program round trips. Linux/macOS hosted CI and emulator execution
remain release checks; see [the validation record](docs/VALIDATION.md).

The disk engine is pinned under [third_party/CiderPress2](third_party/CiderPress2),
with source hashes and license notices. [THIRD_PARTY.md](THIRD_PARTY.md) describes
dependencies. A license for original A2Utils code remains to be selected before
public distribution; the development packages are for local evaluation.
