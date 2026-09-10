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
In preview 0.4, `--json` collects diagnostics in the result/error envelope and
keeps stderr free of unstructured warning/verbose lines. Help and
version output are informational text rather than result envelopes.

Success has this shape:

```json
{
  "schemaVersion": 1,
  "command": "verify",
  "data": {
    "valid": true,
    "diagnostics": []
  },
  "diagnostics": []
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
  }
}
```

Preview 0.4 adds an envelope `diagnostics` array on success and
`error.diagnostics` on failure. Source diagnostics can include `file`, `line`,
`column`, `basicLine`, `symbol`, `expected`, and `actual`. Existing code/message/
exitCode fields remain available. Assembly errors keep their broad classification
while the diagnostic entries provide more specific causes.

`basic check`, `run`, and `test` can also return stdout result envelopes on
nonzero exits: source-check failures and behavioral assertion failures have
useful results. Inspect the process exit and result diagnostics together.
Use `capabilities --json` and `schema NAME --json` for discovery. New command
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

Disk command names are unqualified (`info`, `ls`, `add`, `verify`). Root command
names are also unqualified: `build`, `targets`, `capabilities`, `schema`, `run`,
and `test`. Namespaced canonical names are `asm.compile`, `asm.decompile`,
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
| Image writes, `disk convert`, `disk export` | `outputPath` and nullable `backupPath` |
| `asm` / `basic` conversions | `outputPath`, `origin`, `payloadLength`, `outputLength`, nullable `cpu`, and `format` |
| `asm listing`, `asm map` | `outputPath`, `origin`, and `payloadLength`; the requested report is written to `outputPath` |
| `basic check` | `valid` and structured `diagnostics` |
| `basic renumber` | `outputPath`, old/new/source-line `mapping`, and advisory `diagnostics` |
| `basic prepare` | `outputPath`, source/generated-line/label `mapping`, and `diagnostics` |
| `build` | Output path/hash, target, CPU, filesystem, bootability, tool version, timestamp, hashed inputs, built files, resident memory, and diagnostics. With `--check`, no image is written and `sha256` is `""`. |
| `targets` | `profiles`, `symbols`, and DOS/ProDOS `runtimeReservations` |
| `capabilities` | Declared command metadata, separate `globalOptions`, supported values, schema names, external tools, and limitations. Nested command-local option arrays omit inherited globals; direct root children may include root options, including root-only `--version`. Consult `--help` for effective syntax. |
| `schema` | The requested JSON Schema object itself |
| `cc compile` | Output path, AppleSingle format/hash, compiler version/target, decoded type/auxiliary metadata, payload length, linker map, labels, and hashed inputs |
| `graphics encode`, `graphics decode` | Output path/hash/length, mode, dimensions, and rendering description |
| `graphics assets pack`, `unpack` | Output path/hash/length plus complete cell-layout `metadata` and per-cell offsets |
| `graphics shapes encode` | Output path/hash/payload length and per-shape metadata |
| `graphics dhires encode`, `decode` | Output path/hash, mode, bank order/offsets/lengths, dimensions, and rendering description |
| `run` | Name/pass state/stop reason, emulator version/time, registers, observed memory, screen text, input hash, artifact directory/list, and diagnostics |
| `test` | Suite schema version/pass state and the complete ordered `tests` result array |

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
