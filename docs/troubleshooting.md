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
| An old executable lacks current commands | Check `a2 --version`, rebuild the local package, and update the repository-local tool to 0.6.1-dev |
| `Package.ps1` fails on unsupported PowerShell features | Run it with PowerShell 7 (`pwsh`) |
| Missing `/usr/bin/stat` | Restore that system utility for Linux/macOS host-file checks; program input and directory import require it |
| Published executable cannot start on another computer | Use the complete self-contained package for that computer's OS/architecture, including runtime files |

Installation commands are in [getting started](getting-started.md), with
[external dependency setup](setup.md) covering MAME, ROMs, OS templates, and cc65.
Dependency
changes and accepted runtime identifiers are in [development](development.md).

## Environment checks and locks

Run `a2 env check environment.json --json` and read `data.checks` when
`data.ready` is false. Readiness failures exit 1; invalid JSON/profile errors use
the error envelope. A lock records file identity and does not replace these checks.

| Check or diagnostic | Check or action |
| --- | --- |
| `mame.path` / `roms.path` | Set existing paths relative to the profile or absolute paths. JSON does not expand shell variables or search `PATH`. |
| `mame.version` | Install MAME 0.289 and select its executable; changing an execution version field does not make other versions compatible. |
| `roms.verify` | Audit the chosen machine and devices using the [ROM setup commands](setup.md#configure-and-audit-roms); supply all required sets with matching checksums. |
| `template.verify` | Check the image, declared filesystem, and structural diagnostics. A clean structural check still needs a real boot test. |
| `cc65.version` / `cc65.distribution` | Probe `cl65 --version`; correct any exact version pin and set `cc65Root` to the distribution containing `include`, `asminc`, `lib`, and `cfg`. Compile a small program to test the libraries. |
| `setup.invalid_json` / `setup.invalid_profile` | Use `schemaVersion: 1` and only fields from `a2 schema environment`. `bootSeconds` must be between 1 and 120. |
| `setup.cc65_root` | Locking a configured compiler requires its complete distribution root. |
| `setup.link` | Select physical paths, including parent directories; package-manager symlinks cannot be locked. |
| `setup.lock_location` | Write the lock outside the ROM and cc65 trees and away from existing inputs. |
| `setup.invalid_lock` / `setup.lock_changed` | Review changed profile paths, files, hashes, or directory membership. Create a new lock for intentional changes and update both project and execution references. |
| `setup.lock_scope` | Use the emulator/ROMs/template/compiler selected by the locked profile, or create a separate profile and lock for overrides. |
| `setup.destination_exists` | Initialize into a new project directory; `init` does not merge into existing files. |
| `setup.starter_template` / `setup.starter_compiler` | Configure a bootable OS template; C additionally needs `cc65Path` and `cc65Root`. Use `--bare-metal --language asm` for an OS-free starter. |

If a starter builds but times out, confirm that the template reaches the
Applesoft/BASIC.SYSTEM prompt before `bootSeconds`, then inspect the retained
`result.json` and emulator logs. `AI.MAIN` must be absent from the input template.
Use a new run artifact directory; use `--overwrite` explicitly when rebuilding
an existing project output.

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

## Project manifests, schemas, and builds

Start manifest work with `a2 schema project --json`, and use `a2 targets
--json` for the exact target names, default CPUs, symbols, and reserved memory.
`a2 build PROJECT --check --json` compiles sources and reports memory and
metadata problems without creating an image. It still invokes cc65 when the
project contains a `cc65` source.

| Diagnostic or symptom | Cause and next step |
| --- | --- |
| `project.schema` | The manifest is malformed JSON, omits `schemaVersion: 1`, has a duplicate/unknown property, or uses the wrong JSON type. Compare it with `a2 schema project --json`; property names are case-sensitive. |
| `schema.unknown` | `a2 schema` accepts `project`, `project-resolution`, `diagnostic`, `execution`, `execution-suite`, `environment`, `disk-change-set`, `result`, `error`, or `envelope`. Use `a2 capabilities --json` to discover the current set. |
| `project.target` / `project.cpu` / `project.cpu_target` | The target or CPU is unknown or incompatible. Select a profile reported by `a2 targets --json`; stock project profiles do not accept WDC-only `w65c02` code. |
| `project.filesystem` / `project.geometry` / `project.disk_format` | Use `dos33` or `prodos`, a `raw` or `2mg` container, a `dos` or `prodos` order, and 280 blocks for DOS or 280–65,535 for ProDOS. A template's detected filesystem/order must agree with the manifest. |
| `project.timestamp` | Use an offset-free or UTC date/time from 1980 through 2039 at whole-minute precision. |
| `project.basic_check` | A numbered BASIC source failed the default-on conservative source checks. Run `a2 basic check SOURCE --json`, or fix the located diagnostics before rebuilding. Use `checkBasic: false` only when tokenizer-only validation is intentional. `basic-labels` always follows the prepare/check path and cannot opt out. |
| `project.kind` / `project.compiler` | Use one of the source kinds reported by `a2 capabilities --json`. A `cc65` file also requires a top-level `cc65` configuration. |
| `project.origin_required` / `project.metadata_conflict` / `project.file_type` / `project.entry_point` | Make `kind`, `type`, `origin`, `auxType`, and `entryPoint` describe the same payload. Raw BIN/BAS files need a load origin; compiled origins and AppleSingle metadata are authoritative. |
| `project.memory_range` / `project.memory_overlap` | Inspect the diagnostic's named ranges with `--check --json`. Move the payload/reservation, mark a mutually exclusive overlay `resident: false`, or adjust `basicWorkspaceBytes`. Set `checkMemory: false` only for a layout the program manages deliberately. |
| `project.basic_workspace` | Keep `basicWorkspaceBytes` between 0 and 65,536; its resulting resident range must also fit without overlapping another declared region. |
| `project.payload_limit` | Combined compiled payloads exceed 34 MiB. Remove unrelated/alternative files or split the build; filesystem allocation and catalog limits can fail below this host-side bound. |
| `project.duplicate_path` | Two files, or a generated startup file and another file, resolve to the same image path. Give every entry a distinct filesystem name; build duplicate checks are case-insensitive. |
| `project.startup` / `project.startup_template` / `project.startup_program` / `project.startup_type` / `project.startup_name` / `project.startup_origin` / `project.startup_entry` | Startup generation needs a supplied bootable template and must name one BAS or BIN manifest entry. BASIC must use the default origin; BIN's entry point must equal its load origin; names must be safe in the generated command. |
| `project.template_hash` | The template no longer matches `templateSha256`. Verify that the intended bootable image was selected and update the pin only after reviewing that image. |
| `project.entry_exists` | A template already contains the destination path. Set that file's `replace: true` only for an intentional replacement. The builder will not bypass a locked template entry; prepare and review a separate unlocked template first. |
| `project.source_changed` | A manifest, template, source, include, or compiler input changed during the build. Stop the competing writer and rebuild from stable inputs. |
| `project.validation` / `project.corrupt_image` | The staged image or its stored payload/metadata failed the reopen check. The previous output and template are preserved; retain the diagnostics and reproduce with the smallest project. |

Project paths normally resolve relative to the manifest. A command-line `--to`
override resolves relative to the current directory. Every build starts from a
fresh filesystem or a fresh copy of `disk.template`; it never incrementally
updates the preceding output. See [project builds](projects.md) for source-kind,
startup, template, and repeatability rules.

## cc65 and AppleSingle output

The compiler adapter is optional. Check `cl65 --version` independently before
diagnosing a source failure, and preserve the complete compiler distribution:
the executable version alone does not identify its libraries, headers, linker
configuration, or subprocess tools.

| Diagnostic or symptom | Cause and next step |
| --- | --- |
| `cc65.compiler_missing` | Install cc65 and put `cl65` on `PATH`, or pass `--compiler` with the executable path. In a project manifest, compiler paths resolve relative to the manifest. |
| `cc65.compiler_start` | The selected file exists but cannot be launched. Check execute permission, OS/architecture compatibility, and whether the complete cc65 installation is present. |
| `cc65.compiler` / `cc65.path` / `cc65.start` | Supply a nonempty executable/path and use direct, accessible paths. If a resolved executable still cannot start, check host policy and file access before changing source. |
| `cc65.version` / `cc65.version_mismatch` | `cl65 --version` failed, was empty, or differed from `--expected-version`. Supply the exact combined version text from the reviewed installation; do not weaken a version pin merely to pass a build. |
| `cc65.target` / `project.cpu_target` | Use `apple2` or `apple2enh`. The enhanced compiler target also needs an enhanced A2Utils machine target. |
| `cc65.options` / `cc65.define` / `cc65.sources` | Keep each source/include/define list at 128 entries or fewer, use distinct source paths, and spell defines as `NAME` or `NAME=value` without newlines. |
| `cc65.source_type` / `cc65.include_type` | Main sources must be `.c`, `.s`, `.asm`, or `.a65`. Text includes are `.c`/`.h` for C and `.s`/`.asm`/`.a65`/`.inc` for ca65; use `.incbin` for binary data. |
| `cc65.path_escape` / `cc65.source_path` | Keep sources and quoted includes beneath `--project-root`, outside hidden or excluded build directories, and use direct nonlinked paths. Narrow an overly broad project root rather than moving generated output into it. |
| `cc65.include` / `cc65.include_macro` / `cc65.include_path` / `cc65.include_syntax` | Include directories and literal relative include files must exist. Do not use unclosed/computed/macro filenames, absolute paths, backslashes in ca65 include names, continued ca65 lines, or `.feature`/`.linecont` compatibility switches. |
| `program.too_large` | An individual source or included file exceeds 4 MiB. Reduce or split that input. |
| `cc65.input_limit` | Reduce the isolated source tree: it is limited to 4,096 directories, 4,096 supported files, and 64 MiB total source data. |
| `cc65.compile_failed` | cl65 returned a compiler/linker error. Read the structured diagnostic and cl65 text; correct the source, startup/runtime declarations, or installed target files. The destination is not replaced. |
| `cc65.timeout` | The timeout is outside 1–3,600 seconds, or the compiler exceeded it and its process tree was terminated. Correct an invalid value; for an actual timeout, investigate a hung tool first or raise the bound deliberately. |
| `applesingle.version` / `applesingle.resource_fork` / `applesingle.metadata_range` | The result is not the supported AppleSingle v2 program representation, contains a resource fork that would be lost, or has metadata wider than Apple II fields. Preserve the original archive and use a supported data-fork program output. |

If cl65 succeeds but A2Utils rejects its file, retain the source, exact compiler
version, and full diagnostic details, then reproduce the cl65 invocation
independently if its temporary output must be inspected. The adapter validates
AppleSingle before committing the requested destination. See
[C and ca65 compilation](cc65.md) for the isolated-source rules and required
ca65 startup declarations.

## Graphics conversion and assets

| Diagnostic or symptom | Cause and next step |
| --- | --- |
| `png.invalid` | The PNG is oversized, truncated, has a bad checksum/chunk order/filter, or contains invalid compressed data. Re-export a complete static PNG; do not assume a file is valid from its suffix. |
| `png.unsupported` / `png.animation` / `png.critical_chunk` | Convert the source to a static, noninterlaced supported PNG: 8-bit RGB/RGBA/grayscale or 1/2/4/8-bit indexed/grayscale, without unknown critical chunks. |
| `graphics.mode` / `graphics.dimensions` / `graphics.length` | Select `lores`, `hires`, or `hires-color` and supply its exact documented PNG dimensions or raw page length. The command does not scale, crop, or guess a mode. |
| `graphics.asset_options` / `graphics.asset_dimensions` | Supply positive cell dimensions, 7 or 8 bits per byte, `lsb` or `msb`, threshold 0–255, and `sprite`, `tile`, or `font`. The PNG grid must divide exactly into those cells. |
| `graphics.asset_length` / `graphics.asset_padding` / `graphics.asset_size` | Packed input must contain complete cells in a rectangular preview, fit the 65,536-byte/2048×2048 limits, and keep unused row/high bits zero. Recreate the original layout options rather than discarding ambiguous bits. |
| `graphics.asset_codepoint` | Font labels must remain valid Unicode scalar values and cannot cross the surrogate range. Adjust `--first-codepoint` or reduce the glyph count. |
| `graphics.shape_schema` | The shape JSON is malformed, uses an unknown property/version, has no shapes, or contains duplicate/unnamed shapes. Compare it with the version-1 example in the graphics-assets guide. |
| `graphics.shape_direction` / `graphics.shape_empty` / `graphics.shape_limit` / `graphics.shape_encoding` | Use nonempty `up`, `right`, `down`, or `left` vectors with positive counts. Shorten an oversized table; a final nonplot-up vector cannot be represented exactly. |
| `graphics.dhires_options` / `graphics.dhires_dimensions` / `graphics.dhires_length` | Select `mono` or `color`, specify `aux-main` or `main-aux`, and provide exactly 560×192 or 140×192 pixels when encoding, or 16,384 raw bytes when decoding. |
| `invalid_arguments` with a graphics command | Remove disk-only `--input-order` or `--input-fs` overrides from asset, shape, and double-hires commands. They operate on host PNG/JSON/raw files, not disk images. |
| `graphics.source_changed` | The PNG, packed asset, raw page, or shape source changed while output was staged. Stop the competing writer and retry with stable input. |
| `graphics.validation` | The staged output hash did not match the computed result. The old destination is preserved; retain the input and diagnostic for investigation. |

Preview PNGs are interpretations, not preservation copies: unused screen holes,
padding, and analog NTSC effects may not round-trip. Use the raw source bytes when
those details matter. See [screen conversion](graphics.md) and
[graphics assets](graphics-assets.md) for exact dimensions, packing, and bank order.

## Execution engines and test suites

Once execution creates an artifact directory, `run` and `test` intentionally
retain it after a failed run. Inspect `result.json`, the `version.*.txt` and
`emulator.*.txt` logs that were reached, `screen.txt`, and any requested
`screen.png`/`trace.tsv` before retrying. CPU-engine runs instead retain
`engine.json`, routine evidence, observations, and an optional instruction trace.
A single run can reject invalid JSON or
specification data or an existing artifact path before creating its artifact
directory. MAME runs can also reject a missing disk or ROM directory at this stage.
A suite parses every case before
creating its root, but checks external disk/ROM paths per case, so its root and
earlier case evidence can exist when a later case is rejected. Because an existing
artifact directory is never reused, choose a new `--artifacts` path for the next run,
or pass `--run-subdirectory` with an existing parent to create a unique child.

| Diagnostic or result | Cause and next step |
| --- | --- |
| `execution.invalid_spec` | The JSON is malformed, has an unknown/duplicate property, omits fields required by its engine, or violates an assertion/time/range limit. Compare it with `a2 schema execution --json`; paths resolve relative to the specification file. |
| `execution.invalid_suite` | The suite is not version 1 with 1–128 specification paths, or a case lacks an assertion, bounded completion/debug/routine/cycle condition, or mounted-disk verification. Use `a2 schema execution-suite --json`; all cases are parsed before any starts. |
| `execution.artifacts_exist` | `--artifacts` must name a new file-system entry. Pick a new directory; A2Utils does not merge with or erase prior evidence. |
| `execution.filter_empty` | No suite case matched the supplied `--filter` values or their intersection with `--rerun-failed`. Use `--list` to inspect stable names and paths, then adjust the case-insensitive glob. |
| `execution.jobs` | `--jobs` must be an integer from 1 through 16. Lower it if emulator or host resources are constrained. |
| `execution.rerun_failed` | The prior path is missing, is not a readable suite result, or does not identify failures in the current suite. Point to an artifact directory or its `suite-result.json`; use the original suite when possible. |
| `execution.disk_missing` / `execution.rom_directory_missing` | Correct `diskImage` or `romDirectory` relative to the spec. A2Utils bundles neither a boot disk nor ROMs. |
| `execution.emulator_unavailable` | `emulatorPath` did not launch. Use a full path or a path relative to the spec, not only a command name expected to resolve through `PATH`; check execute permission and architecture. |
| `execution.version_mismatch` | The `-version` probe did not report the pinned MAME `0.289` API. Point to the tested executable and keep `expectedVersion` at `0.289`. |
| `execution.rom_mismatch` | MAME reported wrong, unverified, or redump ROM checksums. Read `emulator.stderr.txt` and audit the selected machine ROM set with MAME; do not suppress the checksum evidence. |
| `execution.emulator_error` | MAME exited nonzero. Inspect `emulator.stderr.txt` for an invalid machine/device option, missing ROM, or disk error. |
| `execution.adapter_error` / `execution.missing_observations` | The disk exceeds 64 MiB, host I/O failed, or the Lua observer failed, produced malformed/oversized output, or never completed. Keep any artifact directory, confirm MAME 0.289, inspect the emulator logs, and inspect `adapter-error.txt` when the Lua side created it. |
| `execution.screenshot_missing` | `screenshot: true` was requested but MAME did not create `screen.png`. Inspect the emulator logs and artifact-directory permissions. |
| `execution.host_timeout` | The host watchdog expired and terminated the emulator process tree. Fix startup/ROM problems or raise `hostTimeoutSeconds` within the 3,600-second limit. |
| `execution.cancelled` | The caller cancelled the run; the process tree was terminated and the CLI exits 6. Start a new artifact directory if the case should run again. |
| `execution.completion_timeout` | The `until` byte did not reach its value before `emulatedSeconds`. Check the boot path, key schedule, address/value, and ensure `afterSeconds` is earlier than the emulated deadline. |
| `execution.cpu_fault` | The CPU routine reached an opcode outside the selected documented 6502/65C02 set or execution evidence could not be completed. Inspect the diagnostic PC/opcode and `trace.tsv`; verify that `machine` selects the intended processor and that ROM/I/O calls are not required. |
| `execution.memory_assertion` / `execution.register_assertion` / `execution.text_assertion` | The machine completed but observed state differed. Compare `expected`/`actual` in JSON with `screen.txt`, mapped-memory assumptions, the selected `textPage`, and input timing. Text matching is case-sensitive and covers 40-column text memory, not OCR. |

Execution/assertion failures normally return a structured result on stdout and
exit 1; invalid specifications exit 2, and cancellation exits 6. A suite keeps
each case in `case-001`, `case-002`, and so on, and writes `suite-result.json`.
See [automated execution](execution.md) for the complete specification, memory
visibility limits, captured artifacts, and the verified MAME/ROM provenance.

## Reporting an unresolved problem

Record the application version, OS/architecture, full command, exit code, and
separate stdout/stderr. For disk issues, include `disk info` and `disk verify`
results, the image format, and whether a copy reproduces the problem. For
native compiler issues, include the smallest source or byte sequence that
demonstrates it and the selected CPU, format, and origin. For project builds,
include a reduced manifest, `build --check --json`, and `targets --json`. For
graphics, record the exact mode/layout options, input dimensions and hash. For
cc65 or MAME, include the external tool's exact version and the retained
compiler text or execution artifact directory; do not distribute ROMs or OS
images with the report. The current repository has no configured public issue
tracker; retain these details for the local project or the review where the
problem is being discussed.
