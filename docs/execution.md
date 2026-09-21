# Automated Apple II execution

For ordered interactions, game inputs, checkpoints, direct routines and CPU cycle
budgets, see [interactive testing](interactive-testing.md). See also
[visual assertions](project-assets.md), [audio capture](audio-execution.md), and
[environment profiles](setup.md). These are optional execution-schema extensions;
existing timed-key specifications remain supported.

`a2 run SPEC --artifacts NEW_DIRECTORY --json` uses the specification's execution
engine and returns observations and assertions. The default `mame` engine boots a
disposable copy of each mounted disk and injects scheduled input. The `cpu` engine
runs one bounded routine in-process without an emulator, ROM, or disk.
`a2 test SUITE --artifacts NEW_DIRECTORY --json` runs up to 128 specifications and
returns an aggregate result. Runs are sequential by default; `--jobs` enables bounded
parallel execution. Every suite case requires an assertion, bounded completion/
debug/routine/cycle condition, or mounted-disk verification. Exit status is 0 on
success, 1 on execution/assertion failure,
2 for invalid input, and 6 for cancellation. An unavailable emulator is a failure.

For `engine: "mame"`, install MAME **0.289** separately and provide its executable and machine ROM directory.
Follow [external dependency setup](setup.md#install-mame-0289) for download/build
instructions, ROM audit commands, OS-template preparation, and a starter smoke test.
The adapter checks the executable version before booting and rejects ROM checksum
warnings. No emulator, ROM, DOS, or ProDOS system files are included in A2Utils.
The current profiles are `apple2`, `apple2p`, `apple2e`, `apple2ee`, and `apple2c`.
The real integration smoke was run with `apple2ee` on MAME 0.289 on Windows x64.
The other profiles share the adapter but have not received that machine smoke test.
The slot-based profiles disable the default serial card in slot 2 and speech card
in slot 4; other slot configuration uses the pinned MAME defaults.

Execution also supports [bounded debugging](runtime-debugging.md): instruction
breakpoints, CPU read/write watchpoints, fixed instruction steps, and recent PC
history. [IIe observations](iie-execution.md) add physical main/auxiliary/language-card
reads, 80-column text, and MouseText cells. These use the same isolated disks and
host/emulated deadlines.

## Specification

Paths resolve relative to the specification. For MAME, `emulatorPath` must be a full
executable path or a relative path to an executable, rather than a PATH command name.
JSON must include `schemaVersion: 1`; unknown and duplicate properties are rejected.
See [the schema](schemas/execution.schema.json) and this example:

```json
{
  "schemaVersion": 1,
  "name": "hello",
  "emulatorPath": "tools/mame.exe",
  "expectedVersion": "0.289",
  "machine": "apple2ee",
  "romDirectory": "roms",
  "diskImage": "build/hello.dsk",
  "emulatedSeconds": 20,
  "hostTimeoutSeconds": 60,
  "keys": [{ "atSeconds": 5, "text": "RUN HELLO\n" }],
  "textContains": ["HELLO APPLE II"],
  "memory": [{ "address": 768, "hex": "2A" }],
  "registers": [{ "name": "A", "value": 42 }],
  "until": { "address": 768, "value": 42, "afterSeconds": 5 },
  "screenshot": true,
  "trace": true
}
```

The specification is strict and case-sensitive: unknown or duplicate properties
are rejected. Specification and suite JSON inputs are capped at 4 MiB with a
maximum nesting depth of 32. `schemaVersion` is always required. The default
`mame` engine requires `emulatorPath`, `machine`, and `romDirectory` (or an
`environment` profile supplying them), plus `diskImage` or a nonempty `disks`
array for standalone machine execution. MAME project tests can omit media; `build
--test` inserts the selected build mount. A CPU project test stays disk-free while
retaining project-symbol binding. The `cpu` engine requires `machine` and `routine`
and refuses machine peripherals and media. Omitted optional
properties use these defaults:

| Property | Default, range, and behavior |
| --- | --- |
| `schemaVersion` | Required integer `1`. |
| `name` | `run`; 1–128 characters, copied into the result. |
| `engine` | `mame`; `mame` for full-system execution or `cpu` for a disk-free routine. |
| `emulatorPath` | Required by MAME: path to its executable. Relative paths resolve from the specification directory. |
| `expectedVersion` | `0.289`; this adapter accepts only that exact version. |
| `machine` | Required: `apple2`, `apple2p`, `apple2e`, `apple2ee`, or `apple2c`. |
| `romDirectory` | Required MAME ROM directory, relative to the specification when not absolute. |
| `diskImage` | Legacy single input image, relative to the specification; maximum 64 MiB. Use this or `disks`. |
| `diskDevice` | `flop1`; `flop1` or `flop2`, plus `hard1`/`hard2` with `storageProfile: "cffa2"`. |
| `disks` | `[]`; one or two explicit mounts with distinct devices and input files. See below. A nonempty array requires `diskImage` to be absent or empty. |
| `diskAssertions` | `[]`; up to 1,024 saved-file assertions, checked after a clean emulator exit. |
| `symbolicMemory`, `symbolicUntil` | Project-build-only symbolic addresses; see [build and test together](projects.md#build-and-test-together). |
| `emulatedSeconds` | `15`; finite value greater than 0 and at most 3,600. |
| `hostTimeoutSeconds` | `60`; independent finite watchdog greater than 0 and at most 3,600. |
| `keys` | `[]`; at most 1,024 `{atSeconds,text}` items. Each time is at least 0 and strictly before the emulated deadline; text is at most 16,384 characters. |
| `memory` | `[]`; at most 1,024 unique start addresses with complete hexadecimal bytes in `hex`. Spaces are allowed; the combined observation is at most 65,536 bytes. |
| `graphicsMemory` | `[]`; up to 16 PNG-backed display-memory assertions. Modes are `lores`, `hires`, `hires-color`, `dhires-mono`, and `dhires-color`; see below. |
| `observeMemory` | `[]`; up to 1,024 `{address,length,bank}` ranges captured without expected values; shares the 65,536-byte budget with assertions. |
| `registers` | `[]`; at most 128 unique `{name,value}` items. Names are uppercase identifiers of 1–16 characters; values are 0–65,535. |
| `textContains` | `[]`; at most 128 nonempty, case-sensitive substrings. |
| `until` | `null`; optional `{address,value,afterSeconds}` completion byte. Address must be observable (`$0000`–`$BFFF` or `$D000`–`$FFFF`), value is 0–255, and `afterSeconds` defaults to 0 and must precede the deadline. |
| `textPage` | `1`; selected text-memory page, `1` or `2`, independently of the visible display. |
| `textColumns` | `40`; `40` or `80`. 80 columns require IIe/IIc and use physical main/auxiliary text memory. |
| `decodeIIeText` | `false`; preserve IIe display attributes and MouseText cells for 40-column text too. Implied by 80 columns. |
| `debug` | `null`; bounded breakpoint/watchpoint/step configuration. Mutually exclusive with `until`/`symbolicUntil`; see [runtime debugging](runtime-debugging.md). |
| `screenshot` | `false`; request `screen.png` and fail if MAME does not create it. |
| `trace` | `false`; request `trace.tsv`. MAME records once-per-frame PC samples; project runs add bounded `trace-source.tsv` when build mappings match. CPU execution records every instruction with opcode, registers, cycles, memory accesses, and routine source columns when available. |

The suite document is also strict. It contains only `schemaVersion: 1` and a
required `tests` array of 1–128 nonempty specification paths. Paths resolve from
the suite file. Before creating the suite artifact directory or launching MAME,
the CLI parses every case and validates its intrinsic specification constraints,
including the requirement for at least one assertion, bounded completion/debug/
routine/cycle condition, or mounted-disk verification.
External path existence and emulator availability are checked per case during
execution, so a later external-resource failure can leave the suite root and
earlier case artifacts in place.

`keys` contains literal keyboard text; `\n` presses Return. Its times, the deadline,
and `until.afterSeconds` are emulated seconds since power-on. Machine boot and disk
loading count toward the deadline. Input is queued through MAME's natural keyboard;
allow time for the emulated program to consume it. Scripted control characters can
be represented as JSON escapes. No arbitrary Lua or shell commands are accepted.

`until` polls one RAM/ROM byte once per emulated frame and exits when it matches.
Initialize that byte in your program and leave the result stable until observation.
This is suited to a persistent completion mailbox, not detecting transient CPU
states or cycle-exact breakpoints. A missing completion byte fails at the emulated
deadline. Without `until` or `debug`, assertions are evaluated at the deadline.
MAME register values use its CPU-state names. The CPU engine exposes PC, A, X, Y,
P, S, and SP; `S` is the stack offset and `SP` includes the 6502 stack page (`$0100`).

Memory assertions compare complete byte sequences. With MAME and the default
`bank: "cpu"`, addresses must stay within `$0000-$BFFF` or `$D000-$FFFF`; reads of
`$C000-$CFFF` are refused because they can change Apple II soft switches. The CPU
engine accepts the full `$0000-$FFFF` flat address space, where I/O addresses have
no device side effects. At most 64 KiB total memory is observed. MAME observations
are the CPU's currently mapped addresses. On IIe/IIc, `bank` can instead name physical
`main`, `aux`, `lc1`, `lc2`, `aux-lc1`, or `aux-lc2` RAM; physical reads do not
touch soft switches. `until` accepts the same bank field. See [bank ranges](iie-execution.md).

## Graphics-memory assertions

`graphicsMemory` compares an expected PNG with the exact Apple II display bytes
captured at the end of a run or at an ordered step condition:

```json
{
  "graphicsMemory": [
    { "expectedImage": "expected.png", "mode": "hires-color", "page": 1, "bank": "main" }
  ]
}
```

`expectedImage` resolves relative to the specification. `page` defaults to 1 and
accepts 1 or 2. `bank` defaults to `cpu`; single-bank modes also accept physical
`main` or `aux` on IIe/IIc machines. Lo-res PNGs are 40 by 48; hi-res modes are
280 by 192. `dhires-mono` expects 560 by 192 and `dhires-color` expects 140 by
192; both require an IIe/IIc and automatically compare auxiliary and main banks,
so omit `bank` for those modes. The in-process CPU engine supports only the flat
`cpu` bank and single-bank modes; physical `main`/`aux` assertions and both
double-hires modes require full-machine MAME execution.

The runner deterministically encodes the PNG, expands it into ordinary bounded
memory observations, and refuses overlapping or over-budget ranges through the
same memory-validation path. A specification accepts at most 16 assertions; each
step condition accepts at most 8. Results include the expected image hash, byte
mismatch count and first address, pixel comparison where possible, and paths to
expected, actual, and difference PNG artifacts. Step results retain the checkpoint
index and name. The input PNG is rehashed before the comparison is accepted.

Text assertions decode a selected **40-column text memory page**, default page 1;
`textPage: 2` selects page 2. Text is case-sensitive and display attributes are
discarded. This does not establish that text mode/page is currently visible, decode
graphics or perform screenshot OCR. Opt into `textColumns: 80` or `decodeIIeText`
for physical text-page decoding with character attributes and MouseText tokens.
Use `screen.png` to inspect the rendered display. With MAME, `trace.tsv` is a
once-per-frame PC sample, not an instruction trace or cycle profiler. The CPU
engine's trace is instruction-level evidence; it has no rendered display. Trace
files are capped at 16 MiB. Missing source maps leave the source columns empty or,
for the project-derived MAME trace, omit `trace-source.tsv` entirely.

## Multiple disks and saved-file assertions

Explicit mounts let an OS boot in drive 1 while a program reads/writes a data
disk in drive 2. For example, the following fragment mounts both disks and checks
a saved binary file after the machine exits:

```json
{
  "disks": [
    { "device": "flop1", "image": "dos33-boot.do" },
    { "device": "flop2", "image": "data.do", "inputFileSystem": "dos33", "verify": true }
  ],
  "diskAssertions": [
    { "device": "flop2", "path": "RESULT", "hex": "2A", "type": "B", "auxType": 768, "length": 1 },
    { "device": "flop2", "path": "TEMP", "exists": false }
  ]
}
```

Each mount requires `device` and `image`. Floppy mounts use `flop1`/`flop2`; the
[explicit CFFA2 profile](block-storage-execution.md) adds `hard1`/`hard2`. Optional
`expectedSha256` pins the whole input image; `inputOrder` (`dos`/`prodos`) and
`inputFileSystem` (`dos33`/`prodos`) disambiguate post-run inspection. `verify: true`
requires a supported filesystem without dubious state or error diagnostics after
execution; warnings are retained in the report. Each input is limited to
64 MiB and receives its own isolated copy. Device collisions, linked paths,
detectable source aliases, and invalid hashes are refused. The explicit CFFA2
single-volume profile is the supported block-device configuration; other
controllers and block-device profiles remain outside this milestone.

Each disk assertion requires a mounted `device` and an image name or `path` of
1–4,096 characters without control characters. Names are resolved by the image's
filesystem: DOS catalog names are literal and case-sensitive, including slashes
and dot segments; ProDOS uses its usual image path rules. These are not host paths.
`exists` defaults to `true`. Optional fields are `hex` **or** `sha256` for logical
file contents, `type`, `auxType` (0–65,535), and logical `length` (0–32 MiB).
DOS binary headers are excluded from payload comparisons; the load address is
checked through `auxType`. Type aliases use the selected filesystem's rules.
Expected hex payloads are bounded to a combined 1 MiB. Identical assertion path
strings cannot repeat on a device, and `exists: false` cannot include payload or metadata expectations.
Image originals are never used as writable emulator media.

The result's `disks` array records each device, original path, isolated artifact
path, input SHA-256, and output SHA-256 when available. A saved-file mismatch is a
behavioral test failure even if all screen/memory assertions pass. Failed starts,
cancellation, and host timeouts keep available disk evidence but do not establish
that post-run filesystem assertions passed.

## Suites, results, and file protection

```json
{"schemaVersion":1,"tests":["hello.execution.json","disk-write.execution.json"]}
```

Suite paths resolve relative to the suite. Cases receive separate `case-001`,
`case-002`, etc. artifact directories based on their positions in the original suite.
Those identities do not change when cases are selected. The suite parses every case
before execution; `suite-result.json` records every selected case and its status.
It also reports explicit `planned`, `completed`, `passed`, `failed`, `cancelled`, and
`notRun` counts. Cancellation terminates active emulator process trees, prevents new
cases from starting, and records selected cases that did not start as `not-run`.

Use `--list` to inspect stable case identities without creating artifacts or starting
an emulator. One or more `--filter` values select cases by name or suite-relative path:

```sh
a2 test tests.execution-suite.json --list --filter "name:save*" --json
a2 test tests.execution-suite.json --artifacts artifacts/tests --filter "path:disk/*" --jobs 4 --progress --json
```

Filters are case-insensitive globs; `*` and `?` are supported, and a value without a
wildcard is a substring match. Prefix a value with `name:` or `path:` to restrict the
field, or omit the prefix to match either field. Multiple filters are combined with OR.
Selection happens after every specification is parsed and intrinsically validated.

`--rerun-failed PREVIOUS` reads `suite-result.json` from a previous artifact directory
(or accepts that file directly) and selects its failed cases. It can be combined with
filters. Case paths and names are matched, so reordering the suite does not silently
select a different case. `--run-subdirectory` treats `--artifacts` as a parent and
creates a unique timestamped run directory below it.
Without that option, `--artifacts` retains its existing contract and must name a new
path. The result's `artifactDirectory` identifies the directory actually used.

`--progress` writes newline-delimited JSON events to `events.jsonl` in the run
directory. Events have a monotonic sequence number and cover suite start, case start,
case completion, and suite completion or abort. The file is flushed after every event
so an agent can observe a long run while `suite-result.json` remains the final durable
summary. `--jobs` accepts 1 through 16. Results and numbered case directories remain in
suite order even when completion events arrive out of order.

Every run records its resolved specification, observations, routine evidence when
present, and `result.json`. MAME runs additionally retain the argument array,
version/stdout/stderr, generated Lua, optional PNG and PC samples, isolated disk
copies, hashes, machine config, NVRAM, and write-difference files. CPU runs retain
`engine.json` and, when requested, an instruction trace. Each actual run and case
directory is newly created; `--run-subdirectory` may use an existing suite parent.
A host watchdog bounds both engines.

Results distinguish `routine_return`, `cycle_limit`, `cpu_fault`,
`completion_condition`, `emulated_limit`, `host_timeout`, `cancelled`,
`version_mismatch`, `rom_mismatch`, `emulator_unavailable`, `emulator_error`,
`adapter_error`, `missing_observations`, `disk_hash_mismatch`, and
`disk_copy_mismatch`. Failed comparisons
include stable diagnostic codes with expected and actual values. Assertions can
fail even when the machine itself reaches its requested stop condition.

## Reproduce the self-booting program smoke

The original [boot.asm](../examples/execution/boot.asm) is a one-sector boot program
that writes `A2 PASS`, waits for `X`, stores `$2A,$D8` at `$0300`, and loops. Its
disk requires no DOS or ProDOS system files. Run from a restored checkout:

```powershell
pwsh examples/execution/Prepare-BootSmoke.ps1 -MamePath C:/tools/mame/mame.exe -RomDirectory C:/tools/mame/roms -OutputDirectory artifacts/my-boot-smoke
dotnet run --project src/A2Utils.Cli -c Release --no-restore -- run artifacts/my-boot-smoke/boot.execution.json --artifacts artifacts/my-boot-smoke/run --json
```

Or run the optional integration test with the same installed assets:

```powershell
$env:A2_MAME_PATH = 'C:/tools/mame/mame.exe'
$env:A2_MAME_ROMS = 'C:/tools/mame/roms'
dotnet test tests/A2Utils.Core.Tests -c Release --filter FullyQualifiedName~MameSmokeTests
```

Without those environment variables that test is explicitly skipped. The ordinary
`ExecutionTests` use a named process-contract test double; they test cancellation,
process-tree cleanup, artifact isolation, errors, and assertions without claiming
to emulate an Apple II.

The optional `DiskOperatingSystemSmokeTests` separately checks a real OS catalog,
then loads and calls original assembly and saves a known binary result. Set
`A2_DOS33_SMOKE_DISK` or `A2_PRODOS_SMOKE_DISK` as well as the MAME variables, using
a local 140 KiB bootable image that reaches an Applesoft/BASIC.SYSTEM prompt.
Template catalog/space requirements and provenance are documented in the
[fixture guide](../tests/TestData/README.md#optional-real-dos-and-prodos-interoperability).
Missing resources skip these tests; passing the ordinary suite does not establish
that either operating-system check ran.

For the validation run, MAME's official portable Windows release was verified
against its published SHA-256 (`a1aa7912168c9d1b05e611906bc21b8b9be3935822aead36d12a1da363150b7d`).
System/character and Disk II boot bytes came from the [AppleWin resource tree](https://github.com/AppleWin/AppleWin/tree/master/resource).
The keyboard decoder came from the [Apple II Documentation Project ROM archive](https://mirrors.apple2.org.za/Apple%20II%20Documentation%20Project/Computers/Apple%20II/Apple%20IIe/ROM%20Images/).
The Disk II sequencer table came from [Apple2js](https://github.com/whscullin/apple2js/blob/main/js/cards/disk2.ts),
reordered to the physical ROM address lines described in [MAME's pinned controller source](https://github.com/mamedev/mame/blob/mame0289/src/devices/machine/wozfdc.cpp).
All six ROM files then matched the MAME 0.289 expected SHA-1 values:

| File | SHA-1 |
| --- | --- |
| `342-0265-a.chr` | `b2b5d87f52693817fc747df087a4aa1ddcdb1f10` |
| `342-0304-a.e10` | `3aecc56a26134df51e65e17f33ae80c1f1ac93e6` |
| `342-0303-a.e8` | `afb09bb96038232dc757d40c0605623cae38088e` |
| `341-0132-d.e12` | `8e14e85c645187504ec9d162b3ea614a0c421d32` |
| `341-0027-a.p5` | `d4181c9f046aafc3fb326b381baac809d9e38d16` |
| `341-0028-a.rom` | `bc39fbd5b9a8d2287ac5d0a42e639fc4d3c2f9d4` |

Obtain machine ROMs separately and use MAME's ROM audit to verify them. These assets
are not copied into the repository's examples or packages.

## Build and boot a ProDOS application

The second verified machine workflow uses the [official ProDOS 2.4.3 distribution](https://prodos8.com/releases/prodos-243/)
([disk download](https://releases.prodos8.com/ProDOS_2_4_3.po)). Its downloaded SHA-256 was
`398d333cb2ab92df9f8bb2cf64b946f2567116910eb8359cf4bdee5d4194f0fa`.
Store it as `artifacts/os-validation/ProDOS_2_4_3.po`. The distribution boots its menu
through `QUIT.SYSTEM`. Create a separate template with that file removed so the
existing `BASIC.SYSTEM` boots and runs `STARTUP`:

```sh
a2 disk delete artifacts/os-validation/ProDOS_2_4_3.po QUIT.SYSTEM --output artifacts/os-validation/basic-template.po
a2 build examples/execution/prodos-project.json --json
a2 run examples/execution/prodos.execution.json --artifacts artifacts/os-validation/hello-run --json
```

Adjust the emulator and ROM paths in the execution specification for your local
installation. The example project compiles `hello.bas` and `hello.asm`, adds them to
the template, and generates a BASIC `STARTUP` launcher. After ProDOS boots, BASIC
prints `HELLO FROM A2UTILS` and `BRUN HELLOASM` invokes the assembled routine at
`$2000`; the routine writes `$2A` at `$0300`. This workflow passed on the verified
MAME configuration, reaching its completion condition after about 13.3 emulated
seconds. The downloaded distribution and the derived template remained unchanged
by the project build and emulator run. To pin your derived template across machines,
set `disk.templateSha256` in the project to that template's SHA-256.

The project target is `apple2enh`; the corresponding MAME machine name is `apple2ee`.
The project builder still labels a copied boot template as unverified until an
execution test establishes the intended boot behavior.
