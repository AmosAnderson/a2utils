# Development preview validation

## Remaining AI programming tools — September 16, 2026

Validated locally on macOS ARM64 with the pinned .NET SDK's permitted patch roll-forward:

| Check | Result |
| --- | --- |
| Locked restore; Release build with warnings as errors | Passed; zero warnings/errors |
| Full Core and CLI suites | 1,158 passed (1,021 Core, 137 CLI); 15 external-MAME tests skipped |
| Formatting outside vendored code and `git diff --check` | Passed |
| Interactive execution | Ordered conditions/input/checkpoints, failure/timeout evidence, cycle/routine evidence validation, initial-state bounds, and source preservation covered |
| Compiler/runtime memory | Labels/segments/diagnostics, staged linker configuration, generated includes, BSS/zero-page/stack/heap declarations, overlays, and failed-build preservation covered |
| Graphics/runtime library | Deterministic virtual asset outputs, native and compiler integration, visual tolerance/crops/diff artifacts, assembled original routine examples, and optional machine smoke cases |
| Setup/BASIC/audio | Staged starters, effective tool-path locks, directory membership, simultaneous profile/lock mutation refusal, retained fingerprint evidence, BASIC source/runtime diagnostics, known PCM metrics and malformed WAV rejection |
| Block storage | CFFA2 arguments, CPU-compatible firmware selection, raw/2IMG validation, copied-image hashes, and unsupported-image preservation covered with process contracts |
| Reproducible ProDOS directories | Raw/2IMG nested directory-entry and redundant-header timestamps independently match the fixed manifest timestamp |
| Schemas/examples | Five valid JSON Schemas and 24 complete example documents validated; local documentation links resolve |
| Generated Lua | 256 temporary mocked-backend checks cover sequence/cycle/routine control and all joystick/paddle input mappings; 1,024 text-byte/charset/MouseText parity cases pass |
| Installed local tool package | New environment schema, capabilities, staged starter, generated-asset source check, and disk-free routine process contract passed |
| Vendor integrity | All 208 recorded hashes verified; one documented local ProDOS timestamp patch retains its upstream hash separately |

Actual MAME and cc65 executables were unavailable in this environment. The 15
optional real-machine tests cover the earlier boot/OS/debug/bank cases plus
runtime routines, clipping, exact cycles, cycle-limit stopping, joystick input,
and audio capture. They were skipped. Source inspection, Lua mocks, known PCM,
and the explicitly named execution/compiler test host do not establish actual
emulator, controller, compiler, or boot-template interoperability. The CFFA2
profile requires its external firmware and an actual boot check with the chosen
OS template. Previous real-tool evidence below applies to its recorded revision.

The package smoke used an isolated local .NET tool installation under ignored
`artifacts/ai-tools-validation/`. No package or release was published. Windows,
Linux, self-contained packages, and release automation were not rerun this round.
The dependency revision and NuGet packages remain pinned; the minimal vendor
patch and preservation regression are recorded in [ADR 0001](decisions/0001-disk-engine.md).


## Runtime debugging and IIe memory/display — September 16, 2026

Validated locally on macOS ARM64 with the pinned .NET SDK's permitted patch roll-forward:

| Check | Result |
| --- | --- |
| Locked restore and Release build with `--warnaserror` | Passed; zero warnings/errors |
| Core and CLI suites | 1,006 passed (873 Core, 133 CLI); eight external-MAME tests skipped |
| Formatting and `git diff --check` | Passed, excluding vendored formatting |
| Execution contracts | Debug evidence consistency, missing triggers, bank identity, observation bounds, 80-column/MouseText results, CLI exits, input preservation, and symbolic resolution covered |
| Project banks | Physical ranges, target availability, language-card shared storage, startup restrictions, template/output preservation, and loader assembly covered |
| Schemas and examples | Four schemas and thirteen complete tracked example documents validate; local documentation links resolve |
| Lua helpers | Temporary mocked-backend checks exercised physical saved-memory reads and debugger callback/step sequencing; these are not emulator evidence |

New optional real-MAME tests cover instruction breakpoints, exact two-instruction
stepping, write watchpoint attribution, bank-copy routines, physical observations
with auxiliary CPU mapping, and 80-column MouseText. They were skipped because
`A2_MAME_PATH` and `A2_MAME_ROMS` were not configured. The existing boot and two OS
checks were also skipped. No claim of actual debugger/banked-display interoperability
is made by the ordinary suite or Lua mock checks; run these cases with MAME 0.289
and matching local ROMs before treating the backend extension as machine-validated.

The backend was checked against pinned upstream source for debugger callback ordering,
saved RAM/video layouts, watchpoint attribution, and history semantics. Native history
retains PCs and uses capture-time memory for disassembly, so it does not reconstruct
historical bytes or bank mappings. Banked project declarations require explicit
loaders. No production dependency changed and no release/package was published.

## Development reliability milestone — September 16, 2026

Validated locally on macOS ARM64 with .NET SDK 10.0.401. This covers the selected
four-item milestone: full build preflight, connected project testing, multiple
isolated disks with saved-file assertions, and independent disk validation.

| Check | Result |
| --- | --- |
| Locked restore and Release build with `--warnaserror` | Passed; zero warnings/errors |
| Core and CLI suites | 887 passed (757 Core, 130 CLI); three external-MAME tests skipped |
| Formatting excluding `third_party` and `git diff --check` | Passed |
| Full build preflight | DOS/ProDOS/2MG preview hashes agree with real builds; capacity, locks, collisions, aliases, protection, and cancellation covered |
| Project workflow | Exact image/input pins, symbol resolution, source annotations, precommit change guards, explicit assertions, cancellation summaries, and CLI exit codes covered |
| Multiple disks and saved files | Both input images preserved; post-run bytes/hash/type/load-address/length checks, literal DOS names, mismatch/failure/timeout/cancellation paths covered |
| Independent ProDOS | Known seedling/sapling/tree payloads, sparse maps, directory/root capacity, valid 65,535-block volume edits, and preserved unused trailing block passed |
| Bounded malformed-image probes | Nineteen deterministic mutations run in isolated child processes with per-case and corpus deadlines |
| Fixture provenance | Independently regenerated ProDOS bytes, all five documented payload hashes, and allocation counts match |
| Schemas/examples | Four JSON Schemas and thirteen complete tracked/inline examples validate; serialized explicit mounts, mount-free project tests, and conflicting mounts checked |
| Local packages | Installed .NET tool and self-contained `osx-arm64` executable passed embedded-schema, preflight, project-test contract, input-preservation, and independent ProDOS verification checks |
| Restore after publishing | Normal locked restore passed; RID locks remain under `obj` |

Local package smoke artifacts are under ignored `artifacts/milestone-validation/`.
The tool package uses the local-only version `0.4.0-dev.milestone20260916`; the
repository version remains `0.4.0-dev`. Packages were built directly with .NET
because PowerShell was unavailable; `eng/Package.ps1`, release automation, and
hosted Windows/Linux checks were not rerun in this environment. No package or
release was published, and no production NuGet or vendored dependency changed.

The project-test package checks used the explicitly named process-contract helper,
not a real emulator. The self-booting MAME check and the new DOS/ProDOS OS checks
were skipped because their external resources were absent. Real OS tests now
observe a catalog separately, then load/call original assembly and save a known
binary result. See [fixture setup](../tests/TestData/README.md#optional-real-dos-and-prodos-interoperability).
These tests provide a repeatable release check; actual OS success and broader
platform/emulator coverage remain outstanding. The previous Windows MAME evidence
below applies to its recorded revision and workflows.

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
