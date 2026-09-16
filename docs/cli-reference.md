# Command-line reference

[Documentation home](../README.md) · [Disk workflows](disk-images.md) · [Program tools](programs.md)

Examples use the installed executable `a2`. See [getting started](getting-started.md)
for installation or running through `dotnet`. `IMAGE`, `HOSTFILE`, and other
uppercase words below are placeholders; brackets mark optional arguments.
Quote host and image paths containing spaces. Use `a2 COMMAND --help` for local
help, for example `a2 disk add --help`.

## Command map

| Command | Purpose and guide |
| --- | --- |
| `build PROJECT [--to IMAGE] [--overwrite] [--check]` | [Project manifests](projects.md), source checks, memory reports, and transactional disk output. |
| `capabilities --json` | Discover the declared command tree, supported values, targets, and schema names; combine `globalOptions` with command-local metadata. |
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

The remainder of this reference lists the complete syntax and command-specific
options. The five custom global options in the next section are recursive and
can follow subcommands. Command help is available at every level, while
`--version` is root-only. Image overrides only affect commands that open an image
(or program decompilation with `--from-image`).

## Global options and value conventions

| Option | Meaning |
| --- | --- |
| `--json` | Write schema-versioned result JSON to stdout and error JSON to stderr. |
| `--quiet` | Suppress normal text results; JSON results and diagnostics are still emitted. |
| `--verbose` | Include additional diagnostics on stderr in text mode; JSON diagnostics stay structured. |
| `--input-order dos\|prodos` | Override the input image's sector/block order. |
| `--input-fs dos33\|prodos` | Select a supported filesystem when opening an image. |
| `-h`, `-?`, `--help` | Show help for the selected command. Windows also accepts `/h` and `/?`. |
| `--version` | Show the executable version. This is root-only: `a2 --version`. |

The five recursive options (`--json`, `--quiet`, `--verbose`, `--input-order`,
and `--input-fs`) can follow subcommands. Layout/filesystem overrides describe
the image being opened: for `disk copy`, this is the destination `IMAGE`; use the
source-specific options for `--from`. Program commands accept image overrides
only with `--from-image`. `disk create` uses its own `--fs` and `--order` options;
`disk convert` uses `--input-order` but does not use `--input-fs`.
Because the command-line parser displays recursive global options on every help
page, their appearance does not make them meaningful for host-only build,
source, graphics, discovery, or execution commands. Do not pass image overrides
to those commands; non-applicable values are either rejected or ignored.

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

## Build and discovery commands

```text
a2 build PROJECT [--to IMAGE] [--overwrite] [--check]
a2 targets
a2 capabilities
a2 schema NAME
```

| Command or option | Behavior |
| --- | --- |
| `build PROJECT` | Load a strict version 1 project manifest, compile every source, validate metadata and memory, then commit one image. Manifest-relative paths resolve from the directory containing `PROJECT`. |
| `build --to IMAGE` | Override the manifest's `output`; this CLI path resolves from the current working directory. |
| `build --overwrite` | Permit replacement of an existing output only after the complete staged build validates. |
| `build --check` | Compile and validate source, metadata, and resident-memory ranges without writing an image. It does not prove disk capacity or bootability. |
| `build --preflight` | Create, populate, reopen, and validate a disposable image; report its hash and allocation/change plan without committing output or creating output parents. Existing output policies apply. |
| `build --test --artifacts DIR` | Preflight and build the project, then run its `execution.suite` against the exact output hash with symbolic assertions resolved. `DIR` must be new. Mutually exclusive with `--check`/`--preflight`; artifacts are retained on behavioral failure. |
| `targets` | Return the four target profiles, platform symbols, and DOS/ProDOS runtime reservations. Text mode prints the profiles; use `--json` for the complete data. |
| `capabilities` | Return declared command metadata plus CPUs, targets, filesystems, containers, source kinds, graphics modes, schemas, external tools, and current limitations. For machine discovery, combine top-level `globalOptions` with each command's local arguments/options; inherited globals are not repeated on nested leaves, and direct root children may list the root-only `--version`. Use `--help` as the authority for effective syntax. |
| `schema NAME` | Return an embedded JSON Schema. `NAME` is `project`, `diagnostic`, `execution`, or `execution-suite`. With `--json`, the schema is the envelope's `data`; text mode writes the schema itself. |

Project defaults and every manifest property are in
[project builds](projects.md). Output records are described in
[scripting and JSON](scripting.md) and the [Core API reference](core-api.md).

## Source-analysis and report commands

```text
a2 asm listing INPUT --to REPORT [--origin ADDRESS] [--cpu CPU] [--overwrite]
a2 asm map INPUT --to REPORT [--origin ADDRESS] [--cpu CPU] [--overwrite]
a2 basic check INPUT
a2 basic renumber INPUT --to SOURCE [--start NUMBER] [--step NUMBER] [--overwrite]
a2 basic prepare INPUT --to SOURCE [--start NUMBER] [--step NUMBER] [--overwrite]
```

| Command or option | Behavior |
| --- | --- |
| `asm listing` | Assemble UTF-8 source and write a text listing containing addresses, emitted bytes, and source. |
| `asm map` | Assemble UTF-8 source and write versioned JSON containing origin, payload length, CPU, symbols, source-map entries, and hashes for all source/binary dependencies. |
| `listing` / `map` `--to REPORT` | Required separate output file. An existing file requires `--overwrite`; source files and included dependencies cannot alias it. |
| `listing` / `map` `--origin ADDRESS` | Optional 16-bit origin. If source begins with `.org` before emitting bytes, it must match; later forward `.org` directives may create gaps. If no initial origin is available, assembly fails. |
| `listing` / `map` `--cpu CPU` | `6502` (default), `65c02`, or `w65c02`. |
| `basic check` | Conservatively validate numbered UTF-8 Applesoft source. It writes a normal result even when errors make the exit status 2. |
| `basic renumber` | Rewrite line prefixes and supported literal branch targets in already numbered source. |
| `basic prepare` | Convert unnumbered source with optional `@label:` declarations and label references to ordinary numbered Applesoft. |
| `--start NUMBER` | First output line, 0–63,999; default `10`. |
| `--step NUMBER` | Positive increment whose final generated line remains at most 63,999; default `10`. |
| `--to SOURCE` | Required separate UTF-8 output for `renumber` and `prepare`; existing files require `--overwrite`. |

Assembly reports use the same include, expression, CPU, and size rules as
`asm compile`. BASIC check/renumber/prepare boundaries and mapping fields are
documented in [BASIC development](basic-development.md).

## C and ca65 compilation

```text
a2 cc compile INPUT --to APPLESINGLE [options]
```

| Option | Default and behavior |
| --- | --- |
| `--to FILE` | Required AppleSingle v2 output. It must not alias an input; an existing output requires `--overwrite`. |
| `--compiler PATH_OR_NAME` | `cl65`. A path selects that executable; a bare name is resolved through `PATH`. |
| `--target apple2\|apple2enh` | `apple2`. Selects the cc65 target library and linker configuration. |
| `--expected-version TEXT` | Require the exact trimmed combined output of `cl65 --version`. Omitted by default. |
| `--project-root DIR` | Main source directory by default. Source, local include, and binary-include paths must remain beneath it. |
| `--timeout SECONDS` | `60` per compiler process; allowed range 1–3,600. Timeout or cancellation terminates the process tree. |
| `--overwrite` | Replace an existing output only after the AppleSingle file and metadata validate. |

The standalone command accepts one main `.c`, `.s`, `.asm`, or `.a65` source.
Additional sources, include directories, definitions, and optimization control
are available through a project manifest. See [C and ca65 compilation](cc65.md).

## Graphics commands

All graphics commands require `--to FILE`; output must be separate from every
input and an existing file requires `--overwrite`. They read or write raw assets,
not DOS headers or disk entries. Base screen input is capped at 4 MiB. Atlas
packing and double-hires input are capped at 32 MiB; atlas unpacking accepts at
most 65,536 raw bytes. Shape JSON is capped at 1 MiB with a maximum nesting depth
of 16 and unknown fields rejected.

```text
a2 graphics encode INPUT --mode MODE --to OUTPUT [--overwrite]
a2 graphics decode INPUT --mode MODE --to OUTPUT [--overwrite]
```

`encode` accepts a PNG and writes display-memory bytes; `decode` does the
reverse. `--mode` is required and accepts `lores` (40×48, 1,024 bytes), `hires`
(280×192 monochrome, 8,192 bytes), or `hires-color` (280×192 approximate
artifact color, 8,192 bytes). Exact PNG and raw input sizes are required.

```text
a2 graphics assets pack INPUT --to OUTPUT --cell-width N --cell-height N [options]
a2 graphics assets unpack INPUT --to OUTPUT --cell-width N --cell-height N [options]
```

| Asset option | Default and behavior |
| --- | --- |
| `--cell-width N`, `--cell-height N` | Required positive cell dimensions. A packed PNG must divide exactly into this grid. |
| `--bits-per-byte 7\|8` | `7`; seven-bit packing keeps bit 7 clear. |
| `--bit-order lsb\|msb` | `lsb`; placement of the leftmost pixel within each seven/eight-bit group. |
| `--kind sprite\|tile\|font` | `sprite`; changes metadata, not the row-major byte layout. |
| `--first-codepoint N` | For `font`, first Unicode scalar label; defaults to 32. Invalid for non-font assets. |
| `--threshold N` | Packing-only luma threshold 0–255; default `128`. During unpack it is retained as metadata but does not change the raw bits. |
| `--invert` | Reverse set/clear pixels. Pass the same value to `unpack` to restore the intended appearance. |
| `--columns N` | `unpack` only; preview columns, default `1`, and must divide the cell count. |

```text
a2 graphics shapes encode INPUT --to OUTPUT [--overwrite]
a2 graphics dhires encode INPUT --mode MODE --bank-order ORDER --to OUTPUT [--overwrite]
a2 graphics dhires decode INPUT --mode MODE --bank-order ORDER --to OUTPUT [--overwrite]
```

`shapes encode` accepts a strict version 1 shape JSON document and writes a raw
Applesoft shape table. Double-hires `--mode` is `mono` (560×192) or `color`
(140×192 logical pixels); `--bank-order` is required and is `aux-main` or
`main-aux`. Both encodings contain two 8 KiB banks (16,384 bytes total).
See [graphics conversion](graphics.md) and [graphics assets](graphics-assets.md)
for palette, packing, JSON metadata, and hardware limitations.

## Execution commands

Specifications support [breakpoints, watchpoints and instruction steps](runtime-debugging.md),
as well as [physical banks, 80-column text and MouseText](iie-execution.md).
Project tests resolve native assembly symbols in debug points. These features use
the existing `run`, `test`, and `build --test` commands.

```text
a2 run SPEC --artifacts NEW_DIRECTORY
a2 test SUITE --artifacts NEW_DIRECTORY
```

`run` loads one strict version 1 execution specification. `test` first loads
and validates every specification named by a strict version 1 suite, then runs
at most 128 cases sequentially. Every suite case needs a memory, register, text,
completion, saved-file assertion, or mounted-disk verification. `--artifacts` is required and must name a path that
does not exist; the commands create it and retain the isolated disk copy,
configuration, logs, observations, and results there. A suite uses numbered
`case-001`, `case-002`, ... subdirectories plus `suite-result.json`.
Ordinary failed cases do not stop later cases; cancellation stops the active
run and the remaining suite. The suite exit status is the highest mapped case
status (0 for pass, 1 for execution/assertion failure, or 6 for cancellation).

A completed behavioral failure returns a normal result and exit status 1;
cancellation returns 6. The complete specification fields, MAME 0.289 pin,
machine names, assertion rules, bounds, and artifact list are in
[automated execution](execution.md).

Execution supports at most two distinct `flop1`/`flop2` mounts, each with an
isolated copy and optional input hash pin. The legacy `diskImage`/`diskDevice`
fields remain supported. `diskAssertions` checks files after MAME exits.
Symbolic assertions require `build --test`; plain `run` and `test` use numeric
addresses.

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
| `disk attr IMAGE PATH` | Read one file or directory's type, auxiliary type, and access flags when no changes are supplied; nonempty directories are valid. Do not supply write options in read mode. |
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

### Setup and extended execution

`a2 env check PROFILE [--json]` checks local tools/ROMs/template readiness.
`a2 env lock PROFILE --output LOCK` records the configured input hashes.
`a2 init DIRECTORY --language basic|asm|c [--environment PROFILE]` creates a staged
starter project. `a2 schema environment --json` returns the profile schema.
See [setup](setup.md).

Existing `run`, `test`, and `build --test` commands accept ordered steps,
routines/cycle budgets, game-port controls, audio assertions, screenshot
comparisons, and the explicit CFFA2 storage profile through their JSON documents.
See [interactive testing](interactive-testing.md), [audio](audio-execution.md),
[graphics assets](project-assets.md), and [block storage](block-storage-execution.md).
