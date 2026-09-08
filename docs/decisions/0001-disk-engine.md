# 0001: Reuse CiderPress II DiskArc through an A2Utils adapter

Status: accepted for the implemented DOS 3.3 and ProDOS sector-image scope.

## Decision

Vendor `CommonUtil` and `DiskArc` from [CiderPress II revision
7a055a200e31f752f3a92bb9fe6ae6f67cd55534](https://github.com/fadden/CiderPress2/tree/7a055a200e31f752f3a92bb9fe6ae6f67cd55534).
Expose only A2Utils records, errors, and operations through
`src/A2Utils.Core/Backends/DiskSession.cs`. DiskArc types remain implementation
details. Do not maintain a second production filesystem engine.

The pinned projects target `net10.0`, compile with the installed .NET SDK
10.0.400, and have no NuGet dependencies. DiskArc references only CommonUtil.
The vendored source is unchanged. `third_party/CiderPress2/SOURCE_MANIFEST.json`
records SHA-256 digests for all 208 upstream project files; the files were
compared byte-for-byte against the pinned checkout before removing that checkout.
The upstream narrative source notes lag behind the actual project target.

## API fit

| Requirement | DiskArc API and adapter policy |
| --- | --- |
| Raw and 2IMG containers | `UnadornedSector.OpenDisk`, `TwoIMG.OpenDisk`, and `IDiskImage.AnalyzeDisk(..., ChunksOnly)` establish chunk access. |
| Filesystem and layout detection | Probe `DOS.TestImage` and `ProDOS.TestImage` for supported orders. The adapter scores all candidates, respects explicit overrides and 2IMG order, and refuses ambiguous results. |
| Catalog and metadata | `IFileSystem.PrepareFileAccess(true)`, `GetVolDirEntry()`, iterable `IFileEntry`, `RawFileName`, types, access flags, and timestamps. |
| Exact stored DOS data | `OpenFile(..., FilePart.RawData)` includes embedded headers and sector slack. `DataFork` provides logical payload bytes. ProDOS uses `DataFork` with its explicit EOF. |
| Sparse allocation | `DiskFileStream.Seek` with `SEEK_ORIGIN_DATA` and `SEEK_ORIGIN_HOLE` returns allocated extents; restore seeks over holes before writing. |
| Mutation | `CreateFile`, `OpenFile(ReadWrite)`, `DeleteFile`, `MoveFile`, mutable entry attributes, and `SaveChanges`. |
| Creation | Raw sector/block and 2IMG sector/block creation followed by `FormatDisk`. A 140 KiB block-form 2IMG must be reanalyzed into sector-capable chunks before DOS formatting. |
| Verification | Full filesystem scan, image/filesystem `Notes`, `IsDubious`, entry damage flags, and embedded-volume detection. |

## Experimental evidence

The repeatable probe is `third_party/evaluation/DiskEngineProbe.csproj`:

```sh
dotnet run --project third_party/evaluation/DiskEngineProbe.csproj -c Release
```

On September 8, 2026, the probe ran successfully on Windows with .NET 10.0.400:

- Independently encoded DOS fixture: two entries, five-byte binary payload,
  256 stored bytes, and one allocated extent. Header and recognizable trailing
  `0xcc` bytes survived raw extraction. Full scan reported no diagnostics.
- A temporary copy was unlocked, replaced, extended with another file, and
  reopened. Replacement bytes and the original binary load address matched.
- DOS and ProDOS filesystems were created in ProDOS-order 2IMG containers,
  modified, and reopened. The ProDOS directory operation succeeded.
- 2IMG header, comment, and creator-data bytes were identical before and after
  filesystem mutations.
- The independent source fixture SHA-256 remained
  `51af1da57dfe814ce1323390f4ad3b3e6b247f81869fc8b83d47d4f2a78ef58c`.

The probe writes only uniquely named temporary outputs. The repository test
suite adds independent sector mappings, malformed images, file boundaries,
metadata restoration, and host transaction failure cases. This is integration
evidence, not a claim that every upstream engine feature has been validated.

## Preservation and safety boundaries

DiskArc writes through to its supplied stream and can partially modify it
before an operation fails. All CLI mutations must therefore use A2Utils host
transactions: stage a complete copy, operate, reopen and validate, then commit.
No original image may be passed to a writable backend session by the CLI.
The backend independently respects image write protection and file access
flags; upstream `DeleteFile`, for example, deliberately ignores file locks.

The adapter refuses writes to damaged, hybrid, or forked-file volumes. Reading
a known filesystem does not make arbitrary container layouts safe to write.
`Inspect` can report geometry-valid containers with an unknown filesystem;
free space is then null and raw sector order remains unknown unless supplied.
This fallback enables no filesystem operations. `DiskInfo.IsReadOnly` describes
the open session; `ImageWriteProtected` reports the container's stored flag.
Nibble/bitstream formats, partitions, extended files, and repair are outside the
advertised scope even when DiskArc contains code for them. Supported metadata
restoration does not promise identical sector placement or free-sector contents.

Independent emulator catalog/load validation and cross-platform execution
remain release gates. The probe's create/reopen operations use the same engine
on both sides; the hand-built fixture supplies independent read evidence.

## Licensing and updates

Retain upstream `LICENSE`, `NOTICE`, `THIRD_PARTY_NOTICES`, `LegalStuff.txt`,
and all file headers. The selected source is distributed under upstream's
Apache-2.0 terms and per-file notices. No upstream TestData was copied; its
redistribution permissions are not assumed. Locally created fixtures document
their own provenance. No official NuGet package is assumed.

Updates replace both projects at an exact revision, refresh hashes and notices,
and rerun preservation and failure tests. Keep A2Utils behavior in the adapter;
record and prominently label any future vendor source patches.
