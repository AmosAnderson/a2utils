# CiderPress II disk engine

Upstream: <https://github.com/fadden/CiderPress2>

Pinned revision: `7a055a200e31f752f3a92bb9fe6ae6f67cd55534`

This directory vendors the complete `CommonUtil` and `DiskArc` source projects
and their accompanying format notes. `SOURCE_MANIFEST.json` records SHA-256
digests for all 208 vendored project files, including the single local patch
below and its original upstream digest as `upstreamSha256`. Both projects
already target .NET 10 at this revision. DiskArc references CommonUtil; neither
project has a NuGet dependency.

`LICENSE`, `NOTICE`, `THIRD_PARTY_NOTICES`, `LegalStuff.txt`, and `SourceNotes.md`
are retained from upstream. Their references to applications, images, and UI
dependencies describe the larger upstream distribution. A2Utils includes no
upstream GUI, applications, FileConv, test harness, or TestData. Upstream
`SourceNotes.md` still says .NET 8; the actual pinned project files target .NET 10.

Keep source headers and these notices with distributions. Repository build
assets such as `bin/`, `obj/`, and `packages.lock.json` are generated locally;
they are not upstream source modifications. A2Utils-specific files in this
directory are this README and the source manifest.

To update, choose an exact upstream commit, replace both source projects
together, retain current license/notice files, regenerate the source manifest,
and rerun the independent fixtures and transaction tests. Document any source
patch in the modified file and in the architecture decision. Do not format
vendor files with A2Utils style rules.

Local patch (September 16, 2026): `DiskArc/FS/ProDOS_FileEntry.cs` synchronizes
the redundant subdirectory-header creation date when `SaveChanges` updates
the directory entry's creation date. This fixes project reproducibility across
wall-clock minute boundaries. The source comment and architecture decision
identify the patch; the other 207 upstream project files remain unchanged.

The evaluation is recorded in `docs/decisions/0001-disk-engine.md`. Run its
repeatable probe from the repository root:

```sh
dotnet run --project third_party/evaluation/DiskEngineProbe.csproj -c Release
```
