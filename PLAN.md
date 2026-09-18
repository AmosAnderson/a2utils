Apple II disk image utility — implementation plan
================================================

Agentic software development milestone (September 18, 2026)
-----------------------------------------------------------

- [x] Read-only logical/physical disk diffs and hash-bound declarative change-set
  planning/apply with immutable input snapshots and transactional commits.
- [x] Noncommitting project resolution with effective settings, hashed build and
  execution dependencies, tool requirements, memory layout, and disk/boot plans.
- [x] Existing-image project import with a pinned template, editable source exports,
  locked reference exports, and optional reassemblable binary disassembly.
- [x] Original bare-metal DOS-order boot sectors without an operating-system template.
- [x] Deterministic in-process 6502/Apple-compatible 65C02 routine execution, plus
  selective and parallel suites, reruns, progress events, and bounded evidence.
- [x] Graphics-memory assertions, symbolic build bindings, compiler/native source
  traces, and structured repair-oriented diagnostics.
- [x] Typed result/error/envelope contracts, complete capability discovery, bundled
  schemas, and a bounded local MCP stdio server for coding agents.
- [x] Opt-in content-addressed project image caching with exact input/tool pins,
  validated transactional restores, and bounded/concurrency-safe metadata.

All new write paths preserve source inputs, validate staged results, and refuse
changed evidence before commit. MAME, cc65, ROMs, firmware, and operating-system
media remain explicit external inputs. Local validation for this milestone is
recorded at the top of [VALIDATION.md](docs/VALIDATION.md).

Remaining AI programming tools milestone (September 16, 2026)
------------------------------------------------------------

- [x] Ordered wait/input/assert sequences, per-step deadlines and inspectable checkpoints.
- [x] cc65 segment/symbol/diagnostic feedback, staged custom linker configuration,
  runtime BSS/zero-page/stack/heap accounting, and explicit overlay groups.
- [x] Original reusable text/keyboard, clipped HGR sprite, DOS/ProDOS file, and BASIC CALL examples.
- [x] Project asset conversion with generated assembly/C metadata and screenshot assertions/diffs.
- [x] Environment checks, BASIC/assembly/C starters, and hash-pinned toolchain profiles.
- [x] Conservative BASIC loop/arity/array checks and mapped runtime error diagnostics.
- [x] Game-port overrides, bounded audio capture/metrics, instruction-cycle budgets,
  and disk-free routine execution with explicit initial state.
- [x] Self-contained deterministic MOS 6502/Apple-compatible 65C02 routine engine
  with bounded cycles, assertions, and instruction-level trace evidence.
- [x] Explicit CFFA2 slot-7 single-volume ProDOS block-device execution profile.
- [x] Schemas, documentation, unit/process-contract tests, and opt-in actual MAME tests.
- [x] Locked restore, warning-free Release build, 1,158 passing local tests, formatting,
  schema/example checks, and installed local tool smoke checks. Fifteen actual-MAME
  cases remain skipped without external emulator/ROM inputs.

These additions complete the remaining AI-programming suggestions from this round.
They use the existing disk engine and selectable execution backends: pinned MAME
0.289 for machine behavior and an in-process CPU engine for pure routines. Runtime/asset/lock
operations retain bounded inputs and immutable snapshots. Runtime memory is
conservative static accounting, and overlay groups are an explicit application
promise about lifetimes. CFFA2 firmware, emulator ROMs, boot systems, and cc65
remain external inputs. Optional machine checks do not become validation evidence
until run with those inputs; see [VALIDATION.md](docs/VALIDATION.md).

Runtime debugging and Apple IIe memory/display milestone
--------------------------------------------------------

- [x] Numeric and build-symbol instruction breakpoints and CPU read/write watchpoints.
- [x] Bounded instruction stepping, recent PC/disassembly evidence, source locations,
  and memory observations without expected values.
- [x] Physical main/auxiliary/language-card observations independent of soft switches.
- [x] Bank-aware project declarations, physical range/alias validation, target capabilities,
  and explicit loader examples.
- [x] 40/80-column physical text decoding, display attributes, and MouseText cells.
- [x] Schemas, Core/CLI integration coverage, and optional real-MAME smoke cases.

The backend remains pinned to MAME 0.289 with no new production dependency. History
stores instruction addresses and disassembles from capture-time memory; historical
bank mappings/bytes remain outside this milestone; the subsequent milestone adds cycle profiling. Project bank
annotations require application loaders. Actual MAME validation is separate from
contract and Lua-helper checks; see [VALIDATION.md](docs/VALIDATION.md).

Development reliability milestone (September 13, 2026)
------------------------------------------------------

The selected next milestone connects the existing build and execution tools:

- [x] Full `build --preflight` with disposable allocation/validation, image hash,
  additions/replacements, created directories, and free-space reporting.
- [x] Project-owned `build --test`, pinned build/test inputs, symbol resolution,
  source annotations, and build/execution evidence. Preflight input and image
  expectations are rechecked before the actual output is committed.
- [x] Isolated `flop1`/`flop2` mounts, per-image hashes, optional post-run filesystem
  verification, and saved-file content/type/load-address assertions.
- [x] Independent ProDOS fixture, sparse/indexed files, directory capacity,
  valid maximum-size volume edits, and bounded malformed-image process probes.
- [x] Optional actual DOS/ProDOS catalog/load/save interoperability tests with
  explicit local emulator/ROM/boot-disk configuration.
- [x] Release build, 887 passing local tests, formatting, schemas/examples, and
  local tool/self-contained macOS smoke checks completed September 16.

Actual OS checks are skipped when their external resources are absent; adding a
test does not constitute emulator evidence. See [VALIDATION.md](docs/VALIDATION.md)
for the completed local checks. Existing single-disk execution and fast source
checks remain available. The subsequent milestone adds an explicit CFFA2 block-device profile.

The subsequent AI-tools milestone above adds runtime memory accounting, compiler
diagnostics, project graphics assets, and toolchain fingerprints. The separate disk
roadmap still includes DOS/ProDOS file migration, a higher-level transactional Core
editing API, per-image capabilities, and resource forks. No new
disk-engine dependency is introduced.

AI development workflow implementation (September 10, 2026)
----------------------------------------------------------

Preview 0.4 adds a complete host-side development workflow. Its implementation
and validation are tracked here:

- [x] Structured program diagnostics, capability discovery, and JSON schemas.
- [x] Assembly file composition, expressions, assertions, symbols, source maps, and listings.
- [x] Applesoft source checks, safe renumbering, symbolic labels, and source mappings.
- [x] Versioned project manifests and transactional, reproducible source-to-disk builds.
- [x] Machine/runtime profiles, memory conflict checks, and documented platform symbols.
- [x] MAME execution/test adapter, bounded automation, assertions, and failure evidence.
- [x] cc65 integration, AppleSingle metadata decoding, ld65 source maps, and source-aware traces.
- [x] Lo-res/hi-res PNGs, sprites, tiles, fonts, shape tables, and double-hires screens.
- [x] Build, unit/integration tests, formatting, schema/examples verification, and documentation.

Bootable builds either preserve a supplied bootable template or emit explicitly
declared original boot sectors; otherwise new formatted disks remain data volumes.
External emulator execution requires a configured emulator, matching
ROMs, and a suitable disk image. Emulator validation is recorded
separately from tests of the adapter contract. Native IIgs development, broad archive
formats, and broader emulator/platform validation remain subsequent milestones.

The new workflow passed real enhanced-IIe MAME tests for an original boot sector,
ProDOS BASIC/assembly, and cc65 C output. Both local tool and Windows self-contained
packages passed smoke checks. See [the validation record](docs/VALIDATION.md) for
test evidence and the remaining platform/release checks.


Build a reusable C# library and a command-line application for inspecting, extracting, creating, and modifying Apple II and Apple IIe disk images. Start with standard DOS 3.3 disks, then add ProDOS. Keep the project organized so later utilities can handle Apple II archives, BASIC programs, graphics, and other files.

The original proposal below now has a working implementation. The workspace started empty on September 7, 2026, with .NET SDK 10.0.400 installed. Current milestone status:

| Milestone | Status |
| --- | --- |
| Disk-engine evaluation | Complete: pinned DiskArc/CommonUtil sources, repeatable probe, and architecture decision |
| Solution and CLI | Implemented: .NET 10 solution, command help, JSON/error contract, and formatting rules |
| Read-only DOS slice | Implemented and tested against independent synthetic DO/PO fixtures |
| DOS writes | Implemented with staged commits, backups, conversion, and failure tests; emulator release check remains |
| ProDOS operations | Implemented with directories, metadata restore, sparse files, and larger volumes; emulator release check remains |
| Packaging and release | Tag-only builds and private GitHub Releases with versioned binaries, tool package, and checksums implemented; original A2Utils code is GPL-2.0-only; emulator checks and resolution of the Apache-2.0/GPL-2.0-only dependency incompatibility remain before public binary distribution |

See `README.md` for actual commands and supported limits. This is a development preview, not a stable release. The [GitHub repository](https://github.com/AmosAnderson/a2utils) is private; no public package or release has been created.

Preview 0.2 adds logical payload export, explicit UTF-8/Apple text conversion,
same-filesystem copies within and between images, directory moves, recursive
ProDOS directory creation, and atomic host-directory import. Tests cover
metadata preservation, text round trips, collisions, source/output aliases,
and rollback after import failure. BASIC conversion and cross-filesystem file
migration remain separate work; Linux execution passed hosted checks, while
the corrected macOS test setup awaits release validation.

Preview 0.3 adds program tools: 6502, Apple-compatible 65C02, and WDC65C02
assembly/disassembly; Applesoft BASIC tokenization/detokenization; explicit raw
and DOS host-file headers; and decompilation directly from supported image
entries. See `docs/programs.md` for the source dialect and boundaries. These
tools do not implement a linker, macros, a high-level machine-code decompiler,
Integer BASIC, or an emulator. Machine execution remains an interoperability
release check.

Use modern .NET 10 with the SDK's default C# language version, nullable reference types, and Windows, macOS, and Linux support. This interprets “.NET” as modern .NET; targeting the older Windows-only .NET Framework would require a different baseline. .NET 10 is an LTS release supported through November 2028. Pin the SDK in `global.json` when implementation begins. [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)

Use `A2Utils` for the solution and library names, with the proposed executable name `a2` and a `disk` command group. The command group leaves room for future `a2 basic`, `a2 archive`, and `a2 graphics` utilities.

1. Establish the scope and choose the disk engine.

The first release should handle ordinary, unprotected 140 KiB DOS 3.3 disks. Version 1.0 should add ProDOS and larger block images. Support is defined by the combination of image container, storage layout, filesystem, and geometry; a filename extension alone is insufficient.

| Capability | Initial release, v0.1 | Version 1.0 | Later |
| --- | --- | --- | --- |
| Raw `.do`, `.po`, ambiguous `.dsk` | Read/write standard 140 KiB images | Continue support | Additional floppy geometries |
| `.2mg` / `.2img` | Read/write sector-data payloads for supported disks | Larger ProDOS block payloads | Nibble payloads |
| DOS 3.3 filesystem | Catalog, extract, create, add, replace, delete, rename, lock/unlock | Maintain full coverage | Nonstandard DOS variants |
| ProDOS filesystem | Identify and report unsupported filesystem operations | Read/write, directories, metadata | Extended forked files |
| Larger `.po` / `.hdv` images | Recognize as outside supported geometry | 800 KiB and single ProDOS volumes up to 65,535 blocks | Partitioned images |
| `.nib`, `.woz`, `.d13`, DiskCopy, ShrinkIt | Explain that support is unavailable | Same | Separate format milestones |

DOS-order and ProDOS-order describe how disk data is arranged in an image, independently of the filesystem inside it. A DOS filesystem can therefore reside in a ProDOS-order image. The 2IMG container records a payload format and can also contain comments and creator data. [CiderPress image-order explanation](https://github.com/fadden/ciderpress/blob/master/diskimg/README.md), [2IMG format notes](https://ciderpress2.com/formatdoc/TwoIMG-notes.html)

Before implementing a disk engine, run a short reuse evaluation of CiderPress II's `DiskArc` and `CommonUtil` libraries. They are C#/.NET libraries, and DiskArc is separated from the application. Its source repository declares Apache 2.0 licensing for code. [Source organization](https://github.com/fadden/CiderPress2/blob/main/SourceNotes.md), [repository and licensing](https://github.com/fadden/CiderPress2)

The recommended default is reuse behind an A2Utils adapter if the evaluation demonstrates all of the following: .NET 10 integration, required read/write operations, preservation of metadata and untouched bytes, usable corruption diagnostics, and a maintainable dependency/update approach. Check notices and fixture permissions separately, and record an exact dependency revision. Do not assume an official NuGet package exists. If a critical requirement fails, record the reason and implement only the scoped formats internally. Avoid maintaining two production engines.

Completion criterion: a recorded architecture decision and a small experiment that lists, extracts, modifies a temporary copy, and reopens a representative DOS image.

2. Create the solution and define the CLI contract.

Start with two production projects and two test projects:

```text
A2Utils.slnx
global.json
Directory.Build.props
src/
  A2Utils.Core/
    Images/          container detection, geometry, image access
    FileSystems/     DOS and ProDOS capabilities, entries, metadata
    Operations/      inspection, extraction, mutation, conversion
    Backends/        chosen disk engine adapter or internal engine
  A2Utils.Cli/
    Commands/
    Output/
tests/
  A2Utils.Core.Tests/
  A2Utils.Cli.Tests/
  TestData/          fixtures, provenance, expected catalogs and hashes
docs/
  decisions/
  formats/
```

Keep disk parsing and operations independent of console output, CLI parsing, and host filename conventions. Use explicit operation results and diagnostics. Represent unsupported capabilities directly, including directory support, metadata fields, and write eligibility.

Use `System.CommandLine` for argument parsing, validation, and generated help; select and pin a stable package during setup. Use `System.Text.Json` for machine-readable output and an established .NET test framework such as xUnit. Keep other dependencies minimal. [System.CommandLine documentation](https://learn.microsoft.com/en-us/dotnet/standard/commandline/)

Proposed commands:

| Command | Purpose |
| --- | --- |
| `a2 disk info IMAGE` | Show container, geometry, layout, filesystem, capacity, and diagnostics |
| `a2 disk ls IMAGE [PATH]` | List entries; support `--recursive` for ProDOS |
| `a2 disk extract IMAGE [PATH] --to DIRECTORY` | Extract one entry or all files with a metadata manifest |
| `a2 disk create OUTPUT --fs dos33 --size 140k --container raw --order dos` | Create a formatted data disk |
| `a2 disk add IMAGE HOSTFILE --name NAME --output NEWIMAGE` | Import a file with explicit type metadata |
| `a2 disk replace IMAGE PATH HOSTFILE --output NEWIMAGE` | Replace an existing file, preserving metadata unless explicitly changed |
| `a2 disk delete IMAGE PATH --output NEWIMAGE` | Remove an entry and reclaim its allocation |
| `a2 disk rename IMAGE OLD NEW --output NEWIMAGE` | Rename an entry |
| `a2 disk mkdir IMAGE PATH --output NEWIMAGE` | Create a ProDOS directory, available in v1.0 |
| `a2 disk attr IMAGE PATH [OPTIONS]` | Read attributes; changes require an output image or explicit in-place mode |
| `a2 disk convert INPUT OUTPUT --container 2mg --order prodos` | Change container/layout while retaining the filesystem |
| `a2 disk verify IMAGE` | Report structural inconsistencies without repairing them |

For example, DOS binary imports should accept `--type B --load-address 0x2000`; ProDOS imports should accept `--type BIN --aux-type 0x2000`. Define `--size` units as KiB and MiB and also offer an exact `--blocks` option for ProDOS creation. DOS creation should expose a volume number; ProDOS creation should expose a volume name.

Common options should include `--json`, `--quiet`, `--verbose`, and explicit input overrides `--input-order` and `--input-fs`. Use stdout for command results and stderr for diagnostics. JSON should include a schema version and stable diagnostic codes. Exit codes: `0` success, `1` unexpected failure, `2` usage error, `3` unsupported/ambiguous format, `4` corrupt image, `5` host I/O failure, `6` operation refused, such as insufficient image space or a name conflict. Scripts must not depend on localized error prose.

Completion criterion: buildable solution, help for planned commands, and an agreed contract for paths, options, output, and errors. Unimplemented commands must report that clearly.

3. Deliver image access and a read-only DOS vertical slice.

Separate container parsing from sector/block addressing and filesystem interpretation. An internal engine would use bounded sector and block access abstractions plus explicit layout mapping tables; a reused engine should provide equivalent behavior through its adapter. Keep binary parsing explicit with spans and endian-aware helpers.

Detection should validate container signatures and ranges, check payload size, then probe supported layout/filesystem combinations. Treat extensions as hints. Report ambiguous results with the applicable override; never select a write layout based only on `.dsk`. Permit geometry-valid container conversion without a recognized filesystem when the caller supplies an unambiguous layout.

The DOS reader must cover the VTOC, linked catalog, allocation bitmap, track/sector lists, deleted entries, file types, and locked state. Standard DOS 3.3 geometry is 35 tracks × 16 sectors × 256 bytes. DOS catalog entries do not provide a dependable byte length: BASIC and binary files have embedded headers, while text and sparse files need different handling. Preserve stored bytes, headers, trailing data, and sparse-position information during extraction. [DOS filesystem notes](https://ciderpress2.com/formatdoc/DOS-notes.html)

Extraction should create a versioned manifest recording original names, types, applicable load addresses, raw metadata, payload hashes, and allocation holes. `add --manifest MANIFEST` restores this representation. Ordinary host-file imports without a manifest treat the input as file payload and require enough metadata to construct the on-disk representation. Do not automatically translate text or BASIC. The preservation contract covers file content and supported metadata; extraction and re-import need not reproduce identical sector placement.

Map host filenames reversibly, accounting for invalid characters, case collisions, and reserved names. Keep output inside the selected directory, including when the directory contains links. Reject silent overwrites. Separate host paths from image paths; preserve original catalog names in the manifest.

Completion criterion: `info`, `ls`, `extract`, and read-only `verify` work on reference images with known catalogs and file hashes. Invalid pointers, truncated input, cycles, and ambiguous layouts produce bounded, useful failures.

4. Add transactional DOS writes and release v0.1.

Implement formatting, allocation/deallocation, catalog updates, add/replace/delete, rename, and attributes. Preflight both free data space and catalog capacity. Preserve unrelated sectors and container metadata. Newly created disks are formatted data volumes; bootable image creation requires a separate, later design for boot code and system files.

Mutations should require `--output NEWIMAGE` or explicit `--in-place`. Stage the complete operation in a temporary sibling file, validate and reopen it, then commit with platform-appropriate rename/replacement semantics. In-place mode creates a uniquely named backup first and checks for concurrent changes. Refuse source/destination aliases in output mode and refuse existing destinations unless `--overwrite` is explicit. Abort cleanly on cancellation, disk-full conditions, and validation failures. Document and test the supported local-filesystem commit guarantees.

Respect image write protection and file access flags. There should be no generic force option that bypasses corruption checks. Refuse writes to hybrid filesystems or images whose unsupported structures prevent a complete allocation assessment; retain read-only diagnostics where possible.

Conversion should reorder sectors or change containers, not migrate DOS files into a ProDOS filesystem. Preserve all payload bytes and retain container metadata where representable. Report metadata that a raw destination cannot store and require explicit `--allow-metadata-loss` before dropping it. Reject nibble/bitstream inputs in this release.

Completion criterion: the full DOS command set passes successful and failed transaction tests, an independent tool can read generated images, and a DOS environment in an emulator can catalog and load a test file from a modified data disk.

5. Add ProDOS and release v1.0.

Cover the volume header and bitmap, directories, seedling/sapling/tree storage, sparse data, and file EOF. Preserve file type, auxiliary type, access flags, timestamps, and supported name-case metadata. Recognize extended/forked files but report their operations as unsupported initially; refuse volume writes until their allocations can be accounted for safely. The on-disk format uses 512-byte blocks with a maximum declared volume size of 65,535 blocks. A 65,536-block host image may contain an unused trailing block, which must be preserved. [ProDOS format notes](https://ciderpress2.com/formatdoc/ProDOS-notes.html)

Use the [ProDOS technical reference](https://prodos8.com/docs/techref/) and relevant technical notes to verify boundary behavior. Exercise the existing CLI operations across ProDOS, then add directory creation, recursive extraction, and explicitly requested recursive deletion. Cover 140 KiB, 800 KiB, and maximum supported volumes; do not infer partitions from arbitrary large images.

Completion criterion: every advertised ProDOS operation works across the supported size matrix, boundary-sized files and directory growth are tested, and an independent ProDOS environment reads files written by A2Utils.

6. Validate interoperability, package, and document releases.

Develop tests alongside each milestone, especially before enabling writes. Required coverage:

| Area | Evidence required |
| --- | --- |
| Layout/container correctness | Known sector mapping vectors; DOS-order → ProDOS-order → DOS-order byte equality; 2IMG offsets, flags, and metadata preservation |
| Filesystem behavior | Known catalogs/hashes; empty and full disks; fragmentation; directory/catalog limits; multi-list and indexed files; sparse data; metadata restoration |
| Failure handling | Truncated structures, cycles, overlapping allocations, out-of-range pointers, ambiguous detection, unsupported storage, and bounded parser fuzzing |
| Mutation integrity | Untouched regions remain unchanged; replace/delete update allocation correctly; pre-commit failures retain source hashes and usable backups |
| Host integration | Path/case collisions, extraction containment, read-only files, existing outputs, cancellation, and concurrent modification |
| CLI behavior | Exit codes, help, JSON schema, binary-safe extraction, and diagnostics kept off stdout |
| Interoperability | Reference images produced independently of our engine; external catalog/file checks; emulator catalog/load smoke tests |

If DiskArc is reused, CiderPress checks alone are not independent validation. Keep documented, redistributable fixtures with provenance and hashes; add independent hand-checked layout vectors and fixtures made by another implementation or an Apple II environment.

Build and test on Windows, Linux, and macOS only when a release version tag is pushed for a commit on main. Package a .NET tool and self-contained downloads for Windows x64, Linux x64, and macOS ARM64, using the tag's version. Test installation and representative commands from the packaged artifacts. After all builds pass, upload archives, the tool package, and SHA-256 checksums to a draft GitHub Release and publish it after upload verification; retain private repository visibility. Document supported format combinations, examples, metadata handling, transaction behavior, and known limitations. Original A2Utils code is GPL-2.0-only. Before public binary distribution, replace or separately license the Apache-2.0 dependencies, or revisit the project license, and preserve every required dependency notice.

Completion criterion: a reproducible release with passing tests, installable artifacts, sample workflows, and no advertised write operation lacking preservation and failure-path coverage.

Future work should be added as separate milestones: DOS 3.2/13-sector disks; other filesystems such as Pascal; NIB and WOZ inspection followed by carefully scoped editing; ShrinkIt and Binary II archives; Integer BASIC import and export; additional graphics formats and hardware-accurate rendering; sector inspection; and explicit filesystem repair/recovery. WOZ captures track-level information beyond sector images, including timing-related details, so it needs a dedicated design rather than another filename handler. [WOZ specification](https://applesaucefdc.com/woz/reference2/)

The first implementation task is the disk-engine evaluation and the `info` → `ls` → `extract` vertical slice on one known DOS 3.3 image. That establishes a useful utility and validates the architecture before enabling writes.
