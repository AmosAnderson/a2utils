# Bounded runtime debugging

An execution specification can stop at an instruction breakpoint or CPU bus
watchpoint, optionally execute a fixed number of additional instructions, and
capture registers, memory, and recent instruction addresses. Debugging uses the
pinned MAME 0.289 backend and works with its headless `none` debugger provider.
The existing emulated and host deadlines still bound the run.

```json
{
  "debug": {
    "breakpoints": [{ "address": 8192, "afterSeconds": 2 }],
    "watchpoints": [{ "address": 768, "length": 16, "access": "write", "afterSeconds": 2 }],
    "stepInstructions": 0,
    "historyInstructions": 64
  },
  "observeMemory": [
    { "address": 256, "length": 256 },
    { "address": 768, "length": 16, "bank": "main" }
  ]
}
```

This fragment belongs in an ordinary [execution specification](execution.md)
with emulator, ROM, machine, disk, and deadline settings. Addresses are decimal
JSON integers. `observeMemory` captures bytes without asserting their contents.
Project executions can replace a numeric address with `program`, `symbol`, and
an optional `offset`; `build --test` resolves native assembler or cc65-exported
symbols and uses their source maps when reporting debug locations.

A specification allows 1–64 debug points in total. Breakpoints match a CPU
instruction address before execution. Watchpoints observe CPU bus reads, writes,
or both (`read`, `write`, `readWrite`); their range may include I/O. They observe
actual accesses without reading the watched location again. These points use
CPU addresses, so a point on a banked address observes whichever bank the
program maps there. Use bank-specific memory observations to inspect physical
RAM independently of that mapping.

The first point to trigger wins. Its result records the point's zero-based
index within its breakpoint or watchpoint array, address, access value for a
watchpoint, and the triggering instruction's PC. A watchpoint stops at the next
instruction boundary after its access; its trigger PC can therefore differ
from the captured CPU PC. The breakpoint or watchpoint `afterSeconds` value
arms the point on the first frame at or after that emulated time. The default
zero arms it when the observation script starts. Delayed arming is useful for
avoiding boot-time accesses to the same addresses.

`stepInstructions` executes 0–1024 instructions after the trigger's stopped
boundary. For example, stopping at `LDA #$2A` with two steps executes that
instruction and the following instruction, then captures before a third
instruction executes. Other debug points are disabled after the first trigger.
The result preserves the original trigger alongside the final registers and
the number of completed steps. A requested trigger or step count that is not
reached before a deadline fails the execution. `until` and `debug` are mutually
exclusive completion modes.

`historyInstructions` defaults to 64 and accepts 0–256. MAME retains 256 recent
instruction addresses, including the current instruction. The adapter excludes
that unexecuted instruction, so at most 255 completed addresses are returned.
Disassembly is produced from the memory and bank mapping at capture time;
self-modifying code or bank switches can make it differ from the bytes that
originally executed. This is an address history, not a record of historical
register values or historical memory banks. The captured PC identifies the
next instruction. The existing `trace: true` frame samples remain a separate
artifact.

The normal `result.json` includes `debug.trigger`, `debug.steppedInstructions`,
and `debug.history`. Stop reasons are `breakpoint`, `watchpoint`, or
`debug_steps`. Input disks retain the existing isolated-copy protections.

Project runs write matching final, history, and sampled PCs to
`source-locations.json`. When a MAME `trace.tsv` contains a mapped sampled PC,
`trace-source.tsv` preserves the raw sample columns and adds program, memory bank,
source file, line, and source text. Both inputs and derived evidence are capped at
16 MiB; absent mappings leave the raw trace usable and create no derived trace.
CPU project runs instead copy unique traced instruction PCs and the CPU trace's
exact routine source columns into `source-locations.json`; those rows use the
routine source path as `program` and `cpu` as the memory bank.

## Adapter implementation and verification

The callback ordering is intentional. MAME invokes Lua's periodic callback
synchronously inside its debugger stop loop, before the `none` debugger provider
resumes execution. The adapter reads its structured stop marker and captures
the stopped state there. The provider overwrites transient single-step state,
so bounded stepping instead uses a persistent registerpoint that stops before
each subsequent instruction. It is installed only after a trigger and removed
when the requested step count completes. Exit requests discard the remaining
scheduled CPU cycles. MAME's 6502 core can perform one pending micro-operation
before it notices the exhausted cycle count during shutdown. Register, memory,
and history evidence is captured before that continuation; a resulting disk
artifact represents the emulator's completed shutdown rather than a guaranteed
snapshot of media at the exact debug boundary.

The implementation follows the pinned upstream sources:

- [Debugger instruction hooks and stop loop](https://github.com/mamedev/mame/blob/mame0289/src/emu/debug/debugcpu.cpp)
  (`instruction_hook`, `wait_for_debugger`, `curpc`, registerpoints).
- [Lua periodic callback forwarding](https://github.com/mamedev/mame/blob/mame0289/src/frontend/mame/mame.cpp)
  (`emulator_info::periodic_check`).
- [Headless debugger provider](https://github.com/mamedev/mame/blob/mame0289/src/osd/modules/debugger/none.cpp)
  (`wait_for_debugger` resumes with `go`).
- [Lua debugger bindings](https://github.com/mamedev/mame/blob/mame0289/src/frontend/mame/luaengine_debug.cpp)
  (breakpoint/watchpoint calls, console log, and execution state).
- [Debugger history command](https://github.com/mamedev/mame/blob/mame0289/src/emu/debug/debugcmd.cpp)
  (capture-time disassembly of retained PC addresses).
- [Exit scheduling](https://github.com/mamedev/mame/blob/mame0289/src/emu/machine.cpp)
  (`schedule_exit` discards the remaining scheduled cycles).
- [6502 execution loop](https://github.com/mamedev/mame/blob/mame0289/src/devices/cpu/m6502/m6502.cpp)
  and [generated instruction bodies](https://github.com/mamedev/mame/blob/mame0289/src/devices/cpu/m6502/m6502make.py)
  (the micro-operation boundary at which an exhausted cycle count is noticed).

`MameDebuggerSmokeTests` contains optional real-emulator tests for stopping before
an instruction, executing exactly two instructions, and reporting a write's
value and triggering PC. Set `A2_MAME_PATH` and `A2_MAME_ROMS` to run them with
your local MAME 0.289 installation and ROMs.
