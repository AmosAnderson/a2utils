# Graphics conversion

Screen conversion works independently of an emulator. The output is a raw
display-memory page suitable for a BIN file at the appropriate address.

```sh
a2 graphics encode picture.png --mode lores --to picture.bin --json
a2 graphics decode picture.bin --mode lores --to preview.png
a2 graphics encode title.png --mode hires-color --to title.bin
a2 graphics decode title.bin --mode hires-color --to title-preview.png
```

| Mode | PNG dimensions | Raw size | Interpretation |
| --- | --- | --- | --- |
| `lores` | 40 × 48 | 1024 bytes | Two vertical pixels per byte; Apple II interleaved text-page layout. |
| `hires` | 280 × 192 | 8192 bytes | Monochrome dots, seven per byte, luminance threshold 128. |
| `hires-color` | 280 × 192 | 8192 bytes | Approximate artifact-color encoding/preview with per-byte phase and adjacent white dots. |

The converter requires exact dimensions; it does not silently scale or crop.
Page holes are initialized to zero on encoding. Decode displays visible pixels;
re-encoding a preview does not preserve unused holes or necessarily reproduce
the original color encoding. Use raw bytes for preservation.

Lo-res uses a fixed documented RGB palette exposed by `AppleGraphics.LoresPalette`.
Hi-res color selects a seven-dot pattern and phase that minimize local RGB error,
then refines it with neighboring groups. Previews approximate colors and do not
simulate a particular NTSC monitor, analog filtering, or all color fringes.
`hires` remains explicitly monochrome. Transparent PNG pixels are composited on
black. PNG color-profile/gamma metadata is not applied.

The PNG reader accepts static, noninterlaced 8-bit RGB/RGBA/grayscale, plus
1/2/4/8-bit indexed or grayscale files. It validates chunk ranges, checksums,
filters, and inflated size. Unsupported bit depths, interlacing, animation,
critical chunks, or invalid data produce diagnostics. The writer emits RGB PNG.
Inputs are bounded to 2048 × 2048 pixels and command input files to 4 MiB.
The codec follows the [PNG specification](https://www.w3.org/TR/png-3/).

Outputs are staged and validated. `--overwrite` permits replacing an existing
destination; conversion failures leave it intact. Source/output aliases and
linked paths are refused. JSON reports output path, size, hash, dimensions, and
rendering mode.

[Sprite, tile, font, shape-table and double-hires tools](graphics-assets.md)
provide additional asset layouts and explicit packing metadata.
