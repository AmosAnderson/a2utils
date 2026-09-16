# Small Apple II programming routines

These are original, callable 6502 routines intended as inspectable building blocks. Copy the needed `.inc` files beneath your program's source directory and include them with the native assembler or ca65. They do not install firmware, reserve memory automatically, or change the operating system's memory configuration.

| Source | Entry points | Inputs and results | Clobbers / machine contract |
| --- | --- | --- | --- |
| `text-keyboard.inc` | `a2_puts` | `$06/$07`: NUL-terminated ASCII string, at most 255 bytes. Outputs through monitor COUT. | A/X/Y/P, `$08`; main RAM/ROM and ALTZP=0. COUT hooks remain active, including DOS command interpretation. |
| `text-keyboard.inc` | `a2_getkey` | C=1 and A=7-bit key when pending; C=0,A=0 otherwise. Consumes only a pending key. | A/P; X/Y preserved. Polling is nonblocking. |
| `text-keyboard.inc` | `a2_home` | Clears the active 40-column monitor window. | ROM routine's register/scratch contract; use BASIC HOME for an active 80-column firmware window. |
| `hgr-sprite.inc` | `a2_hgr_sprite` | `$06/$07` source, `$0A` byte column, `$0B` pixel row, `$0C` row width in bytes, `$0D` height. | A/Y/P, `$06–$10`; X preserved; decimal mode off, main RAM, ALTZP=0, 80STORE=0. |
| `prodos-file.inc` | `a2_file_open/read/write/close` | X/Y point to the appropriate ProDOS MLI parameter list; returns MLI carry and A error code. | A/X/Y/P; writable main RAM code, ProDOS initialized, main RAM/ROM and ALTZP=0; not reentrant. |
| `basic-call.asm` | `a2_add_bytes` at `$6000` | Inputs `$6040/$6041`; 16-bit sum at `$6042/$6043`. | A; preserves X/Y/P, no zero-page or firmware use. |

Reserve all scratch, code, buffers, and display memory in the project. Register preservation does not imply that ROM calls preserve every zero-page location. Do not call the text or file routines while ROM/zero-page banking is incompatible with the operating system.

## HGR sprites

The sprite format matches `sprite`/`tile`/`font` assets packed with 7 bits per byte and LSB order. The renderer replaces whole bytes on HGR page 1; the left edge must be aligned to seven pixels. Width is 1–40 bytes and height is 1–192 pixels. Right and bottom clipping preserve the source row stride. Coordinates outside column 0–39 or row 0–191, or invalid sizes, draw nothing. Negative positioning, masking, transparency, and sub-byte shifting are not provided.

The renderer updates its source pointer and row for the rendered rows. Source bytes must fit main RAM without wrapping and must not overlap destination, code, or scratch. Bit 7 should be clear for monochrome assets. Partial final packed bytes clear padding pixels because this is an opaque byte renderer. Display mode, palette-phase policy, screen clearing, and page flipping belong to the caller.

Build `asset-demo.a2.json` to see PNG packing, generated constants, binary inclusion, and the renderer together. The resulting data disk needs an existing DOS/ProDOS environment to `BRUN DEMO`. The example does not clear the complete HGR page or restore text mode; it draws into the current page.

`asset-c-demo.a2.json` uses the same atlas from C through its generated header and prints the cell count, payload size, and first packed row. It requires the optional cc65 toolchain; configure its compiler/distribution paths for your environment.

## File I/O

`prodos-file.inc` performs existing-file binary I/O. Construct the standard parameter list, load its low address into X and high address into Y, and call the corresponding entry point. Test carry immediately; A is the ProDOS error on failure. Always inspect transferred length for READ/WRITE. Reserve the required page-aligned 1024-byte OPEN buffer and supply a length-prefixed pathname. CLOSE should receive the returned nonzero reference number; zero closes other files as well. CREATE and SET_EOF can be issued through `a2_file_call` by supplying their MLI command in A and a correct parameter list.

`dos-file.bas` demonstrates reusable GOSUB blocks for one text record: F$ is a trusted DOS filename, W$ is the record to write, and R$ receives the record read. It works with DOS 3.3's BASIC hooks and ProDOS BASIC.SYSTEM. The sample owns the ONERR handler and closes files before reporting an error; copying it into an application requires integrating that application's error handling. It replaces a record, not the entire old file's EOF, and is not a transactional file updater. Restrict filenames and records so control characters cannot be interpreted as DOS commands.

The assembly wrappers follow the [ProDOS MLI reference](https://prodos8.com/docs/techref/calls-to-the-mli/). Native assembly tests verify their inline command/parameter layout. Actual OS behavior still depends on a configured boot environment and should be covered by the project's saved-file assertions.

## BASIC/assembly calls

Build `basic-call.a2.json`, mount its data disk from an existing BASIC.SYSTEM environment, and `RUN CALL.DEMO`. The listing lowers HIMEM below the routine, loads ADDER, fills its mailbox, calls address 24576, and prints the 16-bit result (270). The assembly routine preserves processor flags, including decimal mode, and avoids borrowing Applesoft's zero-page state. The manifest reserves the mailbox separately from the routine.
