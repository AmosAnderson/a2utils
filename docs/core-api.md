# A2Utils.Core API reference

`A2Utils.Core` is the reusable, console-independent library behind the `a2`
command. This reference lists every exported first-party type and every
hand-written public method in the Core assembly. Record-generated equality,
copy, deconstruction, and formatting members are omitted.

Core currently ships as a project reference rather than a public NuGet
package. All APIs target .NET 10. Add the project to another project with:

```sh
dotnet add MyApplication.csproj reference src/A2Utils.Core/A2Utils.Core.csproj
```

The detailed guides remain the best place to learn the formats and workflows:

- [disk images](disk-images.md) and [supported image combinations](formats/supported-images.md)
- [assembly and Applesoft BASIC](programs.md) and [BASIC development](basic-development.md)
- [project builds](projects.md) and [C/ca65 compilation](cc65.md)
- [graphics](graphics.md) and [graphics assets](graphics-assets.md)
- [automated execution](execution.md)
- [library integration and development](development.md)

## Conventions and failure behavior

Namespace imports used throughout this reference are:

```csharp
using System.Collections.ObjectModel;
using System.Text.Json;
using A2Utils.Core;
using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Execution;
using A2Utils.Core.Graphics;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;
using A2Utils.Core.Setup;
```

Unless a method says otherwise:

- host paths are resolved with `Path.GetFullPath`; image paths use Apple
  filesystem rules, not host filename rules;
- string option values such as image order, filesystem, graphics mode, and
  project kind accept only the spellings documented for that method;
- `byte[]`, mutable `List<T>` properties, and collections supplied to records
  are not defensively copied merely by constructing the record;
- a `CancellationToken` is checked at bounded work checkpoints and cancellation
  normally raises `OperationCanceledException`; `ExecutionRunner` is the notable
  exception because it converts cancellation during an execution into a failed
  `ExecutionResult`;
- validation records and option records do not validate themselves. The method
  consuming the record performs validation;
- host I/O can also raise ordinary .NET exceptions such as
  `FileNotFoundException`, `DirectoryNotFoundException`, `IOException`,
  `UnauthorizedAccessException`, `ArgumentException`, or `InvalidDataException`.

Domain failures use:

```csharp
public sealed class DiskException : Exception
{
    public DiskException(
        string code,
        string message,
        int exitCode = 6,
        Exception? innerException = null);

    public string Code { get; }
    public int ExitCode { get; }
    public IReadOnlyList<ProgramDiagnostic> Diagnostics { get; init; } = [];
}
```

`Code` is the stable machine-readable identifier. `ExitCode` is the suggested
CLI classification, and `Diagnostics` can carry structured source or assertion
details. Invalid arguments commonly use exit code 2, unsupported features 3,
malformed/corrupt data 4, unavailable external tools 5, and operational failures
6. Callers should still inspect the values rather than infer them from the
exception type. See [scripting and JSON](scripting.md) for the CLI mapping.

## Core disk models

Source: [DiskModels.cs](../src/A2Utils.Core/DiskModels.cs)

### `DiskDiagnostic`

```csharp
public sealed record DiskDiagnostic(
    string Code,
    string Severity,
    string Message);
```

One structural or engine diagnostic. Current severities are textual values such
as `info`, `warning`, and `error`.

### `SparseExtent`

```csharp
public sealed record SparseExtent(long Offset, long Length);
```

One allocated byte range in a stored file representation. Extents are ordered,
nonoverlapping, and positive-length when returned by `DiskSession`.

### `DiskInfo`

```csharp
public sealed record DiskInfo(
    string Path,
    string Container,
    string Order,
    string FileSystem,
    long SizeBytes,
    long? FreeBytes,
    string VolumeName,
    int? VolumeNumber,
    bool IsReadOnly,
    bool IsDubious,
    IReadOnlyList<DiskDiagnostic> Diagnostics,
    bool ImageWriteProtected = false);
```

- `Path` is the absolute host path.
- `Container` is `raw` or `2mg`; `Order` is `dos`, `prodos`, or, for limited
  inspection, `unknown`.
- `FileSystem` is `dos33`, `prodos`, or `unknown`.
- `SizeBytes` is formatted payload size, not necessarily complete host-file size.
- `FreeBytes` is `null` when no supported filesystem was recognized.
- `VolumeNumber` applies to DOS 3.3; ProDOS reports `null`.
- `IsReadOnly` describes the opened session. `ImageWriteProtected` separately
  reports the 2IMG container flag.
- `IsDubious` and `Diagnostics` report structural concerns; they do not prove
  bootability or repair the image.

### `DiskEntry`

```csharp
public sealed record DiskEntry(
    string Path,
    string Name,
    bool IsDirectory,
    string Type,
    byte FileType,
    ushort AuxType,
    byte Access,
    bool IsLocked,
    long Length,
    long StoredLength,
    long StorageSize,
    byte[] RawName,
    DateTime? Created,
    DateTime? Modified,
    bool IsDubious,
    bool HasResourceFork);
```

`Length` is the logical data length, `StoredLength` is the preservation read
length, and `StorageSize` is allocated space. `Type` is the filesystem-appropriate
display alias while `FileType` is its numeric value. `RawName` preserves the
catalog spelling. Timestamps are `null` when absent or invalid. Resource-fork
content is not exposed by Core operations.

## Disk sessions

Source: [DiskSession.cs](../src/A2Utils.Core/Backends/DiskSession.cs),
[DiskSession.Transfer.cs](../src/A2Utils.Core/Backends/DiskSession.Transfer.cs),
[DiskSession.Development.cs](../src/A2Utils.Core/Backends/DiskSession.Development.cs), and
[DiskSession.Boot.cs](../src/A2Utils.Core/Backends/DiskSession.Boot.cs)

```csharp
public sealed partial class DiskSession : IDisposable
{
    public DiskInfo Info { get; }

    public static DiskInfo Inspect(
        string path,
        string? inputOrder = null,
        string? inputFs = null);

    public static DiskSession Open(
        string path,
        string? inputOrder = null,
        string? inputFs = null,
        bool writable = false);

    public static void Create(
        string path,
        string fs,
        long sizeKiB = 140,
        string container = "raw",
        string order = "dos",
        string volumeName = "UNTITLED",
        int volumeNumber = 254,
        long? blocks = null);

    public DiskEntry GetEntry(string path);
    public IReadOnlyList<DiskEntry> List(
        string? path = null,
        bool recursive = false);
    public byte[] ReadFile(string path, bool raw = false);
    public IReadOnlyList<SparseExtent> GetSparseExtents(string path);
    public IReadOnlyList<DiskDiagnostic> Verify();

    public void Add(string name, byte[] data, string type, ushort auxType = 0);
    public void Replace(string path, byte[] data);
    public void Delete(string path, bool recursive = false);
    public void Rename(string path, string newName);
    public void Mkdir(string path);
    public void EnsureDirectory(string path);
    public void SetAttributes(
        string path,
        bool? locked = null,
        string? type = null,
        ushort? auxType = null);
    public void SetTimestamps(string path, DateTime created, DateTime modified);
    public byte[] ReadBootSectors(int count);
    public void WriteBootSectors(byte[] sectors);

    public void Copy(
        string sourcePath,
        string destinationPath,
        bool recursive = false,
        CancellationToken cancellationToken = default);
    public void CopyFrom(
        DiskSession source,
        string sourcePath,
        string destinationPath,
        bool recursive = false,
        CancellationToken cancellationToken = default);
    public void Move(string sourcePath, string destinationPath);

    public void ValidateFileName(string name);
    public void ValidateFileType(string type);
    public void RestoreFile(
        DiskEntry metadata,
        byte[] rawData,
        IReadOnlyList<SparseExtent> dataExtents);
    public void RestoreDirectoryMetadata(DiskEntry metadata);

    public void Flush();
    public void Dispose();
}
```

### Opening, inspection, and ownership

`Inspect` recognizes raw and sector-data 2IMG containers. Unlike `Open`, it can
return limited `DiskInfo` for a supported contiguous image whose filesystem is
unknown. A raw 140 KiB image with unknown order reports `unknown` unless an
input order is supplied.

`Open` requires one unambiguous DOS 3.3 or ProDOS filesystem. `inputOrder`
accepts `dos` or `prodos`; `inputFs` accepts `dos33` or `prodos`. These select an
interpretation and never convert bytes. A conflicting 2IMG declaration is
refused. Standard 140 KiB images may use either order; larger images require
ProDOS block order. The session owns the host stream, disk container, and
filesystem objects, so always dispose it:

```csharp
using DiskSession session = DiskSession.Open(imagePath);
DiskInfo info = session.Info;
```

Sessions are read-only by default. `writable: true` opens the supplied file for
exclusive read/write access and immediately checks container protection,
filesystem integrity, hybrid/embedded filesystems, dubious entries, and resource
forks. It does not provide rollback. Open writable sessions only on a staging
path supplied by `ImageTransactions`.

`Info` is computed from the live session and includes `Verify()` diagnostics.
Access after disposal is unsupported.

Detection/open failures use codes including `truncated_image`,
`invalid_image_size`, `unsupported_container`, `unsupported_geometry`,
`unsupported_order`, `unsupported_filesystem`, `invalid_order`,
`conflicting_order`, `unrecognized_filesystem`, `ambiguous_layout`, and
`corrupt_image`.

### Reading and verification

`GetEntry` returns metadata for one exact path. `List` returns immediate children
of a directory, all descendants when `recursive` is true, or a one-element list
when the path names a file. Omitting the path or using `/` selects the root;
the volume root itself is not included.

`ReadFile` requires a readable, undamaged data file without a resource fork.
The default returns logical data. With `raw: true`, DOS returns its raw stored
file representation, including the applicable header and tail; ProDOS returns
the data fork through EOF. Reads are limited to 32 MiB.

`GetSparseExtents` describes allocated ranges in that same raw/preservation
representation. `Verify` combines container and filesystem notes, reports hybrid
filesystems, damaged entries, and unsupported forks, and never repairs anything.

Typical failures include `file_not_found`, `ambiguous_name`, `not_a_directory`,
`not_a_file`, `file_access_denied`, `damaged_entry`, `unsupported_forks`,
`corrupt_file`, and `directory_depth`.

### Creation and mutation

`Create` uses `FileMode.CreateNew`; its host parent must already exist and the
target must not. `fs` accepts `dos33` or `prodos`, `container` accepts `raw`,
`2mg`, or `2img`, and `order` accepts `dos` or `prodos`. `blocks`, when supplied,
takes precedence over `sizeKiB`; `sizeKiB * 2` otherwise determines the block
count. DOS requires exactly 280 blocks. ProDOS creation accepts 280 through
65,535 blocks, with ProDOS order required above 280. DOS volume numbers are
0 through 254. The result is a formatted data volume with no operating system
or boot code.

All instance mutation methods require a writable, safe session:

- `Add` creates one new data file from logical payload bytes. It never overwrites
  and never strips a host program header. `type` accepts the aliases listed in
  the [disk guide](disk-images.md#file-types-and-addresses) or a ProDOS `0x00`
  through `0xff` value; `0x0f`/`DIR` is reserved.
- `Replace` replaces only the data fork and preserves type, auxiliary type, and
  the previous modification timestamp. The entry must permit writing.
- `Delete` preflights destroy and parent-write permissions for the complete tree.
  Nonempty directories require `recursive: true`; the root cannot be deleted.
- `Rename` changes only the leaf name in its existing directory and accepts no
  slash. `Move` takes an exact new path and can change the parent without copying
  data. Neither overwrites an existing entry.
- `Mkdir` creates one ProDOS directory with an existing parent.
  `EnsureDirectory` retains existing directories and creates every missing
  ProDOS component. DOS has no directories.
- `SetAttributes` changes only supplied values. Locking clears write, rename,
  and destroy permission bits; unlocking sets them. Type/auxiliary changes need
  write permission unless the same call explicitly unlocks with `locked: false`.
- `SetTimestamps` assigns creation and modification fields as supported by the
  open filesystem and is mainly used for reproducible staged project builds.
- `ReadBootSectors` reads 1..16 complete 256-byte track-zero sectors in DOS
  logical order. `WriteBootSectors` writes the same bounded representation and
  requires a writable 140 KiB sector image. These are low-level bare-metal
  operations; use a staged transaction and validate the result before commit.
- `Flush` flushes filesystem and container state and forces writable stream data
  to disk. Individual mutation helpers also flush after a successful mutation.

Direct mutation can leave its target changed if the engine fails partway through.
The transaction wrapper, not `DiskSession`, supplies host-file rollback.

### Copying and restoration primitives

`Copy` copies within one image; `CopyFrom` copies from another open session.
Source and destination filesystems must match, but container, sector order, and
capacity may differ. Directories require `recursive: true`. The destination is
an exact new path with an existing parent. Supported names, stored bytes, logical
length, type, auxiliary value, access flags, and timestamps are retained.
ProDOS zero-filled allocated blocks can become sparse holes, so physical
allocation and `StorageSize` need not match. Cancellation is checked while the
source snapshot is captured and restored.

`Move` includes directory contents automatically and rejects moves into the
source directory or one of its descendants. Copy and move both enforce the
256-level supported directory bound.

`ValidateFileName` checks that one literal name round-trips exactly in the open
filesystem. `ValidateFileType` validates a non-directory data type without
creating an entry.

`RestoreFile` and `RestoreDirectoryMetadata` are low-level primitives for a
validated same-filesystem preservation manifest. They enforce raw-name,
sparse-extent, length, type, fork, damage, and directory rules. Prefer
`FileTransfer.Restore` unless the caller already implements all manifest and
host-path validation.

Mutation failures commonly use `image_read_only`, `corrupt_image`,
`hybrid_filesystem`, `unsafe_allocation`, `file_locked`, `disk_full`,
`operation_refused`, `destination_exists`, `root_operation_refused`,
`recursive_required`, `self_descendant`, `filesystem_mismatch`, and the
validation codes described above.

## Host operations and transactions

Sources: [Operations](../src/A2Utils.Core/Operations)

### `ImageWriteResult`

```csharp
public sealed record ImageWriteResult(string OutputPath, string? BackupPath);
```

`OutputPath` is the committed absolute destination. `BackupPath` is non-null
only for a successful in-place `ImageTransactions.Write`.

### `ImageTransactions`

```csharp
public static class ImageTransactions
{
    public static void ValidatePath(string path);
    public static void EnsureDistinctPaths(
        string sourcePath,
        string destinationPath);

    public static ImageWriteResult Write(
        string inputPath,
        string? outputPath,
        bool inPlace,
        bool overwrite,
        Action<string> editTemporary,
        Action<string>? validate = null,
        CancellationToken cancellationToken = default);

    public static ImageWriteResult Create(
        string outputPath,
        bool overwrite,
        Action<string> createTemporary,
        Action<string>? validate = null,
        CancellationToken cancellationToken = default);
}
```

`ValidatePath` rejects a symbolic link, junction/reparse point, or linked existing
ancestor under the transaction path policy. It does not create the path.
`EnsureDistinctPaths` applies that policy to two paths and rejects equal paths;
on Windows it also compares existing file identities.

`Write` accepts exactly one destination policy:

- `inPlace: true` with `outputPath: null` (or blank) stages beside the input,
  then replaces it and keeps a unique `.bak` sibling;
- `inPlace: false` with a nonblank `outputPath` stages a copy at a separate
  destination and leaves the input unchanged.

The method fingerprints input and any overwritten destination by length,
timestamp, and SHA-256, copies the input, invokes `editTemporary`, invokes the
optional `validate`, flushes, rechecks paths and fingerprints, then commits.
`overwrite` applies only to a separate existing output. The output parent must
exist. Callback exceptions and cancellation prevent commit, and temporary cleanup
is best effort. Supply a validation callback for application writes and dispose
all streams/sessions opened by a callback before it returns.

`Create` follows the same destination, overwrite, callback, validation,
fingerprint, and flush policy but begins with a new temporary file instead of
copying an input. The creation callback must create the supplied path.

Important transaction codes include `write.destination_required`,
`write.source_alias`, `write.destination_exists`, `write.read_only`,
`write.concurrent_change`, and `write.link_refused`.

### `DiskDiff`

```csharp
public sealed record DiskEntrySnapshot(
    string Path, bool IsDirectory, string Type, ushort AuxType, byte Access,
    long Length, long StoredLength, long StorageSize, string? PayloadSha256,
    string? StoredSha256, DateTime? Created, DateTime? Modified);
public sealed record DiskEntryDifference(
    string Path, string Action, DiskEntrySnapshot? Before, DiskEntrySnapshot? After,
    IReadOnlyList<string> ChangedFields);
public sealed record ByteDifference(long Offset, long Length);
public sealed record DiskDifference(
    int SchemaVersion, string BeforePath, string AfterPath,
    string BeforeSha256, string AfterSha256, bool Identical,
    DiskInfo BeforeInfo, DiskInfo AfterInfo,
    IReadOnlyList<DiskEntryDifference> Entries,
    IReadOnlyList<ByteDifference> ByteRanges, long DifferentByteCount,
    bool ByteRangesTruncated);
public static class DiskDiff
{
    public static DiskDifference Compare(
        string beforePath,
        string afterPath,
        string? beforeOrder = null,
        string? beforeFileSystem = null,
        string? afterOrder = null,
        string? afterFileSystem = null,
        CancellationToken cancellationToken = default);
}
```

`Compare` snapshots both inputs, opens their supported filesystems, and reports
logical entry additions/removals/field changes together with whole-image byte
ranges. It never modifies either input. Each image is limited to 34 MiB; at most
4,096 physical ranges are retained, while `DifferentByteCount` remains complete
and `ByteRangesTruncated` identifies omitted ranges.

### Declarative disk change sets

```csharp
public sealed record DiskChangeSet
{
    public int SchemaVersion { get; init; } = 1;
    public string? ExpectedSha256 { get; init; }
    public DateTime Timestamp { get; init; } = new(2000, 1, 1);
    public IReadOnlyList<DiskChange> Changes { get; init; } = [];
}
public sealed record DiskChange
{
    public string Action { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Source { get; init; }
    public string? Hex { get; init; }
    public string? ExpectedSourceSha256 { get; init; }
    public string? Type { get; init; }
    public ushort? AuxType { get; init; }
    public string? NewName { get; init; }
    public bool Recursive { get; init; }
    public bool Parents { get; init; }
    public bool? Locked { get; init; }
}
public sealed record DiskChangeInput(string Path, string Sha256, long Length);
public sealed record DiskChangePlan(
    int SchemaVersion, string InputPath, string InputSha256,
    string ChangeSetPath, string ChangeSetSha256, string PlanSha256,
    string CandidateSha256, string FileSystem, string Order,
    long? FreeBytesBefore, long? FreeBytesAfter,
    IReadOnlyList<DiskChangeInput> Inputs,
    IReadOnlyList<DiskEntryDifference> Changes);
public sealed record DiskApplyResult(
    ImageWriteResult Write, DiskChangePlan Plan, string OutputSha256);
public static class DiskChangeSetRunner
{
    public static DiskChangePlan Plan(
        string imagePath, string changeSetPath,
        string? inputOrder = null, string? inputFileSystem = null,
        CancellationToken cancellationToken = default);
    public static DiskApplyResult Apply(
        string imagePath, string changeSetPath, string? outputPath,
        bool inPlace, bool overwrite, string? expectedInputSha256 = null,
        string? expectedPlanSha256 = null, string? inputOrder = null,
        string? inputFileSystem = null,
        CancellationToken cancellationToken = default);
}
```

`Plan` strictly loads 1..1,024 ordered `add`, `replace`, `delete`, `rename`,
`mkdir`, or `attr` changes, applies them to disposable sibling images, validates
the candidate, and returns reproducible input/change-set/payload/candidate hashes
without committing an output. `Apply` repeats that preflight, can require the
input and plan hashes, rechecks every input, and commits the same candidate through
`ImageTransactions`. Change-set JSON is limited to 1 MiB, each source payload to
32 MiB, and combined source payloads to 128 MiB.

### `ImageConverter`

```csharp
public static class ImageConverter
{
    public static ImageWriteResult Convert(
        string inputPath,
        string outputPath,
        string container,
        string order,
        string? inputOrder = null,
        bool overwrite = false,
        bool allowMetadataLoss = false,
        CancellationToken cancellationToken = default);
}
```

`container` accepts `raw`, `2mg`, or `2img`; `order` and `inputOrder` accept
`dos` or `prodos`. Conversion changes only the container/order representation,
not filesystem, size, files, or bootability. It supports contiguous payloads of
280 through 65,536 blocks; DOS sector order is restricted to a 140 KiB image.
Raw input without detectable filesystem/order needs `inputOrder`. A 2IMG input
order must agree with its header.

Raw-to-2IMG adds an A2Utils header. 2IMG-to-2IMG preserves bytes outside the
payload except required layout fields. 2IMG-to-raw discards wrapper data and
requires `allowMetadataLoss: true`. Output is always separate, transactionally
staged, and byte-validated. Conversion accepts version 0/1 sector-data 2IMG and
validates its payload, block count, comment, and creator-data ranges. Errors use
`image.truncated`, `image.invalid_header`, `image.container_unsupported`,
`image.order_unsupported`, `image.order_conflict`,
`image.geometry_unsupported`, `convert.metadata_loss`, or
`convert.validation_failed`, plus transaction errors.

### `AppleTextCodec`

```csharp
public static class AppleTextCodec
{
    public static byte[] Decode(ReadOnlySpan<byte> data);
    public static byte[] Encode(
        ReadOnlySpan<byte> utf8,
        string fileSystem);
}
```

`Decode` clears bit 7, converts CR/LF/CRLF to LF, and returns BOM-free UTF-8.
Printable ASCII and tab are accepted; NUL, DEL, and other controls are rejected.

`Encode` accepts valid UTF-8, removes a leading UTF-8 BOM, converts all newline
forms to CR, and adds no terminator, padding, or final newline. `fileSystem`
accepts `dos33` (set bit 7 on every output byte) or `prodos` (leave it clear).
Unrepresentable Unicode and controls fail with `text.unsupported_character`;
invalid UTF-8 uses `text.invalid_utf8`, and another filesystem name uses
`text.unsupported_filesystem`.

### `DirectoryImport` and `DirectoryImportResult`

```csharp
public sealed record DirectoryImportResult(
    int FileCount,
    int DirectoryCount,
    long BytesImported);

public static class DirectoryImport
{
    public static DirectoryImportResult Import(
        DiskSession session,
        string hostDirectory,
        string destinationPath,
        string type,
        ushort auxType = 0,
        bool recursive = false,
        Func<byte[], byte[]>? transform = null,
        CancellationToken cancellationToken = default);
}
```

`Import` imports the contents of `hostDirectory` below an existing image
directory. Every file receives the same type/auxiliary value; `transform`, when
provided, converts each complete host payload before it is added. The result
counts entries and transformed payload bytes.

The method validates names, links, regular-file status, limits, conflicts,
metadata, and hashes for the complete host snapshot before creating image
entries. Subdirectories require `recursive: true` and ProDOS; DOS targets only
the root and remains flat. Hidden entries are included. All payloads are held
in memory during preflight. The session must already be a writable transaction
staging session; this method does not roll it back itself.

Failures include `unsupported_directories`, `not_a_directory`,
`import_name_conflict`, `import_recursive_required`, `import_link_refused`,
`unsupported_host_entry`, `import_concurrent_change`, `import_too_large`,
`import_too_many_entries`, `import_directory_depth`, and
`invalid_import_transform`.

### `FileExport`

```csharp
public static class FileExport
{
    public static ImageWriteResult Export(
        DiskSession session,
        string imagePath,
        string destination,
        string format = "binary",
        bool overwrite = false,
        CancellationToken cancellationToken = default);
}
```

Exports one logical data fork through `ImageTransactions.Create`. `format` is
`binary` or `text`. Text export requires file type T/TXT and uses
`AppleTextCodec.Decode`; binary returns logical bytes unchanged. The destination
must not alias the image and its parent must exist. Staged output is verified by
length and SHA-256. The result never has a backup path. Format/type errors use
`export.unsupported_format`, `export.not_a_file`, and
`text.unsupported_file_type`; a staged byte mismatch uses
`export.validation_failed`.

### `ExtractedFile`, `ExtractionManifest`, and `FileTransfer`

```csharp
public sealed record ExtractedFile(
    DiskEntry Entry,
    string? HostFile,
    string? Sha256,
    IReadOnlyList<SparseExtent> DataExtents);

public sealed record ExtractionManifest(
    int SchemaVersion,
    string FileSystem,
    IReadOnlyList<ExtractedFile> Entries);

public static class FileTransfer
{
    public const string ManifestName = "a2-manifest.json";
    public static JsonSerializerOptions JsonOptions { get; }

    public static ExtractionManifest Extract(
        DiskSession session,
        string destination,
        string? imagePath = null,
        CancellationToken cancellationToken = default);

    public static void Restore(
        DiskSession session,
        string manifestPath,
        CancellationToken cancellationToken = default,
        string? outputPath = null);
}
```

`JsonOptions` uses camel-case property names, case-insensitive reads, and
indented writes. `Extract` requires a new destination directory but creates
missing parents. It writes version-1 `a2-manifest.json` plus flat, safely named
`.a2raw` siblings, then renames its temporary directory into place. Files carry
raw stored bytes, SHA-256, sparse extents, and `DiskEntry` metadata. Directories
have null payload/hash and empty extents. Selecting a ProDOS nested entry also
includes directory metadata needed to restore its ancestors. Forked files are
refused.

`Restore` accepts only schema version 1 and the same filesystem type as the open
destination session. It refuses duplicate or inconsistent image paths, unsafe
payload paths, links, changed hashes/lengths, invalid extents, nonzero sparse
holes, and raw-name representations that cannot be restored. It preflights all
payloads before creating directories/files, then verifies restored bytes and
metadata. `outputPath` does not select an output; it is an optional host path
used only to reject aliases with the manifest or payload files. The caller must
open a writable staging session and supply transaction/rollback.

See [the manifest reference](disk-images.md#extract-and-restore-a-manifest) for
the JSON fields and preservation boundary. Failures use codes including
`destination_exists`, `linked_path`, `unsupported_fork`, `invalid_manifest`,
`invalid_manifest_path`, `filesystem_mismatch`, `payload_hash_mismatch`,
`payload_length_mismatch`, and `invalid_sparse_extent`.

### `HostFiles`

```csharp
public static class HostFiles
{
    public static void EnsureRegularFile(
        string path,
        CancellationToken cancellationToken = default);
}
```

On Linux and macOS, invokes `/usr/bin/stat` with a ten-second internal timeout
to reject pipes, sockets, devices, and other nonregular inputs before a caller
could block opening them. On Windows this helper returns immediately; callers
apply normal attributes and stream checks. Supported Unix failures include
`unsupported_host_entry`, `import_host_inspection_timeout`, and
`unsupported_host_platform`; missing `stat` raises `IOException`.

## Program files and diagnostics

Sources: [Programs](../src/A2Utils.Core/Programs)

### `ProgramDiagnostic`

```csharp
public sealed record ProgramDiagnostic(
    string Code,
    string Severity,
    string Message,
    string? File = null,
    int? Line = null,
    int? Column = null,
    int? BasicLine = null,
    string? Symbol = null,
    string? Expected = null,
    string? Actual = null)
{
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public string? JsonPointer { get; init; }
    public string? Phase { get; init; }
    public string? Tool { get; init; }
    public string? HelpUri { get; init; }
    public IReadOnlyList<DiagnosticRelatedLocation> Related { get; init; } = [];
    public IReadOnlyList<DiagnosticFix> Fixes { get; init; } = [];
}
public sealed record DiagnosticRelatedLocation(
    string Message, string? File = null, int? Line = null, int? Column = null,
    int? EndLine = null, int? EndColumn = null, string? JsonPointer = null);
public sealed record DiagnosticEdit(
    string File, string Replacement, int? Line = null, int? Column = null,
    int? EndLine = null, int? EndColumn = null, string? JsonPointer = null);
public sealed record DiagnosticFix(
    string Description, IReadOnlyList<DiagnosticEdit> Edits);
```

A source/build/runtime diagnostic. Physical source ranges, logical `BasicLine`,
JSON Pointer, phase/tool, symbol, comparison values, related locations,
documentation, and machine-applicable replacement edits are populated only when
relevant. `Code` and `Severity` remain the stable routing fields.

### `ProgramFiles`

```csharp
public static class ProgramFiles
{
    public static byte[] ReadBytes(
        string path,
        int maximum = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default);
    public static string DecodeText(byte[] bytes);
    public static string ReadText(
        string path,
        CancellationToken cancellationToken = default);
    public static string Hash(ReadOnlySpan<byte> bytes);
}
```

`ReadBytes` applies the transaction link policy and Unix regular-file check,
opens a shared read-only stream, enforces `maximum`, and detects growth during
the read. `DecodeText` strictly decodes UTF-8 and trims leading BOM characters.
`ReadText` combines those operations. `Hash` returns lowercase SHA-256 hex.
Relevant domain codes are `program.too_large`, `program.source_changed`, and
`program.invalid_utf8`.

### `ProgramFileFormat`

```csharp
public static class ProgramFileFormat
{
    public static byte[] EncodeDosBinary(
        ReadOnlySpan<byte> payload,
        ushort origin);
    public static (ushort Origin, byte[] Payload) DecodeDosBinary(
        ReadOnlySpan<byte> data);
    public static byte[] EncodeDosBasic(ReadOnlySpan<byte> payload);
    public static byte[] DecodeDosBasic(ReadOnlySpan<byte> data);
    public static void ValidateAddressRange(int length, ushort origin);
}
```

DOS binary host files have a little-endian origin, little-endian 16-bit payload
length, and payload. DOS BASIC host files have only the length and payload.
Decode requires the declared length to match the host file exactly; sector
padding is not accepted. Encode permits at most 65,535 payload bytes, and
binary payloads must fit from `origin` through `$FFFF`. These methods do not
add/remove filesystem sector padding. Failures use `program.address_range`,
`program.dos_length`, `program.truncated_header`, or `program.length_mismatch`.

### `AppleSinglePayload` and `AppleSingleProgram`

```csharp
public sealed record AppleSinglePayload(
    byte[] Bytes,
    byte FileType,
    ushort AuxType,
    byte Access);

public static class AppleSingleProgram
{
    public static AppleSinglePayload Decode(ReadOnlySpan<byte> input);
}
```

`Decode` accepts bounded AppleSingle version 2 data containing one data fork and
an eight-byte ProDOS-info entry. It returns the data fork plus byte-sized file
type/access and 16-bit auxiliary type. Duplicate/overlapping/out-of-range entries,
missing required entries, a nonempty resource fork, directory type, or wider
metadata are refused. Codes are `applesingle.invalid`, `applesingle.version`,
`applesingle.resource_fork`, and `applesingle.metadata_range`.

## Assembly

Sources: [Assembly](../src/A2Utils.Core/Assembly). See
[the source dialect and limits](programs.md#source-dialect).

### CPU and instruction metadata

```csharp
public enum CpuKind
{
    Mos6502,
    Apple65C02,
    Wdc65C02
}

public enum AddressingMode
{
    Implied,
    Accumulator,
    Immediate,
    ZeroPage,
    ZeroPageX,
    ZeroPageY,
    Absolute,
    AbsoluteX,
    AbsoluteY,
    Indirect,
    IndexedIndirect,
    IndirectIndexed,
    Relative,
    ZeroPageIndirect,
    AbsoluteIndexedIndirect,
    ZeroPageRelative
}

public sealed record Instruction(
    byte Opcode,
    string Mnemonic,
    AddressingMode Mode)
{
    public int Length { get; }
}

public static class InstructionSet
{
    public static IReadOnlyList<Instruction> GetInstructions(CpuKind cpu);
}
```

`GetInstructions` returns a read-only opcode-sorted table of documented
instructions for the selected CPU. It contains 151 NMOS, 178 Apple-compatible
CMOS, or 212 WDC entries. Undocumented opcodes are absent. `Instruction.Length`
is derived from addressing mode; `BRK` is represented as one byte even though
runtime behavior also skips a following signature byte. An invalid enum value
uses `assembly.unsupported_cpu`.

### Assembly results

```csharp
public sealed record AssemblySourceMapEntry(
    string? File,
    int Line,
    int Address,
    int Length)
{
    public string Source { get; init; } = "";
}

public sealed record AssemblyResult(ushort Origin, byte[] Bytes)
{
    public IReadOnlyDictionary<string, int> Symbols { get; init; }
        = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
    public IReadOnlyList<AssemblySourceMapEntry> SourceMap { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyDictionary<string, string> DependencyHashes { get; init; }
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}
```

`Bytes` is one headerless contiguous payload starting at `Origin`. `Symbols`
uses case-insensitive keys and contains labels/constants that fit `int`.
`SourceMap` records the original source text/location, address, and emitted
length; zero-length entries can represent labels and assertions. `Dependencies`
and lowercase SHA-256 `DependencyHashes` are populated by file assembly and
include the main file plus source/binary includes. In-memory assembly has no
filesystem dependencies.

### `Assembler`

```csharp
public static partial class Assembler
{
    public const int MaximumSourceLength = 4 * 1024 * 1024;

    public static AssemblyResult Assemble(
        string source,
        ushort? origin = null,
        CpuKind cpu = CpuKind.Mos6502,
        CancellationToken cancellationToken = default);

    public static AssemblyResult AssembleFile(
        string path,
        ushort? origin = null,
        CpuKind cpu = CpuKind.Mos6502,
        CancellationToken cancellationToken = default);

    public static string CreateListing(AssemblyResult result);
}
```

`Assemble` compiles source already in memory and does not permit filesystem
includes. `AssembleFile` strictly reads UTF-8 source, expands `.include` and
`.incbin` below the main source directory, records hashes, rejects linked inputs,
and enforces include/count/size/cycle bounds. `origin` supplies the initial
address; it must agree with an initial source `.org`. Without either, assembly
fails. Both methods perform layout then emission, support the complete dialect
documented in [program tools](programs.md#source-dialect), and return only after
all symbols/expressions resolve.

Assembly failures are normally a `DiskException` whose outer code remains
`assembly.invalid_source` and whose `Diagnostics` contains a more specific code,
line, file, symbol, and expected/actual details when available. Address overflow
from disassembly uses `assembly.address_overflow`; cancellation remains an
`OperationCanceledException`.

`CreateListing` renders source-map rows with addresses and up to eight bytes per
row. It is a diagnostic listing, not reassemblable source.

### `Disassembler`

```csharp
public static class Disassembler
{
    public static string Disassemble(
        ReadOnlySpan<byte> data,
        ushort origin,
        CpuKind cpu = CpuKind.Mos6502,
        CancellationToken cancellationToken = default);
}
```

Performs a linear sweep and emits `.org` plus reassemblable statements. Unknown
opcodes and truncated instructions become `.byte`. Absolute operands below
`$0100` are width-qualified so reassembly retains the encoding. This cannot
recover symbols, comments, code/data boundaries, or control flow. Input must fit
from `origin` through `$FFFF`.

## Applesoft BASIC

Sources: [Basic](../src/A2Utils.Core/Basic). See [program tools](programs.md#applesoft-basic)
and [BASIC development](basic-development.md).

### `ApplesoftBasic`

```csharp
public static class ApplesoftBasic
{
    public const ushort DefaultOrigin = 0x0801;
    public const int MaximumLineNumber = 63999;
    public const int MaximumTokenizedLineLength = 250;
    public const int MaximumSourceLength = 1024 * 1024;

    public static byte[] Compile(
        string source,
        ushort origin = DefaultOrigin,
        CancellationToken cancellationToken = default);

    public static string Decompile(
        ReadOnlySpan<byte> data,
        ushort origin = DefaultOrigin,
        CancellationToken cancellationToken = default);
}
```

`Compile` tokenizes strictly increasing numbered ASCII source into a headerless
linked Applesoft payload. Blank physical lines are ignored. Code whitespace is
discarded, strings/DATA/REM tails retain spelling, and `?` becomes `PRINT`.
This is a tokenizer, not a full syntax checker. It accepts an optional leading
BOM, source up to 1 MiB, line numbers 0..63999, tokenized bodies up to 250 bytes,
and an origin `$0100..$FFFE` with room for the complete payload.

`Decompile` validates links, increasing line numbers, terminators, length, byte
representability, and canonical retokenization. It returns LF-terminated
canonical source and rejects any representation that would change on recompilation.
Failures use `basic.*` codes and classify invalid source as input errors and
malformed payloads as data errors.

### BASIC analysis result records

```csharp
public sealed record BasicCheckResult(
    bool Valid,
    IReadOnlyList<ProgramDiagnostic> Diagnostics);

public sealed record BasicLineMapping(
    int SourceLine,
    int OldLine,
    int NewLine);

public sealed record BasicRenumberResult(
    string Source,
    IReadOnlyList<BasicLineMapping> Mapping,
    IReadOnlyList<ProgramDiagnostic> Diagnostics);

public sealed record BasicPreparedLine(
    int SourceLine,
    int BasicLine,
    IReadOnlyList<string> Labels);

public sealed record BasicPrepareResult(
    string Source,
    IReadOnlyList<BasicPreparedLine> Mapping,
    IReadOnlyList<ProgramDiagnostic> Diagnostics);
```

Mappings use one-based physical source lines. `BasicRenumberResult.Source` keeps
the original non-number text and line-ending spelling; `BasicPrepareResult.Source`
is ordinary numbered source with LF endings. Diagnostics can contain warnings
even when a result is valid.

### `ApplesoftTools`

```csharp
public static partial class ApplesoftTools
{
    public static BasicCheckResult Check(
        string source,
        string? file = null,
        CancellationToken cancellationToken = default);

    public static BasicRenumberResult Renumber(
        string source,
        int start = 10,
        int step = 10,
        string? file = null,
        CancellationToken cancellationToken = default);

    public static BasicPrepareResult Prepare(
        string source,
        int start = 10,
        int step = 10,
        string? file = null,
        CancellationToken cancellationToken = default);
}
```

`Check` first applies tokenizer constraints, then performs conservative checks
for supported expressions/statements, literal line targets, missing targets,
variable identity collisions, keyword collisions, and unterminated strings.
Expected source problems are returned as diagnostics with `Valid == false`
rather than thrown. It is not a complete Applesoft grammar or runtime analysis.

`Renumber` rewrites numbered line prefixes and supported literal GOTO/GOSUB/THEN,
ON, ONERR, and RUN targets while preserving other text. It refuses checker
errors, missing/computed targets, LIST/DEL ranges, invalid ranges, and a rewrite
that no longer tokenizes within size limits. Unsafe input throws
`basic.renumber_invalid_source` with the collected diagnostics.

`Prepare` assigns numbers to unnumbered statements and resolves case-insensitive
`@labels` only in complete supported branch-target positions. Standalone and
multiple labels may attach to the next statement. It rejects numbered source,
bad/duplicate/unresolved labels, invalid contexts/ranges, and checker errors.
Failures use `basic.prepare_invalid_source` plus structured diagnostics.

## Graphics

Sources: [Graphics](../src/A2Utils.Core/Graphics). The bitmap APIs use RGB arrays
with exactly three bytes per pixel in row-major R, G, B order.

### `RasterImage` and `PngCodec`

```csharp
public sealed record RasterImage(int Width, int Height, byte[] Rgb);

public static class PngCodec
{
    public static RasterImage Decode(ReadOnlySpan<byte> png);
    public static byte[] Encode(RasterImage image);
}
```

`Decode` accepts a complete static, noninterlaced PNG no larger than 32 MiB and
2048 x 2048 pixels. Supported inputs are 8-bit RGB/RGBA/gray/gray-alpha and
1/2/4/8-bit indexed or grayscale. It validates chunk ordering/ranges/CRC, zlib
framing, filters, inflated length, palette/transparency, and exact IEND. Alpha
is composited against black. Gamma/color-profile metadata is ignored. Invalid
data uses `png.invalid`; unsupported features use `png.unsupported`,
`png.animation`, or `png.critical_chunk`.

`Encode` validates a 1..2048 RGB image and emits a static noninterlaced 8-bit
truecolor PNG with no alpha or ancillary metadata. Invalid dimensions throw
`ArgumentException`.

### `AppleGraphics`

```csharp
public static class AppleGraphics
{
    public static IReadOnlyList<int> LoresPalette { get; }

    public static byte[] EncodePng(byte[] png, string mode);
    public static byte[] DecodePng(byte[] bytes, string mode);
    public static byte[] Encode(RasterImage image, string mode);
    public static RasterImage Decode(ReadOnlySpan<byte> bytes, string mode);
    public static int TextOffset(int row);
    public static int HiresOffset(int y);
}
```

`mode` is case-sensitive and accepts `lores`, `hires`, or `hires-color`.
`EncodePng` decodes PNG then returns raw Apple display memory; `DecodePng`
decodes raw Apple memory then returns encoded PNG. `Encode`/`Decode` operate on
`RasterImage` directly.

- `lores` requires 40 x 48 RGB and produces/consumes 1,024 bytes using
  `LoresPalette` and interleaved text-page addressing.
- `hires` requires 280 x 192 RGB and 8,192 bytes, using a luminance threshold
  of 128 for monochrome dots.
- `hires-color` has the same dimensions/size and performs an approximate local
  artifact-color conversion; it is not an NTSC simulation.

Encoding clears page holes. Decoding displays visible pixels and does not retain
hole bytes or necessarily the same color encoding on a round trip. Invalid mode,
dimensions, or raw length use `graphics.mode`, `graphics.dimensions`, or
`graphics.length`.

`LoresPalette` is a read-only list of 16 packed `0xRRGGBB` integers.
`TextOffset` maps row 0..23 to its interleaved page offset; `HiresOffset` maps
scanline 0..191. Out-of-range indexes throw `ArgumentOutOfRangeException`.

### `DoubleHiresGraphics`

```csharp
public static class DoubleHiresGraphics
{
    public static byte[] Encode(
        RasterImage image,
        string mode = "mono",
        string bankOrder = "aux-main");
    public static RasterImage Decode(
        ReadOnlySpan<byte> bytes,
        string mode = "mono",
        string bankOrder = "aux-main");
}
```

Both methods use exactly 16,384 bytes for two 8 KiB pages. `mode` is `mono`
(560 x 192, thresholded) or `color` (140 x 192, approximate four-bit colors).
`bankOrder` is `aux-main` or `main-aux`. Encoding clears screen holes/high bits;
decoding ignores them. Invalid options, dimensions, or lengths use
`graphics.dhires_options`, `graphics.dhires_dimensions`, or
`graphics.dhires_length`.

### Bitmap asset types and `GraphicsAssets`

```csharp
public sealed record BitmapAssetOptions(
    int CellWidth,
    int CellHeight,
    int BitsPerByte = 7,
    string BitOrder = "lsb",
    int Threshold = 128,
    bool Invert = false,
    string Kind = "sprite",
    int? FirstCodePoint = null);

public sealed record BitmapAssetCell(
    int Index,
    int Column,
    int Row,
    int Offset,
    int Length,
    int? CodePoint);

public sealed record BitmapAssetMetadata(
    int SchemaVersion,
    string Kind,
    int CellWidth,
    int CellHeight,
    int Columns,
    int Rows,
    int CellCount,
    int BitsPerByte,
    string BitOrder,
    int Threshold,
    bool Invert,
    int BytesPerRow,
    int BytesPerCell,
    int PayloadLength,
    IReadOnlyList<BitmapAssetCell> Cells);

public sealed record BitmapAsset(
    byte[] Bytes,
    BitmapAssetMetadata Metadata);

public static class GraphicsAssets
{
    public static BitmapAsset Pack(
        RasterImage image,
        BitmapAssetOptions options);
    public static RasterImage Unpack(
        ReadOnlySpan<byte> bytes,
        BitmapAssetOptions options,
        int columns = 1);
    public static BitmapAssetMetadata Inspect(
        ReadOnlySpan<byte> bytes,
        BitmapAssetOptions options,
        int columns = 1);
}
```

Cells are row-major. Each cell row starts a new byte, with stride
`ceil(CellWidth / BitsPerByte)`. `BitsPerByte` is 7 or 8; `BitOrder` is `lsb`
or `msb`; `Kind` is `sprite`, `tile`, or `font`; threshold is 0..255. Font cells
receive sequential Unicode scalar `CodePoint` values starting at
`FirstCodePoint ?? 32`; other kinds reject that option.

`Pack` requires an RGB atlas no larger than 2048 x 2048 whose dimensions are
exact cell multiples. Pixels at/above the luma threshold are set, subject to
`Invert`. It returns raw bytes and complete schema-versioned layout metadata.

`Inspect` validates options and a 1..65,536-byte raw payload, requires a complete
rectangular atlas for `columns`, rejects nonzero unused padding/high bits, and
returns metadata. `Unpack` first performs that inspection and then renders a
monochrome RGB atlas. Errors use `graphics.asset_options`,
`graphics.asset_dimensions`, `graphics.asset_length`,
`graphics.asset_padding`, `graphics.asset_size`, or
`graphics.asset_codepoint`.

### Applesoft shape-table types and `ApplesoftShapes`

```csharp
public sealed record ShapeCommand(
    string Direction,
    bool Plot = true,
    int Count = 1);
public sealed record ShapeDefinition(
    string Name,
    IReadOnlyList<ShapeCommand> Commands);
public sealed record ShapeDocument(
    int SchemaVersion,
    IReadOnlyList<ShapeDefinition> Shapes);
public sealed record ShapeMetadata(
    int Number,
    string Name,
    int Offset,
    int Length,
    int VectorCount,
    int EndX,
    int EndY);
public sealed record ShapeTable(
    byte[] Bytes,
    IReadOnlyList<ShapeMetadata> Shapes);

public static class ApplesoftShapes
{
    public static ShapeTable Encode(ShapeDocument document);
}
```

`Encode` accepts schema version 1, 1..255 uniquely named shapes, directions
`up`, `right`, `down`, or `left`, and positive repeat counts. Plotting occurs
before movement. It emits the Applesoft count/reserved byte, one-based
little-endian offset table, packed vectors, and zero terminators. Metadata gives
each shape's number, byte range, expanded vector count, and final relative point.
The table and total vector count are limited to 65,535. Paths whose vectors
cannot be encoded losslessly, notably some trailing nonplot-up paths, are
refused. Codes use the `graphics.shape_*` family.

## Projects and optional cc65

Sources: [Projects](../src/A2Utils.Core/Projects). Manifest JSON is described in
[project builds](projects.md) and [project.schema.json](schemas/project.schema.json).

### Manifest option records

```csharp
public sealed record ProjectManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Target { get; init; } = "apple2e";
    public string? Cpu { get; init; }
    public string Output { get; init; } = "build.po";
    public ProjectDisk Disk { get; init; } = new();
    public DateTime Timestamp { get; init; } = new(2000, 1, 1);
    public List<ProjectFile> Files { get; init; } = [];
    public List<ProjectAsset> Assets { get; init; } = [];
    public List<MemoryRegion> Reserve { get; init; } = [];
    public bool CheckMemory { get; init; } = true;
    public int BasicWorkspaceBytes { get; init; }
    public string Runtime { get; init; } = "auto";
    public ProjectStartup? Startup { get; init; }
    public ProjectBoot? Boot { get; init; }
    public Cc65Options? Cc65 { get; init; }
    public ProjectExecutionSettings? Execution { get; init; }
    public string? Environment { get; init; }
    public string? ToolchainLock { get; init; }
}

public sealed record ProjectDisk
{
    public string FileSystem { get; init; } = "prodos";
    public string? Template { get; init; }
    public string? TemplateSha256 { get; init; }
    public int Blocks { get; init; } = 280;
    public string Container { get; init; } = "raw";
    public string? Order { get; init; }
    public string VolumeName { get; init; } = "A2PROJECT";
    public int VolumeNumber { get; init; } = 254;
}

public sealed record ProjectFile
{
    public string Source { get; init; } = "";
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "binary";
    public string? Type { get; init; }
    public ushort? Origin { get; init; }
    public ushort? AuxType { get; init; }
    public ushort? EntryPoint { get; init; }
    public bool Replace { get; init; }
    public bool Resident { get; init; } = true;
    public string MemoryBank { get; init; } = "main";
    public string? OverlayGroup { get; init; }
    public List<MemoryRegion> RuntimeMemory { get; init; } = [];
    public bool CheckBasic { get; init; } = true;
}

public sealed record ProjectStartup
{
    public string Path { get; init; } = "HELLO";
    public string Program { get; init; } = "";
    public bool Replace { get; init; }
}

public sealed record ProjectBoot
{
    public string Source { get; init; } = "";
    public string Kind { get; init; } = "asm";
    public ushort Origin { get; init; } = 0x0800;
    public int Sectors { get; init; } = 1;
}

public sealed record MemoryRegion(string Name, int Start, int Length,
    string MemoryBank = "main", string Kind = "data");
```

Manifest paths, including a relative `ProjectBuilder.Build` output override,
are resolved relative to the manifest directory. A caller can pass an absolute
output override when it needs different base-path behavior. `Kind` accepts
`asm`, `basic`, `basic-labels`, `binary`, `text`, `applesingle`, `cc65`,
`lores`, `hires`, or `hires-color`. See [source and metadata rules](projects.md#sources-and-metadata)
for inference and conflict behavior.

`Resident` includes a loadable file in overlap analysis. `BasicWorkspaceBytes`
extends resident BASIC ranges. `Reserve` adds caller-defined occupied memory.
`CheckMemory: false` disables overlap checks but not address-range validation.
`Startup` generates a BASIC RUN/BRUN launcher only when building from an OS
template, and must target a main-memory program. `Boot` instead compiles or reads
1..16 original 256-byte sectors at `$0800` for a 280-block DOS-order DOS 3.3
image; it does not supply an operating system.

`MemoryBank` accepts `main`, `aux`, `lc1`, `lc2`, `aux-lc1`, and `aux-lc2`.
Main/auxiliary payloads fit `$0000-$BFFF`; language-card payloads fit
`$D000-$FFFF`. Both language-card selections on each side share `$E000-$FFFF`,
so overlapping ranges there conflict even when their bank names differ.
All banked files require BIN metadata and an explicit loader. Bank annotations
do not change stored bytes, `AuxType`, load headers, or execute bank switches.
Bank/range validation applies even with `Resident: false` or `CheckMemory: false`.
Earlier unchecked manifests placing a payload above `$BFFF` in default main
memory must now select its physical language-card bank; `$C000-$CFFF` remains I/O
and ROM rather than payload RAM. See [IIe memory banks](iie-memory.md).

`RuntimeMemory` declares additional BSS, zero-page, software-stack, heap or data
allocations using explicit ranges. Compiler segments and its inferred software
stack populate the built report automatically. `OverlayGroup` names mutually
exclusive programs; different groups and always-resident files still conflict.
`Runtime` accepts `auto`, `basic-system`, and `system` to select ProDOS runtime
reservations. See [runtime accounting](runtime-memory.md) for the allocation and
overlay lifetime contracts.

### Project result records

```csharp
public sealed record BuildInput(string Path, string Sha256);

public sealed record BuiltFile(
    string Path,
    string Kind,
    string Type,
    ushort AuxType,
    int? Origin,
    int? EntryPoint,
    int Length,
    string Sha256,
    bool Resident,
    IReadOnlyDictionary<string, int> Symbols,
    object? SourceMap)
{
    public string MemoryBank { get; init; } = "main";
    public string? OverlayGroup { get; init; }
    public IReadOnlyList<MemoryRegion> RuntimeMemory { get; init; } = [];
}

public sealed record ProjectBuildResult(
    string OutputPath,
    string Sha256,
    string Target,
    string Cpu,
    string FileSystem,
    string Bootability,
    string ToolVersion,
    DateTime Timestamp,
    IReadOnlyList<BuildInput> Inputs,
    IReadOnlyList<BuiltFile> Files,
    IReadOnlyList<MemoryRegion> Memory,
    IReadOnlyList<ProgramDiagnostic> Diagnostics)
{
    public IReadOnlyList<ProjectAssetReport> Assets { get; init; } = [];
    public bool CheckOnly { get; init; }
    public bool Preflight { get; init; }
    public ProjectBuildPlan? Plan { get; init; }
    public ProjectExecutionSettings? Execution { get; init; }
    public BuiltBoot? Boot { get; init; }
    public bool CacheHit { get; init; }
}

public sealed record BuiltBoot(
    string Source, string Kind, int Origin, int Sectors,
    int Length, string Sha256);
```

Paths and hashes identify exact captured inputs and outputs. A `BuiltFile`
contains effective metadata after kind-specific inference. `Symbols` and
`SourceMap` are populated for native assembly/BASIC mappings and compiler
reports as applicable. `Memory` lists resident occupied ranges, not built-in
reservations. `Bootability` is `data-volume` for an ordinary new format,
`self-booting-unverified` for declared original boot sectors, or
`template-preserved-unverified` for a copied template. In check-only mode no
image is produced and `Sha256` is the empty string.

`Plan` is
populated for real builds and full preflight; execution suite paths in results
are absolute. `CacheHit` is true only when `ProjectBuildCache` restored a
validated cached image. The additional records are:

```csharp
public sealed record ProjectFileChange(string Path, string Action, long? PreviousLength, int Length);
public sealed record ProjectBuildPlan(long ImageSizeBytes, long FreeBytesBefore, long FreeBytesAfter,
    IReadOnlyList<ProjectFileChange> Files, IReadOnlyList<string> CreatedDirectories);
public sealed record ProjectExecutionSettings(string Suite, string DiskDevice = "flop1");
```

Changes use `Action` values `add` and `replace`. Lengths/free space are bytes;
`PreviousLength` is null for additions. An empty plan is not inferred from a
fast source check.

### `ProjectJson`

```csharp
public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; }
}
```

The shared manifest serializer uses camel-case output, case-sensitive input,
rejects unknown members, respects required constructor parameters, writes
indented JSON, and limits depth to 32. `ProjectBuilder` additionally rejects
duplicate JSON property names before deserialization.

### Target profiles

```csharp
public sealed record PlatformSymbol(
    string Name,
    int Address,
    string Description);
public sealed record TargetMemoryBank(
    string Name,
    int Start,
    int Length,
    string? SharedUpperBank = null)
{
    public int? SharedUpperStart { get; } // $E000 when SharedUpperBank is set
}
public sealed record TargetProfile(
    string Name,
    string Description,
    string Cpu,
    int MainMemoryBytes)
{
    public int BankedMainMemoryBytes { get; init; }
    public int AuxiliaryMemoryBytes { get; init; }
    public string? AuxiliaryMemoryRequirement { get; init; }
    public bool Supports80ColumnText { get; init; }
    public bool SupportsMouseText { get; init; }
    public IReadOnlyList<TargetMemoryBank> MemoryBanks { get; init; }
}

public static class TargetProfiles
{
    public static IReadOnlyList<TargetProfile> All { get; }
    public static IReadOnlyList<PlatformSymbol> Symbols { get; }
    public static TargetProfile Get(string name);
    public static CpuKind ParseCpu(string name);
    public static IReadOnlyList<MemoryRegion> Reserved(string runtime);
}
```

`All` contains `apple2plus`, `apple2e`, `apple2enh`, and `apple2c`.
`Symbols` exposes common monitor, soft-switch, keyboard, speaker, and ProDOS MLI
addresses, including IIe bank-selection and 80-column switches. `Get` requires an exact profile name. `ParseCpu` accepts exact
`6502`, `65c02`, or `w65c02` and maps them to `CpuKind`. `Reserved` returns zero
page/stack/system workspace, display page 1, and a DOS (`$9600-$BFFF`) or ProDOS
(`$BF00-$BFFF`) main reservation. Pass `dos33` or `prodos`; the latter also protects
both main language-card banks for the kernel, dispatcher and reserved runtime
space. Invalid profile/CPU values use `project.target` or `project.cpu`.

`MemoryBanks` reports physical windows and their shared-upper-memory aliases.
IIe/IIc profiles expose six windows; the baseline 48 KiB Apple II Plus profile
exposes only `main`. `MainMemoryBytes` remains the 48 KiB contiguous region;
`BankedMainMemoryBytes` is 16 KiB on IIe/IIc. Auxiliary capability is 64 KiB and
`AuxiliaryMemoryRequirement` distinguishes optional IIe expansion from built-in
IIc RAM. Bank annotations cannot confirm that a user's machine has that expansion.
Unenhanced IIe supports 80-column text with auxiliary display RAM, but MouseText
requires the enhanced IIe or IIc character ROM. ProDOS auxiliary-bank files emit
`project.prodos_auxiliary_memory` to remind the loader to protect its memory from
`/RAM` or disconnect that RAM disk.

### `Cc65Options`, `Cc65Result`, and `Cc65Compiler`

```csharp
public sealed record Cc65Options
{
    public string Compiler { get; init; } = "cl65";
    public string Target { get; init; } = "apple2";
    public string? ExpectedVersion { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public bool Optimize { get; init; } = true;
    public List<string> AdditionalSources { get; init; } = [];
    public List<string> Includes { get; init; } = [];
    public List<string> Defines { get; init; } = [];
    public string? LinkerConfig { get; init; }
    public string? ToolchainRoot { get; init; }
    public Dictionary<string, string> SegmentBanks { get; init; } = new(StringComparer.Ordinal);
}

public sealed record Cc65Result(
    byte[] AppleSingle,
    string Version,
    string Map,
    string Labels,
    IReadOnlyList<BuildInput> Inputs)
{
    public string CompilerPath { get; init; } = "";
    public IReadOnlyList<BuildInput> ToolchainInputs { get; init; } = [];
    public IReadOnlyDictionary<string, int> Symbols { get; init; }
    public IReadOnlyList<Cc65Segment> Segments { get; init; } = [];
    public string Debug { get; init; } = "";
    public IReadOnlyList<AssemblySourceMapEntry> SourceMap { get; init; } = [];
    public IReadOnlyList<ProgramDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record Cc65Segment(string Name, int Start, int Length, string Kind);
public sealed record Cc65SourceMap(
    string CompilerVersion,
    string CompilerPath,
    string Map,
    string Labels,
    IReadOnlyList<Cc65Segment> Segments,
    IReadOnlyList<AssemblySourceMapEntry> Entries)
{
    public string Format { get; init; } = "cc65-dbg-2.0";
}

public static class Cc65Compiler
{
    public static Cc65Result Compile(
        string source,
        Cc65Options options,
        string projectRoot,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, byte[]>? generatedInputs = null);
}
```

`Compile` runs an existing `cl65` installation synchronously in an isolated
temporary copy of supported project source. `Target` is `apple2` or `apple2enh`;
timeout is 1..3600 seconds; option lists permit at most 128 entries each.
Defines use `NAME` or `NAME=value`. Sources/includes must stay below
`projectRoot`; links, unsafe/computed includes, unsupported source types,
excluded build directories, excessive inputs, and source changes are refused.
The trusted system temporary root is resolved to a physical directory before
staging, without relaxing link checks on caller-supplied paths. Cleanup is bounded
and best-effort so a lingering compiler-process lock cannot mask the compile result.

The compiler is resolved as an explicit path or from `PATH`, version-probed,
then run with map, VICE-label, and ld65 v2 debug output. A nonzero compiler exit raises
`cc65.compile_failed` with a diagnostic. Timeout kills the process tree and uses
`cc65.timeout`; missing/start failures use exit code 5. The returned AppleSingle
has already passed `AppleSingleProgram.Decode`, but consumers still decode it
to obtain the data fork/type/auxiliary value. See [cc65 reproducibility limits](cc65.md).
Located compiler diagnostics map staged sources back to original paths. Parsed
VICE symbols and ld65 segments feed project symbols and runtime footprints.
Validated debug file/line/span records feed stable original-source address ranges;
unmapped external or generated-intermediate files are omitted. Debug input is
limited to 4 MiB and 131,072 records, and mapped coverage is limited to 1 MiB.
`LinkerConfig` selects a validated project-local single-output `.cfg`; `SegmentBanks`
assigns runtime segment banks. `ToolchainRoot` selects and fingerprints bounded
distribution trees. Optional `generatedInputs` supplies absolute project paths
and bytes for isolated compilation without writing generated host source files.

### Project inspection and import

`ProjectResolver.Inspect` exposes the same resolved manifest inputs used by a
build while leaving the declared output untouched:

```csharp
public sealed record ProjectResolvedSettings(
    string Target, string Cpu, string Runtime, bool CheckMemory,
    int BasicWorkspaceBytes, string? EnvironmentPath,
    string? ToolchainLockPath, ProjectExecutionSettings? Execution);
public sealed record ProjectResolvedDisk(
    string FileSystem, string Container, string Order, int Blocks,
    string VolumeName, int VolumeNumber, string? TemplatePath);
public sealed record ProjectResolvedBoot(
    string SourcePath, string Kind, int Origin, int Sectors,
    int PayloadLength, string PayloadSha256, string Action);
public sealed record ProjectResolvedFile(
    string SourcePath, string ImagePath, string Kind, string Type,
    int? Origin, int? EntryPoint, int Length, string MemoryBank,
    string? OverlayGroup, bool Resident, bool Replace);
public sealed record ProjectResolvedAsset(
    string Name, string SourcePath, string Kind,
    IReadOnlyList<string> VirtualOutputs);
public sealed record ProjectDependency(
    string Role, string Path, string? Sha256, bool Exists);
public sealed record ProjectToolRequirement(
    string Id, bool Required, string Purpose, string? Path,
    string? ExpectedVersion, bool? Available);
public sealed record ProjectPlannedEntry(
    string ImagePath, string? SourcePath, string Kind, string Action,
    string Type, int? Origin, int Length);
public sealed record ProjectResolutionDiskPlan(
    long? ImageSizeBytes, long? FreeBytesBefore, long CompiledPayloadBytes,
    long BootSectorBytes, IReadOnlyList<ProjectPlannedEntry> Entries,
    IReadOnlyList<string> Directories);
public sealed record ProjectResolutionResult(
    int SchemaVersion, string ManifestPath, string ProjectRoot,
    string OutputPath, ProjectResolvedSettings Settings,
    ProjectResolvedDisk Disk, ProjectResolvedBoot? Boot,
    IReadOnlyList<ProjectResolvedFile> Files,
    IReadOnlyList<ProjectResolvedAsset> Assets,
    IReadOnlyList<ProjectDependency> Dependencies,
    IReadOnlyList<ProjectToolRequirement> ToolRequirements,
    IReadOnlyList<MemoryRegion> Memory, ProjectResolutionDiskPlan DiskPlan,
    IReadOnlyList<ProgramDiagnostic> Diagnostics);
public static class ProjectResolver
{
    public static ProjectResolutionResult Inspect(
        string manifestPath,
        CancellationToken cancellationToken = default);
}

public sealed record ImportedProjectFile(
    string ImagePath, string SourcePath, string Kind,
    bool Editable, string? Reason);
public sealed record ProjectImportResult(
    int SchemaVersion, string Directory, string ProjectPath,
    string TemplatePath, string InputSha256,
    IReadOnlyList<ImportedProjectFile> Files,
    IReadOnlyList<string> Diagnostics);
public static class ProjectImporter
{
    public static ProjectImportResult Import(
        string imagePath, string destination,
        bool disassembleBinaries = false, string target = "apple2e",
        string? inputOrder = null, string? inputFileSystem = null,
        CancellationToken cancellationToken = default);
}
```

`ProjectResolutionResult` contains the absolute manifest, project root, and
output paths; effective target, CPU, runtime, environment, execution, and disk
settings; compiled file metadata and assets; hashed direct/transitive
dependencies; external-tool requirements; occupied memory; and a disk-entry
plan. Inspection uses check-only compilation, so cc65 sources can invoke the
configured compiler in temporary storage, but it does not create the output
image or its parent directory. The corresponding schema is
[project-resolution.schema.json](schemas/project-resolution.schema.json).

The nullable resolved `Boot` record contains its absolute source path, kind,
origin, sector count, emitted payload length/hash, and `write-sectors` action.
The disk plan counts emitted boot bytes in `CompiledPayloadBytes` and reports the
full padded sector span as `BootSectorBytes`. When execution is configured,
inspection loads every suite case without running an engine and adds hashed
dependencies for suite/spec/profile/lock files, routine sources and assembler
includes, visual expectations, and auxiliary disks. MAME cases produce separate
`mame` executable and `mame-roms` directory requirements. Each reports a path or
expected version only when every case resolves to one common value, and reports
availability from the resolved host paths. The project build mount is excluded
because the workflow replaces it with the future output.

`ProjectImporter.Import` creates a new project directory from a verified image.
It writes a hash-pinned template, strict manifest, exported editable sources,
locked reference files, and an import report. Optional binary disassembly emits
reassemblable source for load-addressed files; input order and filesystem may be
overridden explicitly. The source image is limited to 34 MiB, the destination
must not exist, and `target` is resolved through `TargetProfiles` before staging.

### `ProjectBuilder`

```csharp
public static class ProjectBuilder
{
    public static ProjectBuildResult Build(
        string manifestPath,
        string? outputPath = null,
        bool overwrite = false,
        bool checkOnly = false,
        CancellationToken cancellationToken = default,
        bool preflight = false);
}
```

`Build` strictly loads schema-version-1 JSON, resolves manifest inputs, compiles
every file kind, reconciles type/origin/auxiliary/entry-point metadata, checks
target/CPU compatibility and memory ranges, rechecks input hashes, and creates
or copies one staged disk. `outputPath` overrides the manifest `output`;
`overwrite` permits replacing a host output, while each `ProjectFile.Replace`
separately permits replacing an image entry in a template.

With `checkOnly: true`, compilation, cc65 invocation, schema/metadata checks,
input hashing, and memory checks still run, but disk allocation, population,
and final image validation do not. Normal builds reopen the staged image and
verify structure, payload, type, and applicable load metadata before commit.
Inputs/templates remain unchanged on failure.

With `preflight: true`, the full build/validation runs on a disposable image and
returns its hash and change/capacity plan. The output path and overwrite policy
are checked, but no output parent is created or output committed. Combining
`checkOnly` and `preflight` is refused. Preflight cannot guarantee host capacity
or emulator behavior.

Failures primarily use `project.*`; compilation and lower-level disk/graphics
errors can propagate with their own codes. Source diagnostics are attached for
BASIC checking, assembly, memory overlap, and external compiler failures.

### `ProjectBuildCache`

```csharp
public static class ProjectBuildCache
{
    public static ProjectBuildResult Build(
        string manifestPath,
        string cacheDirectory,
        string? outputPath = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default);
}
```

This opt-in library API stores complete validated images by content. A cache hit
requires matching hashes for every recorded input and the current library
version, then revalidates the cached image hash, filesystem, and structure before
transactionally restoring it to the requested output. It rechecks inputs during
the restore and sets `ProjectBuildResult.CacheHit` to `true`; a normal build or
cache miss reports `false`.

The output must be outside the cache directory. Cache metadata and images are
bounded to 1,024 entries per normalized manifest path, 8 MiB per metadata entry,
34 MiB per image, and 8,192 inputs of at most 64 MiB each and 512 MiB combined.
A build remains successful and uncached when new metadata
would cross a limit or another writer owns the manifest cache lock; over-limit
caches are refused rather than silently pruned. Cache files are implementation
data and should not be edited or treated as build artifacts. The
CLI exposes the same opt-in behavior through `a2 build --cache DIRECTORY` for
normal builds; builds without that option do not read or populate the cache.

### `ProjectWorkflow`

```csharp
public sealed record ProjectWorkflowResult(int SchemaVersion, bool Passed, ProjectBuildResult Build,
    ExecutionSuiteResult Tests, string ArtifactDirectory)
{
    public bool Cancelled { get; init; }
}
public static class ProjectWorkflow
{
    public static ProjectWorkflowResult Run(string manifestPath, string artifactDirectory,
        string? outputPath = null, bool overwrite = false, CancellationToken cancellationToken = default);
    public static ProjectWorkflowResult Run(string manifestPath, string artifactDirectory,
        string? outputPath, bool overwrite, ExecutionSuiteRunOptions suiteOptions,
        CancellationToken cancellationToken = default);
    public static bool HasAssertions(ExecutionSpec spec);
}
```

`Run` requires manifest execution settings. It preflights, validates suite inputs
and symbols, builds with input/image hash guards before commit, and runs each
case with the selected mount pinned to the build. Other disks are also pinned.
The artifact directory must be new unless `suiteOptions.CreateRunSubdirectory`
creates a unique child beneath the supplied parent. Invalid manifest/suite fields,
missing input files, and input/hash conflicts are rejected before output
replacement. Runtime setup failures (such as a wrong emulator version or invalid
ROMs), behavioral failures, and later artifact I/O failures retain a successfully
committed image.
`HasAssertions` expects validated fields and counts assertions, bounded debug/
routine/cycle conditions, symbolic assertions, and explicit disk verification.
See [project testing](projects.md#build-and-test-together) for
target matching, reports, cancellation, and source-map limits.

## Execution engines

Sources: [Execution](../src/A2Utils.Core/Execution). MAME is the compatible default
and its adapter is pinned to 0.289; no emulator or ROM is bundled. The `cpu` engine
runs bounded 6502/Apple-compatible 65C02 routines in-process. See
[automated execution](execution.md).

### Execution specifications

```csharp
public sealed record ExecutionSpec
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "run";
    public string Engine { get; init; } = "mame";
    public string EmulatorPath { get; init; } = "";
    public string ExpectedVersion { get; init; } = MameAdapter.ApiVersion;
    public string Machine { get; init; } = "";
    public string RomDirectory { get; init; } = "";
    public string? Environment { get; init; }
    public string? ToolchainLock { get; init; }
    public string StorageProfile { get; init; } = "floppy";
    public string GamePort { get; init; } = "none";
    public string DiskImage { get; init; } = "";
    public string DiskDevice { get; init; } = "flop1";
    public IReadOnlyList<ExecutionDisk> Disks { get; init; } = [];
    public IReadOnlyList<DiskFileAssertion> DiskAssertions { get; init; } = [];
    public IReadOnlyList<SymbolicMemoryAssertion> SymbolicMemory { get; init; } = [];
    public SymbolicCompletionCondition? SymbolicUntil { get; init; }
    public double EmulatedSeconds { get; init; } = 15;
    public double HostTimeoutSeconds { get; init; } = 60;
    public IReadOnlyList<ExecutionKeys> Keys { get; init; } = [];
    public IReadOnlyList<ExecutionStep> Steps { get; init; } = [];
    public IReadOnlyList<string> TextNotContains { get; init; } = [];
    public bool CheckBasicRuntime { get; init; }
    public IReadOnlyList<MemoryAssertion> Memory { get; init; } = [];
    public IReadOnlyList<GraphicsMemoryAssertion> GraphicsMemory { get; init; } = [];
    public IReadOnlyList<MemoryCapture> ObserveMemory { get; init; } = [];
    public IReadOnlyList<RegisterAssertion> Registers { get; init; } = [];
    public IReadOnlyList<string> TextContains { get; init; } = [];
    public CompletionCondition? Until { get; init; }
    public int TextPage { get; init; } = 1;
    public int TextColumns { get; init; } = 40;
    public bool DecodeIIeText { get; init; }
    public ExecutionRoutine? Routine { get; init; }
    public ExecutionCycleMeasurement? Cycles { get; init; }
    public ExecutionDebug? Debug { get; init; }
    public bool Screenshot { get; init; }
    public ScreenshotAssertion? ScreenshotAssertion { get; init; }
    public ExecutionAudioOptions? Audio { get; init; }
    public bool Trace { get; init; }

    public static JsonSerializerOptions JsonOptions { get; }
    public static ExecutionSpec Load(string path);
    public ExecutionSpec ResolvePaths(string directory);
    public IReadOnlyList<ExecutionDisk> GetDisks();
}

public sealed record ExecutionKeys(double AtSeconds, string Text);
public sealed record MemoryAssertion(int Address, string Hex, string Bank = "cpu");
public sealed record MemoryCapture(int Address, int Length, string Bank = "cpu");
public sealed record RegisterAssertion(string Name, long Value);
public sealed record CompletionCondition(
    int Address,
    int Value,
    double AfterSeconds = 0, string Bank = "cpu");
public sealed record GraphicsMemoryAssertion(
    string ExpectedImage, string Mode, int Page = 1, string Bank = "cpu");
public sealed record ExecutionGraphicsMemoryResult(
    string Mode, int Page, string Bank, string ExpectedImage,
    string ExpectedSha256, bool Passed, int MismatchedBytes,
    string? FirstMismatch, VisualComparisonResult? Pixels,
    string ExpectedPreview, string? ActualPreview, string? DifferenceImage)
{
    public int? StepIndex { get; init; }
    public string? StepName { get; init; }
}
```

`Load` strictly decodes JSON with camel-case names, required `schemaVersion`, no
unknown/duplicate properties, and maximum depth 32. It resolves emulator, ROM,
environment/lock, disk, routine-source, screenshot, and graphics-expectation
paths relative to the specification file. JSON failures become
`execution.invalid_spec`; host read failures propagate. `ResolvePaths` performs
the same path resolutions, including explicit disk mounts and step graphics, for
a record constructed in code. `GetDisks` normalizes legacy single-image fields
into one mount; validate untrusted specifications before using it.

`Engine` selects `mame` or `cpu`; omission keeps the MAME behavior. CPU execution
requires a disk-free routine and a machine name, but no emulator or ROM path.
`apple2`, `apple2p`, and `apple2e` select the MOS 6502; `apple2ee` and `apple2c`
select the Apple-compatible 65C02 instruction set.

Times are finite seconds since power-on. Key text is posted through MAME's
natural keyboard. Memory hex may contain spaces. Register names are uppercase
MAME CPU-state names. Completion polls one observable byte after its delay.
Text assertions inspect case-sensitive decoded text-page memory. The default is
legacy 40-column decoding; `TextColumns: 80` or `DecodeIIeText: true` selects physical
IIe text pages, display attributes, and MouseText tokens. See [IIe observations](iie-execution.md).

```csharp
public sealed record ExecutionDisk(string Device, string Image, string? ExpectedSha256 = null,
    string? InputOrder = null, string? InputFileSystem = null, bool Verify = false);
public sealed record DiskFileAssertion(string Device, string Path, bool Exists = true,
    string? Sha256 = null, string? Hex = null, string? Type = null, int? AuxType = null, long? Length = null);
public sealed record SymbolicMemoryAssertion(string Program, string Symbol, string Hex, int Offset = 0, string? Bank = null);
public sealed record SymbolicCompletionCondition(string Program, string Symbol, int Value,
    int Offset = 0, double AfterSeconds = 0, string? Bank = null);
public sealed record ExecutionSourceLocation(string Program, int Address, string? File, int Line,
    string Source, string Observation)
{
    public string MemoryBank { get; init; } = "main";
}

public static class BuildExecution
{
    public static ExecutionSpec Bind(ExecutionSpec spec, ProjectBuildResult build, string device = "flop1");
    public static int ResolveAddress(ProjectBuildResult build, string program, string symbol, int offset = 0);
    public static IReadOnlyList<ExecutionSourceLocation> Locate(ProjectBuildResult build, ExecutionResult result);
    public static IReadOnlyList<ExecutionSourceLocation> Locate(ProjectBuildResult build, ExecutionResult result,
        ExecutionSpec? source);
    public static string? WriteSourceTrace(ProjectBuildResult build, ExecutionResult result);
    public static ExecutionResult Annotate(ExecutionSpec source, ProjectBuildResult build, ExecutionResult result);
}
```

`Bind` requires a full build/preflight hash, replaces the selected mount, resolves
symbols, clears symbolic fields, and validates the resulting specification.
`ResolveAddress` resolves one exported symbol plus offset into a 16-bit address.
`Locate` uses native assembly and cc65 source maps with final/unique MAME
sampled PCs; the overload with a source specification also retains unique CPU
instruction PCs and the routine source columns already recorded in its trace.
`ProjectBuildCache` rehydrates the supported assembly, BASIC/basic-labels, and
cc65 source-map representations when restoring cached build results. Arbitrary
caller-supplied opaque `SourceMap` objects have no general rehydration contract.
`Annotate` adds symbol names and available source locations to symbolic memory
failures. Disk mount and assertion limits are in
[execution disk assertions](execution.md#multiple-disks-and-saved-file-assertions).

### Debugging and bank-aware observations

```csharp
public sealed record ExecutionDebug
{
    public IReadOnlyList<ExecutionBreakpoint> Breakpoints { get; init; } = [];
    public IReadOnlyList<ExecutionWatchpoint> Watchpoints { get; init; } = [];
    public int StepInstructions { get; init; }
    public int HistoryInstructions { get; init; } = 64;
}
public sealed record ExecutionBreakpoint(int? Address = null, string? Program = null,
    string? Symbol = null, int Offset = 0, double AfterSeconds = 0);
public sealed record ExecutionWatchpoint(int? Address = null, int Length = 1,
    string Access = "write", string? Program = null, string? Symbol = null,
    int Offset = 0, double AfterSeconds = 0);
public sealed record ExecutionMemory(string Bank, int Address, string Hex);
public sealed record ExecutionDebugStop(string Kind, int Index, int Address,
    int? Value, int ProgramCounter);
public sealed record ExecutionInstruction(int Address, string Disassembly);
public sealed record ExecutionDebugResult(ExecutionDebugStop Trigger,
    int SteppedInstructions, IReadOnlyList<ExecutionInstruction> History);
```

See [debugging](runtime-debugging.md) for trigger timing, bounded steps, and
capture-time disassembly limitations. Symbolic addresses are resolved by
`BuildExecution.Bind`; standalone execution requires numeric points.

`ExecutionObservation` and `ExecutionResult` add `BankMemory`, `Debug`,
`TextScreen`, and `Video` init properties. `BankMemory` includes CPU and physical
captures; the legacy `Memory` dictionary contains CPU captures only.
`ExecutionObservation.TextPages` holds raw text-page hex by `main`/`aux` bank.

```csharp
public static class AppleIIeMemory
{
    public static bool IsIIeMachine(string? machine);
    public static bool IsValidRange(string? bank, int address, int length);
    public static string CreateLuaHelpers();
}
public static class AppleIIeTextDecoder
{
    public static string CreateLuaDecoder();
    public static AppleIIeTextScreen Decode(ReadOnlySpan<byte> mainPage,
        ReadOnlySpan<byte> auxiliaryPage, int columns, bool alternateCharacterSet,
        bool mouseTextSupported = true);
}
public sealed record AppleIIeTextScreen(int Columns, int Rows, string Text,
    IReadOnlyList<AppleIIeTextCell> Cells);
public sealed record AppleIIeTextCell(int Row, int Column, string Bank, int Offset,
    byte Value, string Text, string DisplayMode, int? MouseTextIndex);
```

`CreateLuaDecoder` returns the equivalent bounded byte-decoder used by generated
execution scripts. `Decode` requires one complete 1024-byte main page and, for
80 columns, one auxiliary page. It preserves inverse/flashing attributes and
enhanced-machine MouseText indices. Unenhanced IIe callers pass
`mouseTextSupported: false`.

### `ExecutionSuite` and `ExecutionSuiteRunner`

```csharp
public sealed record ExecutionSuite
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<string> Tests { get; init; } = [];

    public static ExecutionSuite Load(string path);
}

public sealed record ExecutionSuiteRunOptions
{
    public IReadOnlyList<string> Filters { get; init; } = [];
    public int Jobs { get; init; } = 1;
    public string? RerunFailedPath { get; init; }
    public bool WriteProgress { get; init; }
    public bool CreateRunSubdirectory { get; init; }
}
public sealed record ExecutionSuiteCaseInfo(
    int Index, string Id, string Path, string Name, bool Selected);
public sealed record ExecutionSuitePlan(
    int SchemaVersion, string SuitePath, int Discovered, int Planned,
    IReadOnlyList<ExecutionSuiteCaseInfo> Cases);
public sealed record ExecutionSuiteCounts(
    int Planned, int Completed, int Passed, int Failed,
    int Cancelled, int NotRun);
public sealed record ExecutionSuiteCaseResult(
    int Index, string Id, string Path, string Name, bool Selected,
    string Status, string? ArtifactDirectory);
public sealed record ExecutionSuiteEvent(
    int SchemaVersion, long Sequence, string Event, ExecutionSuiteCounts Counts)
{
    public int? Index { get; init; }
    public string? Id { get; init; }
    public string? Path { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? ArtifactDirectory { get; init; }
}
public static class ExecutionSuiteRunner
{
    public static ExecutionSuitePlan Plan(
        string suitePath, ExecutionSuiteRunOptions? options = null);
    public static ExecutionSuiteResult Run(
        string suitePath, string artifactDirectory,
        ExecutionSuiteRunOptions? options = null,
        CancellationToken cancellationToken = default);
    public static Task<ExecutionSuiteResult> RunAsync(
        string suitePath, string artifactDirectory,
        ExecutionSuiteRunOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

`Load` accepts schema version 1 with 1..128 nonblank test paths and resolves
them relative to the suite file. Invalid JSON or suite shape uses
`execution.invalid_suite`. `Plan` loads and intrinsically validates every case,
applies up to 32 case-insensitive name/path filters of at most 256 characters,
and can select failures from an earlier `suite-result.json` without creating
artifacts. `Run` and `RunAsync` use stable suite indexes for case identities and
artifact directory names, run sequentially by default, and accept `Jobs` from
1 through 16 for bounded parallel execution. They can flush ordered progress
events and create a unique child beneath an artifact parent. Cancellation stops
new work and records selected cases that did not start.

### `MameAdapter`

```csharp
public static partial class MameAdapter
{
    public const string ApiVersion = "0.289";

    public static void Validate(ExecutionSpec spec);
    public static IReadOnlyList<string> CreateArguments(
        ExecutionSpec spec,
        string copiedDisk,
        string script,
        string artifacts);
    public static IReadOnlyList<string> CreateArguments(
        ExecutionSpec spec,
        IReadOnlyDictionary<string, string> copiedDisks,
        string script,
        string artifacts);
    public static string CreateScript(
        ExecutionSpec spec,
        string artifacts);
    public static byte[] ParseHex(string hex);
}
```

`Validate` enforces schema/version, machine (`apple2`, `apple2p`, `apple2e`,
`apple2ee`, or `apple2c`), explicit paths, compatible `flop1`/`flop2` or CFFA2
`hard1`/`hard2` mounts, time bounds, count limits, unique register names/memory
start addresses, and assertion ranges.
Memory and completion reads must stay within `$0000-$BFFF` or `$D000-$FFFF` so
I/O soft-switch reads are excluded. Total observed memory is at most 65,536
bytes. Errors use `execution.invalid_spec`.

`CreateArguments` validates the spec and returns the exact data-only MAME
argument array for the supplied disk/script/artifact paths. It disables host
configuration, plugins, video/sound output, throttling, and separates all
mutable MAME directories under `artifacts`; non-IIc profiles also clear the
default slot-2/slot-4 cards.

The dictionary overload maps every device to its isolated copy; the single-string
overload requires exactly one mount. Unresolved symbolic assertions are refused.

`CreateScript` validates the spec and returns generated Lua that posts keys,
samples optional PC trace data once per frame, polls completion, captures
register/memory/text observations, optionally requests a screenshot, and exits.
User strings are byte-escaped; no user-provided Lua is evaluated.

`ParseHex` removes literal spaces and decodes complete hexadecimal byte pairs.
Malformed/null input becomes `execution.invalid_spec`.

### Execution observations and results

```csharp
public sealed record ExecutionObservation(
    string StopReason,
    double EmulatedSeconds,
    IReadOnlyDictionary<string, long> Registers,
    IReadOnlyDictionary<int, string> Memory,
    string ScreenText)
{
    public IReadOnlyList<ExecutionMemory> BankMemory { get; init; } = [];
    public ExecutionCycleResult? Cycles { get; init; }
    public ExecutionDebugResult? Debug { get; init; }
    public IReadOnlyDictionary<string, string> TextPages { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, int> Video { get; init; } = new Dictionary<string, int>();
    public AppleIIeTextScreen? TextScreen { get; init; }
    public IReadOnlyList<ExecutionStepResult> Steps { get; init; } = [];
}

public sealed record ExecutionResult(
    int SchemaVersion,
    string Name,
    bool Passed,
    string StopReason,
    string? EmulatorVersion,
    double? EmulatedSeconds,
    IReadOnlyDictionary<string, long> Registers,
    IReadOnlyDictionary<int, string> Memory,
    string? ScreenText,
    string? InputSha256,
    string ArtifactDirectory,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<ProgramDiagnostic> Diagnostics)
{
    public IReadOnlyList<ExecutionDiskResult> Disks { get; init; } = [];
    public IReadOnlyList<ExecutionMemory> BankMemory { get; init; } = [];
    public IReadOnlyList<ExecutionStepResult> Steps { get; init; } = [];
    public IReadOnlyList<ExecutionCheckpoint> Checkpoints { get; init; } = [];
    public ExecutionAudioResult? Audio { get; init; }
    public ExecutionEnvironmentEvidence? Environment { get; init; }
    public ExecutionCycleResult? Cycles { get; init; }
    public ExecutionDebugResult? Debug { get; init; }
    public AppleIIeTextScreen? TextScreen { get; init; }
    public ExecutionScreenshotResult? ScreenshotComparison { get; init; }
    public IReadOnlyList<ExecutionGraphicsMemoryResult> GraphicsMemory { get; init; } = [];
    public IReadOnlyDictionary<string, int> Video { get; init; } = new Dictionary<string, int>();
}

public sealed record ExecutionDiskResult(
    string Device, string InputPath, string ArtifactPath,
    string InputSha256, string? OutputSha256);

public sealed record ExecutionSuiteResult(
    int SchemaVersion,
    bool Passed,
    IReadOnlyList<ExecutionResult> Tests)
{
    public string SuitePath { get; init; } = "";
    public string ArtifactDirectory { get; init; } = "";
    public ExecutionSuiteCounts Counts { get; init; } = new(0, 0, 0, 0, 0, 0);
    public IReadOnlyList<ExecutionSuiteCaseResult> Cases { get; init; } = [];
    public bool Cancelled { get; init; }
}
```

An observation is the parsed adapter output before assertions are applied.
Memory values are uppercase/lowercase-tolerant hex strings keyed by start
address. `ExecutionResult.Passed` requires a parsed observation and no error
diagnostic. Optional values can be null when execution fails before capture.
`Artifacts` contains absolute paths produced under the run directory, including
`result.json`. `ExecutionSuiteResult` is the aggregate data contract used by
suite orchestration; constructing it performs no validation.

The legacy `ExecutionResult.InputSha256` is the first mount's input hash.
Graphics-memory comparisons include byte/pixel differences and reviewable
preview paths. Suite counts and case records preserve planned selection and
stable original indexes even when cases execute in parallel.

`ExecutionRunner.EvaluateDisks(ExecutionSpec spec,
IReadOnlyDictionary<string, string> copiedDisks,
CancellationToken cancellationToken = default)` returns
`IReadOnlyList<ProgramDiagnostic>` for filesystem and saved-file checks. Call it
only after the emulator has released its copies. It reads those copies, reports
inspection failures as diagnostics, and does not modify original images.

### `ExecutionRunner`

```csharp
public static partial class ExecutionRunner
{
    public static void Validate(ExecutionSpec spec);

    public static ExecutionResult Run(
        ExecutionSpec spec,
        string artifactDirectory,
        CancellationToken cancellationToken = default);

    public static Task<ExecutionResult> RunAsync(
        ExecutionSpec spec,
        string artifactDirectory,
        CancellationToken cancellationToken = default);

    public static IReadOnlyList<ProgramDiagnostic> Evaluate(
        ExecutionSpec spec,
        ExecutionObservation observation);

    public static IReadOnlyList<ProgramDiagnostic> EvaluateDisks(
        ExecutionSpec spec,
        IReadOnlyDictionary<string, string> copiedDisks,
        CancellationToken cancellationToken = default);

    public static bool ConditionMatches(
        ExecutionCondition condition,
        ExecutionObservation observation);

    public static ExecutionObservation ParseObservation(
        string text,
        string screenText);
}
```

`Validate` dispatches strict validation to the selected engine. `Run` synchronously
waits for `RunAsync`. Every run requires a new artifact directory. MAME additionally
requires bounded disk images, an existing ROM directory, and an executable whose
version is exactly `MameAdapter.ApiVersion`; it receives only disposable disk copies.
The CPU engine assembles or reads one routine, executes it in flat memory, evaluates
final assertions, and writes the same result envelope and routine evidence without
launching an external process.

Spec/path validation errors before the run starts throw. Once execution is under
way, timeout, cancellation, missing emulator, ROM/version mismatch, emulator
exit, malformed/missing adapter output, and assertion failures normally return
a failed `ExecutionResult` with stable diagnostics instead of throwing.
`StopReason` can be `routine_return`, `cycle_limit`, `cpu_fault`,
`completion_condition`, `emulated_limit`, `host_timeout`,
`cancelled`, `version_mismatch`, `rom_mismatch`, `emulator_unavailable`,
`emulator_error`, `adapter_error`, `missing_observations`, `disk_hash_mismatch`,
or `disk_copy_mismatch`. The newly created
artifact directory is retained on both pass and failure.

`Evaluate` validates the selected engine's spec, compares completion, memory, register, and text
assertions against an already parsed observation, and returns only failure
diagnostics. It does not start either engine.

`ParseObservation` parses the strict tab-separated `A2EXEC1`/`END` adapter format,
accepting one stop reason/time and unique register/memory records. It attaches
the separately supplied screen text verbatim. Invalid, missing, or duplicate
fields throw `InvalidDataException`.

### `CpuExecutionEngine`

```csharp
public static class CpuExecutionEngine
{
    public const string Version = "cpu-1";
    public static void Validate(ExecutionSpec spec);
}
```

The CPU backend supports the documented instruction set exposed by the assembler
for MOS 6502 and Apple-compatible 65C02 targets. It initializes A/X/Y to zero,
P to `$24`, SP to `$01FD`, applies declared register/memory inputs, and stops after
the routine returns through the harness sentinel or crosses `MaxCycles`. Optional
`trace.tsv` rows include instruction bytes, PC, before/after registers, cycle range,
logical memory accesses, and source file/line/text when the assembled routine has
a mapping. The UTF-8 artifact is capped at 16 MiB. Machine I/O and ROM services
are not emulated.

## Extended AI programming APIs

The additive APIs below are described in their focused guides. They follow the
same versioned project/execution JSON options and bounded input rules as the
original workflow.

Their remaining public data contracts are:

```csharp
public sealed record EnvironmentCheck(string Code, bool Passed, string Message);
public sealed record EnvironmentCheckResult(
    int SchemaVersion, bool Ready, IReadOnlyList<EnvironmentCheck> Checks);
public sealed record EnvironmentFingerprint(
    string Role, string Path, long Length, string Sha256);
public sealed record DevelopmentEnvironmentLock(
    int SchemaVersion, string ProfilePath, string ProfileSha256,
    IReadOnlyList<EnvironmentFingerprint> Files);
public sealed record ProjectStarterResult(
    string Directory, string Language, bool NeedsEnvironmentConfiguration,
    IReadOnlyList<string> Files);

public sealed record PreparedRoutine(
    int Origin, int EntryPoint, byte[] Bytes,
    IReadOnlyDictionary<string, string> InputHashes,
    IReadOnlyDictionary<string, int> Symbols,
    IReadOnlyList<AssemblySourceMapEntry> SourceMap);
public sealed record ExecutionVisualInput(
    string SourcePath, string ExpectedSha256, RasterImage Expected,
    VisualComparisonOptions Options);

public sealed record ImageCrop(int X, int Y, int Width, int Height);
public sealed record VisualComparisonOptions(
    int ChannelTolerance = 0, double MaxDifferentFraction = 0,
    ImageCrop? Crop = null);

public sealed record ProjectAssetCompilation(
    byte[] Payload, IReadOnlyDictionary<string, byte[]> Outputs,
    IReadOnlyDictionary<string, int> Constants, object Metadata);
```

| Namespace / entry point | Purpose |
| --- | --- |
| `Setup.DevelopmentEnvironmentProfile.Load` | Resolve a local emulator/ROM/template/compiler profile |
| `Setup.DevelopmentEnvironment.CheckAsync`, `CreateLock`, `ValidateLock` | Check tool readiness and pin/verify file identities and directory membership |
| `Setup.DevelopmentEnvironment.Apply` | Fill project or execution defaults from a profile |
| `Setup.DevelopmentEnvironment.ValidateExecutionLock`, `ValidateProjectLock` | Validate both lock content and the effective inputs used by a run/build |
| `Setup.ProjectStarter.Create` | Stage a BASIC/assembly/C starter or an assembly bare-metal boot project into a new directory |
| `Execution.ExecutionStep`, `ExecutionCondition`, `GameInput` | Describe ordered waits, assertions, keyboard/game input, delays, and checkpoints |
| `Execution.ExecutionRunner.ConditionMatches` | Compare captured memory/register/text evidence with a condition |
| `Execution.RoutineHarness.Assemble`, `Prepare`, `ValidateInputs` | Read/assemble a routine, retain payload/source evidence, and revalidate dependencies |
| `Execution.ExecutionRoutine`, `ExecutionCycleMeasurement` | Configure initial routine state or an instruction-boundary cycle window |
| `Execution.ExecutionAudio.Validate`, `Analyze` | Inspect bounded PCM16 WAV input and report normalized RMS/peak/silence metrics |
| `Execution.ExecutionVisual.Validate`, `Prepare`, `Compare` | Pin expected PNG input and retain screenshot comparison/difference evidence |
| `Graphics.VisualComparison.Validate`, `Compare` | Compare RGB pixels with a crop, per-channel tolerance, and differing-pixel fraction |
| `Projects.ProjectAssets.Compile` | Convert a declared asset into immutable virtual binary/include/header/metadata outputs |
| `Projects.Cc65Feedback.ParseLabels`, `ParseSegments`, `ParseDiagnostics`, `ParseDebugMap` | Normalize compiler/linker feedback and ld65 source mappings for callers |
| `Projects.Cc65LinkerConfiguration.Validate` | Validate the supported single-output linker configuration subset |
| `Basic.ApplesoftTools.RuntimeDiagnostics` | Recognize captured Applesoft errors, optionally mapping through a build's line maps |

`ExecutionResult` adds `Steps`, `Checkpoints`, `Cycles`, `Audio`,
`ScreenshotComparison`, and environment fingerprint evidence. Project results
include generated asset output hashes; built files include `RuntimeMemory` and
`OverlayGroup`. Compiler results include parsed symbols, segments, diagnostics,
the resolved compiler path, and toolchain inputs.

See [setup](setup.md), [runtime memory](runtime-memory.md),
[assets](project-assets.md), [interactive execution](interactive-testing.md),
[audio](audio-execution.md), and [block storage](block-storage-execution.md).
