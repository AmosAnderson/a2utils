# Automated Apple II execution

`a2 run SPEC --artifacts NEW_DIRECTORY --json` launches MAME, boots a disposable copy
of a disk, injects scheduled keystrokes, and returns observations and assertions.
`a2 test SUITE --artifacts NEW_DIRECTORY --json` runs up to 128 specifications in
sequence and returns an aggregate result. Every suite case requires an assertion or
completion condition. Exit status is 0 on success, 1 on execution/assertion failure,
2 for invalid input, and 6 for cancellation. An unavailable emulator is a failure.

Install MAME **0.289** separately and provide its executable and machine ROM directory.
The adapter checks the executable version before booting and rejects ROM checksum
warnings. No emulator, ROM, DOS, or ProDOS system files are included in A2Utils.
The current profiles are `apple2`, `apple2p`, `apple2e`, `apple2ee`, and `apple2c`.
The real integration smoke was run with `apple2ee` on MAME 0.289 on Windows x64.
The other profiles share the adapter but have not received that machine smoke test.
The slot-based profiles disable the default serial card in slot 2 and speech card
in slot 4; other slot configuration uses the pinned MAME defaults.

## Specification

Paths resolve relative to the specification, including `emulatorPath`; use a full
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

`keys` contains literal keyboard text; `\n` presses Return. Its times, the deadline,
and `until.afterSeconds` are emulated seconds since power-on. Machine boot and disk
loading count toward the deadline. Input is queued through MAME's natural keyboard;
allow time for the emulated program to consume it. Scripted control characters can
be represented as JSON escapes. No arbitrary Lua or shell commands are accepted.

`until` polls one RAM/ROM byte once per emulated frame and exits when it matches.
Initialize that byte in your program and leave the result stable until observation.
This is suited to a persistent completion mailbox, not detecting transient CPU
states or cycle-exact breakpoints. A missing completion byte fails at the emulated
deadline. Without `until`, assertions are evaluated at the deadline. Register values
are MAME state values: for example, `SP` includes the 6502 stack page (`$0100`).

Memory assertions compare complete byte sequences. Addresses must stay within
`$0000-$BFFF` or `$D000-$FFFF`; reads of `$C000-$CFFF` are refused because they can
change Apple II soft switches. At most 64 KiB total memory is observed. These are
the CPU's currently mapped addresses, not a bank-independent RAM dump.

Text assertions decode a selected **40-column text memory page**, default page 1;
`textPage: 2` selects page 2. Text is case-sensitive and display attributes are
discarded. This does not establish that text mode/page is currently visible, decode
80-column auxiliary memory, MouseText, graphics, or perform screenshot OCR.
Use `screen.png` to inspect the rendered display. `trace.tsv` is a once-per-frame
PC sample, not an instruction trace or cycle profiler.

## Suites, results, and file protection

```json
{"schemaVersion":1,"tests":["hello.execution.json","disk-write.execution.json"]}
```

Suite paths resolve relative to the suite. Cases receive separate `case-001`,
`case-002`, etc. artifact directories. The suite parses every case before execution;
`suite-result.json` records the completed cases. Cancellation terminates the active
emulator process tree and stops the suite.

Each run records the resolved spec and argument array, MAME version/stdout/stderr,
SHA-256 of the input image, generated Lua, observations, screen text, optional PNG
and PC trace, the emulated disk copy, and `result.json`. Machine config, NVRAM and
write-difference files are isolated in the new artifact directory; host configuration
is not read. Existing artifact directories are refused. Images are limited to
64 MiB. Emulator logs are drained to avoid pipe deadlocks and retained up to 4 MiB
per stream. A separate host watchdog also covers startup and the version probe.

Machine results distinguish `completion_condition`, `emulated_limit`, `host_timeout`,
`cancelled`, `version_mismatch`, `rom_mismatch`, `emulator_unavailable`,
`emulator_error`, `adapter_error`, and `missing_observations`. Failed comparisons
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
