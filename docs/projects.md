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
memory regions. Native assembly hashes include included source and binary files.
Inputs are rechecked before commit. cc65 additionally depends on its external
distribution and environment; see its reproducibility limits in [cc65.md](cc65.md).

Any compile, validation, capacity, lock, or concurrent-input failure leaves the
existing output and template intact. Source/output aliases and linked host
paths are refused. Intermediate payloads remain in memory; the complete image
uses the existing staged transaction layer.
