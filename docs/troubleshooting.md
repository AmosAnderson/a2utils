# Troubleshooting

[Documentation home](../README.md#documentation) · [Command reference](cli-reference.md)

Start with the diagnostic code and the exact command. `--json` exposes stable
error codes; `--verbose` adds detail for an interactive investigation. Keep
stdout and stderr separate when collecting evidence. The
[scripting guide](scripting.md) explains exit categories and verification results.

## Setup and installation

| Symptom | Check or action |
| --- | --- |
| `dotnet` is unavailable | Install the SDK required by `global.json` for source builds, or use a matching self-contained package |
| Compatible SDK cannot be found | Run `dotnet --list-sdks` from the repository root; the pin is 10.0.400 with patch roll-forward, not unrestricted .NET 10 roll-forward |
| Restore fails in locked mode | Check access to the source in `NuGet.Config`; if dependencies were intentionally changed, follow the lockfile workflow in the development guide |
| `a2` is not recognized | Invoke `./artifacts/tools/a2.exe` on Windows or `./artifacts/tools/a2` on Unix, or define the session function in getting started |
| An old executable lacks `asm` or `basic` | Check `a2 --version`, rebuild the local package, and update the repository-local tool to 0.4.0-dev |
| `Package.ps1` fails on unsupported PowerShell features | Run it with PowerShell 7 (`pwsh`) |
| Missing `/usr/bin/stat` | Restore that system utility for Linux/macOS host-file checks; program input and directory import require it |
| Published executable cannot start on another computer | Use the complete self-contained package for that computer's OS/architecture, including runtime files |

Installation commands are in [getting started](getting-started.md). Dependency
changes and accepted runtime identifiers are in [development](development.md).

## Paths and numeric arguments

Quote host filenames containing spaces. Image paths and host paths are
different namespaces: `--from-image work.po` selects a host disk file, while
its `INPUT` argument names an entry inside the image.

```sh
a2 asm decompile "PROGRAMS/HELLO" --from-image "My Disks/work.po" --to "Output/hello.asm"
```

The `Output` directory must exist for that command. Most file outputs require
an existing host parent; `disk extract` can create its missing parent paths
but requires its final extraction directory to be new.

Use `0x2000` for addresses on command lines. Bare `2000` means decimal 2000.
`--origin` also accepts `$2000`, but shells may expand `$` unless quoted.
Disk `--load-address` and `--aux-type` accept decimal or `0x` syntax, not the
assembler's complete expression syntax. In assembly source, `$2000` is hex.

DOS names are case-sensitive catalog names. ProDOS path components are
case-insensitive and use `/` separators. Use the path reported by `disk ls`.
ProDOS names do not accept arbitrary host filenames; directory import rejects
invalid names instead of silently renaming them. See the
[disk guide](disk-images.md) for name and path rules.

## Disk inspection and modification

| Diagnostic or symptom | Cause and next step |
| --- | --- |
| Ambiguous order/filesystem | Inspect the image's provenance; use `--input-order dos` or `prodos`, and `--input-fs dos33` or `prodos` only when justified |
| `unsupported_geometry` / `unsupported_order` | Compare the image with the supported format matrix; larger images require ProDOS block order |
| `write.destination_required` | Supply exactly one of `--output NEWIMAGE` and `--in-place` |
| `write.destination_exists` | Choose another host filename or use `--overwrite` for intentional host-file replacement |
| `write.source_alias` | The destination identifies an input; use a separate output, or `--in-place` for an image mutation |
| `destination_exists` during extraction | Choose a new extraction directory; extraction has no overwrite mode |
| `file_not_found` / `ambiguous_name` | List the parent catalog and use an exact supported image path |
| `file_locked` | Inspect `disk attr`; deliberately unlock the working image entry before changing it |
| `image_read_only` / `write.read_only` | Check host permissions and container write protection; an entry unlock does not clear either |
| `disk_full` | Free space or catalog capacity is insufficient; use a fresh/larger supported destination where appropriate |
| `directory_not_empty` | Deletion of a nonempty directory requires `--recursive` |
| `import_name_conflict` | An exact host name collides with an existing/planned entry; use an empty target or rename source files intentionally |
| `import_output_in_source` | Move the destination image outside the host directory being imported |
| `unsupported_directories` | DOS has no subdirectories; use a flat import or a ProDOS volume |
| `write.concurrent_change` | An input/output changed during staging; stop the competing writer, inspect both files, and rerun from a known state |
| `linked_path` / `write.link_refused` | A source, destination, or ancestor is a symbolic link/junction; use direct regular paths |
| `corrupt_image`, `unsafe_allocation`, or failed verification | Inspect a preserved copy; this preview does not repair or force writes through structural problems |
| Hybrid/embedded filesystem warning | More than one filesystem structure is present; writes are refused |

To update a locked file, work on a copy and unlock that copy's entry explicitly.
This example assumes `work.do` exists and contains `HELLO`:

```sh
a2 disk attr work.do HELLO
a2 disk attr work.do HELLO --unlock --output unlocked.do
a2 disk replace unlocked.do HELLO hello.bin --in-place
```

`--overwrite` replaces host output files; it does not authorize duplicate image
names, directory merges, or bypassing locks. `disk copy` and manifest restore
require matching source/destination filesystems. `disk convert` changes the
container/order, not DOS files into ProDOS files.

An `info` result with `isReadOnly: true` is normal for an inspection session.
Look separately at `imageWriteProtected`, entry lock flags, and host permissions.

## Exported bytes do not look like source

Choose the operation for the representation you need:

| Desired result | Command |
| --- | --- |
| Original stored bytes and metadata for restoration | `disk extract` |
| Logical payload without applicable DOS headers | `disk export` |
| Readable TXT content | `disk export --format text` |
| Assembly listing | `asm decompile` |
| Applesoft listing | `basic decompile` |

`.a2raw` payloads from extraction can include DOS headers and sector slack.
They are intended to accompany `a2-manifest.json`, not to be imported as a new
logical payload. A changed payload hash (`payload_hash_mismatch`) is an
integrity check: retrieve the matching original extraction, or import an
intentional edit as a logical payload with explicit metadata. Do not expect
manifest restoration to accept arbitrary edited bytes.

`text.unsupported_character` means the explicit ASCII text conversion cannot
represent a byte/character. Export binary or preserve raw bytes if the file is
not ordinary TXT. BASIC and binary files should use their own program tools.

## Assembly and program headers

| Diagnostic or symptom | Cause and next step |
| --- | --- |
| `program.origin_required` | A raw machine-code file has no load address; supply `--origin`, read its DOS header with `--format dos`, or use `--from-image` |
| `assembly.invalid_source` | Read the reported source line; check mnemonic/CPU, operand mode, label definitions, directive syntax, and numeric ranges |
| Branch target out of range | Relative branches need a signed byte displacement; reorganize control flow or explicitly write a nearby branch plus an absolute jump |
| Zero-page instruction/address rejected | The operand is outside `$00`–`$FF`, or that CPU/mode has no encoding; use a valid addressing mode |
| Forward operand used three bytes | Unknown forward labels retain absolute encoding where available; use `z:` explicitly only for a known zero-page target |
| A WDC instruction fails with `--cpu 65c02` | Apple-compatible 65C02 excludes WDC bit operations and WAI/STP; use `w65c02` only for a matching processor |
| `program.truncated_header` / `program.length_mismatch` | `--format dos` needs the exact header plus payload; check raw versus DOS format and remove no bytes speculatively |
| `program.address_range` / `assembly.address_overflow` | The selected origin and payload exceed the 16-bit address space |
| `program.file_type` | Direct image decompilation expects B/BIN for assembly or A/BAS for Applesoft |
| `program.invalid_utf8` | Save source as UTF-8, optionally with a UTF-8 BOM |

For an unsupported direct image type such as ProDOS `SYS`, export its logical
payload and disassemble it with a load address established from its format or
provenance. Changing its file type is not necessary just to inspect bytes.

If a disassembly is mostly strange instructions, check the CPU, load address,
and header selection. It may contain data: the disassembler scans sequentially
and cannot infer which bytes are instructions. Its preservation guarantee is
byte-for-byte reassembly using the same CPU, not recovery of original source.

## Applesoft BASIC

| Diagnostic or symptom | Cause and next step |
| --- | --- |
| `basic.line_number` | Each nonblank line needs a decimal number from 0 through 63999 |
| `basic.line_order` | Numbers must be strictly increasing; duplicates are rejected |
| `basic.empty_line` | A bare line number is an interactive deletion command, not a stored source statement |
| `basic.origin` | Use an origin from `$0100` through `$FFFE` that leaves room for the program |
| Invalid linked program / wrong-origin failure | Use the address at which its line links were stored; host DOS BASIC headers do not contain it |
| `basic.noncanonical_program` | The byte stream cannot survive listing and retokenization exactly; retain it with raw export/extraction |
| Line/source size error | Keep tokenized bodies at most 250 bytes and BASIC source at most 1 MiB |
| Keywords appear inside a variable name | Applesoft recognizes keyword sequences without modern word boundaries; choose a variable spelling that tokenizes as intended |

When using a nondefault BASIC origin, supply it again while compiling a
decompiled listing. Listings do not record the address. Strings, `REM`, and
`DATA` have different tokenization rules; see the examples in
[the program reference](programs.md).

## Reporting an unresolved problem

Record the application version, OS/architecture, full command, exit code, and
separate stdout/stderr. For disk issues, include `disk info` and `disk verify`
results, the image format, and whether a copy reproduces the problem. For
compiler issues, include the smallest source or byte sequence that demonstrates
it and the selected CPU, format, and origin. The current repository has no
configured public issue tracker; retain these details for the local project
or the review where the problem is being discussed.
