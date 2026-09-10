# Graphics assets for software development

The `graphics assets` commands pack sprite, tile, and bitmap-font PNG grids into
raw bytes. The `graphics shapes` commands encode Applesoft vector tables.
`graphics dhires` converts complete double-hires screens with explicit bank order.
All commands support `--json`, `--quiet`, and `--overwrite`; writes are staged,
validated, and protected against source aliases. Invalid input leaves existing
outputs intact. PNG transparency is composited against black by the PNG reader.

## Sprite, tile, and font atlases

```sh
a2 graphics assets pack atlas.png --to sprites.bin --cell-width 14 --cell-height 16 --bits-per-byte 7 --bit-order lsb --kind sprite --json
a2 graphics assets unpack sprites.bin --to preview.png --cell-width 14 --cell-height 16 --bits-per-byte 7 --bit-order lsb --columns 4
a2 graphics assets pack font.png --to font.bin --cell-width 8 --cell-height 8 --bits-per-byte 8 --kind font --first-codepoint 32 --json
```

| Option | Pack | Unpack | Meaning |
| --- | --- | --- | --- |
| `--to FILE` | Required | Required | Separate output; existing files require `--overwrite`. |
| `--cell-width`, `--cell-height` | Required | Required | Positive cell dimensions. |
| `--bits-per-byte 7\|8` | Yes | Yes | Default `7`. |
| `--bit-order lsb\|msb` | Yes | Yes | Default `lsb`. |
| `--kind sprite\|tile\|font` | Yes | Yes | Default `sprite`; controls metadata and font labels. |
| `--first-codepoint N` | Fonts only | Fonts only | Defaults to 32 for fonts and is invalid for other kinds. |
| `--threshold 0..255` | Pixel conversion | Metadata only | Default `128`; unpacking raw bits needs no luminance decision. |
| `--invert` | Invert encoded bits | Invert preview pixels | Default off; use the same setting in both directions. |
| `--columns N` | — | Yes | Preview columns, default `1`; must divide the cell count. |

Pack input is limited to a 32 MiB PNG. Unpack input and packed output are limited
to 65,536 bytes. Cell counts, dimensions, and multiplication are checked before
allocation; previews cannot exceed 2048×2048.

Supply cell dimensions explicitly. PNG width and height must be exact multiples
of them; margins and gaps are unsupported. Cells are ordered left to right, then
top to bottom. Each cell's rows are contiguous, and each row starts a fresh byte.
The row stride is `ceil(cellWidth / bitsPerByte)`, and bytes per cell are that
stride times cell height. No headers or hardware sprite formats are implied.

`--bits-per-byte` accepts `7` (default) or `8`. `--bit-order lsb` maps the leftmost
pixel in each group to bit zero; `msb` maps it to bit six or seven, respectively.
Unused bits are zero. For example, eight pixels with the first and last white
pack as `01 01` with seven-bit LSB order, or `81` with eight-bit LSB order.
Seven-bit packing supports copying aligned data into HGR rows; a renderer must
still calculate interleaved screen addresses, clipping, and any pixel shifts.

Luma is `(299*R + 587*G + 114*B) / 1000`; pixels at or above `--threshold` are set
(default 128; range 0–255). `--invert` reverses this decision. Pass the same
inversion and layout when unpacking. The threshold is retained in unpack metadata
but does not change already packed bits. Preview output is monochrome and rejects
nonzero unused bits so data is never silently discarded. `--columns` defaults to
one when unpacking and must divide the cell count.

The JSON result includes schema-versioned metadata: cell dimensions, grid shape,
bit order, threshold, row/cell byte counts, total memory length, and each cell's
byte offset. `--kind font` additionally labels cells with sequential Unicode
scalar values beginning at `--first-codepoint` (default 32). These labels describe
the supplied glyphs; the tool does not rasterize a system font. `tile` and `sprite`
share the same binary layout. Assets must fit 65,536 bytes and a 2048×2048 preview.

Use `.incbin "sprites.bin"` from assembly to include the result. Save the JSON
report to retain the layout needed for decoding or renderer generation.

## Applesoft shape tables

Create a UTF-8 JSON source such as:

```json
{
  "schemaVersion": 1,
  "shapes": [
    {
      "name": "square",
      "commands": [
        { "direction": "right", "count": 8 },
        { "direction": "down", "count": 8 },
        { "direction": "left", "count": 8 },
        { "direction": "up", "count": 8 }
      ]
    }
  ]
}
```

```sh
a2 graphics shapes encode shapes.json --to shapes.bin --json
```

Shape input is limited to 1 MiB. It must be a JSON object with `schemaVersion: 1`
and a `shapes` array; unknown fields, nesting deeper than 16 levels, null commands,
and unsupported values are rejected. Existing output requires `--overwrite`.

Directions are `up`, `right`, `down`, and `left`. Each vector plots before moving;
`plot: false` moves without plotting. Counts default to one. The encoder writes
a shape count, reserved zero byte, little-endian relative offsets, packed vectors,
and zero terminators. Shape numbers start at one. Paths that cannot be encoded
exactly, including a final nonplot up vector, are rejected. Packing follows
[Apple's Applesoft II manual, chapter 9](https://mirrors.apple2.org.za/ftp.apple.asimov.net/documentation/programming/basic/Applesoft%20II%20GREENBOOK%202019.pdf#page=110).

The JSON result gives shape offsets, lengths, vector counts, and final coordinates.
Limits are 255 shapes, 65,535 total vectors, and 65,535 output bytes. The file has
no DOS binary header. Choose a safe load address, load the table there, and set
Applesoft's shape pointer at `$E8/$E9`; the tool does not allocate runtime memory.

## Double-hires screens

```sh
a2 graphics dhires encode mono.png --to screen.dhgr --mode mono --bank-order aux-main
a2 graphics dhires decode screen.dhgr --to preview.png --mode mono --bank-order aux-main
a2 graphics dhires encode color.png --to color.dhgr --mode color --bank-order aux-main
```

`--mode` and `--bank-order` are required for both encode and decode. Input files
are capped at 32 MiB; decode input must nevertheless have the exact 16,384-byte
bank-pair length. Existing output requires `--overwrite`.

`mono` uses 560×192 pixels; `color` uses 140×192 logical pixels and the approximate
RGB lo-res palette. Both produce 16,384 bytes. `--bank-order aux-main` places the
auxiliary 8 KiB first; `main-aux` reverses the two halves. The display alternates
seven LSB-first auxiliary dots with seven main-memory dots. Encoding clears unused
high bits and screen holes; decoding ignores them. See [Apple IIe Technical Note
#3, Figure 3 and Table 2](https://mirrors.apple2.org.za/ftp.apple.asimov.net/documentation/hardware/misc/Apple%20IIe%20Technical%20Notes.pdf#page=20).

Color conversion reproduces the documented four-bit patterns; its preview does
not simulate NTSC edge fringes. Output is a raw bank pair with no mode byte or
load header. A loader must put each half into the appropriate memory bank and
configure compatible double-hires hardware. A single BIN load address cannot
describe this layout, and project memory checks currently cover main memory only.
