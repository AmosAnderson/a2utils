# Development preview validation

## Preview 0.4 development workflow — September 10, 2026

Validated locally on Windows x64 with .NET SDK 10.0.401 (the pinned SDK's
permitted patch roll-forward). This records implementation evidence, not a
stable-release certification or validation of every supported machine profile.

| Check | Result |
| --- | --- |
| Locked dependency restore and Release build with `--warnaserror` | Passed; zero warnings/errors, no new NuGet dependencies |
| Core and CLI suites | 794 passed (673 Core, 121 CLI), zero skipped, including the real MAME integration test |
| Formatting outside `third_party` | Passed |
| Project/execution schemas | Tracked examples, complete inline examples, and serialized project defaults validate |
| Documentation | Local links, anchors, fences, JSON examples, and new command help checked |
| Independent fixture and vendor integrity | Both DO/PO fixture hashes and all 208 pinned upstream files match |
| Native builds | DOS/ProDOS mixed sources, byte-identical rebuilds, source maps, metadata, overlays, and memory conflicts checked |
| Failure preservation | Source/output aliases, includes, invalid source/PNG/schema, locks, capacity, cancellation, and failed staged writes covered |
| Graphics | Independent PNG, screen addresses, palettes, bitmap packing, Apple shape vectors, and double-hires bank vectors checked |
| Local tool and self-contained Windows package | Built and smoke-tested; embedded schemas, project builds, source maps, BASIC labels, graphics, and actual MAME execution passed |
| Restore after self-contained publish | Normal locked restore passed; RID lockfiles remain under `obj` |

Actual external-tool validation used MAME **0.289** and cc65
**`cl65 V2.19 - Git e11fb5c`**. Process-contract test doubles are separately named
and do not count as emulator or compiler execution evidence. Real checks passed:

- Original assembled boot sector: injected keyboard input, registers, memory,
  text, completion condition, screenshot, and PC samples.
- Project build into a copied ProDOS 2.4.3 template: BASIC startup ran assembly,
  printed the expected text, and wrote the asserted completion byte.
- A real two-case `a2 test` suite containing both boot workflows.
- C source compiled for `apple2` and `apple2enh`, including byte-identical repeated
  compilation. The enhanced target also passed a full project build, generated
  startup launcher, ProDOS boot, and text assertion in MAME. The installed tool
  and self-contained executable both repeated that machine check.

The final local evidence is under ignored `artifacts/development-final/`;
earlier boot/suite evidence is under `artifacts/execution-smoke/` and
`artifacts/os-validation/`. [Execution setup and provenance](execution.md) record
the tool/ROM identities and reproduction commands. The downloaded ProDOS image's
SHA-256 also matched AppleWin's HTTPS copy. No downloaded binaries, ROMs, or OS
images are included in tracked source, examples, or packages.

Actual machine coverage is the enhanced Apple IIe on Windows. Other MAME profiles,
native Linux/macOS execution, and a real DOS 3.3 operating-system workflow remain
release checks. New formatted disks remain data volumes. Memory checks describe
declared main-memory ranges, not dynamic/banked allocations or the complete C
runtime. cc65 reproducibility also depends on its external distribution and
environment. Native IIgs and broad archive support remain outside this preview.

## Preview 0.4 hosted release follow-up — September 10, 2026

The [`v0.4.0-dev` release run](https://github.com/AmosAnderson/a2utils/actions/runs/34525257217)
validated its tag and release scripts. Linux passed the full test, packaging,
installed-tool, archived-executable, and artifact-upload sequence. GitHub's
action versions and runner setup completed normally.

Windows failed one cancellation test because recursive cleanup raced a terminated
compiler child that still held the generated staging directory. macOS failed the
cc65 tests because its system temporary path traverses `/var`, a link to
`/private/var`, and the adapter applied the user-path link policy to that trusted
internally generated workspace. The follow-up resolves the physical system temp
root before creating the workspace, retains strict checks on project/compiler
paths, and makes generated-directory cleanup bounded and non-masking.

The Release build now passes locally with warnings as errors. The expanded suites
pass 795 tests (674 Core and 121 CLI), with only the environment-dependent real
MAME test skipped, and formatting verification passes. New tests cover linked
temporary-directory resolution and cleanup while a child file is locked. All 49
release-automation checks also pass; a local `0.4.0-dev.1` Windows package passed
installed-tool and archived-executable smoke tests.

The failed tag is intentionally left at its original commit under the release
policy. Rerunning that workflow would test the old code, so hosted confirmation
and publication require a new version tag containing this fix.

## Documentation coverage refresh — September 10, 2026

This documentation-only pass compared the current command tree, public Core
surface, schemas, defaults, validation limits, result records, and failure paths
with their implementations:

- All 39 CLI leaf commands are represented in the command reference. Generated
  capability metadata was cross-checked with effective help and source where
  recursive/root-only options require interpretation.
- The Core reference covers all 79 exported types and 87 hand-written public
  methods, including signatures, defaults, lifecycle rules, and result fields.
- Project, execution, cc65, graphics, BASIC, assembly, JSON, troubleshooting,
  packaging, and emulator guides now cover the preview 0.4 behavior.
- All 25 first-party Markdown files have resolvable local links, valid referenced
  heading anchors, balanced code fences, and no trailing-whitespace errors.
- `dotnet test -c Release` passed with 793 tests and one real-MAME test skipped
  because this documentation environment had no configured emulator. The 794-test run with
  real MAME remains recorded above. The Release build passed with warnings as
  errors, and formatting verification also passed.

No product source or test code changed during this pass.

## Previous preview 0.3 validation

Preview 0.3.0-dev validated locally on Windows x64 with .NET SDK 10.0.400. This records completed
checks for the development preview; it is not a stable-release certification.

| Check | Result |
| --- | --- |
| Release solution build with `--warnaserror` | Passed: zero warnings and errors |
| Core xUnit suite | 387 passed, including two temporary-root regressions |
| CLI xUnit suite | 73 passed |
| Repository formatting, excluding vendored code | Passed |
| Independent DO/PO fixture hashes | Match documented SHA-256 values |
| Vendored source integrity | All 208 files match the pinned upstream manifest |
| DiskArc evaluation probe | DOS/ProDOS read/write/reopen and 2IMG metadata preservation passed |
| Local .NET tool package installation | Passed, installed under `artifacts/tools` |
| Self-contained `win-x64` package | Built and smoke-tested |
| Packaged operations | Disk workflows plus assembly/BASIC compile, decompile, image import, and byte round trips passed |
| Normal locked restore after RID-specific publishing | Passed; publishing uses separate lockfiles under `obj` |
| Package license/notice inclusion | Tool contains DiskArc/CommonUtil and System.CommandLine notices; self-contained archive also contains runtime notices |

Run the automated checks from the repository root:

```sh
dotnet restore A2Utils.slnx --locked-mode
dotnet build A2Utils.slnx -c Release --no-restore --warnaserror
dotnet test A2Utils.slnx -c Release --no-restore
dotnet format A2Utils.slnx --verify-no-changes --no-restore --exclude third_party
```

The tests cover raw DOS headers and trailing bytes, known layout vectors,
malformed catalog/allocation structures, full disks, multi-list DOS files,
ProDOS storage boundaries and mixed-case directories, sparse and metadata
restoration, rejected malicious manifests, metadata-preserving conversions,
locked files, failed transactions, backups, and CLI contracts. Preview 0.2 adds
logical payload export, strict text conversion, recursive copies, moves,
parent directory creation, atomic directory imports, and input/output alias
protection. Transfer tests include independent disk fixtures, allocation
limits, sparse files, metadata, and rollback after failed writes.

Preview 0.3 adds independently specified machine-code and Applesoft token
vectors, all 107 BASIC tokens, CPU compatibility, label and branch boundaries,
malformed source and program rejection, DOS header validation, source/output
protection, and compile/import/decompile workflows on DOS and ProDOS.
Disassembly tests include 27,648 opcode/operand/truncation round-trip cases and
mixed code/data streams across the three CPU modes. No Apple ROM is embedded.

Platform-specific transaction tests exercise Windows file identities locally;
their Unix branches and the `/usr/bin/stat` regular-file check passed on the
initial hosted Linux job. Native macOS validation remains pending. Fixtures are hand-generated and
contain no boot code. The emulator catalog/load smoke check has not been run.

Before a stable/public release, run the configured hosted CI matrix, validate
generated data disks in independent DOS and ProDOS emulator environments, and
select a license for original A2Utils code. Initial implementation and validation
were performed locally. A private [GitHub repository](https://github.com/AmosAnderson/a2utils)
has since been created; no public package has been published.

## Documentation verification

The expanded guides were checked locally on Windows on September 8, 2026:

- All 20 command help pages were compared with the command reference.
- All 37 command lines in the disk workflows ran against temporary images;
  conversion retained expected bytes and the reference fixture was unchanged.
- All 21 `a2` command lines in the getting-started guide ran successfully;
  rebuilt assembly and Applesoft payloads matched their originals byte for byte.
- Both C# library examples compiled and ran, including staged image editing
  with structural and content validation.
- The PowerShell JSON example returned the expected unlocked fixture entry.
- Local Markdown links, heading references, and code-fence pairing were checked.

Bash/zsh and `jq` examples were reviewed but not executed on this Windows host.
The original 458-test result records the preceding implementation validation;
documentation verification did not rerun the full solution test suite.

## Release workflow validation

The [initial hosted run](https://github.com/AmosAnderson/a2utils/actions/runs/34257159465)
passed Windows and Ubuntu. macOS failed because its system temporary path uses
the `/var` symlink, which correctly triggered image-write link protection.
The shared test setup now resolves temporary-directory ancestors to physical
paths. Production link checks remain unchanged.

After this correction, all **460 tests (387 Core, 73 CLI)** passed locally with
`TEMP` and `TMP` deliberately routed through a Windows junction. Formatting
verification also passed. The Unix-specific regression additionally checks
that physical writes succeed and writes through a created alias are refused;
that branch awaits native macOS/Linux execution of the updated tests.

All 49 local release checks passed. They exercise real Git tags and branch ancestry, then use a
simulated GitHub CLI to cover private-repository enforcement, changed-tag
rejection, required assets, checksums, draft publication, failed/incomplete
uploads, retries, and stable/prerelease flags. Versioned Windows packaging for
`0.3.0` produced matching package and binary versions, bundled notices, and
working tool/self-contained disk and program commands.

Actionlint 1.7.12 accepted the workflow. All PowerShell scripts parsed, updated
documentation links resolved, and the executable extracted from the versioned
Windows archive passed disk verification, assembly, and BASIC smoke checks.

The updated workflow runs only on release-tag pushes; ordinary branch pushes
and pull requests do not build. Actual GitHub Release publication and native
macOS validation will occur when an intended release tag is pushed. No tag or
release was created merely to validate this configuration.
