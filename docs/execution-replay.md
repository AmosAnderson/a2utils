# Check saved execution observations

Use `a2 replay` to check a completed capture after moving the run directory or
removing its original project, ROMs, disk images, and emulator. Replay reads
the archived observations in place. It makes no changes to the evidence.

```sh
a2 replay artifacts/run-001
a2 replay artifacts/run-001 --checkpoint 3
a2 replay artifacts/run-001 --expect checks.json --json
```

By default replay uses the memory, register and screen-text assertions from
the archived `spec.json`. Display-page assertions saved by current runners
already contain the expected memory bytes. A checkpoint number is the
one-based step number used in filenames. For example, step 3 uses
`checkpoint-003.tsv` and `checkpoint-003.txt`. A capture step without its own
condition needs an explicit expectation file.

The final observation comes from `observations.tsv` and any captured MAME
`screen.txt`. CPU routines have no screen. The old result's pass/fail flag is
independent of the replay verdict. Disk assertions, run completion, screenshots,
audio, cycle budgets, debug stops and BASIC runtime checks belong to the original
run. The replay result reports the recorded stop reason for context.

## Check a different expectation

Put the checks in a JSON file. These examples use decimal addresses and values.
The byte range at 768 starts at CPU address $0300.

```json
{
  "schemaVersion": 1,
  "memory": [{ "bank": "cpu", "address": 768, "hex": "2A 00" }],
  "registers": [{ "name": "A", "value": 42 }],
  "textContains": ["READY"],
  "textNotContains": ["SYNTAX ERROR"]
}
```

`memory` and `memoryNotEqual` may select subranges of a capture or span
adjacent captured ranges in the same bank. Specify `main`, `aux`, `lc1`,
`lc2`, `aux-lc1`, or `aux-lc2` for physical memory. Both equality and
inequality require a complete range. Text comparisons are case-sensitive.
Missing bytes, registers and screen text produce `replay.missing_evidence`.
An assertion that disagrees with saved evidence produces `replay.mismatch`.

Use `graphicsMemory` to compare a captured Apple display page with a PNG,
resolved relative to the expectation file. Replay uses the same byte encoding
and page selection as the execution runner. Double-hires checks require both
main and auxiliary page captures.

```json
{
  "schemaVersion": 1,
  "graphicsMemory": [
    { "expectedImage": "screens/title.png", "mode": "lores", "page": 1 }
  ]
}
```

A successful check exits with 0. A mismatch or unavailable requested value
exits with 1 and a replay result. Invalid expectations exit with 2. Missing
observation files or malformed TSV evidence exit with 4. JSON results include
the assertion count and SHA-256 of the archived spec, selected observation, expectation JSON,
captured screen text when available, and each replacement PNG.

Replay accepts at most 1,024 total memory assertions, 128 register assertions,
256 combined text assertions and 16 PNG assertions. The expectation JSON is
limited to 4 MiB. See the [expectation schema](schemas/replay-expectations.schema.json).
