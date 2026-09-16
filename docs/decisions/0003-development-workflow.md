# Development workflow and artifact contracts

Date: September 10, 2026

Status: Accepted

Preview 0.4 adds native project builds, source analysis, structured
diagnostics, machine profiles, graphics codecs, and optional external tools.
Core implementations remain independent of the command line and console.

Project builds compile all input payloads before opening a writable disk
session. A fresh volume or copied template is populated in one ImageTransactions
transaction, reopened, verified against expected file bytes/type/load metadata,
and committed only after inputs are rechecked. New ProDOS dates use an explicit
manifest timestamp so native build outputs can be reproduced exactly.

Compiled origins are authoritative. Explicit load/auxiliary metadata must agree
where it describes a load address. Source maps and symbols accompany reports;
the raw binary format remains unchanged. Disk verification remains filesystem
verification, and execution remains a separate operation with its own evidence.

Machine profiles are conservative policies for main-memory development. They
do not silently infer a CPU from a filename, make WDC extensions available on
stock Apple hardware, or claim banked-memory/runtime safety from payload ranges.

Applesoft checking is an additional source operation. Tokenizer compatibility
and acceptance of ROM-style token streams are preserved. Diagnostics carry
source location and specific codes without requiring prose parsing; existing
CLI error classifications remain available. JSON envelope v1 receives additive
diagnostic and metadata fields. Bundled JSON Schemas describe new inputs.

cc65 remains an optional external installation, invoked with structured argument
lists in a bounded isolated source tree. Its program output uses AppleSingle v2;
a bounded program decoder validates entry ranges and widths and refuses resource
fork loss. This is not a general archive-preservation implementation. No new
NuGet dependency is introduced. Source/library revisions for DiskArc are unchanged.

PNG and display conversion use small bounded native codecs with explicit
supported representations and RGB approximations. PNG interpretation follows
the W3C specification; tests include an independent GDI+ encoded fixture.
Graphics layout and shape tests use independently specified Apple memory vectors.

External execution is described in [0002-execution.md](0002-execution.md).
Emulators, ROMs, operating systems, and external compilers used for local tests
remain outside tracked/published artifacts. Their identities and test results
are documented separately from unit and process-contract checks.

## September 13, 2026: preflight and project execution

Full preflight uses the same population and validation callbacks as a real build
on a disposable image. Its plan records actual allocation outcomes. The existing
fast source check remains separate. The additive build result includes mode,
capacity/change plan, and resolved optional execution settings.

Project execution preflights first, binds a suite to the target machine and build
symbols, captures execution input hashes, then rebuilds. Both source hashes and
the image hash must match preflight, and execution inputs must still match before
the image transaction commits. After commit, behavioral failures retain the valid
build and execution evidence. This is not an atomic transaction spanning emulator
execution or all evidence files.

Explicit mounts support the two existing floppy devices. Every disk is copied,
input-pinned when requested, and retained with before/after hashes. Saved-file
assertions and filesystem verification occur after the emulator exits. The CLI
and Core retain the legacy single-disk contract. Symbolic addresses are resolved
only through a matching full build, with source annotations limited to available
native assembly maps. No controller configuration, emulator version expansion,
or instruction tracing is implied.

## September 16, 2026: remaining AI programming tools

Compiler reports retain normalized linker symbols, typed segments, diagnostics,
and toolchain input identities. Project runtime accounting combines payloads
with BSS, zero-page, stack, and declared heap/other allocations by physical bank.
Members of one named overlay group promise mutually exclusive runtime lifetimes;
other resident code and reserved regions continue to conflict.

Asset transformations produce immutable virtual project inputs consumed by the
native assembler and staged cc65 build. Metadata/includes and hashes are build
evidence; the source tree receives no temporary compiler or asset output.
Environment profiles and explicit locks provide local tool/ROM/template identity
without bundling external assets. Starters are staged in a new directory.
Project suites use these same pinned inputs and the extended execution schema,
including symbol-aware ordered waits and cycle windows. CFFA2 is an explicit
storage profile with validated single-volume ProDOS images; default floppy
behavior is retained for supported floppy geometry.
