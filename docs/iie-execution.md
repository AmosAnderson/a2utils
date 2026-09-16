# Apple IIe memory and text observations

Execution specifications can identify physical RAM banks as well as the CPU's currently mapped memory. These observations do not toggle soft switches: the MAME adapter reads the RAM device's saved storage directly. This allows a test to distinguish bytes at the same address in main and auxiliary memory even when the program leaves auxiliary reads enabled.

| `bank` | Address range | Meaning |
| --- | --- | --- |
| `cpu` (default) | `$0000–$BFFF`, `$D000–$FFFF` | Current CPU mapping, including ROM when selected |
| `main` | `$0000–$BFFF` | Main RAM |
| `aux` | `$0000–$BFFF` | Auxiliary RAM |
| `lc1` | `$D000–$FFFF` | Main language-card bank 1 and shared upper 8 KiB |
| `lc2` | `$D000–$FFFF` | Main language-card bank 2 and shared upper 8 KiB |
| `aux-lc1` | `$D000–$FFFF` | Auxiliary language-card bank 1 and shared upper 8 KiB |
| `aux-lc2` | `$D000–$FFFF` | Auxiliary language-card bank 2 and shared upper 8 KiB |

Ranges may not cross the I/O/slot-ROM window at `$C000–$CFFF`. Language-card addresses use CPU addresses: `$D000` in bank 1 and bank 2 identifies distinct storage, while `$E000–$FFFF` is shared within main or auxiliary RAM. Reading `lc1` and `lc2` at `$E000` therefore returns the same bytes. Physical RAM remains observable when the CPU maps ROM at those addresses.

Physical observations support the pinned MAME `apple2e`, `apple2ee`, and `apple2c` profiles. The IIe profiles use the default extended 80-column card. The adapter requires the exact saved-item layout and fails if a device or saved item is unavailable or has an unexpected size. It does not silently substitute CPU-visible memory or support arbitrary replacement/expansion cards.

For example, add the following fields to an execution specification:

```json
{
  "memory": [
    { "bank": "main", "address": 8192, "hex": "1122" },
    { "bank": "aux", "address": 8192, "hex": "3344" },
    { "bank": "lc1", "address": 53248, "hex": "55" },
    { "bank": "lc2", "address": 53248, "hex": "66" }
  ],
  "observeMemory": [
    { "bank": "aux", "address": 8200, "length": 32 }
  ],
  "until": { "bank": "main", "address": 768, "value": 1 }
}
```

Assertions compare expected bytes; observations capture bytes for inspection without an expected value. Bank identity accompanies captured ranges, so equal addresses in separate banks remain distinct.
An assertion and observation may not have the same start address in the same bank.

## Text pages and MouseText

Set `decodeIIeText` to `true` for a 40-column IIe page, or set `textColumns` to `80` for an 80-column page. `textPage` continues to select page 1 or 2 explicitly. Capture reads the physical 1 KiB page from main memory, and auxiliary memory for 80 columns. Each 80-column pair consists of the auxiliary character followed by the main character. Unused bytes between Apple II text rows are excluded from decoded cells.

The actual alternate-character-set state is captured with the text memory. Decoded cells preserve their original byte, bank, offset within the page, row, column, and display mode (`normal`, `inverse`, `flash`, or `mousetext`). Flashing characters retain their character value and flashing attribute; the text representation does not animate them.

On `apple2ee` and `apple2c`, alternate-set bytes `$40–$5F` become the stable tokens `{MT:00}` through `{MT:1F}`. A token represents one screen cell even though its text is longer than one character. `mouseTextIndex` preserves the glyph index independently of text representation. These tokens avoid ambiguous Unicode approximations and differences between character ROM revisions. On unenhanced `apple2e`, the same bytes remain inverse uppercase characters. Alternate-set `$60–$7F` retains inverse lowercase; it is not treated as flashing punctuation.

Video flags describe the captured machine state: `altCharset`, `columns80`, `page2`, `store80`, `graphics`, `mixed`, `hires`, `doubleHires`, and `flash`. The selected text page and column count are explicit observations, not automatic screenshot interpretation. For the visible text page, MAME uses page 2 only when `page2` is enabled and `store80` is disabled. In graphics or mixed mode, text memory can contain bytes that are not currently displayed as text.

## Backend and validation references

The implementation follows the pinned MAME 0.289 sources:

- [Apple IIe RAM and language-card mapping](https://github.com/mamedev/mame/blob/mame0289/src/mame/apple/apple2e.cpp): bank 1's `$D000–$DFFF` lives at physical offset `$C000`, bank 2 at `$D000`; both use physical `$E000–$FFFF` for the upper range. The IIc's auxiliary RAM begins at offset `$10000` in its motherboard RAM device.
- [Extended 80-column card](https://github.com/mamedev/mame/blob/mame0289/src/devices/bus/a2bus/a2eext80col.cpp): 64 KiB saved `m_ram` storage.
- [RAM device](https://github.com/mamedev/mame/blob/mame0289/src/devices/machine/ram.cpp) and [Lua saved-item API](https://github.com/mamedev/mame/blob/mame0289/src/frontend/mame/luaengine.cpp): saved-byte storage and checked item indexing.
- [Video implementation](https://github.com/mamedev/mame/blob/mame0289/src/mame/apple/apple2video.cpp): main/auxiliary interleave, alternate characters, and saved video flags.
- [Apple Mouse Technical Note #6](https://mirrors.apple2.org.za/apple.cabi.net/FAQs.and.INFO/A2.TECH.NOTES.ETC/A2.CLASSIC.TNTS/mouse006.html): MouseText replaces one alternate inverse-uppercase range on enhanced IIe and IIc machines.

Automated tests cover physical address-range boundaries, invalid ranges, text row interleave, main/auxiliary column ordering, attributes, lowercase, MouseText indices, and malformed captures. These tests and source checks do not constitute a real-emulator validation run; that requires a configured MAME 0.289 installation and the appropriate ROMs.
