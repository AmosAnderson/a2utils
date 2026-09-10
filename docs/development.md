# Development and library integration

This guide explains how to build A2Utils, find the relevant source, embed its
library, and produce local packages. Start with [getting started](getting-started.md)
for the command-line application. The [CLI reference](cli-reference.md),
[disk image guide](disk-images.md), [program tools guide](programs.md),
[scripting guide](scripting.md), and [troubleshooting guide](troubleshooting.md)
describe user-facing behavior.
The [Core API reference](core-api.md) is the exhaustive public-type and method
index; this guide focuses on architecture, safe integration patterns, and builds.

## Prerequisites and build

Use the SDK selected by [global.json](../global.json): .NET SDK **10.0.400**,
with patch roll-forward enabled. All projects target `net10.0`; this is modern
.NET, not the older Windows-only .NET Framework. PowerShell 7 (`pwsh`) is needed
for the packaging script and fixture generator. Unix directory imports and
host program inputs use `/usr/bin/stat` to reject nonregular files before reading them.

Run these commands from the repository root:

```sh
dotnet --version
dotnet restore A2Utils.slnx --locked-mode
dotnet build A2Utils.slnx -c Release --no-restore --warnaserror
dotnet test A2Utils.slnx -c Release --no-restore
dotnet format A2Utils.slnx --verify-no-changes --no-restore --exclude third_party
```

Restore uses the repository's [NuGet.Config](../NuGet.Config), which explicitly
selects nuget.org. Tracked `packages.lock.json` files pin dependency resolutions.
A locked restore failure means the package graph and lockfiles disagree;
investigate that difference before regenerating locks.

For an edit/build/run cycle, use the default Debug configuration:

```sh
dotnet run --project src/A2Utils.Cli -- disk --help
dotnet run --project src/A2Utils.Cli -- asm compile examples/hello.asm --to artifacts/hello.bin
```

Create `artifacts/` before the second command; host output parents are not
created automatically. In PowerShell, use
`New-Item -ItemType Directory -Path artifacts -Force | Out-Null`; in a POSIX
shell, use `mkdir -p artifacts`. The program output must not already exist
unless the command includes `--overwrite`. For IDE debugging, launch the
`A2Utils.Cli` project with the repository root as its working directory and
arguments such as `disk info tests/TestData/independent-dos33.do`.

## Architecture and source map

The CLI owns argument parsing, command orchestration, JSON, and console output.
Core exposes operations and diagnostics without depending on the CLI. Disk
operations go through `DiskSession`, the adapter over pinned DiskArc/CommonUtil
sources. Assembly and BASIC codecs operate independently of the disk engine.

| Area | Responsibility |
| --- | --- |
| `src/A2Utils.Cli/CliApplication.cs` | Disk commands, parsing, results, diagnostics, and staged mutation orchestration |
| `src/A2Utils.Cli/CliApplication.Transfers.cs` | Export, text conversion options, directory import, copy, and move commands |
| `src/A2Utils.Cli/CliApplication.Programs.cs` | Assembly/BASIC host-file and disk-entry workflows |
| `src/A2Utils.Cli/CliApplication.Development.cs` | Project builds, capabilities, target profiles, and embedded schemas |
| `src/A2Utils.Cli/CliApplication.BasicTools.cs` | BASIC checking, renumbering, and symbolic-source preparation commands |
| `src/A2Utils.Cli/CliApplication.Cc65.cs` | Standalone cc65 command and AppleSingle output transaction |
| `src/A2Utils.Cli/CliApplication.Graphics.cs` | Lo-res and hi-res screen conversion commands |
| `src/A2Utils.Cli/CliApplication.GraphicsAssets.cs` | Atlas, shape-table, and double-hires commands |
| `src/A2Utils.Cli/CliApplication.Execution.cs` | Single-run and execution-suite command orchestration |
| `src/A2Utils.Core/DiskModels.cs` | Public disk records, diagnostics, and `DiskException` |
| `src/A2Utils.Core/Backends/` | Image detection, filesystem access, metadata, and write eligibility |
| `src/A2Utils.Core/Operations/` | Host transactions, manifests, imports/exports, text conversion, and image conversion |
| `src/A2Utils.Core/Assembly/` | Instruction tables, expressions, assembler, and disassembler |
| `src/A2Utils.Core/Basic/` | Applesoft codecs, source checks, renumbering, and symbolic label preparation |
| `src/A2Utils.Core/Programs/` | DOS/AppleSingle program metadata, bounded input reads, and source diagnostics |
| `src/A2Utils.Core/Projects/` | Strict manifests, source-to-disk builds, memory checks, and optional cc65 adapter |
| `src/A2Utils.Core/Execution/` | Pinned MAME adapter, isolated runs, assertions, and artifact capture |
| `src/A2Utils.Core/Graphics/` | PNG/screens, sprites, tiles, bitmap fonts, shape tables, and double-hires codecs |
| `examples/` | Original assembly and Applesoft sample sources |
| `tests/` | Core/CLI tests, an external-process contract test host, and independent disk fixtures |
| `third_party/CiderPress2/` | Unmodified upstream source, format notes, notices, and source hashes |
| `third_party/evaluation/` | Repeatable disk-engine integration probe |
| `eng/Package.ps1` | Local tool package and self-contained archive generation |
| `eng/Get-ReleaseInfo.ps1` | Release-tag syntax, main-history, and version validation |
| `eng/Publish-Release.ps1` | Draft release creation, exact asset upload, verification, and publication |
| `eng/Test-Release.ps1` | Offline release-guard and publish/retry contract tests |

See [the disk-engine decision](decisions/0001-disk-engine.md) for the reuse
evaluation and reasons for keeping all engine access behind the adapter.
Upstream support for an image format does not automatically make that format
supported by A2Utils.

The [development workflow decision](decisions/0003-development-workflow.md)
and [execution adapter decision](decisions/0002-execution.md) describe the
new boundaries. Start with [project builds](projects.md) to combine source
compilation and disk creation, then use [execution tests](execution.md) for
behavioral checks. External cc65 and MAME installations are optional; no
emulator, compiler, Apple ROM, or operating-system disk is bundled.

## Using Core from C#

Core is currently consumed as a project reference; no public Core NuGet package
has been published. To create a disposable example under the ignored artifact
directory, run:

```sh
dotnet new console --name CoreSample --output artifacts/core-sample --framework net10.0
dotnet add artifacts/core-sample/CoreSample.csproj reference src/A2Utils.Core/A2Utils.Core.csproj
```

Replace the generated `Program.cs` with either example below and run
`dotnet run --project artifacts/core-sample` from the repository root.

### Compile and decompile in memory

```csharp
using A2Utils.Core.Assembly;
using A2Utils.Core.Basic;
using A2Utils.Core.Programs;

AssemblyResult program = Assembler.Assemble("""
    .org $2000
    LDA #$C1
    JSR $FDED
    RTS
    """, cpu: CpuKind.Mos6502);

string assembly = Disassembler.Disassemble(
    program.Bytes, program.Origin, CpuKind.Mos6502);
AssemblyResult rebuilt = Assembler.Assemble(assembly, cpu: CpuKind.Mos6502);
if (!program.Bytes.AsSpan().SequenceEqual(rebuilt.Bytes))
{
    throw new InvalidOperationException("Assembly round trip changed the bytes.");
}

byte[] dosBinary = ProgramFileFormat.EncodeDosBinary(program.Bytes, program.Origin);
(ushort loadAddress, byte[] payload) = ProgramFileFormat.DecodeDosBinary(dosBinary);
Console.WriteLine($"DOS binary: {payload.Length} payload bytes at ${loadAddress:X4}");

byte[] basic = ApplesoftBasic.Compile("10 PRINT \"HELLO\"\n20 END\n");
string listing = ApplesoftBasic.Decompile(basic);
if (!basic.AsSpan().SequenceEqual(ApplesoftBasic.Compile(listing)))
{
    throw new InvalidOperationException("BASIC round trip changed the bytes.");
}
Console.Write(listing);
```

`AssemblyResult.Bytes` and `ApplesoftBasic.Compile` return headerless program
payloads. `ProgramFileFormat` adds/removes DOS binary or BASIC host-file headers
when needed. Pass payload bytes directly to `DiskSession.Add`; the filesystem
adapter constructs the on-disk header from the file type and auxiliary value.
Adding an already wrapped DOS host file as a payload would store a second header.

Keep origin and CPU settings consistent across a round trip. The default CPU
is `Mos6502`; alternatives are `Apple65C02` and `Wdc65C02`. Applesoft defaults
to origin `$0801`. Disassembly preserves bytes through a linear sweep, without
recovering original symbols or separating embedded data from instructions.
BASIC decompilation produces a canonical listing and rejects representations
that would change when recompiled. See [program tools](programs.md) for syntax,
input bounds, and compatibility limits.

### Read an image and make a validated copy

This example reads the independent fixture, then adds a one-byte machine-code
program to a new image. It never opens the reference fixture for writing.

```csharp
using A2Utils.Core;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

string input = Path.GetFullPath("tests/TestData/independent-dos33.do");
string outputDirectory = Path.GetFullPath("artifacts/core-sample-output");
Directory.CreateDirectory(outputDirectory);
string output = Path.Combine(outputDirectory, "demo.do");
byte[] payload = [0x60]; // RTS at $2000.

string order;
string fileSystem;
using (DiskSession source = DiskSession.Open(input))
{
    DiskInfo info = source.Info;
    order = info.Order;
    fileSystem = info.FileSystem;
    foreach (DiskEntry entry in source.List())
    {
        Console.WriteLine($"{entry.Path}: {entry.Length} logical bytes");
    }
    byte[] originalProgram = source.ReadFile("HELLO.BIN");
    Console.WriteLine($"Read {originalProgram.Length} payload bytes.");
}

ImageWriteResult result = ImageTransactions.Write(
    inputPath: input,
    outputPath: output,
    inPlace: false,
    overwrite: false,
    editTemporary: temporary =>
    {
        using DiskSession staged = DiskSession.Open(
            temporary, order, fileSystem, writable: true);
        staged.Add("DEMO", payload, type: "BIN", auxType: 0x2000);
        staged.Flush();
    },
    validate: temporary =>
    {
        using DiskSession check = DiskSession.Open(temporary, order, fileSystem);
        if (check.Info.IsDubious || check.Verify().Any(diagnostic =>
            diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DiskException("sample.validation", "Staged image failed validation.", 4);
        }
        DiskEntry added = check.GetEntry("DEMO");
        if (added.FileType != 0x06 || added.AuxType != 0x2000 ||
            !check.ReadFile("DEMO").AsSpan().SequenceEqual(payload))
        {
            throw new DiskException("sample.content", "Staged program did not match.", 4);
        }
    });
Console.WriteLine(result.OutputPath);
```

The output must be absent; choose another filename when rerunning. `ReadFile`
defaults to logical payload bytes. `raw: true` reads the preservation
representation, which retains DOS headers and sector tails. Use
`FileTransfer` for manifest-based extraction/restoration rather than recreating
that format in application code.

## Session and transaction rules

`DiskSession` implements `IDisposable` and owns the image stream and filesystem.
Open it with `using` and finish disposal before validating or committing its
file. A default session is read-only. `Info.IsReadOnly` describes that session;
`Info.ImageWriteProtected` describes the stored container flag. A read-only
session therefore does not prove that the underlying image is write-protected.

DiskArc can modify its supplied stream before an operation fails. Opening an
original image with `writable: true` bypasses host rollback protection. Always
open writable sessions on the temporary path supplied by `ImageTransactions`:

1. `Write` copies the input to a temporary sibling of the destination.
2. The edit callback modifies that temporary image and disposes its session.
3. The validation callback reopens the result and checks structure and intended
   content. Although optional in the API, supply it for application writes.
4. The transaction flushes data and checks source/destination fingerprints for
   concurrent changes before replacing the destination entry.
5. In-place writes return a uniquely named `.bak` path; output-copy writes leave
   the input intact. Failures before commit discard the staged result.

For an in-place update, pass `outputPath: null` and `inPlace: true`. For a newly
formatted image, use `ImageTransactions.Create` with `DiskSession.Create` inside
its creation callback and a reopen check inside its validation callback. Create
the destination's host directory first. The transaction layer rejects linked
paths, unintended source aliases, and existing outputs unless overwrite is
explicit. Operation-specific inputs, such as source files or manifests, must
also be protected against output aliases; use the existing operations as the
pattern and `ImageTransactions.EnsureDistinctPaths` where appropriate.

The backend also checks filesystem integrity, container write protection, and
entry access flags. Transactional staging does not enable writes to damaged,
hybrid, or unsupported forked-file volumes. Byte preservation applies to the
documented operation contract, not identical allocation after file copying.
See [disk image integrity and preservation](disk-images.md) for these boundaries.

`DiskException.Code` supplies a diagnostic identifier and `ExitCode` supplies
the CLI classification. Host I/O and cancellation can also raise ordinary .NET
exceptions. Core does not write diagnostics to a console; callers decide how
to present them. Pass cancellation tokens to APIs that expose them and let
failures propagate out of transaction callbacks so the staged result is not
committed.

### Embedding the CLI boundary

`A2Utils.Cli` exposes one public entry point for hosts and tests:

```csharp
int exitCode = CliApplication.Run(
    args: ["disk", "info", imagePath, "--json"],
    output: resultWriter,
    error: diagnosticWriter,
    cancellationToken: cancellationToken);
```

`Run(string[] args, TextWriter? output = null, TextWriter? error = null,
CancellationToken cancellationToken = default)` builds and invokes the same
command tree as the `a2` executable, returns the documented process-style exit
code, and does not terminate the hosting process. Null writers use
`Console.Out`/`Console.Error`. The CLI assembly is packaged as a tool, not as a
supported library NuGet package; use a project reference when embedding it.
Prefer direct Core calls when the host does not need CLI parsing or JSON
envelopes.

## Tests and fixtures

The test projects use xUnit 2.9.3 through `Microsoft.NET.Test.Sdk` and
`xunit.runner.visualstudio`. Name tests `Operation_Condition_ExpectedResult`.
There is no coverage percentage threshold; prioritize externally observable
behavior and preservation/failure checks for every write operation.

Run a focused class or one named case while developing:

```sh
dotnet test tests/A2Utils.Core.Tests -c Release --no-restore --filter "FullyQualifiedName~AssemblerTests"
dotnet test tests/A2Utils.Core.Tests -c Release --no-restore --filter "FullyQualifiedName~CopyFrom_InsufficientSpaceTransaction_PreservesBothOriginalImages"
dotnet test tests/A2Utils.Cli.Tests -c Release --no-restore --filter "FullyQualifiedName~ProgramWorkflowTests"
dotnet test A2Utils.slnx -c Release --no-restore --logger trx
```

Core tests cover codecs, layouts, metadata, allocation limits, malformed inputs,
and transactions. CLI tests cover command parsing, JSON/errors, host outputs,
and complete workflows. Preserve independent expected values: images generated
and reopened by DiskArc alone cannot establish interoperability. Program tests
use known opcode/token vectors as well as round-trip checks.

The [fixture guide](../tests/TestData/README.md) records the catalogs, hashes,
provenance, and generator for two synthetic DOS 3.3 images in different sector
orders. Neither contains boot code. Make temporary copies for mutation tests;
never write to the reference fixtures. Regeneration with
`pwsh -File tests/TestData/Generate-Fixtures.ps1` is an intentional fixture
maintenance step, not a prerequisite to run the tests. Review generated hashes
and expected catalogs when changing it.

## Style and dependencies

Follow [AGENTS.md](../AGENTS.md) and [.editorconfig](../.editorconfig): four-space
indentation, Allman braces, file-scoped namespaces, nullable annotations,
PascalCase public names, and `_camelCase` private fields. Parse binary values
with explicit endianness and bounds checks. Keep host-path behavior in the
operation layer and console concerns in the CLI.

Apply formatting only to project-owned source:

```sh
dotnet format A2Utils.slnx --no-restore --exclude third_party
```

For an intentional NuGet update, edit the relevant project reference/version,
run `dotnet restore A2Utils.slnx --force-evaluate`, review every changed lockfile,
then verify a locked restore and the relevant tests. Runtime-specific publishing
uses separate lockfiles under each project's `obj/<RID>/`; those generated files
must not replace the tracked normal-restore locks.

DiskArc/CommonUtil are pinned together at revision
`7a055a200e31f752f3a92bb9fe6ae6f67cd55534`. Their 208 upstream files are recorded in
[SOURCE_MANIFEST.json](../third_party/CiderPress2/SOURCE_MANIFEST.json).
The [vendor guide](../third_party/CiderPress2/README.a2utils.md) describes updates:
replace both projects at one exact revision, refresh the manifest and notices,
record changes in the architecture decision, and rerun independent fixture and
transaction checks. Keep A2Utils behavior in the adapter; explicitly document
any exceptional vendor patch. `.gitattributes` disables newline conversion for
vendored files and disk images so their hashes survive checkout.

To verify the recorded upstream file hashes in PowerShell:

```powershell
$vendorRoot = Join-Path (Get-Location) 'third_party/CiderPress2'
$manifest = Get-Content -LiteralPath (Join-Path $vendorRoot 'SOURCE_MANIFEST.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $vendorRoot $entry.path) -Algorithm SHA256).Hash
    if ($actual -ne $entry.sha256) { throw "Vendor hash mismatch: $($entry.path)" }
}
Write-Output "Verified $($manifest.files.Count) upstream files."
```

The evaluation probe is available separately from the solution test suite:

```sh
dotnet run --project third_party/evaluation/DiskEngineProbe.csproj -c Release
```

## Packaging and local installation

The shared version is currently `0.4.0-dev` in
[Directory.Build.props](../Directory.Build.props). When changing it, also update
versioned local installation examples. Release builds take their version from
the Git tag instead. The CLI package ID is `A2Utils.Tool`; its command is `a2`.

From the repository root:

```sh
pwsh -File eng/Package.ps1
pwsh -File eng/Package.ps1 -Runtime win-x64 -SkipTests
pwsh -File eng/Package.ps1 -Runtime win-x64 -Version 0.4.0-dev -SkipTests
```

The default runtime is the host's runtime identifier. Accepted explicit values
are `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.
Use `-SkipTests` only after the relevant tests have passed; it skips tests, not
restore, pack, or publish. Building for another runtime does not validate
execution on that platform. Unix archives also require `tar` on the build host.

| Artifact | Location and requirements |
| --- | --- |
| .NET tool package | `artifacts/packages/A2Utils.Tool.0.4.0-dev.nupkg`; running the installed tool requires the .NET 10 runtime |
| Self-contained files | `artifacts/publish/<RID>/`; includes the runtime and uses `a2.exe` on Windows or `a2` on Unix |
| Windows archive | `artifacts/a2utils-<RID>.zip` |
| Linux/macOS archive | `artifacts/a2utils-<RID>.tar.gz` |

The script copies dependency notices, adds runtime notices to self-contained
output, creates the archive, and prints its SHA-256 hash. Keep accompanying
files and notices with the executable. Generated artifacts, `bin/`, and `obj/`
are ignored by Git.

`-Version` overrides the shared version for restore, tests, packing, and
publishing. It accepts a version such as `0.4.0` or `0.4.0-rc.1`, without a
leading `v` or build metadata. Versioned archives use
`a2utils-<version>-<RID>.zip` or `.tar.gz`, and the tool package uses the same
version. Omitting this option preserves the local artifact names above.

Install the tool into this checkout:

```sh
dotnet tool install A2Utils.Tool --version 0.4.0-dev --add-source artifacts/packages --tool-path artifacts/tools --configfile NuGet.Config
```

Use `dotnet tool update` with the same arguments when upgrading an existing
installation. If you rebuilt the same version, `update` can leave the previous
binaries installed; run `dotnet tool uninstall A2Utils.Tool --tool-path artifacts/tools`
and then repeat the install command. Run `./artifacts/tools/a2 disk ls tests/TestData/independent-dos33.do`
to check the installed tool. A Windows x64 self-contained smoke check is
`./artifacts/publish/win-x64/a2.exe disk verify tests/TestData/independent-dos33.do`.
These commands use local artifacts and do not publish a package.

## Tagged releases

[The release workflow](../.github/workflows/release.yml) runs only when a `v*`
tag is pushed. Branch pushes and pull requests do not run builds. Before any
build, it validates `vMAJOR.MINOR.PATCH` (optionally `-rc.1`, `-beta.2`, etc.)
and checks that the tagged commit belongs to `origin/main` history. Annotated
and lightweight tags work; tags on unmerged branches are rejected. Numeric
version identifiers cannot have leading zeroes; build metadata is unsupported.

To release a reviewed commit, update your checkout and push a new version tag.
For example, to validate the current 0.4 line as a prerelease:

```sh
git switch main
git pull --ff-only origin main
git tag -a v0.4.0-rc.1 -m "Release 0.4.0-rc.1"
git push origin v0.4.0-rc.1
```

The tag supplies the package and binary version; editing `Directory.Build.props`
is unnecessary. Use a prerelease tag such as `v0.4.0-rc.1` to mark the GitHub
Release as a prerelease. Push one release tag at a time and keep existing
release tags unchanged.

Each platform restores locked dependencies, verifies formatting, runs tests,
builds packages, and smoke-tests the installed tool and executable extracted
from its archive. Only after all platforms pass does publication begin.

| Release asset | Platform or purpose |
| --- | --- |
| `a2utils-<version>-win-x64.zip` | Windows x64, self-contained |
| `a2utils-<version>-linux-x64.tar.gz` | Linux x64, self-contained |
| `a2utils-<version>-osx-arm64.tar.gz` | macOS Apple Silicon, self-contained |
| `A2Utils.Tool.<version>.nupkg` | Portable .NET tool; requires .NET 10 |
| `SHA256SUMS.txt` | SHA-256 hashes of the four downloads |

Downloads and generated release notes appear in the repository's
[Releases section](https://github.com/AmosAnderson/a2utils/releases). The
repository remains private, and publication refuses a public repository.
The workflow uses GitHub's built-in token, with write access only in the
publication job; no additional secret or NuGet publishing account is needed.
TRX reports remain workflow artifacts, separate from release downloads.

Publication uploads and verifies assets in a draft before publishing it. If
uploading fails, rerun the failed job to complete that draft. Published releases
are preserved; use a new version tag for changed binaries. Missing packages,
unexpected draft assets, or a tag moved since validation stop publication.

Run `pwsh -File eng/Test-Release.ps1` to test the release guards and simulated
upload/retry paths locally. It creates disposable fixtures under `artifacts/`
and never calls GitHub. The `v0.4.0-dev` hosted attempt passed Linux packaging
and smoke tests, while Windows exposed a cancelled-child cleanup race and macOS
exposed the system `/var` alias in cc65's generated workspace. Compiler staging
now resolves only its trusted system temp root to a physical path and uses
bounded best-effort cleanup; user-supplied linked paths remain refused. Current
test counts and external-tool evidence are maintained in
[VALIDATION.md](VALIDATION.md). A new version tag is required for
hosted confirmation and actual publication; see [PLAN.md](../PLAN.md).

For a contribution, use a focused imperative commit subject and explain the
problem, resulting behavior, affected formats, and validation in the pull request.
Link relevant issues and update the plan when scope or architecture changes.
