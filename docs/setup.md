# External dependencies and local environments

[Documentation home](../README.md#documentation) · [Getting started](getting-started.md) · [Troubleshooting](troubleshooting.md)

Install only the dependencies needed for your workflow. A2Utils never downloads
an emulator, compiler, Apple ROM, or operating-system disk for you.

| Workflow | Required external components |
| --- | --- |
| Disk operations, native assembly, Applesoft, graphics, and native project builds | None beyond the A2Utils host requirements below |
| In-process routine tests (`engine: "cpu"`) | No emulator, ROM, or OS image |
| C or ca65 compilation (`cc compile`, project `kind: "cc65"`) | Complete cc65 distribution, including `cl65` and target libraries |
| Full-machine `run`, `test`, or `build --test` using `engine: "mame"` | MAME **0.289**, matching machine/device ROMs, and media appropriate to the program |
| BASIC, assembly, or C OS-based starter | The above MAME components plus a bootable DOS 3.3 or ProDOS template; C also needs cc65 |
| Bare-metal assembly starter | MAME and ROMs to execute; no OS template or cc65 |
| Build A2Utils or run its normal test suite | Pinned .NET SDK; external-tool contract tests use test doubles |

Follow [host setup](#host-tools), then [MAME](#install-mame-0289) and
[ROM setup](#configure-and-audit-roms) for full-machine execution. Add an
[OS template](#prepare-an-os-template) for OS-based programs and
[cc65](#install-cc65) for C/ca65. Finally [configure a profile](#environment-profile),
[run a starter](#create-and-test-a-starter), and optionally
[lock the inputs](#lock-your-environment).

## Host tools

Self-contained release archives include .NET; extract the entire archive as
described in [getting started](getting-started.md#download-a-tagged-release).
For source builds, install the SDK selected by [global.json](../global.json):
**10.0.400**, or a later patch in the **10.0.4xx** feature band. A runtime-only
installation cannot build the solution. Follow Microsoft's
[Windows, Linux, or macOS installation instructions](https://learn.microsoft.com/en-us/dotnet/core/install/)
and select the required SDK version and host architecture. Check from the checkout:

```sh
dotnet --list-sdks
dotnet --version
dotnet restore A2Utils.slnx --locked-mode
dotnet build A2Utils.slnx -c Release --no-restore --warnaserror
```

Restore obtains the pinned NuGet packages using [NuGet.Config](../NuGet.Config).
DiskArc/CommonUtil sources are already vendored; no separate CiderPress install
is needed. Network/proxy failures and unavailable vulnerability metadata are
different from lockfile mismatches; investigate the actual restore diagnostic
before changing dependencies.

Install [PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell)
for `eng/*.ps1`, the DOS fixture generator, and the boot-smoke helper. Check
`pwsh --version`; Windows PowerShell 5.1 (`powershell.exe`) is not a substitute.
Python 3 is needed only to regenerate the independent ProDOS fixture with
`tests/TestData/Generate-ProdosFixtures.py`, not to build or run normal tests.
On Linux/macOS, `/usr/bin/stat` must exist for host-file safety checks. Minimal
Linux installations also need the native runtime dependencies listed in the
.NET installation instructions, even when using a self-contained archive.

Examples below assume `a2` is installed or defined as in
[the local-command instructions](getting-started.md#install-the-local-command).
From a built checkout, replace `a2` with
`dotnet run --project src/A2Utils.Cli -c Release --no-build --` if preferred.
Use a physical, writable directory for your local tools and assets. Within this
checkout, `artifacts/` is ignored; keep downloaded ROMs, OS images, local profiles,
and generated output there or outside the repository.

## Install MAME 0.289

The execution adapter requires **0.289**, rather than whichever MAME version a
package manager currently installs. Obtain that version from the
[official MAME 0.289 release](https://github.com/mamedev/mame/releases/tag/mame0289).
On Windows, extract the binary distribution into a dedicated folder, for example
`C:/tools/mame0289`, and retain its supporting files. On Linux or macOS, use a
build for your OS/architecture reporting 0.289, or build the `mame0289` source
release using MAME's [platform-specific build instructions](https://docs.mamedev.org/initialsetup/compilingmame.html).
Those instructions cover MAME's own compiler, SDL, and other native dependencies.

Probe the executable directly. PowerShell:

```powershell
& 'C:/tools/mame0289/mame.exe' -version
```

Bash/zsh (replace the path with your installation):

```sh
"$HOME/tools/mame0289/mame" -version
```

The output must identify 0.289. Use the actual executable path in A2Utils;
`mamePath` and `emulatorPath` do not search `PATH`. Keep `expectedVersion` at
`"0.289"` in execution specifications: changing it does not port the Lua adapter
to another emulator version. A successful version probe alone does not test ROMs
or boot a disk.

## Configure and audit ROMs

ROMs are separate from both MAME and DOS/ProDOS disk images. Supply ROM sets for
your selected machine and its configured devices. Start with `apple2ee`
(enhanced IIe), the machine used in the recorded Windows smoke checks. See MAME's
[ROM-set guide](https://docs.mamedev.org/usingmame/aboutromsets.html) for set layout
and parent/device dependencies. Keep the set archives or their correctly named
directories beneath one dedicated Apple II ROM directory.

MAME can list required filenames/checksums and audit what you installed. These
PowerShell commands use the same disabled slot 2/4 devices as the A2Utils adapter:

```powershell
& 'C:/tools/mame0289/mame.exe' apple2ee -noreadconfig -sl2 '' -sl4 '' -listroms
& 'C:/tools/mame0289/mame.exe' apple2ee -noreadconfig -sl2 '' -sl4 '' -rompath 'C:/a2-local/roms' -verifyroms
```

Use PowerShell 7.3 or later with its default
[native argument passing](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_parsing#passing-arguments-that-contain-quote-characters)
for these direct invocations so empty arguments are preserved, or run the
Bash/zsh equivalent:

```sh
"$HOME/tools/mame0289/mame" apple2ee -noreadconfig -sl2 '' -sl4 '' -listroms
"$HOME/tools/mame0289/mame" apple2ee -noreadconfig -sl2 '' -sl4 '' -rompath "$HOME/a2-local/roms" -verifyroms
```

These options are documented in the
[MAME command reference](https://docs.mamedev.org/commandline/commandline-all.html).
Replace `apple2ee` consistently when selecting another supported machine; omit
`-sl2` and `-sl4` for `apple2c`. Resolve missing or incorrect ROM reports before
testing a program. The [recorded ROM filenames, hashes, and provenance](execution.md#reproduce-the-self-booting-program-smoke)
describe the previously verified enhanced-IIe configuration. Other profiles or
optional cards can need additional ROMs; their requirements come from the selected
MAME configuration. No ROM assets are included in this repository.

## Prepare an OS template

Skip this section for in-process CPU tests and bare-metal boot programs. OS-based
starters need a supported bootable DOS 3.3 or ProDOS floppy that reaches an
Applesoft prompt. ProDOS must include `BASIC.SYSTEM`; a disk that stops at a boot
menu needs preparation before the timed `RUN`/`BRUN` input will work. A2Utils
`disk create` and the synthetic fixtures produce data volumes without an OS.

Use a copy of your OS media. One documented route is the
[ProDOS 2.4.3 distribution](https://prodos8.com/releases/prodos-243/).
Create `artifacts/os-validation/`, download the `.po` distribution there as
`ProDOS_2_4_3.po`, then follow the
[verified template preparation](execution.md#build-and-boot-a-prodos-application).
For that particular distribution, removing `QUIT.SYSTEM` into a new output
allows `BASIC.SYSTEM` to start:

```sh
a2 disk info artifacts/os-validation/ProDOS_2_4_3.po
a2 disk ls artifacts/os-validation/ProDOS_2_4_3.po
a2 disk delete artifacts/os-validation/ProDOS_2_4_3.po QUIT.SYSTEM --output artifacts/os-validation/basic-template.po
a2 disk verify artifacts/os-validation/basic-template.po
```

The parent directory must exist and `basic-template.po` must be a new destination.
This preserves the downloaded image. For another distribution, inspect its boot
sequence rather than assuming that deleting the same filename is appropriate.

Set `templateImage` to the prepared image and `templateFileSystem` to `prodos`
or `dos33`. Leave enough free space for your program and ensure `AI.MAIN` is
absent for generated starters. `bootSeconds` is the delay before starter input;
adjust it if your template takes longer than the default ten emulated seconds.
Only a successful execution test proves the intended boot and launch behavior.

## Install cc65

Use the [upstream installation guide](https://cc65.github.io/getting-started.html).
On Windows, unpack the Windows snapshot linked there into a directory without
spaces, such as `C:/tools/cc65`. Keep `bin`, `include`, `asminc`, `lib`, and `cfg`
together. Verify the driver and its companion tools:

```powershell
& 'C:/tools/cc65/bin/cl65.exe' --version
& 'C:/tools/cc65/bin/cc65.exe' --version
& 'C:/tools/cc65/bin/ca65.exe' --version
& 'C:/tools/cc65/bin/ld65.exe' --version
```

On Linux/macOS, a cc65 source build offers a single directory suitable for
A2Utils fingerprinting. With Git, GNU Make, and a host C compiler installed
(on macOS, install Apple's command-line developer tools), run in your tools
directory:

```sh
git clone https://github.com/cc65/cc65.git
cd cc65
make
./bin/cl65 --version
git rev-parse HEAD
```

Record the commit and version, or check out your chosen revision before `make`.
The source tree contains the tools and target data after building. A packaged
installation is also usable, but inspect its layout: some packages put binaries
and target data under different prefixes or expose executables through symlinks.
Use physical paths; for a complete lock, a dedicated distribution containing all
five trees is easiest to audit. See [compiler fingerprints](cc65.md) for bounds.

Set profile `cc65Path` to `bin/cl65.exe` on Windows or `bin/cl65` on Unix, and
`cc65Root` to the distribution root. Project manifests use `cc65.compiler` and
`cc65.toolchainRoot` for the same settings. The `apple2` and `apple2enh` target
libraries and configurations must be present; copying only `cl65` is insufficient.
A2Utils has no mandatory cc65 version pin. To enforce one, set
`expectedCc65Version` in a profile or `cc65.expectedVersion` in a project to the
exact trimmed, combined stdout/stderr from `cl65 --version`.

For standalone `a2 cc compile`, either put the distribution's `bin` on your
session's `PATH` or pass `--compiler` explicitly. To use the example installations
for the current shell session:

```powershell
$env:PATH = 'C:/tools/cc65/bin;' + $env:PATH
cl65 --version
```

```sh
export PATH="$HOME/tools/cc65/bin:$PATH"
cl65 --version
```

Adjust the Unix path to the source directory you built. These changes last for
this shell session; explicit executable paths avoid needing persistent `PATH`
changes. The standalone command has no
`--environment` or `--toolchain-root` option; it uses the inherited compiler
environment. Remove stale cc65 search-path overrides from previous installations
if they select the wrong headers/libraries. Project compilation with
`toolchainRoot` sets `CC65_HOME`, prepends `bin`, and clears the inherited search
overrides listed in [the compiler guide](cc65.md).

To verify compilation without MAME, save this as `main.c` in a new, dedicated
source directory:

```c
#include <stdio.h>
int main(void) { puts("HELLO FROM CC65"); return 0; }
```

From that directory, with `cl65` available on `PATH`, run:

```sh
a2 cc compile main.c --target apple2 --to hello.as --json
```

Alternatively add `--compiler /absolute/path/to/cl65` (including `.exe` on
Windows). A successful `cc.compile` result confirms compilation and AppleSingle
validation; it does not execute the program. Keep `hello.as` new, or use
`--overwrite` intentionally. For a full compile/build/boot check, use the C
starter below.

## Environment profile

An environment profile centralizes local tool, ROM, OS-template, and optional
cc65 distribution paths. It never downloads or installs external assets.

```json
{
  "schemaVersion": 1,
  "machine": "apple2ee",
  "mamePath": "tools/mame",
  "romDirectory": "roms",
  "templateImage": "templates/prodos.po",
  "templateFileSystem": "prodos",
  "bootSeconds": 10,
  "cc65Path": "tools/cc65/bin/cl65",
  "cc65Root": "tools/cc65"
}
```

Paths resolve relative to the profile. Omit cc65 settings when unused. The
optional `expectedCc65Version` pins the exact `cl65 --version` output. MAME is
pinned to the execution adapter's version, currently 0.289. `bootSeconds`
(1–120) configures starter input timing for your template.

Save this example as your own `environment.json` and replace the illustrative
paths. Windows absolute JSON paths can use forward slashes, for example
`"C:/tools/mame0289/mame.exe"` and `"C:/tools/cc65/bin/cl65.exe"`.
On Unix, use literal absolute paths such as `/home/alice/tools/cc65/bin/cl65`.
JSON paths do not expand `~`, `$HOME`, or `%USERPROFILE%`. Profile `cc65Path`,
unlike the standalone compiler command, is a file path and does not search `PATH`.
Omit `templateImage` for a bare-metal-only profile.

```sh
a2 env check environment.json --json
```

`env check` reports separate machine-version, ROM-verification, template
structure, compiler-version, and compiler-distribution results. ROM checking
uses MAME's `-verifyroms` for the selected machine. Structural disk checks do
not prove bootability: run the generated execution test with your own bootable
template. ProDOS starters require BASIC.SYSTEM and a template that reaches an
Applesoft prompt; DOS 3.3 templates must likewise reach the prompt.

Success returns exit code 0 and JSON `data.ready: true`; failed readiness checks
return 1 with individual entries under `data.checks`. Malformed profiles instead
produce an error. Checks always include MAME and the ROM directory; a compiler-only
workflow can use `cc compile` without an environment profile. With `cc65Path`
configured, readiness also requires `cc65Root` and its target-data directories.
It probes the compiler version but does not compile a test program.

Set `environment` in a project or execution JSON file to reuse the profile.
Execution files inherit omitted emulator, ROM-directory, and machine settings.
Projects inherit an omitted disk template and compiler configuration; an
explicit compiler configuration keeps its compiler selection and can inherit
an omitted distribution root. A project's target and filesystem remain its
own explicit settings. Explicit paths override defaults in unlocked projects.

## Lock your environment

After successful checks and a starter run, capture the reviewed inputs:

```sh
a2 env lock environment.json --output environment.lock.json --json
```

`env lock` fingerprints files; it does not run `env check` or establish bootability.
Creating a lock also does not attach it to existing projects. Set `toolchainLock`
alongside `environment` in both the project and its execution specification to
enforce it, for example this fragment when the profile/lock are one directory up:

```json
{
  "environment": "../environment.json",
  "toolchainLock": "../environment.lock.json"
}
```

Both paths resolve relative to their owning project or execution file.
Locked runs must use the profile's emulator and ROM directory; locked projects
must use its template and compiler distribution. Create a separate profile and
lock to use different paths. The lock hashes the profile, emulator executable,
every file in the ROM directory, the template, and the configured cc65
executable plus its `bin`, `include`, `asminc`, `lib`, and `cfg` trees. Directory
membership is checked too, so added libraries or ROM files invalidate a lock.

Locks use explicit physical paths and reject symbolic links and special files.
Resolve package-manager links to their installed directories before locking.
Limits are 16,384 files/directories, 2 GiB per file, and 8 GiB total; use a
dedicated Apple II ROM directory instead of a complete arcade ROM collection.
Keep the lock outside the directories it fingerprints. Existing lockfiles are
preserved; create a new lock filename after reviewing intentional changes.
Environment locks are checked before dependent work and again before build
commit or emulator launch. They pin local inputs, not external assets bundled
with the project or guaranteed cross-host emulator behavior.
If `cc65Path` is configured, locking requires `cc65Root` too. Moving the profile,
upgrading a tool, changing ROMs or a template, or editing the profile requires
reviewing the new inputs and creating a new lock, then updating its references.

Execution artifacts retain the exact profile and lockfile bytes as
`environment.json` and `toolchain-lock.json`, together with original paths and
SHA-256 values in the result's environment evidence. Both files are checked
again against that captured baseline before machine launch and after execution,
so replacing a profile and its lock together during the version probe cannot
change the run.
Copied profiles are evidence: their relative paths still refer to the original
profile directory and may need adjustment before using the copies elsewhere.

## Create and test a starter

With a configured profile and OS template, choose one language and a new project
directory (`asm` is the default):

```sh
a2 init my-program --language asm --environment environment.json --json
a2 build my-program/project.a2.json --check --json
a2 build my-program/project.a2.json --test --artifacts my-program/runs/first --json
```

Substitute `basic` or `c` in the `init` command for those starters. `init` does
not install tools or run a boot test. `build --check` compiles and checks memory
without creating the disk; C still invokes cc65. `build --test` creates the disk
and executes the generated suite using that exact build.

`init` supports `--language basic`, `asm`, and `c`, and requires a new directory.
It creates source, a project, a suite, an execution specification, and a README
in a staging directory before moving the completed project into place. The
program writes the exact mailbox bytes `2A A5 5A` at `$0300–$0302`; its test
asserts all three bytes. `AI.MAIN` must be absent from the template. The template
is copied by the build and never edited in place.

`a2 init DIRECTORY --language asm --bare-metal` creates an original DOS-order
140 KiB boot-sector project instead. It uses no operating-system template;
`--bare-metal` accepts only the assembly starter. Its generated full-machine test
still needs a separately configured MAME executable and ROM directory.

For example, using a profile with MAME and ROMs but no `templateImage`:

```sh
a2 init boot-demo --language asm --bare-metal --environment environment.json --json
a2 build boot-demo/project.a2.json --test --artifacts boot-demo/runs/first --json
```

Starter machines are `apple2p`, `apple2e`, `apple2ee`, and `apple2c`; the original
`apple2` is supported by execution but has no starter target profile.

Without `--environment`, initialization creates an editable `environment.json`.
Configure its local paths before building or testing; generated configuration
is not evidence of a successful emulator run.

Use a fresh artifact directory each run. To rebuild an existing output, add
`--overwrite` and choose another artifact directory, such as `runs/second`.
The C starter also requires a working
cc65 distribution. The starter README explains how to add the environment
lock to both its project and execution specification.

## Enable real-tool repository tests

Environment profiles configure the CLI. The optional xUnit machine tests instead
read these process environment variables; setting them does not configure `a2 run`:

| Variable | Meaning |
| --- | --- |
| `A2_MAME_PATH` | Absolute path to the MAME 0.289 executable |
| `A2_MAME_ROMS` | Absolute path to the enhanced-IIe ROM directory |
| `A2_DOS33_SMOKE_DISK` | Optional bootable 140 KiB DOS 3.3 OS-test template |
| `A2_PRODOS_SMOKE_DISK` | Optional bootable 140 KiB ProDOS OS-test template |

PowerShell, from the repository root after restoring/building:

```powershell
$env:A2_MAME_PATH = 'C:/tools/mame0289/mame.exe'
$env:A2_MAME_ROMS = 'C:/a2-local/roms'
dotnet test tests/A2Utils.Core.Tests -c Release --no-restore --filter FullyQualifiedName~MameSmokeTests
```

Bash/zsh:

```sh
export A2_MAME_PATH="$HOME/tools/mame0289/mame"
export A2_MAME_ROMS="$HOME/a2-local/roms"
dotnet test tests/A2Utils.Core.Tests -c Release --no-restore --filter FullyQualifiedName~MameSmokeTests
```

That filter selects the original self-booting smoke. Run
`dotnet test A2Utils.slnx -c Release --no-restore` to include the other optional
machine tests. OS tests additionally require the corresponding disk variable and
the [template catalog/space conditions](../tests/TestData/README.md#optional-real-dos-and-prodos-interoperability).
Missing configuration skips the affected tests. Most cc65 and execution tests use
process-contract doubles; passing them does not validate an installed compiler or
emulator. Use the compile and starter checks above for your installation, and
consult [the validation record](VALIDATION.md) for checks actually performed.
