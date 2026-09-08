# Development preview validation

Preview 0.3.0-dev validated locally on Windows x64 with .NET SDK 10.0.400. This records completed
checks for the development preview; it is not a stable-release certification.

| Check | Result |
| --- | --- |
| Release solution build with `--warnaserror` | Passed: zero warnings and errors |
| Core xUnit suite | 387 passed, including two temporary-root regressions |
| CLI xUnit suite | 73 passed |
| Repository formatting, excluding vendored code | Passed |
| Independent DO/PO fixture hashes | Match documented SHA-256 values |
| Vendored source integrity | All 208 files match the pinned upstream manifest |
| DiskArc evaluation probe | DOS/ProDOS read/write/reopen and 2IMG metadata preservation passed |
| Local .NET tool package installation | Passed, installed under `artifacts/tools` |
| Self-contained `win-x64` package | Built and smoke-tested |
| Packaged operations | Disk workflows plus assembly/BASIC compile, decompile, image import, and byte round trips passed |
| Normal locked restore after RID-specific publishing | Passed; publishing uses separate lockfiles under `obj` |
| Package license/notice inclusion | Tool contains DiskArc/CommonUtil and System.CommandLine notices; self-contained archive also contains runtime notices |

Run the automated checks from the repository root:

```sh
dotnet restore A2Utils.slnx --locked-mode
dotnet build A2Utils.slnx -c Release --no-restore --warnaserror
dotnet test A2Utils.slnx -c Release --no-restore
dotnet format A2Utils.slnx --verify-no-changes --no-restore --exclude third_party
```

The tests cover raw DOS headers and trailing bytes, known layout vectors,
malformed catalog/allocation structures, full disks, multi-list DOS files,
ProDOS storage boundaries and mixed-case directories, sparse and metadata
restoration, rejected malicious manifests, metadata-preserving conversions,
locked files, failed transactions, backups, and CLI contracts. Preview 0.2 adds
logical payload export, strict text conversion, recursive copies, moves,
parent directory creation, atomic directory imports, and input/output alias
protection. Transfer tests include independent disk fixtures, allocation
limits, sparse files, metadata, and rollback after failed writes.

Preview 0.3 adds independently specified machine-code and Applesoft token
vectors, all 107 BASIC tokens, CPU compatibility, label and branch boundaries,
malformed source and program rejection, DOS header validation, source/output
protection, and compile/import/decompile workflows on DOS and ProDOS.
Disassembly tests include 27,648 opcode/operand/truncation round-trip cases and
mixed code/data streams across the three CPU modes. No Apple ROM is embedded.

Platform-specific transaction tests exercise Windows file identities locally;
their Unix branches and the `/usr/bin/stat` regular-file check passed on the
initial hosted Linux job. Native macOS validation remains pending. Fixtures are hand-generated and
contain no boot code. The emulator catalog/load smoke check has not been run.

Before a stable/public release, run the configured hosted CI matrix, validate
generated data disks in independent DOS and ProDOS emulator environments, and
select a license for original A2Utils code. Initial implementation and validation
were performed locally. A private [GitHub repository](https://github.com/AmosAnderson/a2utils)
has since been created; no public package has been published.

## Documentation verification

The expanded guides were checked locally on Windows on September 8, 2026:

- All 20 command help pages were compared with the command reference.
- All 37 command lines in the disk workflows ran against temporary images;
  conversion retained expected bytes and the reference fixture was unchanged.
- All 21 `a2` command lines in the getting-started guide ran successfully;
  rebuilt assembly and Applesoft payloads matched their originals byte for byte.
- Both C# library examples compiled and ran, including staged image editing
  with structural and content validation.
- The PowerShell JSON example returned the expected unlocked fixture entry.
- Local Markdown links, heading references, and code-fence pairing were checked.

Bash/zsh and `jq` examples were reviewed but not executed on this Windows host.
The original 458-test result records the preceding implementation validation;
documentation verification did not rerun the full solution test suite.

## Release workflow validation

The [initial hosted run](https://github.com/AmosAnderson/a2utils/actions/runs/34257159465)
passed Windows and Ubuntu. macOS failed because its system temporary path uses
the `/var` symlink, which correctly triggered image-write link protection.
The shared test setup now resolves temporary-directory ancestors to physical
paths. Production link checks remain unchanged.

After this correction, all **460 tests (387 Core, 73 CLI)** passed locally with
`TEMP` and `TMP` deliberately routed through a Windows junction. Formatting
verification also passed. The Unix-specific regression additionally checks
that physical writes succeed and writes through a created alias are refused;
that branch awaits native macOS/Linux execution of the updated tests.

All 49 local release checks passed. They exercise real Git tags and branch ancestry, then use a
simulated GitHub CLI to cover private-repository enforcement, changed-tag
rejection, required assets, checksums, draft publication, failed/incomplete
uploads, retries, and stable/prerelease flags. Versioned Windows packaging for
`0.3.0` produced matching package and binary versions, bundled notices, and
working tool/self-contained disk and program commands.

Actionlint 1.7.12 accepted the workflow. All PowerShell scripts parsed, updated
documentation links resolved, and the executable extracted from the versioned
Windows archive passed disk verification, assembly, and BASIC smoke checks.

The updated workflow runs only on release-tag pushes; ordinary branch pushes
and pull requests do not build. Actual GitHub Release publication and native
macOS validation will occur when an intended release tag is pushed. No tag or
release was created merely to validate this configuration.
