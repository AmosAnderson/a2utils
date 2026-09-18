# Building Apple II projects

Projects can also declare [runtime memory and overlay groups](runtime-memory.md),
[generated graphics assets](project-assets.md), and [environment/toolchain locks](setup.md).
Execution suites accept the [ordered interaction and cycle options](interactive-testing.md).

`a2 build PROJECT --json` compiles a versioned manifest, checks source and load
metadata, and commits one validated disk image. Paths inside the manifest are
relative to the manifest's directory. `--to PATH` overrides the output relative
to the current directory. Output parents are created after source checks pass.

```json
{
  "schemaVersion": 1,
  "target": "apple2e",
  "output": "out/demo.po",
  "disk": { "fileSystem": "prodos", "volumeName": "DEMO" },
  "basicWorkspaceBytes": 1024,
  "files": [
    { "source": "main.asm", "path": "ROUTINE", "kind": "asm" },
    { "source": "hello.bas", "path": "HELLO", "kind": "basic" },
    { "source": "readme.txt", "path": "README", "kind": "text" }
  ]
}
```

```sh
a2 build examples/development/mixed.a2.json --to artifacts/mixed.po --json
a2 build examples/development/hires.a2.json --check --json
a2 build examples/development/mixed.a2.json --preflight --json
a2 targets --json
a2 capabilities --json
a2 schema project --json
```

## Resolve or import a project

`a2 project resolve PROJECT --json` (alias `project inspect`) applies manifest
defaults and an optional environment profile, validates locks and target/CPU
compatibility, compiles source checks, and returns the effective output/disk
settings, absolute source paths, hashed direct and transitive inputs, external
tool requirements, memory ranges, and a disk-entry plan. It does not create the
declared output image or its parent directory. Like `build --check`, resolving a
cc65 project may run the configured compiler in temporary staging.
Execution suites are inspected by engine, so an all-CPU suite does not report
MAME as a required tool. MAME cases report separate `mame` executable and
`mame-roms` directory requirements, including availability and a path/version
when every case resolves to one common value. Resolution hashes the suite and
every case specification, case environment and lock, routine source/includes,
expected screenshot/display images, and auxiliary disk mounts. The future build
mount is omitted because the project output replaces it during `build --test`;
MAME itself is never started.
For a boot project, `boot` reports the resolved source, kind, origin, sector count,
payload length/hash, and planned `write-sectors` action. `diskPlan` includes the
payload in `compiledPayloadBytes` and reports the complete track-zero mutation in
`bootSectorBytes`.

```sh
a2 project inspect examples/development/mixed.a2.json --json
```

`a2 project import IMAGE --to NEW_DIRECTORY [--disassemble] [--target TARGET]`
adopts an existing supported image without changing it. The new directory
contains a hash-pinned template copy, an editable strict manifest, exported
sources, an import report, and locked reference entries that remain preserved by
the template. `--disassemble` converts load-addressed BIN files to reassemblable
source; without it their payload bytes remain binary. Input layout/filesystem
overrides use the global `--input-order` and `--input-fs` options.

## Bare-metal boot projects

```sh
a2 init my-boot-project --language asm --bare-metal
```

The bare-metal starter creates original assembly at `$0800`, a 140 KiB DOS 3.3
DOS-order raw project, a one-sector `boot` declaration, and an execution test.
It needs no operating-system template. The generated environment still needs a
local MAME/ROM setup to run that full-machine test; no emulator, ROM, or Apple
software is bundled. Bare-metal initialization currently accepts only `asm` and
requires a new destination directory.

A manifest can declare `boot` directly with `source`, `kind` (`asm` or `binary`),
`origin` (exactly 2048 / `$0800`), and `sectors` (1 through 16). Boot sectors
require a 280-block DOS 3.3 image in DOS order. The emitted boot payload must fit the declared
sector capacity; assembly includes and binary inputs are hashed like other build
inputs. The builder writes these original bytes into track-zero sectors and
verifies them after reopening the staged image. It reports
`self-booting-unverified`; use an execution test to establish actual boot behavior.

Use `--overwrite` to replace a previous output. Every build starts from a fresh
data volume or the original template. It does not incrementally modify the
previous output. `--check` compiles and checks metadata/memory without producing
an image; it is not a disk-allocation or bootability test. cc65 checks still run
the compiler in temporary staging. The JSON Schema is bundled in the executable
and also available at [project.schema.json](schemas/project.schema.json).

`--preflight` performs the complete build in a disposable image, including actual
allocation, name validation, template replacement rules, and stored-file checks.
It returns the would-be image hash and a `plan` with file additions/replacements,
created directories, image size, and free bytes before/after. It creates no output
parent directories and leaves existing outputs and templates unchanged. Output
overwrite/read-only rules still apply, so pass `--overwrite` when previewing a
replacement. Preflight validates the image contents; it does not test host free
space, power-loss behavior, or bootability. The existing fast `--check` has no plan
or image hash. Choose only one of `--check`, `--preflight`, or `--test`.

Normal CLI builds can opt into a content-addressed image cache with
`a2 build PROJECT --cache DIRECTORY`. Library callers can use
`ProjectBuildCache.Build(manifestPath, cacheDirectory, outputPath, overwrite)`.
The cache accepts a hit only when all recorded input hashes and the tool version
match, validates the cached image before a transactional restore, and reports the
hit in `ProjectBuildResult.CacheHit` and the CLI's `cacheHit` JSON field. Keep
outputs outside the cache directory. `--cache` conflicts with `--check`,
`--preflight`, and `--test`; builds without it do not read or populate the cache.
Each normalized manifest path retains at most 1,024 metadata entries, each capped
at 8 MiB; images are capped at the normal 34 MiB project-image limit, and cache
validation considers at most 8,192 inputs of at most 64 MiB each. At the
same time, their combined bytes are capped at 512 MiB. At the entry/metadata or
input limits, or during concurrent writer contention, the requested build
still succeeds but is reported uncached. A cache with more than the supported
entry count is refused for review instead of being pruned automatically.

Project JSON is strict and case-sensitive: unknown or duplicate properties are
errors. `schemaVersion` is required. `files` can be empty only when an explicit
disk template or `boot` declaration supplies the preserved/bare-metal image;
omitted optional properties use the defaults below. The manifest is limited to 1 MiB
and a JSON nesting depth of 32. It may contain at most 1,024 files and 1,024
explicit reservations. Each source is capped at 4 MiB, a template at 34 MiB,
and combined compiled payloads at 34 MiB. All addresses and block counts in JSON
are decimal numbers (8192 is `$2000`).

### Manifest properties

| Top-level property | Default and meaning |
| --- | --- |
| `schemaVersion` | Required; must be `1`. |
| `target` | `apple2e`; one of `apple2plus`, `apple2e`, `apple2enh`, or `apple2c`. |
| `cpu` | Target default; optional `6502` or `65c02`. A 65C02 CPU requires an enhanced target. |
| `output` | `build.po`; output path relative to the manifest, unless CLI `--to` overrides it. |
| `timestamp` | `2000-01-01T00:00:00`; reproducible file/directory/volume date in 1980–2039 at whole-minute precision. Use UTC or no offset, never a local offset. |
| `disk` | Defaults to a 140 KiB ProDOS raw data volume as described below. |
| `files` | Array of up to 1,024 source entries, processed in order. At least one is required unless `disk.template` or `boot` is present. |
| `reserve` | Empty array of additional named resident-memory ranges. |
| `checkMemory` | `true`; set `false` only when the program deliberately manages otherwise overlapping ranges. |
| `basicWorkspaceBytes` | `0`; extra bytes reserved immediately after each resident BASIC payload, range 0–65,536. |
| `startup` | `null`; optional generated BASIC launcher for a supplied OS template. |
| `boot` | `null`; optional original `asm`/`binary` track-zero boot payload at `$0800`, spanning 1 through 16 sectors on a 140 KiB DOS-order DOS 3.3 image. |
| `cc65` | `null`; required when any file has `kind: "cc65"`. |
| `execution` | `null`; optional `{ "suite": "tests/suite.json", "diskDevice": "flop1" }` used by `build --test`. Suite paths resolve relative to the manifest; the build device may be `flop1`/`flop2`, or `hard1`/`hard2` with an execution CFFA2 profile. |

| `disk` property | Default and meaning |
| --- | --- |
| `fileSystem` | `prodos`; `prodos` or `dos33`. |
| `template` | `null`; existing image path relative to the manifest. When present, its container, size, volume metadata, boot data, and untouched files are retained. |
| `templateSha256` | `null`; optional 64-digit hexadecimal hash that must match the template before work begins. |
| `blocks` | `280`; new-volume size in 512-byte blocks. ProDOS accepts 280–65,535; DOS requires 280. |
| `container` | `raw`; `raw` or sector-data `2mg` for a new volume. A template keeps its existing container. |
| `order` | Filesystem default (`dos` for DOS, `prodos` for ProDOS); explicit `dos` or `prodos`. Larger new images require ProDOS order. With a template, this is an input-layout override and must match. |
| `volumeName` | `A2PROJECT`; name used when formatting a new ProDOS volume. |
| `volumeNumber` | `254`; DOS volume number 0–254 for a new DOS volume. |

Every `files` item requires `source` (host path relative to the manifest) and
`path` (relative image path using `/`). Empty components, `.`, `..`, a leading
slash, and backslashes are refused. File extensions do not select `kind`.

| File property | Default and meaning |
| --- | --- |
| `kind` | `binary`; one of `asm`, `basic`, `basic-labels`, `binary`, `text`, `applesingle`, `cc65`, `lores`, `hires`, or `hires-color`. |
| `type` | Inferred: BAS for BASIC, TXT for text, otherwise BIN. It must agree with kinds or wrapper metadata that constrain the type. |
| `origin` | `null`; load origin for assembled/BASIC/raw programs. Assembly can supply `.org`; BASIC defaults to 2049 (`$0801`); graphics use 1024 or 8192. |
| `auxType` | `null`; explicit ProDOS auxiliary value (or equivalent DOS load metadata). When a payload has an origin, the two values must agree. |
| `entryPoint` | Origin by default; when explicit, it must fall inside the payload. It is reported metadata and does not relocate code. |
| `replace` | `false`; authorize replacement of the exact entry in a template. It is unrelated to host `--overwrite`. |
| `resident` | `true`; include the load range in simultaneous-memory checks. |
| `memoryBank` | `main`; explicit destination RAM bank. IIe/IIc also support `aux`, `lc1`, `lc2`, `aux-lc1`, `aux-lc2`; requires an application loader. See [banked projects](iie-memory.md). |
| `checkBasic` | `true`; run the conservative source checker for `basic`. Tokenizer validation always runs, and `basic-labels` is always prepared and checked. |

The optional `startup` object requires `program`, the image path of one manifest
file. `path` defaults to `HELLO` and names the launcher entry; `replace` defaults
to `false`. The optional `cc65` object defaults to compiler `cl65`, target
`apple2`, no expected-version pin, a 60-second timeout, optimization enabled,
and empty `additionalSources`, `includes`, and `defines` arrays. Each list accepts
at most 128 entries. See [C and ca65](cc65.md) for path and isolation rules.

## Build and test together

Add `execution` to a project, then run:

```sh
a2 build project.a2.json --test --artifacts evidence/run-001 --json
```

`--artifacts` is required with `--test` and must be outside the build output and
inputs. It must name a new directory unless `--run-subdirectory` treats it as an
existing parent for a unique child run. A normal build never starts an emulator.
The configured suite uses the [execution specification](execution.md), with paths
relative to each test file. For a MAME case, the chosen `execution.diskDevice` is
replaced or added with the newly built image; other mounts retain their configured
OS/data disks. The runner pins every mounted image to its input hash and creates
isolated copies for
each case. Any image path or hash on the selected build mount is replaced by the
build output. If no additional disks are needed, omit `diskImage` and `disks`; the
workflow inserts only the selected build mount. A CPU-engine case remains disk-free,
but can still use `symbolicMemory` resolved from the project build. Standalone MAME
`run`/`test` requires media; the CPU engine does not.

Project test execution uses the same suite selection and scheduling model as
`a2 test`: filters match stable case names or suite-relative paths, `jobs` bounds
parallel cases, a prior `suite-result.json` can select failed cases, progress can
be written as JSON Lines, and a unique child run directory can be created under
an artifact parent.
These controls are exposed by the project workflow API and by the corresponding
`build --test` command options. Selection never renumbers case evidence: selecting the
second suite entry still writes `case-002`. Build and execution input hashes remain
pinned before any selected case starts. Every suite case is build-bound and
engine-validated before selection, so a filter cannot hide an invalid case.

The workflow first performs full preflight, binds symbols, checks test inputs,
and then builds again. Source hashes, test inputs, and the would-be image hash
must still match before replacement. A nonreproducible compiler output is refused.
After a valid image is committed, failed tests retain it and their evidence;
an artifact I/O failure after commit can also leave the validated image in place.

Each source test must explicitly configure an assertion, a bounded completion/
debug/routine/cycle condition, or mounted-disk `verify: true`. Automatic
verification on an added build mount does not qualify an otherwise
assertion-free test. Emulator executable hashes are streamed without the
disk-image size cap; configuration and disk inputs retain their documented limits.

Build targets must match the test machine: `apple2plus` → `apple2p`, `apple2e` →
`apple2e`, `apple2enh` → `apple2ee`, and `apple2c` → `apple2c`. A data volume still
needs an OS on another mounted disk, or use an existing bootable template.

For native assembly, a test can use an exported label or constant instead of a
numeric address:

```json
{
  "symbolicMemory": [{ "program": "MAIN", "symbol": "RESULT", "hex": "2A" }],
  "symbolicUntil": { "program": "MAIN", "symbol": "RESULT", "value": 42, "afterSeconds": 8 }
}
```

This is a fragment of an execution specification. `program` is the file's image
path. Symbol names follow the assembler's case-insensitive matching; `offset`
defaults to zero and the resolved address must fit the 16-bit observable range.
Use either `until` or `symbolicUntil`. Ordinary `run`/`test` refuse unresolved
symbolic assertions; `build --test` resolves them using this build's symbols.
cc65 VICE labels provide exported build symbols. Its ld65 debug file also provides
typed source ranges for project files when the compiler emits line/span records.

Breakpoint/watchpoint addresses in `debug` also accept `program`, `symbol`, and
`offset`. Symbolic memory assertions/completion inherit a banked file's declared
bank unless `bank` is explicit; a main-memory file retains the legacy `cpu` default.
Debug points always use CPU addresses. Source-location evidence includes the
declared bank and all matching resident programs when a PC is ambiguous.

Evidence includes `build.json`, execution input hashes, the resolved per-case
specifications, `suite-result.json`, and `project-result.json`. Each case also
receives `source-locations.json`, mapping final and sampled PCs to resident native
assembly or cc65 source when available. A MAME run with trace samples and at least
one mapping also receives bounded `trace-source.tsv`, which retains each sampled
row and adds program, bank, file, line, and source columns. Multiple matches are
retained in `source-locations.json` for overlapping regions. These are source
annotations of observations, not instruction traces.
Traced CPU project runs add each unique executed routine address directly from
the CPU trace's source columns, labeled with the routine source path and `cpu`
memory bank; they do not claim that the separately built disk image was executed.
Symbolic memory failures include their program/symbol name and a source location
when the address maps to emitted assembly. Exit status is 0 for passing tests,
1 for behavioral failure, and 6 for execution cancellation.

The [project test example](../examples/development/project-tests/README.md) shows
a separate boot disk and an assembled data disk. MAME, ROMs, and OS disks must be
provided locally.

## Sources and metadata

| Kind | Input and disk behavior |
| --- | --- |
| `asm` | Native assembly; includes/incbin supported. Origin comes from assembly and must agree with explicit `origin` or `auxType`. Type BIN. |
| `basic` | Numbered Applesoft, checked then tokenized. Default origin `$0801`, type BAS. `checkBasic: false` skips the optional checker, retaining tokenizer validation. |
| `basic-labels` | Unnumbered Applesoft with `@labels`, prepared and checked before tokenization; mappings retain original source lines. |
| `binary` | Raw payload. BIN/BAS requires an explicit `origin` or `auxType`; SYS loads at `$2000`. Other data types can omit a load address. |
| `text` | UTF-8 Apple text conversion for the selected filesystem. Type TXT. |
| `applesingle` | AppleSingle v2 data fork and ProDOS type/auxiliary metadata. Nonempty resource forks are refused. |
| `cc65` | C/ca65 source compiled with the optional [cc65 adapter](cc65.md); its AppleSingle metadata follows the payload. |
| `lores`, `hires`, `hires-color` | PNG converted to a raw display page; default address `$0400` for lo-res and `$2000` for hi-res, type BIN. |

`path` is the intended image path. ProDOS parents are created as needed. Each
file can set `replace: true` to replace that entry in a template; locked entries
remain protected. Replacing a different file type recreates the entry with the
new representation. Host `--overwrite` and image-entry `replace` are separate.
The builder verifies payload bytes, file type, and applicable load metadata
after reopening the staged image. AppleSingle program import does not restore
all archive attributes, dates, comments, or arbitrary wrapper entries.

`entryPoint` defaults to the payload origin and, when supplied, must lie within
the payload. It is reported for runners/loaders; it does not relocate code or
change what DOS BRUN executes. File extensions do not select formats.

## Targets and memory

Targets are `apple2plus`, `apple2e`, `apple2enh`, and `apple2c`. The first two
default to 6502; the latter two to Apple-compatible 65C02. A 6502 project may
run on an enhanced target. WDC-only instructions are refused by these machine
profiles. `a2 targets --json` includes platform symbols and runtime reservations.

The checker compares resident payload ranges, optional BASIC workspace, and
explicit reservations. For DOS it conservatively reserves `$9600..$FFFF`; for
ProDOS it reserves `$BF00..$FFFF`. It also reserves zero page/stack/system
workspace and display page 1. A lo-res asset can intentionally occupy that
display page. The DOS limit is a conservative development policy, not a claim
that every DOS configuration allocates those exact bytes.

```json
{
  "reserve": [{ "name": "HGR page", "start": 8192, "length": 8192 }],
  "basicWorkspaceBytes": 1024
}
```

Use `resident: false` for files that are not loaded simultaneously, such as
overlays or alternative programs. `checkMemory: false` explicitly disables
overlap checking for layouts managed by the application. The checker does not
infer dynamic allocations, prove stack safety, execute bank switching, or prove
C runtime safety: linker maps report additional BSS/stack/zero-page regions that
need application review. Double-hires assets need an explicit auxiliary/main
bank loader and are handled by the standalone graphics commands.

Physical bank ranges and language-card aliases are now checked explicitly; see
[IIe project memory](iie-memory.md). `reserve` entries accept `memoryBank` too.
These declarations describe residency after loading; they do not change where
DOS/ProDOS loads a file. Generated startup must launch a main-memory loader.

Shared constants and executable projects are under
[examples/development](../examples/development/README.md). ROM calls can alter
registers and flags; use the cited machine manuals for complete contracts.

## Templates and startup

New disks without a `boot` declaration are formatted data volumes without an
operating system. Use `boot` for original bare-metal track-zero sectors, or set
`disk.template` to a known bootable disk of the same filesystem to retain boot
code and system files. `templateSha256` can pin its exact contents. Builds copy
and validate the template, preserving the original. The result reports
`template-preserved-unverified` until a separate execution test establishes
boot behavior.

An optional `startup` object writes a BASIC launcher:

```json
{
  "disk": { "fileSystem": "prodos", "template": "system.po" },
  "startup": { "path": "STARTUP", "program": "ROUTINE", "replace": true }
}
```

The template must already boot the named launcher. ProDOS BASIC launchers need
BASIC.SYSTEM. DOS templates need the appropriate boot greeting name. Launchers
use RUN for BASIC and BRUN for BIN; they do not install an operating system or
change boot-sector settings. Use [execution tests](execution.md) to validate
the intended machine and startup path.

## Repeatability and failures

The default timestamp is `2000-01-01T00:00:00`. Override it with an offset-free
or UTC time between 1980 and 2039 at whole-minute precision. New file/directory
and volume timestamps are normalized; template files not replaced are retained.
Identical native sources, options, and template produce byte-identical images.

JSON reports include the output SHA-256, source/dependency hashes, tool version,
CPU/target, per-file hashes and addresses, symbols/source maps, and resident
memory regions. A check-only build writes no image and reports `sha256: ""`.
Native assembly hashes include included source and binary files.
Inputs are rechecked before commit. cc65 additionally depends on its external
distribution and environment; see its reproducibility limits in [cc65.md](cc65.md).

Any compile, validation, capacity, lock, or concurrent-input failure leaves the
existing output and template intact. Source/output aliases and linked host
paths are refused. Intermediate payloads remain in memory; the complete image
uses the existing staged transaction layer.
