# Project asset generation and screenshot assertions

Projects can compile source graphics before assembling or compiling their programs. The `assets` array produces virtual build inputs: generated files exist in memory for the native assembler and inside cc65's disposable source stage. A build does not write intermediates into the source tree.

```json
"assets": [{
  "name": "HERO",
  "source": "art/hero.png",
  "output": "generated/hero.bin",
  "kind": "sprite",
  "cellWidth": 14,
  "cellHeight": 16,
  "assemblyInclude": "generated/hero.inc",
  "cHeader": "generated/hero.h",
  "metadataOutput": "generated/hero.json"
}]
```

`source` is an existing project input; `output` is a new `.bin` virtual path. `name` is an uppercase identifier, at most 48 characters, used as the constant prefix. Optional `.inc`, `.h`, and `.json` paths produce assembly constants, C constants plus a static byte array, and detailed packing metadata. Paths use `/`, remain inside the project, and cannot contain `.` or `..` segments. Existing files or duplicate output paths cause a failure; generated files never replace source files. Asset steps do not depend on another step's output.

| Kind | Input | Options and generated constants |
| --- | --- | --- |
| `sprite`, `tile`, `font` | PNG atlas | `cellWidth`, `cellHeight`, `bitsPerByte` (7/8), `bitOrder` (`lsb`/`msb`), `threshold`, `invert`; fonts also accept `firstCodePoint`. Constants include cell count/dimensions, row/cell byte sizes, and each cell's offset; fonts add codepoints. |
| `shapes` | Versioned shape JSON | Applesoft shape table; constants include one-based shape numbers, offsets, and lengths. |
| `dhires` | PNG screen | `mode` (`mono`/`color`), `bankOrder` (`aux-main`/`main-aux`); constants identify both 8192-byte bank offsets. |
| `lores`, `hires`, `hires-color` | PNG screen | Same encoding and required dimensions as the existing graphics commands. |

Every asset exports `NAME_LENGTH`. Atlas offsets are zero-based byte offsets into the payload: `HERO_CELL_0_OFFSET`, `HERO_CELL_1_OFFSET`, etc. Shapes use `NAME_SHAPE_1_OFFSET`, `NAME_SHAPE_1_LENGTH`, etc. The binary formats and image conversion contracts are described in [graphics assets](graphics-assets.md).

The native assembler can consume generated files normally:

```asm
.org $6000
.include "generated/hero.inc"
; Address cell 1 as hero_data + HERO_CELL_1_OFFSET.
hero_data:
.incbin "generated/hero.bin"
```

Includes retain the native assembler's existing boundary: they must stay beneath the directory containing the main assembly source. Put generated files under that directory. A project `files` entry can also use a generated `.bin` as its `source`, with a normal load address and physical memory-bank declaration. A double-hires combined payload contains two physical screen banks: load it to a staging buffer and explicitly distribute the banks; declaring it as one ordinary resident screen page is incorrect.

C can use `#include "generated/hero.h"` and `HERO_DATA[HERO_CELL_1_OFFSET]`. Include the generated header in one translation unit to avoid duplicate static arrays. ca65 can use the generated `.inc` and `.bin` files from the same isolated source stage. The manifest's cc65 include-directory settings still apply.

Build reports list source input hashes and an `assets` section with every virtual output hash and format metadata. Original inputs are rechecked before the disk transaction commits. At most 128 steps, 64 MiB of combined asset inputs, and 32 MiB of combined generated outputs are accepted. Existing PNG and asset dimension/payload limits also apply. Failed conversion, path collisions, compilation, or validation leave an existing destination disk intact.

The original [asset demo](../examples/runtime/asset-demo.a2.json) compiles a two-cell atlas, emits assembly/C/JSON intermediates, and links a clipped HGR sprite renderer. Its PNG is original repository artwork.

## Screenshot comparison

An execution specification can require a reference screenshot:

```json
"screenshotAssertion": {
  "expectedImage": "references/title-screen.png",
  "channelTolerance": 3,
  "maxDifferentFraction": 0.001,
  "crop": { "x": 0, "y": 0, "width": 560, "height": 192 }
}
```

The expected PNG path is relative to the specification. The runner pins its bytes before launching MAME, captures a screenshot even if `screenshot` is false, and saves `expected-screen.png` and `screen-diff.png`. A mismatch fails the run with `execution.screenshot_assertion`; the result's `screenshotComparison` includes input hashes and numerical comparison evidence. Changing the reference image during execution is a failure, not a new baseline. Omit `crop` to compare the entire screenshot.

`VisualComparison.Compare(expected, actual, options)` compares decoded RGB images without scaling, alignment, or filtering. Images must have identical dimensions. An optional crop selects the same rectangle in both images; a crop outside the images is rejected.

`channelTolerance` is an integer 0–255. A pixel differs if **any** of its RGB channel differences exceeds this tolerance. `maxDifferentFraction` is a number 0–1; the comparison passes when the number of differing pixels divided by compared pixels is at most this fraction. Both defaults are zero. This makes acceptance criteria explicit and avoids hiding entire moving regions behind a per-channel average.

The report records compared and differing pixel counts, fraction, maximum channel delta, and compared dimensions. Its diff image contains absolute RGB channel differences and has the crop's dimensions. The execution runner retains the report and diff PNG as evidence. PNG transparency is composited against black by the existing decoder; expected screenshots should use the same rendering configuration as the tested emulator.
