# Getting started

[Documentation home](../README.md#documentation) · [Command reference](cli-reference.md)

This walkthrough starts from a local checkout and creates an Apple II data
disk containing a machine-code program and an Applesoft BASIC program. All
commands run on the host computer, not at an Apple II prompt.

## Requirements

| How you run A2Utils | What you need |
| --- | --- |
| Build or run source | .NET SDK selected by `global.json`: 10.0.400 with `latestPatch` roll-forward |
| Locally packaged .NET tool | .NET 10 runtime to run; the SDK to build, install, or update the tool |
| Self-contained download | The complete package for the host OS/architecture; no separately installed .NET runtime |
| Run `eng/Package.ps1` | PowerShell 7 (`pwsh`), the SDK, and access to dependencies/runtime packs |
| Unix directory import or host program input | `/usr/bin/stat` for regular-file checks |

Check your SDK from the repository root:

```sh
dotnet --version
dotnet --list-sdks
```

The pin does not automatically accept every .NET 10 feature band. See
[SDK troubleshooting](troubleshooting.md#setup-and-installation) if a compatible
SDK cannot be found. Initial restore uses the package source in
[NuGet.Config](../NuGet.Config); later operations can use cached dependencies.
Windows x64 has been tested locally. The `v0.4.0-dev` hosted attempt completed
Linux packaging and smoke tests but exposed cc65 temporary-workspace defects on
Windows and macOS. Those defects are fixed with local regression coverage; release
policy requires a new version tag for hosted confirmation. See
[the validation record](VALIDATION.md).

## Download a tagged release

When available, binaries are attached to
[GitHub Releases](https://github.com/AmosAnderson/a2utils/releases), accessible
to users with access to this private repository. Choose `win-x64` for Windows
x64, `linux-x64` for Linux x64, or `osx-arm64` for Apple Silicon macOS. Extract
the whole archive and keep its accompanying files and notices. Run `a2.exe`
on Windows or `./a2` on Unix; these archives include the .NET runtime.

Each release includes `SHA256SUMS.txt`. Compare the downloaded archive's hash
using `Get-FileHash -Algorithm SHA256` in PowerShell, `sha256sum` on Linux, or
`shasum -a 256` on macOS. The portable `A2Utils.Tool.<version>.nupkg` can also be
installed from a local download folder using `dotnet tool install A2Utils.Tool
--version <version> --add-source <folder> --tool-path <tools-folder>`.
It requires .NET 10. See [release automation](development.md#tagged-releases)
for how tags create these downloads.

## Build and inspect the sample image

```sh
dotnet restore A2Utils.slnx --locked-mode
dotnet build A2Utils.slnx -c Release --no-restore
dotnet run --project src/A2Utils.Cli -c Release --no-build -- disk ls tests/TestData/independent-dos33.do
```

The catalog contains a locked binary named `HELLO.BIN` and a text file named
`README`. Listing is read-only. The fixture is synthetic and nonbootable;
its expected contents and hashes are documented in
[the fixture guide](../tests/TestData/README.md).

The separator `--` sends the remaining arguments to A2Utils instead of to
`dotnet run`. The direct-source invocation is useful even without installing
the `a2` command.

## Install the local command

Build and install the preview package into this checkout:

```sh
dotnet pack src/A2Utils.Cli -c Release --no-restore -o artifacts/packages
dotnet tool install A2Utils.Tool --version 0.5.0-dev --add-source artifacts/packages --tool-path artifacts/tools --configfile NuGet.Config
```

For an already installed preview, substitute `update` for `install`:

```sh
dotnet tool update A2Utils.Tool --version 0.5.0-dev --add-source artifacts/packages --tool-path artifacts/tools --configfile NuGet.Config --no-cache
```

The package ID is `A2Utils.Tool`; its executable is `a2`. Installation is local
to `artifacts/tools`. Invoke it directly or define a session function for the
shorter commands used in these guides.

PowerShell on Windows, from the repository root:

```powershell
$a2Executable = Join-Path (Get-Location) 'artifacts/tools/a2.exe'
function a2 { & $a2Executable @args }
a2 --version
a2 --help
```

Bash or zsh, from the repository root:

```sh
a2_executable="$PWD/artifacts/tools/a2"
a2() { "$a2_executable" "$@"; }
a2 --version
a2 --help
```

The functions retain an absolute executable path even if you later change
directories. Commands in the guides still assume the repository root unless
they say otherwise.

### Self-contained package

To build a package for the current host:

```sh
pwsh -File eng/Package.ps1
```

This runs tests, creates the tool package, and publishes a self-contained
executable. Windows x64 output is `artifacts/a2utils-win-x64.zip`, with an
unpacked executable at `artifacts/publish/win-x64/a2.exe`. Extract the complete
archive to retain its runtime files and notices. Other runtime names and
packaging options are in [the development guide](development.md#packaging-and-local-installation).

## Build a disk with two programs

The following steps assume `a2` is available using the setup above. Create a
scratch output directory first. Use a new directory name if `artifacts/tutorial`
already contains outputs from an earlier run.

PowerShell:

```powershell
New-Item -ItemType Directory -Path artifacts/tutorial | Out-Null
```

Bash/zsh:

```sh
mkdir -p artifacts/tutorial
```

### 1. Compile the sources

```sh
a2 asm compile examples/hello.asm --to artifacts/tutorial/hello.bin
a2 basic compile examples/hello.bas --to artifacts/tutorial/hello.basbin
```

[hello.asm](../examples/hello.asm) uses `.org $2000` and the monitor output
routine to print a message. [hello.bas](../examples/hello.bas) is numbered
Applesoft source. Both commands write **raw payloads**, suitable for disk
import. BASIC uses its default memory origin `$0801`.

Do not add `--format dos` here: `disk add` creates the on-disk headers itself.
The [program guide](programs.md) explains when DOS host-file headers are useful.

### 2. Create and populate a DOS 3.3 disk

```sh
a2 disk create artifacts/tutorial/work.do --fs dos33 --volume-number 42
a2 disk add artifacts/tutorial/work.do artifacts/tutorial/hello.bin --name HELLO --type B --load-address 0x2000 --in-place
a2 disk add artifacts/tutorial/work.do artifacts/tutorial/hello.basbin --name DEMO --type A --in-place
a2 disk ls artifacts/tutorial/work.do
a2 disk verify artifacts/tutorial/work.do
```

`--type B` identifies machine code and requires a DOS load address. `--type A`
identifies Applesoft. Each successful in-place addition creates a uniquely
named `.bak` file containing the preceding disk image. Verification checks
disk structures; it does not execute either program.

New images contain a formatted filesystem without boot code. To run these
programs on an Apple II or emulator, use an existing compatible DOS/ProDOS
environment. The MAME adapter has passed real enhanced-IIe checks with an
original self-booting sector and a supplied ProDOS template, but this newly
formatted DOS data disk is not bootable by itself. See [automated execution](execution.md)
and the [validation record](VALIDATION.md) for the exact tested configuration.

### 3. Recover editable source

```sh
a2 asm decompile HELLO --from-image artifacts/tutorial/work.do --to artifacts/tutorial/recovered.asm
a2 basic decompile DEMO --from-image artifacts/tutorial/work.do --to artifacts/tutorial/recovered.bas
a2 asm compile artifacts/tutorial/recovered.asm --to artifacts/tutorial/rebuilt.bin
a2 basic compile artifacts/tutorial/recovered.bas --to artifacts/tutorial/rebuilt.basbin
```

The disk supplies the binary's load address. Disassembly uses generated numeric
operands and emits `.org`; original labels and comments are not recoverable.
The BASIC listing retains statements with normalized formatting. Programs at
a nondefault BASIC origin need the same `--origin` when rebuilding the listing.

Compare bytes using PowerShell:

```powershell
$originalHash = (Get-FileHash artifacts/tutorial/hello.bin -Algorithm SHA256).Hash
$rebuiltHash = (Get-FileHash artifacts/tutorial/rebuilt.bin -Algorithm SHA256).Hash
if ($originalHash -ne $rebuiltHash) { throw 'Machine-code round trip differs' }
$originalHash = (Get-FileHash artifacts/tutorial/hello.basbin -Algorithm SHA256).Hash
$rebuiltHash = (Get-FileHash artifacts/tutorial/rebuilt.basbin -Algorithm SHA256).Hash
if ($originalHash -ne $rebuiltHash) { throw 'BASIC round trip differs' }
```

Or Bash/zsh:

```sh
cmp artifacts/tutorial/hello.bin artifacts/tutorial/rebuilt.bin
cmp artifacts/tutorial/hello.basbin artifacts/tutorial/rebuilt.basbin
```

### 4. Use ProDOS instead

The same payload files can be imported into a new ProDOS disk with explicit
types and auxiliary addresses:

```sh
a2 disk create artifacts/tutorial/work.po --fs prodos --size 800k --volume-name WORK
a2 disk mkdir artifacts/tutorial/work.po PROGRAMS --in-place
a2 disk add artifacts/tutorial/work.po artifacts/tutorial/hello.bin --name PROGRAMS/HELLO --type BIN --aux-type 0x2000 --in-place
a2 disk add artifacts/tutorial/work.po artifacts/tutorial/hello.basbin --name PROGRAMS/DEMO --type BAS --aux-type 0x0801 --in-place
a2 disk ls artifacts/tutorial/work.po --recursive
a2 disk verify artifacts/tutorial/work.po
```

Use `/` between ProDOS path components. Existing image entries are not
overwritten by `add`; use `replace` for intentional payload updates. Copies
between images require matching filesystems, so this workflow imports the
raw payloads explicitly rather than using `disk copy` across filesystems.

## Next steps

- [Disk workflows](disk-images.md): export text, preserve original stored bytes,
  restore manifests, import directories, and convert image layout.
- [Program tools](programs.md): assembly syntax, CPU choices, BASIC rules, and
  nondefault origins.
- [Command reference](cli-reference.md): exact options and defaults.
- [Scripting](scripting.md): process JSON and check native exit codes.
- [Troubleshooting](troubleshooting.md): resolve a refused operation or format error.
