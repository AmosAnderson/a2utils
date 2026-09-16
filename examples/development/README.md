# Development examples

These original examples target the baseline Apple II Plus/IIe/IIc environment.
`apple2.inc` defines monitor and I/O addresses used by the assembly examples.

| Manifest | Program | Use from an existing ProDOS BASIC.SYSTEM environment |
| --- | --- | --- |
| `keyboard.a2.json` | Keyboard polling and monitor output | `BRUN KEYECHO`; Return exits |
| `hires.a2.json` | HGR page 1 stripe display | `BRUN STRIPES`; any key restores text |
| `mixed.a2.json` | Applesoft calls a machine-code subroutine | `RUN DEMO` |
| `disk.a2.json` | Sequential text-file write and read | `RUN DISKDEMO`; creates `SAMPLE.DATA` |

Build from the repository root, for example:

```sh
a2 build examples/development/mixed.a2.json --json
a2 asm listing examples/development/routine.asm --to routine.lst
a2 graphics shapes encode examples/development/shapes.json --to shapes.bin --json
```

The generated images are data volumes. Boot an existing compatible OS image and
mount the generated volume before running the examples. The files must be in the
active volume/directory when BASIC uses `BLOAD`, `OPEN`, or `RUN`. No Apple ROM or
operating system is distributed here. The build tests check the compiled payloads
and their disk metadata; interactive keyboard/display/OS behavior still requires
the documented emulator interoperability checks.

The mixed example loads code at `$2000` and calls decimal `8192`. Its manifest
reserves 1 KiB after the BASIC program for interpreter variables. The HGR example
places code at `$6000` and reserves `$2000-$3FFF` for its display data. The disk
example writes a short sequential file using the DOS-compatible command interface
provided by ProDOS BASIC.SYSTEM; the returned string is printed after closing it.

`shapes.json` contains two original vector examples for the Applesoft shape-table
encoder. See [Graphics assets](../../docs/graphics-assets.md) for loading shape
tables and packing sprite, tile, font, and double-hires assets.

The [project testing example](project-tests/README.md) adds full preflight and a
project-owned suite that boots a supplied DOS disk alongside a newly built data
disk. It demonstrates symbolic assertions and build/execution evidence.

The [IIe bank examples](iie-banks/README.md) demonstrate explicit main-to-auxiliary
and language-card loaders. See [runtime debugging](../../docs/runtime-debugging.md)
for stopping at source symbols and observing the loaded physical banks.
