# Scripting and JSON

[Documentation home](../README.md#documentation) · [Command reference](cli-reference.md)

Use `--json` and the process exit code for automation. Human-readable catalogs
and status messages are intended for interactive use and should not be parsed.
The examples use a locally installed executable as described in
[getting started](getting-started.md#install-the-local-command).

## Output and error contract

| Channel or option | Behavior |
| --- | --- |
| Standard output | Normal command result; with `--json`, an indented JSON document |
| Standard error | Failure messages and diagnostic details; it may be nonempty even when a command succeeds |
| `--json` | Uses a versioned result or error envelope; takes precedence over `--quiet` for normal output |
| `--quiet` | Suppresses normal text results; does not suppress errors |
| `--verbose` | Adds diagnostic detail in text mode; JSON mode keeps diagnostics structured |
| `--to` / `--output` | Programs, exported payloads, and images go to files, not standard output |

Keep the channels separate. Do not use `2>&1` before parsing stdout as JSON.
JSON mode collects diagnostics in the result/error envelope and keeps stderr
free of unstructured warning/verbose lines. Help and version output are
informational text rather than result envelopes.

Current envelopes retain those version-1 fields and add `contractVersion: 2`,
`envelopeType`, and a resolvable `schemaId`. Result envelopes also identify the
payload contract through `resultSchemaId`; most commands use the generic result
schema, while typed results such as `project.resolve` name their specific schema.
Use `a2 schema result|error|envelope --json` to retrieve the bundled contracts.

Success has this shape:

```json
{
  "schemaVersion": 1,
  "command": "verify",
  "data": {
    "valid": true,
    "diagnostics": []
  },
  "diagnostics": [],
  "contractVersion": 2,
  "envelopeType": "result",
  "schemaId": "urn:a2utils:schema:result:2",
  "resultSchemaId": "urn:a2utils:schema:result:2"
}
```

Failures caught by the CLI normally write an error envelope to stderr and no
normal stdout result:

```json
{
  "schemaVersion": 1,
  "error": {
    "code": "program.origin_required",
    "message": "Raw machine code requires --origin because its load address is not stored in the file.",
    "exitCode": 2,
    "diagnostics": []
  },
  "contractVersion": 2,
  "envelopeType": "error",
  "schemaId": "urn:a2utils:schema:error:2",
  "exitCode": 2
}
```

The current envelope includes a `diagnostics` array on success and
`error.diagnostics` on failure. Source diagnostics can include `file`, `line`,
`column`, `endLine`, `endColumn`, `jsonPointer`, `basicLine`, `symbol`, `expected`,
`actual`, `phase`, `tool`, `helpUri`, related locations, and machine-applicable
fixes. Existing code/message/exitCode fields remain available. Assembly errors
keep their broad classification while the diagnostic entries provide more
specific causes.

`basic check`, `run`, and `test` can also return stdout result envelopes on
nonzero exits: source-check failures and behavioral assertion failures have
useful results. Inspect the process exit and result diagnostics together.
Use `capabilities --json` and `schema NAME --json` for discovery. Capability
records include value types, defaults, choices, option relationships, path roles,
side effects, result/error schema identifiers, execution engines, selective-suite
features, CPU processors/tracing, graphics-memory assertions, bare-metal project
support, and the MCP stdio interface. MAME is required only for execution specs
or suite cases whose `engine` is `mame`. New command
results include build hashes/files/memory maps, assembly symbols/source maps,
renumber mappings, graphics artifact metadata, and execution state/artifact paths.

`disk verify` can also finish inspection and
report structural errors with **exit code 4 and a stdout result envelope**
whose `data.valid` is `false`. Check its diagnostics rather than assuming
every nonzero exit has an error envelope on stderr.

## Exit codes

| Code | Meaning | Typical script response |
| --- | --- | --- |
| 0 | Success | Parse the result and continue |
| 1 | Behavioral assertion or unexpected failure | Inspect execution assertions or retain diagnostic output for investigation |
| 2 | Invalid arguments or source syntax | Correct options, source, or a malformed manifest definition |
| 3 | Unsupported/ambiguous format or representation | Select a supported format/CPU, or provide a justified override |
| 4 | Damaged or inconsistent input | Inspect diagnostics; do not repeatedly retry the same write |
| 5 | Host filesystem I/O failure | Check file existence, access, and output parent directories |
| 6 | Refused or cancelled operation | Resolve destination conflicts, locks, capacity, or cancellation |

Use `error.code` for a specific decision and `error.message` for a person.
Diagnostic codes are more precise than an exit category; for example, a missing
host file is an I/O error, while a missing path *inside* an opened disk image is
an operation refusal. Selected codes and remedies are in
[troubleshooting](troubleshooting.md).

## Result fields

Most disk command names are unqualified (`info`, `ls`, `add`, `verify`, `diff`);
declarative mutation reports use `disk.plan` and `disk.apply`. Root command names
are also unqualified: `build`, `targets`, `capabilities`, `schema`, `run`, and
`test`. Their specialized modes report `build.test` and `test.list`. Namespaced
canonical names include `project.resolve`, `project.import`, `asm.compile`,
`asm.decompile`,
`asm.listing`, `asm.map`, `basic.compile`, `basic.decompile`, `basic.check`,
`basic.renumber`, `basic.prepare`, `cc.compile`, `graphics.encode`,
`graphics.decode`, `graphics.assets.pack`, `graphics.assets.unpack`,
`graphics.shapes.encode`, `graphics.dhires.encode`, and
`graphics.dhires.decode`. Aliases still report the corresponding canonical name.

| Command family | `data` contents |
| --- | --- |
| `disk info` | `path`, `container`, `order`, `fileSystem`, `sizeBytes`, `freeBytes`, volume fields, session/protection flags, and `diagnostics` |
| `disk ls` | An array of entry objects; an empty catalog is `[]` |
| Read-only `disk attr` | One entry object |
| `disk verify` | `valid` plus `diagnostics` |
| `disk extract` | `destination` and the complete `manifest` |
| `disk diff` | Whole-image hashes/info, logical entry changes, bounded physical byte ranges, different-byte count, and truncation flag |
| `disk.plan` | Input/change-set/candidate/plan hashes, payload input hashes, free space before/after, and logical entry changes; no requested output is committed |
| `disk.apply` | Transactional write result, the exact preflight `plan`, and committed output hash |
| Image writes, `disk convert`, `disk export` | `outputPath` and nullable `backupPath` |
| `asm` / `basic` conversions | `outputPath`, `origin`, `payloadLength`, `outputLength`, nullable `cpu`, and `format` |
| `asm listing`, `asm map` | `outputPath`, `origin`, and `payloadLength`; the requested report is written to `outputPath` |
| `basic check` | `valid` and structured `diagnostics` |
| `basic renumber` | `outputPath`, old/new/source-line `mapping`, and advisory `diagnostics` |
| `basic prepare` | `outputPath`, source/generated-line/label `mapping`, and `diagnostics` |
| `build` | Output path/hash, target, CPU, filesystem, bootability, tool version, timestamp, hashed inputs, built files, resident memory, diagnostics, mode flags, optional execution settings, and `cacheHit`. Full builds/preflight include `plan` with changes/directories and free space before/after. With `--check`, `plan` is null and `sha256` is `""`. `cacheHit` is true only when `--cache DIRECTORY` restores a verified cached image. |
| `build.test` | `schemaVersion`, `passed`, `cancelled`, complete `build`, `tests` suite result, and `artifactDirectory`. Behavioral failure still returns the result on stdout with exit 1; cancellation uses exit 6. |
| `project.resolve` | Effective target/runtime/disk settings, nullable resolved boot metadata, absolute source paths, hashed build and execution-suite dependencies, required tools, compiled memory ranges, and a noncommitting disk/file/boot-sector plan. |
| `project.import` | New project directory, manifest/template paths, input hash, imported files, editability, and conversion diagnostics. |
| `targets` | `profiles`, `symbols`, and DOS/ProDOS `runtimeReservations` |
| `capabilities` | Typed command/option metadata, separate `globalOptions`, constraints, path roles, side effects, result schemas, supported values, structured external tools, and limitations. Each command record includes its effective inherited globals once. Consult `--help` for effective syntax. |
| `schema` | The requested JSON Schema object itself |
| `cc compile` | Output path, AppleSingle format/hash, compiler version/target, decoded type/auxiliary metadata, payload length, linker map, labels, and hashed inputs |
| `graphics encode`, `graphics decode` | Output path/hash/length, mode, dimensions, and rendering description |
| `graphics assets pack`, `unpack` | Output path/hash/length plus complete cell-layout `metadata` and per-cell offsets |
| `graphics shapes encode` | Output path/hash/payload length and per-shape metadata |
| `graphics dhires encode`, `decode` | Output path/hash, mode, bank order/offsets/lengths, dimensions, and rendering description |
| `run` | Name/pass state/stop reason, emulator version/time, registers, observed memory, screen text, legacy first input hash, `disks` with input/output hashes and paths, artifact directory/list, and diagnostics |
| `test` | Suite schema version/pass/cancel state, suite and artifact paths, explicit planned/completed/passed/failed/cancelled/not-run `counts`, stable ordered case identities/statuses, and the complete ordered `tests` result array |
| `test.list` | Read-only suite plan with matching stable case identities, names, paths, and original suite indexes; no artifact directory is created |

Execution results also expose `bankMemory` (`bank`, `address`, `hex`), optional
`debug` trigger/step/history evidence, optional `textScreen` cells, and captured
`video` flags. The legacy `memory` dictionary continues to contain only CPU-mapped
observations. See [debugging](runtime-debugging.md) and [IIe observations](iie-execution.md).
Source-location records include `memoryBank`; CPU addresses may match more than
one resident bank and all matches are retained.

With `test --progress`, `events.jsonl` contains one JSON object per line and is
flushed as suite and case events occur. Use each event's monotonic `sequence` for
ordering. Parallel completion order can differ from the stable case order in the
final result. Treat `suite-result.json` as the final summary; an interrupted process
can leave only the progress prefix and available per-case evidence.

Numbers are numeric JSON values, including addresses and file types. For
example, `$2000` is `8192`. Dates are strings or `null`, and byte arrays such
as `rawName` use Base64. All lengths and offsets are bytes unless a field
explicitly says otherwise. `freeBytes: null` means unknown, not zero.

`info.isReadOnly` describes the current session: inspection opens read-only,
so it may be `true` for a writable image. `imageWriteProtected` describes the
container's protection flag. File entry `isLocked` describes the entry's access
flags; these are separate concepts.

An entry's `length` is logical payload length, `storedLength` is the stored
representation available through raw extraction, and `storageSize` measures
filesystem allocation. The independent fixture's `HELLO.BIN` has values
5, 256, and 512 respectively. Entry `fileType` uses the library's normalized
ProDOS-style type code: DOS B is `6`, even though its native DOS catalog type
byte is `$04`.

For program results, `payloadLength` excludes a DOS host header.
`outputLength` includes the header when compiling in DOS format, or measures
UTF-8 source bytes when decompiling. `format` is `raw`/`dos` for compilation
and `source` for decompilation. `cpu` is `null` for BASIC.

The output envelope and the extraction manifest each carry their own
`schemaVersion`, currently `1`. Consumers should check the relevant version,
require fields they use, tolerate additional fields, and not rely on property
ordering. See [manifest preservation](disk-images.md) before modifying a
manifest or its payloads.

## PowerShell: inspect a catalog

This example creates a directory for diagnostic output, checks the native
exit code immediately, and lists only unlocked files. Run from the repository
root with PowerShell 7 on Windows.

```powershell
$a2Executable = Join-Path (Get-Location) 'artifacts/tools/a2.exe'
New-Item -ItemType Directory -Path artifacts/script -Force | Out-Null
$jsonLines = & $a2Executable disk ls tests/TestData/independent-dos33.do --json 2> artifacts/script/catalog.stderr.txt
$a2ExitCode = $LASTEXITCODE
if ($a2ExitCode -ne 0) {
    Get-Content artifacts/script/catalog.stderr.txt
    throw "Catalog failed with exit code $a2ExitCode"
}
$catalog = ($jsonLines -join [Environment]::NewLine) | ConvertFrom-Json
if ($catalog.schemaVersion -ne 1) { throw 'Unsupported result schema' }
if ($catalog.command -ne 'ls' -or $catalog.data -isnot [array]) { throw 'Unexpected catalog result' }
$catalog.data |
    Where-Object { -not $_.isDirectory -and -not $_.isLocked } |
    Select-Object path, type, length
```

The unlocked fixture entry is `README`. A native program's nonzero exit code
does not automatically become a terminating PowerShell error in every setup;
explicit `$LASTEXITCODE` checks make the intended behavior clear.

## Bash: verify and inspect diagnostics

This example requires `jq` for JSON processing in addition to A2Utils. `jq`
is a scripting convenience, not an A2Utils dependency.

```bash
a2_executable="$PWD/artifacts/tools/a2"
mkdir -p artifacts/script
if "$a2_executable" disk verify tests/TestData/independent-dos33.do --json \
    > artifacts/script/verify.json 2> artifacts/script/verify.stderr.txt; then
    jq -e '.schemaVersion == 1 and .command == "verify" and .data.valid == true and (.data.diagnostics | type == "array")' artifacts/script/verify.json
else
    a2_exit_code=$?
    if [ "$a2_exit_code" -eq 4 ] && \
        jq -e '.schemaVersion == 1 and .command == "verify" and .data.valid == false and (.data.diagnostics | type == "array")' \
        artifacts/script/verify.json > /dev/null; then
        jq '.data.diagnostics' artifacts/script/verify.json
    else
        cat artifacts/script/verify.stderr.txt >&2
    fi
    exit "$a2_exit_code"
fi
```

The branch preserves the command's actual exit status before running another
process. An empty/unparseable stdout result follows the stderr path.

## Writes, batching, and cancellation

Each mutation is a separate transaction. An invocation with `--in-place`
creates a backup and returns its absolute path in `data.backupPath`; record
that path rather than guessing the randomly generated name. `--output` writes
a separate image and returns `backupPath: null`.

`disk import` batches a host directory into one transaction. A sequence of
separate `disk add` commands has one transaction per command, so earlier
successful additions remain if a later command fails. For a multi-command
build, operate on a new working image and retain the original until the whole
workflow is verified.

Cancellation through Ctrl+C is cooperative and reported as exit 6. The tools
check cancellation before committing staged output. Cancelling later work
does not undo operations that have already completed. Transaction guarantees
and backup handling are described in [the disk guide](disk-images.md).
