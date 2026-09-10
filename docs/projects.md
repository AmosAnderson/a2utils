# Building Apple II projects

`a2 build PROJECT --json` compiles a versioned manifest, checks source and load
metadata, and commits one validated disk image. Paths inside the manifest are
relative to the manifest's directory. `--to PATH` overrides the output relative
to the current directory. Output parents are created after source checks pass.

```json
{
  "schemaVersion": 1,
  "target": "apple2e",
  "output": "out/demo.po",
  "disk": { "fileSystem": "prodos", "volumeName": "DEMO" },
  "basicWorkspaceBytes": 1024,
  "files": [
    { "source": "main.asm", "path": "ROUTINE", "kind": "asm" },
    { "source": "hello.bas", "path": "HELLO", "kind": "basic" },
    { "source": "readme.txt", "path": "README", "kind": "text" }
  ]
}
```

```sh
a2 build examples/development/mixed.a2.json --to artifacts/mixed.po --json
a2 build examples/development/hires.a2.json --check --json
a2 targets --json
a2 capabilities --json
a2 schema project --json
```

Use `--overwrite` to replace a previous output. Every build starts from a fresh
data volume or the original template. It does not incrementally modify the
previous output. `--check` compiles and checks metadata/memory without producing
an image; it is not a disk-allocation or bootability test. cc65 checks still run
the compiler in temporary staging. The JSON Schema is bundled in the executable
and also available at [project.schema.json](schemas/project.schema.json).

Project JSON is strict and case-sensitive: unknown or duplicate properties are
errors. `schemaVersion` and a nonempty `files` array are required; omitted
optional properties use the defaults below. The manifest is limited to 1 MiB
and a JSON nesting depth of 32. It may contain at most 1,024 files and 1,024
explicit reservations. Each source is capped at 4 MiB, a template at 34 MiB,
and combined compiled payloads at 34 MiB. All addresses and block counts in JSON
are decimal numbers (8192 is `$2000`).

### Manifest properties

| Top-level property | Default and meaning |
| --- | --- |
| `schemaVersion` | Required; must be `1`. |
| `target` | `apple2e`; one of `apple2plus`, `apple2e`, `apple2enh`, or `apple2c`. |
| `cpu` | Target default; optional `6502` or `65c02`. A 65C02 CPU requires an enhanced target. |
| `output` | `build.po`; output path relative to the manifest, unless CLI `--to` overrides it. |
| `timestamp` | `2000-01-01T00:00:00`; reproducible file/directory/volume date in 1980–2039 at whole-minute precision. Use UTC or no offset, never a local offset. |
| `disk` | Defaults to a 140 KiB ProDOS raw data volume as described below. |
| `files` | Required array of 1–1,024 source entries, processed in order. |
| `reserve` | Empty array of additional named resident-memory ranges. |
| `checkMemory` | `true`; set `false` only when the program deliberately manages otherwise overlapping ranges. |
| `basicWorkspaceBytes` | `0`; extra bytes reserved immediately after each resident BASIC payload, range 0–65,536. |
| `startup` | `null`; optional generated BASIC launcher for a supplied OS template. |
| `cc65` | `null`; required when any file has `kind: "cc65"`. |

| `disk` property | Default and meaning |
| --- | --- |
| `fileSystem` | `prodos`; `prodos` or `dos33`. |
| `template` | `null`; existing image path relative to the manifest. When present, its container, size, volume metadata, boot data, and untouched files are retained. |
| `templateSha256` | `null`; optional 64-digit hexadecimal hash that must match the template before work begins. |
| `blocks` | `280`; new-volume size in 512-byte blocks. ProDOS accepts 280–65,535; DOS requires 280. |
| `container` | `raw`; `raw` or sector-data `2mg` for a new volume. A template keeps its existing container. |
| `order` | Filesystem default (`dos` for DOS, `prodos` for ProDOS); explicit `dos` or `prodos`. Larger new images require ProDOS order. With a template, this is an input-layout override and must match. |
| `volumeName` | `A2PROJECT`; name used when formatting a new ProDOS volume. |
| `volumeNumber` | `254`; DOS volume number 0–254 for a new DOS volume. |

Every `files` item requires `source` (host path relative to the manifest) and
`path` (relative image path using `/`). Empty components, `.`, `..`, a leading
slash, and backslashes are refused. File extensions do not select `kind`.

| File property | Default and meaning |
| --- | --- |
| `kind` | `binary`; one of `asm`, `basic`, `basic-labels`, `binary`, `text`, `applesingle`, `cc65`, `lores`, `hires`, or `hires-color`. |
| `type` | Inferred: BAS for BASIC, TXT for text, otherwise BIN. It must agree with kinds or wrapper metadata that constrain the type. |
| `origin` | `null`; load origin for assembled/BASIC/raw programs. Assembly can supply `.org`; BASIC defaults to 2049 (`$0801`); graphics use 1024 or 8192. |
| `auxType` | `null`; explicit ProDOS auxiliary value (or equivalent DOS load metadata). When a payload has an origin, the two values must agree. |
| `entryPoint` | Origin by default; when explicit, it must fall inside the payload. It is reported metadata and does not relocate code. |
| `replace` | `false`; authorize replacement of the exact entry in a template. It is unrelated to host `--overwrite`. |
| `resident` | `true`; include the load range in simultaneous-memory checks. |
| `checkBasic` | `true`; run the conservative source checker for `basic`. Tokenizer validation always runs, and `basic-labels` is always prepared and checked. |

The optional `startup` object requires `program`, the image path of one manifest
file. `path` defaults to `HELLO` and names the launcher entry; `replace` defaults
to `false`. The optional `cc65` object defaults to compiler `cl65`, target
`apple2`, no expected-version pin, a 60-second timeout, optimization enabled,
and empty `additionalSources`, `includes`, and `defines` arrays. Each list accepts
at most 128 entries. See [C and ca65](cc65.md) for path and isolation rules.

## Sources and metadata

| Kind | Input and disk behavior |
| --- | --- |
| `asm` | Native assembly; includes/incbin supported. Origin comes from assembly and must agree with explicit `origin` or `auxType`. Type BIN. |
| `basic` | Numbered Applesoft, checked then tokenized. Default origin `$0801`, type BAS. `checkBasic: false` skips the optional checker, retaining tokenizer validation. |
| `basic-labels` | Unnumbered Applesoft with `@labels`, prepared and checked before tokenization; mappings retain original source lines. |
| `binary` | Raw payload. BIN/BAS requires an explicit `origin` or `auxType`; SYS loads at `$2000`. Other data types can omit a load address. |
| `text` | UTF-8 Apple text conversion for the selected filesystem. Type TXT. |
| `applesingle` | AppleSingle v2 data fork and ProDOS type/auxiliary metadata. Nonempty resource forks are refused. |
| `cc65` | C/ca65 source compiled with the optional [cc65 adapter](cc65.md); its AppleSingle metadata follows the payload. |
| `lores`, `hires`, `hires-color` | PNG converted to a raw display page; default address `$0400` for lo-res and `$2000` for hi-res, type BIN. |

`path` is the intended image path. ProDOS parents are created as needed. Each
file can set `replace: true` to replace that entry in a template; locked entries
remain protected. Replacing a different file type recreates the entry with the
new representation. Host `--overwrite` and image-entry `replace` are separate.
The builder verifies payload bytes, file type, and applicable load metadata
after reopening the staged image. AppleSingle program import does not restore
all archive attributes, dates, comments, or arbitrary wrapper entries.

`entryPoint` defaults to the payload origin and, when supplied, must lie within
the payload. It is reported for runners/loaders; it does not relocate code or
change what DOS BRUN executes. File extensions do not select formats.

## Targets and memory

Targets are `apple2plus`, `apple2e`, `apple2enh`, and `apple2c`. The first two
default to 6502; the latter two to Apple-compatible 65C02. A 6502 project may
run on an enhanced target. WDC-only instructions are refused by these machine
profiles. `a2 targets --json` includes platform symbols and runtime reservations.

The checker compares resident payload ranges, optional BASIC workspace, and
explicit reservations. For DOS it conservatively reserves `$9600..$FFFF`; for
ProDOS it reserves `$BF00..$FFFF`. It also reserves zero page/stack/system
workspace and display page 1. A lo-res asset can intentionally occupy that
display page. The DOS limit is a conservative development policy, not a claim
that every DOS configuration allocates those exact bytes.

```json
{
  "reserve": [{ "name": "HGR page", "start": 8192, "length": 8192 }],
  "basicWorkspaceBytes": 1024
}
```

Use `resident: false` for files that are not loaded simultaneously, such as
overlays or alternative programs. `checkMemory: false` explicitly disables
overlap checking for layouts managed by the application. The checker does not
infer dynamic allocations, prove stack safety, model bank switching, or prove
C runtime safety: linker maps report additional BSS/stack/zero-page regions that
need application review. Double-hires assets need an explicit auxiliary/main
bank loader and are handled by the standalone graphics commands.

Shared constants and executable projects are under
[examples/development](../examples/development/README.md). ROM calls can alter
registers and flags; use the cited machine manuals for complete contracts.

## Templates and startup

New disks are formatted data volumes without an operating system. To retain
boot code and system files, set `disk.template` to a known bootable disk of the
same filesystem. `templateSha256` can pin its exact contents. Builds copy and
validate the template, preserving the original. The result reports
`template-preserved-unverified` until a separate execution test establishes
boot behavior.

An optional `startup` object writes a BASIC launcher:

```json
{
  "disk": { "fileSystem": "prodos", "template": "system.po" },
  "startup": { "path": "STARTUP", "program": "ROUTINE", "replace": true }
}
```

The template must already boot the named launcher. ProDOS BASIC launchers need
BASIC.SYSTEM. DOS templates need the appropriate boot greeting name. Launchers
use RUN for BASIC and BRUN for BIN; they do not install an operating system or
change boot-sector settings. Use [execution tests](execution.md) to validate
the intended machine and startup path.

## Repeatability and failures

The default timestamp is `2000-01-01T00:00:00`. Override it with an offset-free
or UTC time between 1980 and 2039 at whole-minute precision. New file/directory
and volume timestamps are normalized; template files not replaced are retained.
Identical native sources, options, and template produce byte-identical images.

JSON reports include the output SHA-256, source/dependency hashes, tool version,
CPU/target, per-file hashes and addresses, symbols/source maps, and resident
memory regions. A check-only build writes no image and reports `sha256: ""`.
Native assembly hashes include included source and binary files.
Inputs are rechecked before commit. cc65 additionally depends on its external
distribution and environment; see its reproducibility limits in [cc65.md](cc65.md).

Any compile, validation, capacity, lock, or concurrent-input failure leaves the
existing output and template intact. Source/output aliases and linked host
paths are refused. Intermediate payloads remain in memory; the complete image
uses the existing staged transaction layer.
