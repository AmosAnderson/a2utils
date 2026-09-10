# ADR 0002: Use an external MAME adapter for machine execution

Status: Accepted

AI development needs a repeatable compile/package/execute/observe loop. A CPU
instruction runner alone cannot validate Apple II firmware calls, disk booting,
keyboard input, video state, or DOS/ProDOS behavior.

Use an external **MAME 0.289** process with a generated Lua observation script.
The executable, machine and ROM directory are explicit inputs. The dependency is
optional, version-checked, and not bundled. The implementation adds no runtime
NuGet package. It keeps emulation outside the disk library's filesystem adapter.

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

Current scope excludes native IIgs execution, custom slot selection, bank-independent
RAM access, instruction tracing, cycle-accurate breakpoint assertions, 80-column
text decoding and CPU-only routine simulation. Frame PC samples are labelled as
such. New backends should preserve the versioned execution/result boundary rather
than embed emulator-specific behavior in disk operations.
