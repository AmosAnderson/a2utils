# ADR 0002: Use selectable MAME and CPU execution backends

Status: Accepted

AI development needs a repeatable compile/package/execute/observe loop. A CPU
instruction runner alone cannot validate Apple II firmware calls, disk booting,
keyboard input, video state, or DOS/ProDOS behavior.

Use an external **MAME 0.289** process with a generated Lua observation script.
The executable, machine and ROM directory are explicit inputs. The dependency is
optional, version-checked, and not bundled. The implementation adds no runtime
NuGet package. It keeps emulation outside the disk library's filesystem adapter.

For isolated assembly routines, also provide a self-contained `cpu` engine behind
the same execution specification/result boundary. MAME remains the default for
backward compatibility. The CPU engine requires an explicit Apple II machine name
to select MOS 6502 or Apple-compatible 65C02 semantics, then runs in a zero-filled
flat 64 KiB address space with initial registers/memory, an RTS sentinel, and a
hard instruction-cycle budget. It has no ROM, operating system, device, video, or
disk behavior. This makes edit-build-run-debug-test loops deterministic and removes
external setup when the behavior under test is a pure routine.

The in-process engine derives its accepted opcodes from the assembler's documented
instruction tables and implements their addressing, flags, decimal arithmetic,
branch/page timing, and NMOS/CMOS indirect-jump distinction. Optional instruction
traces record PC/opcode, register transitions, cycles, logical memory accesses,
and routine source locations when assembly mappings exist.
Illegal opcodes fail explicitly instead of acquiring undocumented host behavior.

cc65 builds request ld65 version 2.0 debug output and reduce its bounded
file/line/segment/span graph to stable 16-bit source ranges. Project MAME traces
remain raw frame samples; a separate capped artifact adds source columns when a
resident native or cc65 build range matches. Missing mappings do not change run
success or remove the raw trace.

The Lua API is not stable across MAME releases, so changing the accepted version
requires repeating the real machine smoke. Its public interfaces for machine time,
memory, CPU state, natural keyboard, frame callbacks and snapshots are sufficient
for persistent mailbox assertions and end-state inspection. See the official
[Lua interface documentation](https://docs.mamedev.org/luascript/index.html),
[core classes](https://docs.mamedev.org/luascript/ref-core.html), and
[command-line options](https://docs.mamedev.org/commandline/commandline-all.html).

Every machine run uses a new directory and disk copy, disables user INI loading,
redirects machine state outputs, and applies both an emulated deadline and host
watchdog. The host terminates the process tree on timeout/cancellation. Machine
output remains reviewable on failure. ROM checksum warnings fail validation.

The first integration verification boots an original one-sector assembled program
on an enhanced Apple IIe, injects keyboard input, checks registers and memory,
decodes text memory, and saves an actual machine screenshot. Process-contract
tests also cover missing executables, version disagreement, process failure,
malformed/incomplete observations, cancellation and host timeouts.

The runtime-debugging/IIe extension adds instruction breakpoints and bus watchpoints
through the same pinned backend. A synchronous debugger-stop callback captures state;
bounded registerpoint stops implement instruction stepping with the headless provider.
Native PC history is disassembled from capture-time memory and is not a historical
byte/bank trace. See [the design and upstream evidence](../runtime-debugging.md).

Physical main/auxiliary/language-card observations read verified saved RAM items
without touching soft switches. Optional 40/80-column text decoding preserves
attributes and MouseText codes. The saved-item layouts are part of the pinned
backend contract and must be revalidated when changing MAME versions; missing or
different layouts fail rather than falling back to CPU reads. No dependency changes.

The September 16 extension adds ordered conditions/input/checkpoints, screenshot
comparison, game-port inputs, bounded PCM audio evidence, cycle windows, direct
routine injection into the real machine, and an explicit CFFA2 slot-7 profile.
Cycle windows use native totalcycles counters at instruction boundaries. The
routine harness supplies stack/return state and retains its exact code/input hashes.
Environment locks pin emulator/ROM/toolchain content. These features preserve the
same isolated-image and host/emulated-deadline contract.

Current scope excludes native IIgs execution, arbitrary slot selection, and a
full-machine historical bus trace. MAME frame PC samples remain separately labelled
from CPU-engine instruction traces. New backends should preserve the versioned
execution/result boundary rather than embed emulator-specific behavior in disk
operations.
