# Independent disk fixtures

## DOS 3.3

`Generate-Fixtures.ps1` constructs the fixture bytes directly with PowerShell arrays.
It does not call DiskArc, CiderPress, A2Utils, or a disk-formatting tool. All file
contents and unused-sector patterns are original synthetic test data. No Apple
boot code or third-party software is included; the disks are intentionally not
bootable. These fixtures may be redistributed with this repository.

The DOS layout follows the VTOC, catalog, and track/sector-list descriptions in
the [Apple II DOS mini-manual by Neil Parker and Rubywand](https://mirrors.apple2.org.za/apple.cabi.net/FAQs.and.INFO/A2.CSA2.FAQS.IN.HTML/apple.II.dos.and.prodos.mi.html).
The order permutation is independently checked against the logical-sector
tables in [AppleWin's disk image implementation](https://github.com/AppleWin/AppleWin/blob/master/source/DiskImageHelper.cpp).
These references describe formats; their source code and prose are not embedded
in the fixture generator.

Both images represent DOS 3.3, volume 42, with 35 tracks and 16 256-byte sectors
per track. `.do` stores DOS sector order; `.po` stores ProDOS sector order.
Track 17 contains the VTOC and fifteen catalog sectors. Tracks 0–2 are reserved.
Track 18 sectors 12–15 contain two files, each using one data sector and one
track/sector-list sector. The remaining free sectors contain their track/sector
numbers followed by `5A` bytes, making unintended writes observable.

| Name | DOS type | Locked | Logical bytes | Load address | Data sector |
| --- | --- | --- | --- | --- | --- |
| `HELLO.BIN` | B (`04`) | Yes | `00 7F 80 FF 0D` (5) | `2000` | 18/14 |
| `README` | T (`00`) | No | `C1 B2 D5 D4 C9 CC D3 8D` (8) | — | 18/12 |

The binary's raw sector starts `00 20 05 00`, followed by its payload, followed
by 247 `CC` bytes. The text sector ends with zero padding. Tests derive corrupt
catalog cycles, invalid pointers, and allocation conflicts from temporary
copies; reference fixture files are never modified.

Regenerate from the repository root with `pwsh -File tests/TestData/Generate-Fixtures.ps1`.
Expected SHA-256 hashes:

```text
51af1da57dfe814ce1323390f4ad3b3e6b247f81869fc8b83d47d4f2a78ef58c  independent-dos33.do
eb9b9baf41a3c097c716ac0a2e0ea2bac6f1424c879c24ed90c9b9d00e88e8c7  independent-dos33.po
```

## ProDOS

`Generate-ProdosFixtures.py` constructs `independent-prodos.po` with Python's
standard library. It writes the directory records, allocation bitmap, split-byte
index pointers, and original payloads directly; it does not call DiskArc,
CiderPress, A2Utils, or another disk formatter. The format reference is Apple's
[ProDOS 8 Technical Reference, Appendix B](https://prodos8.com/docs/techref/file-organization/),
especially figures B-3 through B-8. The index diagrams specify 256 low pointer
bytes followed by 256 high pointer bytes; the page's 0–127/128–255 prose is a
transcription error. No reference text, boot code, operating system, or third-party
program is included in the fixture. Its original synthetic contents are
redistributable with this repository.

The image is a 280-block ProDOS-order data volume named `INDEPENDENT`. Blocks
0–1 are zero-filled reserved boot space, blocks 2–5 are the root directory, and
block 6 is the bitmap. Exactly 49 blocks are allocated and 231 are free. Free
blocks start with their little-endian block number and otherwise contain `5A`,
so unintended changes are visible. Both directories have valid parent-entry
pointers. Their reserved bytes follow the original specification's zero-filled
representation; the engine may emit informational reserved-field notes.

| Entry | EOF | Storage and independently assigned blocks |
| --- | ---: | --- |
| `SEED` | 5 | Seedling, block 32; bytes `00 7F 80 FF 0D`; access `21` |
| `EDGE512` | 512 | Seedling, block 33; byte values 0–255 repeated twice |
| `SAP513` | 513 | Sapling index 34; fragmented data blocks 35 and 190 |
| `SPARSE` | 1,537 | Sapling index 36; logical blocks 0/3 map to blocks 37/200; logical blocks 1/2 are holes |
| `TREE` | 131,073 | Master index 38, indices 39/40; logical blocks 0/255/256 map to 41/201/42 |
| `FULLDIR` | 512 | Directory block 43 with twelve empty seedling files `F00`–`F11`, data blocks 44–55 |
| `GROWNDIR` | 1,024 | Directory blocks 56/180 with thirteen one-byte files `G00`–`G12`, data blocks 57–69 |

All data files have type `BIN` and auxiliary type `2000`; all entries except
`SEED` have access `E3`. Creation time is 1993-07-16 14:35 and modification time
is 1994-02-03 08:09. `G00`–`G12` contain bytes `80`–`8C` respectively.

Regenerate only this fixture from the repository root with:

```sh
python3 tests/TestData/Generate-ProdosFixtures.py
```

Expected SHA-256 hashes (payloads follow ProDOS EOF, excluding block slack):

```text
806223a7a89ea1581cb4628151b6e83a3d8db706dab306f60a1aec5efd544c0e  independent-prodos.po
0eaa9ec06bea17a38488c995b9ab650f838a738d2c361750cb2fe74680868254  SEED
110009dcee21620b166f3abfecb5eff7a873be729d1c2d53822e7acc5f34eb9b  EDGE512
f528d1cc7ca16650c934e76a98384b82d4ac57e991629cc719029a3e98dce00b  SAP513
93fe567abe8c81307a3aa72c7a94d11f70e5d85ab29b6ad64bd0cb0e38966a1a  SPARSE
eb5b8b750906fe68292ac4d67dca095743574fbc3a8d5a51f2521a34cb32dc30  TREE
```

`IndependentProdosTests` verifies these structures and payloads, directory growth,
file growth across the sapling/tree boundary, and untouched blocks during staged
edits. Temporary variants fill all 51 root entries to test rollback and expand
the independently generated image to a valid 65,535-block volume. Expansion
rewrites the bitmap and volume size directly, adds a file in block 65,534, and
optionally appends a marked unused host block 65,535. No 32 MiB binary is tracked.

The deterministic malformed-image corpus changes header/index/bitmap fields,
introduces directory cycles and cross-linked allocations, and truncates input.
Each read/verify probe runs in a separate process with a five-second deadline;
the complete corpus has a sixty-second deadline. Clean completion or a classified
`DiskException` is acceptable; hangs, crashes, unexpected exceptions, and changed
input bytes fail the test. These are bounded mutation checks, not exhaustive
fuzzing or evidence that corrupt images are repairable.

## Optional real DOS and ProDOS interoperability

`DiskOperatingSystemSmokeTests` runs only when `A2_MAME_PATH`, `A2_MAME_ROMS`, and
the relevant `A2_DOS33_SMOKE_DISK` or `A2_PRODOS_SMOKE_DISK` environment variable
are set. Use a local writable-compatible bootable 140 KiB DOS 3.3 or ProDOS disk
that reaches an Applesoft/BASIC.SYSTEM prompt at the volume root within ten
emulated seconds, with `A2.SMOKE` and `A2.RESULT` absent and enough free space.
Use at most eleven existing root entries so the staged program and catalog fit
on one 40-column text screen without paging.
The test requires MAME 0.289 and the enhanced Apple IIe ROM configuration described
in [execution setup](../../docs/execution.md). Templates and ROMs are user-supplied;
they are never added to the repository or modified by the test.

Each test stages original assembly into the template with a filename extension
matching its detected container and sector order. A first isolated run asks the
actual operating system to `CATALOG` and observes the staged filename before any
load command can echo it. A second isolated run asks the OS to `BLOAD` and `CALL`
the program, then `BSAVE`s an eight-byte result.
It checks memory and the saved file's bytes/type/load address after MAME exits,
and verifies that the template's SHA-256 is unchanged. Missing configuration is
reported as a skip. Process-contract tests do not count as real OS evidence.
