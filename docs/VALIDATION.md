# Development preview validation

Validated locally on Windows x64 with .NET SDK 10.0.400. This records completed
checks for the development preview; it is not a stable-release certification.

| Check | Result |
| --- | --- |
| Release solution build with `--warnaserror` | Passed: zero warnings and errors |
| Core xUnit suite | 98 passed |
| CLI xUnit suite | 23 passed |
| Repository formatting, excluding vendored code | Passed |
| Independent DO/PO fixture hashes | Match documented SHA-256 values |
| Vendored source integrity | All 208 files match the pinned upstream manifest |
| DiskArc evaluation probe | DOS/ProDOS read/write/reopen and 2IMG metadata preservation passed |
| Local .NET tool package installation | Passed, installed under `artifacts/tools` |
| Self-contained `win-x64` package | Built and smoke-tested |
| Packaged operations | Catalog, ProDOS create/add/verify/extract, binary hash round trip passed |
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
locked files, failed transactions, backups, and CLI contracts.

Platform-specific transaction tests exercise Windows file identities locally;
their Unix branches require Linux/macOS CI. Fixtures are hand-generated and
contain no boot code. The emulator catalog/load smoke check has not been run.

Before a stable/public release, run the configured hosted CI matrix, validate
generated data disks in independent DOS and ProDOS emulator environments, and
select a license for original A2Utils code. No remote repository or public
package was created during this implementation.
