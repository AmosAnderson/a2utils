# Local development environments

An environment profile centralizes local tool, ROM, OS-template, and optional
cc65 distribution paths. It never downloads or installs external assets.

```json
{
  "schemaVersion": 1,
  "machine": "apple2ee",
  "mamePath": "tools/mame",
  "romDirectory": "roms",
  "templateImage": "templates/prodos.po",
  "templateFileSystem": "prodos",
  "bootSeconds": 10,
  "cc65Path": "tools/cc65/bin/cl65",
  "cc65Root": "tools/cc65"
}
```

Paths resolve relative to the profile. Omit cc65 settings when unused. The
optional `expectedCc65Version` pins the exact `cl65 --version` output. MAME is
pinned to the execution adapter's version, currently 0.289. `bootSeconds`
(1–120) configures starter input timing for your template.

```sh
a2 env check environment.json --json
a2 env lock environment.json --output environment.lock.json --json
a2 init my-program --language asm --environment environment.json --json
```

`env check` reports separate machine-version, ROM-verification, template
structure, compiler-version, and compiler-distribution results. ROM checking
uses MAME's `-verifyroms` for the selected machine. Structural disk checks do
not prove bootability: run the generated execution test with your own bootable
template. ProDOS starters require BASIC.SYSTEM and a template that reaches an
Applesoft prompt; DOS 3.3 templates must likewise reach the prompt.

Set `environment` in a project or execution JSON file to reuse the profile.
Execution files inherit omitted emulator, ROM-directory, and machine settings.
Projects inherit an omitted disk template and compiler configuration; an
explicit compiler configuration keeps its compiler selection and can inherit
an omitted distribution root. A project's target and filesystem remain its
own explicit settings. Explicit paths override defaults in unlocked projects.

Set `toolchainLock` alongside `environment` to enforce an environment lock.
Both paths resolve relative to their owning project or execution file.
Locked runs must use the profile's emulator and ROM directory; locked projects
must use its template and compiler distribution. Create a separate profile and
lock to use different paths. The lock hashes the profile, emulator executable,
every file in the ROM directory, the template, and the configured cc65
executable plus its `bin`, `include`, `asminc`, `lib`, and `cfg` trees. Directory
membership is checked too, so added libraries or ROM files invalidate a lock.

Locks use explicit physical paths and reject symbolic links and special files.
Resolve package-manager links to their installed directories before locking.
Limits are 16,384 files/directories, 2 GiB per file, and 8 GiB total; use a
dedicated Apple II ROM directory instead of a complete arcade ROM collection.
Keep the lock outside the directories it fingerprints. Existing lockfiles are
preserved; create a new lock filename after reviewing intentional changes.
Environment locks are checked before dependent work and again before build
commit or emulator launch. They pin local inputs, not external assets bundled
with the project or guaranteed cross-host emulator behavior.

Execution artifacts retain the exact profile and lockfile bytes as
`environment.json` and `toolchain-lock.json`, together with original paths and
SHA-256 values in the result's environment evidence. Both files are checked
again against that captured baseline before machine launch and after execution,
so replacing a profile and its lock together during the version probe cannot
change the run.
Copied profiles are evidence: their relative paths still refer to the original
profile directory and may need adjustment before using the copies elsewhere.

`init` supports `--language basic`, `asm`, and `c`, and requires a new directory.
It creates source, a project, a suite, an execution specification, and a README
in a staging directory before moving the completed project into place. The
program writes the exact mailbox bytes `2A A5 5A` at `$0300–$0302`; its test
asserts all three bytes. `AI.MAIN` must be absent from the template. The template
is copied by the build and never edited in place.

`a2 init DIRECTORY --language asm --bare-metal` creates an original DOS-order
140 KiB boot-sector project instead. It uses no operating-system template;
`--bare-metal` accepts only the assembly starter. Its generated full-machine test
still needs a separately configured MAME executable and ROM directory.

Without `--environment`, initialization creates an editable `environment.json`.
Configure its local paths before building or testing; generated configuration
is not evidence of a successful emulator run. With a configured profile:

```sh
a2 build my-program/project.a2.json --check --json
a2 build my-program/project.a2.json --test --artifacts my-program/runs/first --json
```

Use a fresh artifact directory each run. The C starter also requires a working
cc65 distribution. The starter README explains how to add the environment
lock to both its project and execution specification.
