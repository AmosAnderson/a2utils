# Runtime memory and overlays

Project memory checking includes loadable payloads, parsed cc65 runtime segments,
and explicit per-program allocations. `BuiltFile.runtimeMemory` exposes compiler
segments and declarations; `ProjectBuildResult.memory` reports their union with
each owner's payload, so initialized data and linker-described reuse are not
counted twice. Regions from different files remain separate, including overlay
alternatives: do not sum every alternative to estimate simultaneous memory use.

```json
{
  "schemaVersion": 1,
  "runtime": "basic-system",
  "files": [
    {
      "source": "app.asm", "path": "APP", "kind": "asm",
      "runtimeMemory": [
        { "name": "uninitialized state", "start": 12288, "length": 512, "kind": "bss" },
        { "name": "software stack", "start": 32768, "length": 1024, "kind": "stack" },
        { "name": "heap budget", "start": 16384, "length": 4096, "kind": "heap" }
      ]
    }
  ]
}
```

`runtimeMemory` regions use `name`, `start`, `length`, optional `memoryBank`
(default `main`), and `kind` (`data`, `bss`, `zero-page`, `stack`, or `heap`).
Stack and heap declarations reserve a bounded address range; they do not change
the program's allocator, stack pointer, or linker settings. The compiler's
software stack is distinct from the 6502's fixed hardware stack in page one.
Zero-page declarations must fit `$0000-$00FF` in main or auxiliary RAM.
Ranges and bank availability are validated even with overlap checking disabled.

Distinct explicit allocations cannot overlap each other or their own payload.
A declaration exactly repeating a compiler allocation is permitted and counted
once. Linker-described BSS reuse of startup/ONCE bytes is included in the same
owner's union. Another simultaneously resident file still conflicts with those
bytes. An inferred C stack overlapping the program or BSS is an error.

The compiler's stock saved zero-page workspace `$80-$99` is exempt from the
blanket main zero-page reservation only for a cc65 zero-page segment. Explicit
reservations and other programs still conflict with it. Other zero-page areas
remain protected; a custom runtime must deliberately disable overlap checks
and implement the required preservation contract if it borrows them.

`runtime` chooses the conventional ProDOS reservation:

| Value | Policy |
| --- | --- |
| `auto` (default) | Protect BASIC.SYSTEM when a BASIC payload or generated startup is present; otherwise use the system-program layout. |
| `basic-system` | Reserve main `$9600-$BEFF` for BASIC.SYSTEM and its command buffer, in addition to the ProDOS global page and language-card reservations. |
| `system` | Use the ProDOS system-program layout; the program owns reclaiming BASIC.SYSTEM before using that space. |

DOS builds retain their conventional upper-memory reservation. ProDOS BASIC file
buffers can lower HIMEM below `$9600`; reserve the maximum number of buffers your
application keeps open. Static budgets do not prove dynamic stack/heap usage or
OS buffer behavior. See Apple's
[BASIC.SYSTEM memory and buffer documentation](https://prodos8.com/docs/techref/the-prodos-basic-system-program/).

## Named overlays

```json
{
  "files": [
    { "source": "loader.asm", "path": "LOADER", "kind": "asm", "origin": 8192 },
    { "source": "menu.asm", "path": "MENU", "kind": "asm", "origin": 16384, "overlayGroup": "screens" },
    { "source": "game.asm", "path": "GAME", "kind": "asm", "origin": 16384, "overlayGroup": "screens" }
  ]
}
```

Members of the same nonempty `overlayGroup` are declared mutually exclusive and
may overlap. They remain checked against always-resident files, reservations,
and members of other groups. Each member's runtime allocations follow its
lifetime. Put shared buffers in top-level `reserve` or an always-resident owner.
Group names are 1..64 ASCII letters, digits, underscores, hyphens or dots.
The builder does not generate an overlay loader or prove that only one member
is live; your loader must enforce that contract. `resident: false` continues to
exclude a file's runtime footprint from simultaneous residency checks.

Banked and overlay loading can be combined with
[custom cc65 linker configurations](cc65.md#custom-linker-configurations) and
[physical IIe bank annotations](iie-memory.md).
