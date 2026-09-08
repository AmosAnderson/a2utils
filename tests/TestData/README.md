# Independent disk fixtures

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
