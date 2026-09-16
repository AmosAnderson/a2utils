# Build and test a separate DOS data disk

`main.asm` is original code that stores `$2A` at `RESULT` (`$0300`) and returns.
The project builds it into a DOS 3.3 data volume. The suite boots an independently
supplied OS disk in drive 1 and loads the built program from drive 2.

Preflight needs only A2Utils:

```sh
a2 build examples/development/project-tests/project.a2.json --preflight --json
```

To run the machine test, first edit `main.execution.json` with the path to your
MAME 0.289 executable, matching enhanced-IIe ROM directory, and DOS 3.3 boot disk.
Paths are relative to that JSON file unless absolute. The boot disk must reach
an Applesoft prompt within ten emulated seconds. No emulator, ROM, or OS disk is
included here. Use a new artifact directory on each run:

```sh
a2 build examples/development/project-tests/project.a2.json --test --artifacts artifacts/project-test-001 --json
```

For later builds, use `--overwrite` to replace the previous `build/demo.do`.
The manifest binds the resulting image to `flop2` and pins its exact hash.
Each test mounts isolated copies, so the supplied boot disk and built output
remain unchanged by MAME. The evidence includes build/source maps, resolved
assertions, screenshots, sampled PCs, and test results. This is a runnable
configuration example; external machine success depends on the supplied setup
and is recorded separately from process-contract tests.

For a debugging run, change the manifest's `execution.suite` to `debug-suite.json`
and configure the same local paths in `debug.execution.json`. That case resolves
`MAIN:start`, stops before its `LDA`, executes the `LDA` and `STA`, and checks
`RESULT` while capturing the stack page and recent instruction history. Source
locations are saved with the result. See [runtime debugging](../../../docs/runtime-debugging.md).
