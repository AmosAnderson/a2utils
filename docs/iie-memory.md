# Apple IIe project memory banks

Project files and explicit `reserve` regions accept `memoryBank`. Omission means
`main`, preserving existing manifests. Build results include this field on each
file and resident memory region. Addresses remain CPU addresses; the bank says
which physical bytes the program will occupy once its loader has placed it there.

| `memoryBank` | Address range | Relationship |
| --- | --- | --- |
| `main` | `$0000-$BFFF` | Main low RAM |
| `aux` | `$0000-$BFFF` | Auxiliary low RAM, independent of main |
| `lc1` | `$D000-$FFFF` | Main language-card bank 1 |
| `lc2` | `$D000-$FFFF` | Main language-card bank 2 |
| `aux-lc1` | `$D000-$FFFF` | Auxiliary language-card bank 1 |
| `aux-lc2` | `$D000-$FFFF` | Auxiliary language-card bank 2 |

The two main language-card banks have separate `$D000-$DFFF` storage and **share
the same `$E000-$FFFF` bytes**. The auxiliary pair has the same relationship;
main and auxiliary storage are independent. Overlap checks account for these
aliases, including payloads crossing `$E000`. `$C000-$CFFF` is I/O and ROM, not
payload RAM. These bank relationships follow Apple's
[Apple IIe Technical Reference Manual, chapter 4](https://www.applelogic.org/files/AIIETECHREF3.pdf).

The `apple2e`, `apple2enh`, and `apple2c` profiles describe these six spaces.
Using auxiliary banks on a IIe requires a 64 KiB auxiliary expansion; IIc auxiliary
RAM is built in. The baseline `apple2plus` profile continues to describe only its
48 KiB main RAM. `a2 targets --json` reports supported banks, auxiliary-memory
requirements, and 80-column/MouseText capabilities. `mainMemoryBytes` continues
to mean contiguous low RAM; `bankedMainMemoryBytes` describes the additional
16 KiB. The shared upper language-card windows must not be counted twice.

```json
{
  "schemaVersion": 1,
  "target": "apple2e",
  "disk": { "fileSystem": "dos33" },
  "files": [
    { "source": "loader.asm", "path": "LOADER", "kind": "asm", "origin": 8192 },
    { "source": "aux-code.asm", "path": "AUX.CODE", "kind": "asm",
      "origin": 16384, "memoryBank": "aux" }
  ],
  "reserve": [
    { "name": "aux scratch", "start": 24576, "length": 256, "memoryBank": "aux" }
  ]
}
```

Both `origin` and ProDOS `auxType`/DOS binary headers continue to describe the load
address. `auxType` does not mean auxiliary RAM. A bank annotation neither installs
a loader nor changes the OS. Banked entries must be BIN payloads with an origin.
Supply a main-memory loader that reads the file into a safe main-memory staging
area and copies it to the declared bank; account for that staging area with
`reserve` when appropriate. Generated `startup` can launch that main-memory
loader; it rejects a banked payload as the direct RUN/BRUN target.

Physical bank and range validation always applies, including `resident: false`
files and builds with `checkMemory: false`. `resident: false` excludes a file from
simultaneous residency checks. `checkMemory: false` disables overlap checks, so a
custom runtime can deliberately replace conventional reservations; it does not
make an unavailable bank or I/O address valid RAM.

Conventional main-memory workspace/display reservations still apply. ProDOS
builds also protect both main language-card banks, including the shared upper
region. This conservative reservation includes kernel, dispatcher and space
reserved by ProDOS; explicit replacement requires disabling overlap checks and
providing a compatible runtime. See Apple's
[ProDOS Technical Reference, memory use](https://prodos8.com/docs/techref/memory-use/).

Auxiliary banks are not presumed to contain a second copy of the main OS
workspace. Programs remain responsible for their auxiliary zero page, stack,
display buffers and other runtime allocations. ProDOS auxiliary payloads receive
a diagnostic reminding the loader to protect its allocations against `/RAM` or
disconnect that RAM disk. The builder does not alter the OS or prove that a loader
performs this work. See Apple's
[ProDOS Technical Reference, using the alternate RAM bank](https://prodos8.com/docs/techref/writing-a-prodos-system-program/).

## Routines and execution checks

The [banked-memory example](../examples/development/iie-banks/README.md) provides
main-to-auxiliary and main-to-language-card copy routines with documented calling
contracts, a project manifest, and an optional self-booting MAME smoke test.
The language-card demonstration targets DOS/custom runtimes because it uses
memory occupied by ProDOS.

Execution assertions use the field `bank` (default `cpu`) to choose physical
memory independently of the CPU's current switches. Use `bank: "aux"` to inspect
the auxiliary copy after the routine restores main reads. See
[IIe execution observations](iie-execution.md) and the [execution guide](execution.md).
