# Apple IIe banked memory example

`bank-copy.inc` contains two reusable 6502 routines with explicit entry, exit,
register and scratch-memory contracts. `demo.asm` fills main RAM with a pattern,
copies it to auxiliary RAM and language-card bank 1, and leaves sentinels in main
RAM and language-card bank 2. It writes `$2A` to main `$0300` when finished.

```sh
a2 build examples/development/iie-banks/project.a2.json --check --json
a2 build examples/development/iie-banks/project.a2.json --json
```

The generated disk is a data disk. To run `BRUN BANK.DEMO`, supply your own DOS 3.3
boot environment and a 128 KiB IIe (64 KiB auxiliary expansion) or IIc. The demo
uses main language-card RAM and is **not a ProDOS program**. Neither a disk
`auxType` nor project `memoryBank` loads auxiliary RAM automatically; an OS loader
must first read a file into a safe staging buffer, then call a copy routine.

`copy_page_to_aux` copies exactly 256 bytes from main to auxiliary RAM. The main
source and auxiliary destination ranges must both fit `$0200-$BFFF`. It preserves
80STORE and RAMWRT, disables interrupts temporarily, and relies on main reads and
main zero page/stack already being selected. `copy_page_to_lc1` uses two soft-switch
reads to enable writes, then returns to ROM with language-card writes disabled.
It intentionally sets the exit mapping instead of claiming to restore an
unreadable write-enable latch. Read the contracts in the include before reuse.

Core tests assemble the actual include and check the project. An optional
`MameSmokeFact` boots the same demonstration from an original single-sector boot
image and checks both copies, both sentinels, and the restored soft-switch state.
Set `A2_MAME_PATH` and `A2_MAME_ROMS` to run it. A skipped smoke test does not validate
these routines on an emulator or physical machine.

See [banked memory](../../../docs/iie-memory.md) for the manifest model and hardware
references.
