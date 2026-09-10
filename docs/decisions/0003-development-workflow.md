# Development workflow and artifact contracts

Date: September 10, 2026

The next preview adds native project builds, source analysis, structured
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
