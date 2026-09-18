# Interactive tests and routine execution

Execution schema version 1 now accepts ordered `steps`. Existing timestamped
`keys` remain supported; use one scheduling style per run. All conditions in a
step must match. Waits poll once per emulated video frame, so use debugger points
or cycle measurements when instruction timing matters.

```json
{
  "schemaVersion": 1,
  "environment": "environment.json",
  "diskImage": "program.po",
  "emulatedSeconds": 20,
  "steps": [
    { "name": "menu ready", "action": "wait", "timeoutSeconds": 8,
      "condition": { "textContains": ["SELECT OPTION"] } },
    { "name": "choose", "action": "keys", "text": "1\n" },
    { "name": "answer ready", "action": "wait", "timeoutSeconds": 4,
      "condition": { "memory": [{ "address": 768, "hex": "2A" }] } },
    { "name": "verify answer", "action": "assert",
      "condition": { "textContains": ["DONE"], "textNotContains": ["ERROR"] } }
  ]
}
```

Actions are `wait`, `assert`, `keys`, `delay` (`seconds`), `input`, and `capture`.
Wait/assert conditions support `memory`, `memoryNotEqual`, `registers`,
`textContains`, and `textNotContains`. In project tests, `symbolicMemory` also
accepts program/symbol/offset/bank references. Memory uses the same physical bank
names as final assertions. Text uses the selected page, 40/80 columns, and IIe
character decoding. Substring comparisons are case-sensitive. A wait times out
independently of the overall emulated and host deadlines; an assert fails at its
first evaluation. One action completes per frame. A delay starts when its step
becomes current. Natural keyboard posting is asynchronous; a following wait
should observe the application's response.

Wait, assert, and capture steps retain `checkpoint-NNN.tsv` and `.txt` with
registers, relevant memory, and text. The runner validates passing conditions
against their checkpoint evidence. `result.json` includes step status/timing and
checkpoints. Failed waits/assertions stop the run and preserve artifacts. An
entire completed sequence stops the run unless an `until`, debug, routine, or
cycle stop mechanism is also configured. Final assertions still apply.

## Game input

Select `gamePort: "joystick"` or `"paddles"`. Input steps override `paddle0` through
`paddle3` (0..255) or `button0` through `button3` (0 or 1). A null value releases
the override. For joysticks, paddles 0/1 map to joystick 1 X/Y and 2/3 to joystick
2 X/Y. For digital buttons, zero and null remove the injected press; ordinary
host input can still press the button. The adapter uses MAME's game-port inputs; Apple software still reads the
normal paddle timers and switches.

```json
{ "name": "move right", "action": "input",
  "input": { "control": "paddle0", "value": 255 } }
```

## Direct routine harness

A standalone `a2 run routine.test.json --artifacts NEW-DIRECTORY` can execute a
routine without a disk. The default `mame` engine injects it into a running machine
and still requires MAME and the machine ROMs. Set `engine: "cpu"` for a deterministic,
in-process routine run with no emulator, ROM, or disk.

```json
{
  "schemaVersion": 1,
  "engine": "cpu",
  "machine": "apple2ee",
  "emulatedSeconds": 5,
  "routine": {
    "source": "routine.asm",
    "kind": "asm",
    "origin": 24576,
    "startAfterSeconds": 1,
    "maxCycles": 10000,
    "registers": [{ "name": "A", "value": 42 }],
    "memory": [{ "address": 768, "hex": "0000" }]
  },
  "memory": [{ "address": 768, "hex": "2A00" }]
}
```

`routine.asm` could contain `STA $0300` followed by `RTS`. `kind: "binary"`
accepts raw payload bytes without a DOS header. An assembly `entrySymbol` or a
numeric `entryPoint` can select an entry within the payload. Code must fit
$0800..$BFFF. Initial memory writes must avoid code and `$0100..$02FF`, which the
harness reserves for stack/return handling. MAME injection restricts them to main
RAM; the CPU engine accepts the rest of the 16-bit flat address space. A/X/Y default
to zero, P to $24 (interrupts disabled, decimal clear). Explicit register inputs
may override these values. SP is $01FD; RTS returns to a breakpoint at $02FF.
With MAME, IIe/IIc injection selects main RAM, disables 80STORE/ALTZP, and restores
ROM reads. Other machine state is the state reached by power-on and any preceding
steps.

The CPU engine starts with zero-filled flat 64 KiB memory plus the routine and its
declared initial memory. It maps `apple2`, `apple2p`, and `apple2e` to the documented
MOS 6502 instruction set; `apple2ee` and `apple2c` select the Apple-compatible 65C02
set. It implements the documented instructions, addressing modes, decimal arithmetic,
the NMOS indirect-jump behavior, and instruction cycle accounting. Apple soft
switches, ROM calls, interrupts, video, audio, keyboard, disks, ordered steps, and
debug points are absent. I/O addresses behave as ordinary flat memory. The routine's
`startAfterSeconds` is validated but has no delay effect.

`maxCycles` bounds execution at instruction boundaries. `RTS` to the harness return
sentinel produces `routine_return`; exceeding the budget produces `cycle_limit`; an
opcode outside the selected documented instruction set produces `cpu_fault`.
Assertions inspect final memory and PC/A/X/Y/P/S/SP values. With `trace: true`,
`trace.tsv` contains every executed PC and opcode, before/after registers, cumulative
cycles, and logical reads/writes. Trace evidence is capped at 16 MiB.

The adapter snapshots the assembled payload, symbols, source map, and dependency
hashes as `routine.bin` and `routine.json`. It revalidates source inputs before
launch and after completion. Project workflows also pin every routine source and
include before committing the build, retain hashes in `execution-inputs.json`, and
reject paths that alias the output image. The emulated and host deadlines still apply if
code changes memory mapping or fails to reach the return breakpoint.

## Cycle budgets

`cycles` measures a window in a disk-loaded program:

```json
{
  "start": { "program": "MAIN", "symbol": "DRAW", "afterSeconds": 2 },
  "end": { "program": "MAIN", "symbol": "DRAW_DONE" },
  "maxCycles": 20000
}
```

Program/symbol references require `build --test`; standalone runs use numeric
`address`. Start/end CPU addresses must differ. Measurement starts at the first
start hit and ends before executing the end instruction. MAME uses the CPU's
`totalcycles` counter, including branches, page crossings, ROM calls and any
interrupt work within the interval. The CPU engine computes cycles from the
selected processor's documented instruction timings. A routine measurement
includes its final RTS but excludes the caller's JSR. Budget enforcement stops
at the next instruction boundary after the count exceeds the budget. Results include the
start/end addresses, cycle count, and whether the end was reached. Debug,
routine, cycle, and `until` stops are mutually exclusive. CPU mapping at the
breakpoint determines which code executes at that address.

## BASIC runtime errors

Set `checkBasicRuntime: true` to recognize an Applesoft `?… ERROR IN n` on the
captured text page. Project tests also map it through the build's BASIC line map. This supplements final assertions and cannot detect errors that
have already scrolled off the selected page. Ambiguous line numbers in multiple
BASIC programs are reported without inventing a source location.

See [graphics comparison](project-assets.md), [audio capture](audio-execution.md),
[setup profiles](setup.md), and [runtime routines](../examples/runtime/README.md).
