# Command-line reference

[Documentation home](../README.md) · [Disk workflows](disk-images.md) · [Program tools](programs.md)

Examples use the installed executable `a2`. See [getting started](getting-started.md)
for installation or running through `dotnet`. `IMAGE`, `HOSTFILE`, and other
uppercase words below are placeholders; brackets mark optional arguments.
Quote host and image paths containing spaces. Use `a2 COMMAND --help` for local
help, for example `a2 disk add --help`.

## Development workflow commands

| Command | Purpose and guide |
| --- | --- |
| `build PROJECT [--to IMAGE] [--overwrite] [--check]` | [Project manifests](projects.md), source checks, memory reports, and transactional disk output. |
| `capabilities --json` | Discover commands, arguments, options, formats, targets, and schema names. |
| `targets --json` | Inspect machine profiles, platform symbols, and runtime memory reservations. |
| `schema NAME --json` | Return project, diagnostic, execution, or execution-suite JSON Schema under `data`. |
| `asm listing INPUT --to OUTPUT` | Assembly listing with addresses and bytes; accepts origin/CPU options. |
| `asm map INPUT --to OUTPUT` | JSON symbols/source-map/dependency report. |
| `basic check INPUT` | [Applesoft source checks](basic-development.md); failures return a result with diagnostics. |
| `basic renumber INPUT --to OUTPUT` | Safe line-reference rewriting and mappings; `--start` and `--step` default to 10. |
| `basic prepare INPUT --to OUTPUT` | Unnumbered source with symbolic labels to numbered Applesoft, with source mappings. |
| `cc compile INPUT --to OUTPUT` | [Optional cc65 compiler](cc65.md); AppleSingle output, maps, labels, and input hashes. |
| `run SPEC --artifacts NEWDIR` | [MAME execution](execution.md) with bounded input, assertions, and captured state. |
| `test SUITE --artifacts NEWDIR` | Execute a list of run specifications and aggregate results. |
| `graphics encode INPUT --mode MODE --to OUTPUT` | [PNG to display memory](graphics.md); lores, hires, or hires-color. |
| `graphics decode INPUT --mode MODE --to OUTPUT` | Display-memory preview as PNG. |
| `graphics assets`, `graphics shapes`, `graphics dhires` | [Sprites, fonts, tiles, shape tables and double-hires](graphics-assets.md); consult subcommand help. |

Development commands accept the usual `--json` output option. File-producing
commands require explicit overwrite; run/test use a new artifact directory.

## Global options and value conventions

| Option | Meaning |
| --- | --- |
| `--json` | Write schema-versioned result JSON to stdout and error JSON to stderr. |
| `--quiet` | Suppress normal text results; JSON results and diagnostics are still emitted. |
| `--verbose` | Include additional diagnostics on stderr in text mode; JSON diagnostics stay structured. |
| `--input-order dos\|prodos` | Override the input image's sector/block order. |
| `--input-fs dos33\|prodos` | Select a supported filesystem when opening an image. |
| `-h`, `-?`, `--help` | Show help for the selected command. |
| `--version` | Show the executable version at the root: `a2 --version`. |

Global options can follow subcommands. Layout/filesystem overrides describe the
image being opened: for `disk copy`, this is the destination `IMAGE`; use the
source-specific options for `--from`. Program commands accept image overrides
only with `--from-image`. `disk create` uses its own `--fs` and `--order` options;
`disk convert` uses `--input-order` but does not use `--input-fs`.

Use the documented lowercase spellings for commands and choice values such as
`dos33`, `prodos`, `binary`, `text`, `raw`, `dos`, `2mg`, and `65c02`. The CLI
container value is `2mg`, including when the filename ends in `.2img`. File-type
names are case-insensitive; accepted names are listed under
[file types](disk-images.md#file-types-and-addresses).

| Value | Accepted syntax |
| --- | --- |
| `--size` | Positive whole-number KiB, optionally suffixed `k`, or whole MiB suffixed `m`; suffixes are case-insensitive. `140`, `140k`, `800k`, `16m`. |
| `--blocks` | Decimal number of 512-byte blocks. |
| `--volume-number` | Decimal integer, 0–254. |
| Disk `--load-address`, `--aux-type` | Decimal or `0x` hexadecimal, 0–65535; e.g. `8192` or `0x2000`. |
| Program `--origin` | Decimal, `0x` hexadecimal, or `$` hexadecimal, 0–65535. Quote `$2000` in shells that expand `$`. |
| Numeric `--type` | Hexadecimal `0x00`–`0xff`; decimal file-type values are not accepted. |

`--size` does not accept fractions, `kb`, or `mib`. Image order and filesystem
are separate concepts: a `.po` file can contain DOS 3.3.

## Shared disk write options

These apply to `add`, `replace`, `import`, `copy`, `move`, `delete`, `rename`,
`mkdir`, and `attr` when changing attributes.

| Option | Meaning |
| --- | --- |
| `--output NEWIMAGE` | Write a separate image, leaving `IMAGE` unchanged. |
| `--in-place` | Replace `IMAGE` after validation and create `IMAGE.<unique-id>.bak`. |
| `--overwrite` | Permit replacement of an existing host output file. |

Specify **exactly one** of `--output` or `--in-place`. `--output` must not refer
to the input image. `--overwrite` does not authorize overwriting an existing
entry *inside* an image, bypass locks, or make an unsupported image writable.
Output file parent directories must already exist. Read the
[transaction guarantees](disk-images.md#write-transactions-and-backups) before
choosing in-place mode.

## Inspect and export disks

| Command | Options and behavior |
| --- | --- |
| `disk info IMAGE` | Report container, order, filesystem, volume, capacity, free space, and diagnostics. No command-specific options. |
| `disk ls IMAGE [PATH]` | Default: root catalog. A file path lists that entry; a directory path lists its children. `--recursive` includes descendants. |
| `disk verify IMAGE` | Check structure without repair; exits 4 when structural errors are found. |
| `disk attr IMAGE PATH` | Read one entry's type, auxiliary type, and access flags when no changes are supplied. Do not supply write options in read mode. |
| `disk extract IMAGE [PATH] --to DIR` | Extract stored bytes and `a2-manifest.json`. Default selection: the whole volume. Directories are always recursive; there is no `--recursive` option. `DIR` must be new; missing host parents are created. No overwrite mode. |
| `disk export IMAGE PATH --to HOSTFILE` | Export one logical file payload. `--format binary` is the default; `--format text` converts TXT content to UTF-8 LF. `--overwrite` permits replacement of an existing host file. |

`extract` and `export` serve different purposes: see
[preservation and logical export](disk-images.md#export-or-extract).

## Create and convert images

```text
a2 disk create OUTPUT --fs dos33|prodos [options]
a2 disk convert INPUT OUTPUT --container raw|2mg --order dos|prodos [options]
```

| `create` option | Default and restrictions |
| --- | --- |
| `--fs dos33\|prodos` | Required. |
| `--size SIZE` | `140k`; mutually exclusive with `--blocks`. DOS requires 140 KiB. |
| `--blocks COUNT` | Exact capacity; 280–65,535 for ProDOS, 280 for DOS. |
| `--container raw\|2mg` | `raw`. |
| `--order dos\|prodos` | `dos` for DOS, `prodos` for ProDOS. Capacities over 140 KiB require `prodos`. |
| `--volume-name NAME` | `UNTITLED`; ProDOS volume name. |
| `--volume-number NUMBER` | `254`; DOS volume number. |
| `--overwrite` | Replace an existing output file after validation. |

Creation produces formatted **data volumes without boot code**. It does not
resize an existing image or install DOS/ProDOS system files.

`convert` requires both output `--container` and output `--order`. Its additional
options are `--overwrite` and `--allow-metadata-loss`. Every 2IMG-to-raw conversion
requires `--allow-metadata-loss`, even when there are no optional comments.
Use `--input-order` when a raw input cannot be detected reliably. Conversion
requires a separate output; it has no `--in-place` or `--output` option and
retains the filesystem. See [format limits](formats/supported-images.md).

## Add, replace, and restore files

```text
a2 disk add IMAGE HOSTFILE --name PATH --type TYPE WRITE_OPTIONS
a2 disk add IMAGE HOSTFILE --name PATH --format text WRITE_OPTIONS
a2 disk add IMAGE --manifest DIR/a2-manifest.json WRITE_OPTIONS
a2 disk replace IMAGE PATH HOSTFILE [--format binary|text] WRITE_OPTIONS
```

| `add` option | Behavior |
| --- | --- |
| `--name PATH` | Required for a host payload; exact new filename or ProDOS path. Its parent must exist. |
| `--type TYPE` | Required for binary import; defaults to `TXT` for text import. |
| `--format binary\|text` | Default `binary`, meaning logical payload bytes. `text` requires the T/TXT type. |
| `--load-address ADDRESS` | Required for DOS B/BIN payloads; invalid for other types or ProDOS. |
| `--aux-type VALUE` | ProDOS auxiliary type; default 0. Cannot accompany `--load-address`. |
| `--manifest FILE` | Restore into the same filesystem type. Cannot accompany `HOSTFILE`, `--name`, `--type`, `--format`, `--load-address`, or `--aux-type`. |

`replace` takes only `--format` and shared write options. It uses the existing
entry's type and load address/auxiliary type. Text replacement requires an
existing T/TXT file. Neither command automatically decodes DOS host headers:
use program-tool **raw** output as the payload for `add` and `replace`.

## Import host directories

```text
a2 disk import IMAGE HOSTDIRECTORY [options] WRITE_OPTIONS
```

| Option | Behavior |
| --- | --- |
| `--to IMAGEPATH` | Existing destination directory; default `/`, the image root. Imports the host directory's **contents**. |
| `--recursive` | Include host subdirectories on ProDOS. Without it, encountering a subdirectory fails the operation. DOS always requires a flat host directory. |
| `--format binary\|text` | Default `binary`; `text` explicitly converts every file. |
| `--type TYPE` | Type for every file; required for binary, defaults to `TXT` for text. |
| `--load-address ADDRESS` | Required for DOS B/BIN import; same restrictions as `add`. |
| `--aux-type VALUE` | ProDOS auxiliary type for every file; default 0. |

Names must be representable exactly. Existing names, even existing directories,
cause a conflict; directories are not merged. The operation commits as a single
transaction. See [import restrictions](disk-images.md#import-a-host-directory).

## Copy, move, rename, and delete

| Command | Options and semantics |
| --- | --- |
| `disk copy IMAGE SOURCE DESTINATION` | `IMAGE` receives the copy. By default, `SOURCE` is in that image. `--from SOURCEIMAGE` selects another image of the same filesystem type. `--recursive` is required for a directory. |
| `disk move IMAGE SOURCE DESTINATION` | Move within the same image, retaining file-data allocation. Directory moves include their contents; there is no recursive flag. |
| `disk rename IMAGE PATH NEWNAME` | Rename in the current directory; `NEWNAME` must not contain `/` or `\`. |
| `disk delete IMAGE PATH` | Remove an entry. Nonempty directories require `--recursive`; empty directories do not. |
| `disk mkdir IMAGE PATH` | ProDOS only. `--parents` creates missing ancestors and accepts existing directories. Without it, the parent must exist and the leaf must be new. |

All commands in this table use the shared write options. Copy additionally
accepts `--source-order dos|prodos` and `--source-fs dos33|prodos`, only with
`--from`. Source overrides must agree with destination detection when both
refer to the same image.

Copy and move destinations are **exact new paths**, including the new leaf
name; the parent must exist. Passing an existing directory does not mean “put
the source inside this directory.” Existing destinations, volume-root
copy/move/delete/rename, and moving or copying a directory into itself or a
descendant are refused. Directory depth is limited to 256 levels.

## Change attributes

```text
a2 disk attr IMAGE PATH [--lock | --unlock] [--type TYPE] [--aux-type VALUE] WRITE_OPTIONS
```

Supply at least one change and a write destination. `--lock` and `--unlock`
are mutually exclusive. `--aux-type` sets the ProDOS auxiliary value or a DOS
binary's load address. Changing type/auxiliary metadata on a locked entry
requires `--unlock`, which can be part of the same command. Changing metadata
does not compile, tokenize, or convert the program content.

## Assemble and disassemble machine code

```text
a2 asm compile INPUT --to HOSTFILE [--origin ADDRESS] [--cpu CPU] [--format raw|dos] [--overwrite]
a2 asm decompile INPUT --to SOURCEFILE [--origin ADDRESS] [--cpu CPU] [--format raw|dos] [--from-image IMAGE] [--overwrite]
```

Aliases: `asm assemble` = `asm compile`; `asm disassemble` = `asm decompile`.

| Option | Behavior |
| --- | --- |
| `--to FILE` | Required host output. |
| `--cpu 6502\|65c02\|w65c02` | Default `6502`. `65c02` selects Apple enhanced IIe/IIc instructions; `w65c02` additionally enables WDC bit instructions and WAI/STP. |
| `--origin ADDRESS` | Compile: supplied here or by source `.org`. Decompile: required for raw host input; otherwise inferred from DOS header or image metadata unless overridden. |
| `--format raw\|dos` | Default `raw`. Compile controls output binary; decompile describes input binary. `dos` includes/reads a four-byte binary load-address and length header. |
| `--from-image IMAGE` | Decompile only: `INPUT` is a DOS B or ProDOS BIN entry path. Cannot accompany `--format`. |
| `--overwrite` | Allow replacing an existing host output file. |

Source is UTF-8, with an optional BOM. Decompilation produces reassemblable
source, without recovering original labels/comments or identifying code/data.
See [program tools](programs.md) for grammar, directives, address rules, and
round-trip limits.

## Tokenize and list Applesoft BASIC

```text
a2 basic compile INPUT --to HOSTFILE [--origin ADDRESS] [--format raw|dos] [--overwrite]
a2 basic decompile INPUT --to SOURCEFILE [--origin ADDRESS] [--format raw|dos] [--from-image IMAGE] [--overwrite]
```

Aliases: `basic tokenize` = `basic compile`; `basic detokenize` = `basic decompile`.

The input to compile is a numbered UTF-8 Applesoft listing. `--origin` defaults
to `0x0801`; image decompilation uses a nonzero ProDOS auxiliary type when
available, otherwise `0x0801`. Explicit `--origin` takes precedence.
`--format raw` is the default; `dos` adds/reads a two-byte length header.
With `--from-image`, `INPUT` must be a DOS A or ProDOS BAS file and `--format`
must be omitted. There is no `--cpu` option for BASIC. Output is always a host
file, with existing outputs requiring `--overwrite`.

These commands handle Applesoft tokens, not machine-code compilation or Integer
BASIC. Host source is capped at 4 MiB, program binaries at the 64 KiB address
space (plus host headers), and the selected origin must leave room for the
program. See [program tools](programs.md) for additional BASIC constraints.

## Exit status and automation

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Behavioral assertion or unexpected failure |
| 2 | Usage, invalid arguments, or source syntax |
| 3 | Unsupported or ambiguous format/feature |
| 4 | Corruption or failed data validation |
| 5 | Host filesystem I/O or permission error |
| 6 | Refused operation or cancellation |

Always check the exit status. `verify` can emit a normal result with
`data.valid: false` and then exit 4. JSON error codes provide more specific
causes than the status alone. See [scripting](scripting.md) for envelopes,
stdout/stderr handling, and shell examples, or [troubleshooting](troubleshooting.md)
for remedies.
