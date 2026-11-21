// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Operations;
using CommonUtil;
using DiskArc;
using DiskArc.Disk;
using DiskArc.FS;
using static DiskArc.Defs;

namespace A2Utils.Core.Backends;

/// <summary>Disk engine adapter. Writable sessions must target a transaction's staging file.</summary>
public sealed partial class DiskSession : IDisposable
{
    internal const int MaximumImageFileSize = 34 * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly IDiskImage _disk;
    private readonly IFileSystem _fs;
    private readonly string _path;
    private readonly bool _writable;
    private readonly bool _hybrid;
    private bool _disposed;

    private DiskSession(string path, FileStream stream, IDiskImage disk, IFileSystem fs,
        bool writable, bool hybrid)
    {
        _path = System.IO.Path.GetFullPath(path);
        _stream = stream;
        _disk = disk;
        _fs = fs;
        _writable = writable;
        _hybrid = hybrid;
    }

    public DiskInfo Info
    {
        get
        {
            DiskInfo info = new(_path, _disk is TwoIMG ? "2mg" : "raw",
                _disk.ChunkAccess!.FileOrder == SectorOrder.DOS_Sector ? "dos" : "prodos",
                _fs is DOS ? "dos33" : "prodos", _disk.ChunkAccess.FormattedLength,
                _fs.FreeSpace, _fs.GetVolDirEntry().FileName, _fs is DOS dos ? dos.VolumeNum : null,
                !_writable || _disk.IsReadOnly || _fs.IsReadOnly || IsWriteProtected,
                _disk.IsDubious || _fs.IsDubious, Verify(), IsWriteProtected);
            return info with { Capabilities = DiskCapabilities.Describe(info, _fs.FindEmbeddedVolumes() is not null) };
        }
    }

    private bool IsWriteProtected => _disk is TwoIMG { WriteProtected: true };

    /// <summary>Inspects supported containers, including images without a recognized filesystem.</summary>
    public static DiskInfo Inspect(string path, string? inputOrder = null, string? inputFs = null)
    {
        try
        {
            using DiskSession session = Open(path, inputOrder, inputFs);
            return session.Info;
        }
        catch (DiskException exception) when (exception.Code == "unrecognized_filesystem" && inputFs is null)
        {
            ValidateImageFile(path);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] signature = new byte[4];
            stream.ReadExactly(signature);
            stream.Position = 0;
            AppHook hook = new(new SimpleMessageLog());
            using IDiskImage image = signature.AsSpan().SequenceEqual("2IMG"u8)
                ? TwoIMG.OpenDisk(stream, hook) : UnadornedSector.OpenDisk(stream, hook);
            if (!image.AnalyzeDisk(null, inputOrder is null ? SectorOrder.ProDOS_Block : ParseOrder(inputOrder),
                IDiskImage.AnalysisDepth.ChunksOnly) || image.ChunkAccess is null)
                throw new DiskException("unsupported_geometry", "The image geometry is not supported.", 3);
            long length = image.ChunkAccess.FormattedLength;
            string order = image is TwoIMG || length != 143360
                ? image.ChunkAccess.FileOrder == SectorOrder.DOS_Sector ? "dos" : "prodos"
                : inputOrder ?? "unknown";
            var diagnostics = image.Notes.GetNotes().Select(note => new DiskDiagnostic(
                "engine_" + note.Type.ToString().ToLowerInvariant(),
                note.Type.ToString().ToLowerInvariant(), note.Text)).ToList();
            diagnostics.Add(new("unrecognized_filesystem", "warning",
                "No supported filesystem was recognized; filesystem operations are unavailable."));
            if (order == "unknown") diagnostics.Add(new("unknown_order", "warning",
                "Raw sector order is unknown; use --input-order for conversion."));
            DiskInfo info = new(System.IO.Path.GetFullPath(path), image is TwoIMG ? "2mg" : "raw",
                order, "unknown", length, null, "", null, true, image.IsDubious, diagnostics,
                image is TwoIMG { WriteProtected: true });
            return info with { Capabilities = DiskCapabilities.Describe(info) };
        }
    }

    public static DiskSession Open(string path, string? inputOrder = null,
        string? inputFs = null, bool writable = false)
    {
        SectorOrder? requestedOrder = inputOrder is null ? null : ParseOrder(inputOrder);
        string? requestedFs = inputFs is null ? null : ParseFileSystem(inputFs);
        ValidateImageFile(path);
        FileStream stream = new(path, FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            writable ? FileShare.None : FileShare.Read);
        IDiskImage? disk = null;
        IFileSystem? fs = null;
        try
        {
            AppHook hook = new(new SimpleMessageLog());
            Span<byte> signature = stackalloc byte[4];
            if (stream.Read(signature) != signature.Length)
            {
                throw new DiskException("truncated_image", "Image is too short.", 4);
            }
            stream.Position = 0;
            bool twoImg = signature.SequenceEqual("2IMG"u8);
            if (twoImg)
            {
                disk = TwoIMG.OpenDisk(stream, hook);
                if (((TwoIMG)disk).ImageFormat is not (0 or 1))
                {
                    throw new DiskException("unsupported_container", "Only sector-data 2IMG images are supported.", 3);
                }
            }
            else
            {
                if (signature.SequenceEqual("WOZ1"u8) || signature.SequenceEqual("WOZ2"u8))
                {
                    throw new DiskException("unsupported_container", "This image container is not supported.", 3);
                }
                if (stream.Length < 512 || stream.Length % 512 != 0)
                {
                    throw new DiskException("invalid_image_size", "Raw images require complete 512-byte blocks.", 4);
                }
                disk = UnadornedSector.OpenDisk(stream, hook);
            }

            if (!disk.AnalyzeDisk(null, requestedOrder ?? SectorOrder.ProDOS_Block,
                IDiskImage.AnalysisDepth.ChunksOnly) || disk.ChunkAccess is null)
            {
                throw new DiskException("unsupported_geometry", "The image geometry is not supported.", 3);
            }
            var chunks = disk.ChunkAccess;
            long length = chunks.FormattedLength;
            if (length != 143360 && (length < 280 * 512L || length > 65536 * 512L || length % 512 != 0))
            {
                throw new DiskException("unsupported_geometry", "Supported images are 140 KiB floppies or single ProDOS block volumes up to 65,536 blocks.", 3);
            }
            SectorOrder containerOrder = chunks.FileOrder;
            if (twoImg && requestedOrder is not null && requestedOrder != containerOrder)
            {
                throw new DiskException("conflicting_order", "Input order conflicts with the 2IMG payload declaration.", 3);
            }
            if (length != 143360 && requestedOrder == SectorOrder.DOS_Sector)
            {
                throw new DiskException("unsupported_order", "Larger images require ProDOS block order.", 3);
            }

            // Score every supported candidate instead of accepting the engine's first match.
            var candidates = new List<(string Fs, SectorOrder Order, int Score)>();
            SectorOrder[] orders = twoImg || length != 143360
                ? [containerOrder] : [SectorOrder.DOS_Sector, SectorOrder.ProDOS_Block];
            foreach (SectorOrder order in orders)
            {
                chunks.FileOrder = order;
                if (length == 143360)
                {
                    int score = (int)DOS.TestImage(chunks, hook);
                    if (score > 1) candidates.Add(("dos33", order, score));
                }
                int prodosScore = (int)ProDOS.TestImage(chunks, hook);
                if (prodosScore > 1) candidates.Add(("prodos", order, prodosScore));
            }
            var eligible = candidates.Where(candidate =>
                (requestedOrder is null || candidate.Order == requestedOrder) &&
                (requestedFs is null || candidate.Fs == requestedFs)).ToList();
            if (eligible.Count == 0)
            {
                throw new DiskException("unrecognized_filesystem", "No supported DOS 3.3 or ProDOS filesystem matches the supplied layout.", 3);
            }
            int bestScore = eligible.Max(candidate => candidate.Score);
            var best = eligible.Where(candidate => candidate.Score == bestScore).ToList();
            if (best.Count != 1)
            {
                throw new DiskException("ambiguous_layout", "Multiple layouts match; specify --input-order and --input-fs.", 3);
            }
            var selected = best[0];
            chunks.FileOrder = selected.Order;
            fs = selected.Fs == "dos33" ? new DOS(chunks, hook) : new ProDOS(chunks, hook);
            fs.PrepareFileAccess(true);
            bool hybrid = candidates.Any(candidate => candidate.Fs != selected.Fs && candidate.Score >= 4);
            var session = new DiskSession(path, stream, disk, fs, writable, hybrid);
            if (writable) session.EnsureWritable();
            return session;
        }
        catch (Exception exception)
        {
            fs?.Dispose();
            disk?.Dispose();
            stream.Dispose();
            if (exception is DiskException) throw;
            throw new DiskException("corrupt_image", $"Unable to read the disk structures: {exception.Message}", 4, exception);
        }
    }

    public DiskEntry GetEntry(string path)
    {
        IFileEntry entry = Resolve(path);
        return Describe(entry, TransferEntryPath(entry));
    }

    public IReadOnlyList<DiskEntry> List(string? path = null, bool recursive = false)
    {
        IFileEntry entry = Resolve(path);
        string catalogPath = TransferEntryPath(entry);
        if (!entry.IsDirectory) return [Describe(entry, catalogPath)];
        var result = new List<DiskEntry>();
        Walk(entry, catalogPath, recursive, result, 0);
        return result;
    }

    public byte[] ReadFile(string path, bool raw = false)
        => ReadFile(ResolveFile(path), Part(raw));

    /// <summary>Reads a specific logical fork, including an empty ProDOS resource fork.</summary>
    public byte[] ReadFile(string path, DiskFileFork fork)
    {
        IFileEntry entry = ResolveFile(path, allowForks: true);
        return ReadFile(entry, SelectFork(entry, fork));
    }

    private byte[] ReadFile(IFileEntry entry, FilePart part)
    {
        if ((entry.Access & (byte)AccessFlags.Read) == 0)
            throw new DiskException("file_access_denied", "The file does not permit reading.");
        return ReadEngine(() =>
        {
            using DiskFileStream file = _fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadOnly, part);
            if (file.Length > 32 * 1024 * 1024) throw new DiskException("file_too_large", "File exceeds the supported volume size.", 4);
            byte[] data = new byte[checked((int)file.Length)];
            if (data.Length != 0) file.ReadExactly(data);
            return data;
        });
    }

    internal byte[]? ReadDosTextWithTrailingRecords(DiskEntry entry)
    {
        if (_fs is not DOS || entry.FileType != 0x04 || entry.StoredLength <= entry.Length)
            return null;
        byte[] stored = ReadFile(entry.Path, raw: true);
        return stored.AsSpan(checked((int)entry.Length)).ContainsAnyExcept((byte)0) ? stored : null;
    }

    public IReadOnlyList<SparseExtent> GetSparseExtents(string path)
        => GetSparseExtents(ResolveFile(path), Part(true));

    public IReadOnlyList<SparseExtent> GetSparseExtents(string path, DiskFileFork fork)
    {
        IFileEntry entry = ResolveFile(path, allowForks: true);
        return GetSparseExtents(entry, SelectFork(entry, fork));
    }

    private IReadOnlyList<SparseExtent> GetSparseExtents(IFileEntry entry, FilePart part)
    {
        return ReadEngine<IReadOnlyList<SparseExtent>>(() =>
        {
            using DiskFileStream file = _fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadOnly, part);
            var extents = new List<SparseExtent>();
            long cursor = 0;
            while (cursor < file.Length)
            {
                long start = file.Seek(cursor, SEEK_ORIGIN_DATA);
                if (start >= file.Length) break;
                long end = file.Seek(start, SEEK_ORIGIN_HOLE);
                if (start < cursor || end <= start || end > file.Length)
                    throw new DiskException("invalid_sparse_map", "Invalid sparse extent returned by the filesystem.", 4);
                extents.Add(new SparseExtent(start, end - start));
                cursor = end;
            }
            return extents;
        });
    }

    public IReadOnlyList<DiskDiagnostic> Verify()
    {
        var diagnostics = _disk.Notes.GetNotes().Concat(_fs.Notes.GetNotes())
            .Select(note => new DiskDiagnostic("engine_" + note.Type.ToString().ToLowerInvariant(),
                note.Type.ToString().ToLowerInvariant(), note.Text)).ToList();
        if (_hybrid) diagnostics.Add(new("hybrid_filesystem", "warning", "More than one filesystem is present; writes are refused."));
        foreach (DiskEntry entry in List(recursive: true))
        {
            if (entry.IsDubious) diagnostics.Add(new("damaged_entry", "error", $"File entry is damaged or dubious: {entry.Path}"));
            if (entry.HasResourceFork) diagnostics.Add(new("unsupported_forks", "warning", $"General edits to a volume containing resource forks are refused: {entry.Path}"));
        }
        return diagnostics;
    }

    public static void Create(string path, string fs, long sizeKiB = 140,
        string container = "raw", string order = "dos", string volumeName = "UNTITLED",
        int volumeNumber = 254, long? blocks = null)
    {
        string fileSystem = ParseFileSystem(fs);
        SectorOrder fileOrder = ParseOrder(order);
        long blockCount = blocks ?? checked(sizeKiB * 2);
        if (blockCount < 280 || blockCount > 65535 || fileSystem == "dos33" && blockCount != 280)
            throw new DiskException("unsupported_geometry", "DOS 3.3 requires 280 blocks; ProDOS supports 280 through 65,535 blocks.", 3);
        if (blockCount != 280 && fileOrder != SectorOrder.ProDOS_Block)
            throw new DiskException("unsupported_order", "Larger images require ProDOS block order.", 3);
        if (volumeNumber is < 0 or > 254) throw new DiskException("invalid_volume_number", "DOS volume number must be 0 through 254.", 2);
        if (container is not ("raw" or "2mg" or "2img")) throw new DiskException("unsupported_container", "Container must be raw or 2mg.", 3);
        AppHook hook = new(new SimpleMessageLog());
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using IDiskImage image = container == "raw"
            ? blockCount == 280
                ? UnadornedSector.CreateSectorImage(stream, 35, 16, fileOrder, hook)
                : UnadornedSector.CreateBlockImage(stream, (uint)blockCount, hook)
            : fileOrder == SectorOrder.DOS_Sector
                ? TwoIMG.CreateDOSSectorImage(stream, 35, hook)
                : TwoIMG.CreateProDOSBlockImage(stream, (uint)blockCount, hook);
        if (blockCount == 280 && image.ChunkAccess is { HasSectors: false })
            image.AnalyzeDisk(null, fileOrder, IDiskImage.AnalysisDepth.ChunksOnly);
        // Format the selected geometry directly. FormatDisk reopens it without an order hint.
        IChunkAccess chunks = image.ChunkAccess!;
        chunks.Initialize();
        using (IFileSystem filesystem = fileSystem == "dos33" ? new DOS(chunks, hook) : new ProDOS(chunks, hook))
        {
            filesystem.Format(volumeName, volumeNumber, false);
        }
        if (!image.AnalyzeDisk(null, fileOrder, IDiskImage.AnalysisDepth.Full) || image.Contents is not IFileSystem)
            throw new DiskException("format_failed", "The formatted volume could not be reopened using its requested order.", 4);
        image.Flush();
        stream.Flush(true);
    }

    public void Add(string name, byte[] data, string type, ushort auxType = 0)
    {
        EnsureWritable();
        byte fileType = ValidateFileType(type, auxType == 0 ? null : auxType);
        Mutate(() =>
        {
            IFileEntry entry = CreateEntry(name, IFileSystem.CreateMode.File);
            entry.FileType = fileType;
            entry.AuxType = auxType;
            entry.SaveChanges();
            using (DiskFileStream file = _fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadWrite, FilePart.DataFork))
                file.Write(data, 0, data.Length);
            entry.AuxType = auxType;
            entry.SaveChanges();
        });
    }

    public void Replace(string path, byte[] data)
    {
        EnsureWritable();
        IFileEntry entry = ResolveFile(path);
        RequireAccess(entry, AccessFlags.Write);
        ushort auxType = entry.AuxType;
        DateTime modified = entry.ModWhen;
        Mutate(() =>
        {
            using (DiskFileStream file = _fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadWrite, FilePart.DataFork))
            {
                file.SetLength(0);
                file.Write(data, 0, data.Length);
            }
            entry.AuxType = auxType;
            entry.ModWhen = modified;
            entry.SaveChanges();
        });
    }

    public void Delete(string path, bool recursive = false)
    {
        EnsureWritable();
        IFileEntry entry = Resolve(path);
        if (entry == _fs.GetVolDirEntry()) throw new DiskException("root_operation_refused", "Cannot delete the volume root.");
        var entries = new List<IFileEntry>();
        CollectDelete(entry, recursive, entries, 0);
        foreach (IFileEntry item in entries)
        {
            RequireAccess(item, AccessFlags.Destroy);
            RequireAccess(item.ContainingDir, AccessFlags.Write);
        }
        Mutate(() => { foreach (IFileEntry item in entries) _fs.DeleteFile(item); });
    }

    public void Rename(string path, string newName)
    {
        EnsureWritable();
        IFileEntry entry = Resolve(path);
        if (entry == _fs.GetVolDirEntry()) throw new DiskException("root_operation_refused", "Cannot rename the volume root with the file rename operation.");
        RequireAccess(entry, AccessFlags.Rename);
        RequireAccess(entry.ContainingDir, AccessFlags.Write);
        if (newName.Contains('/') || newName.Contains('\\')) throw new DiskException("invalid_name", "Rename requires a filename, not a path.", 2);
        Mutate(() => _fs.MoveFile(entry, entry.ContainingDir, newName));
    }

    public void Mkdir(string path)
    {
        EnsureWritable();
        if (_fs is DOS) throw new DiskException("unsupported_directories", "DOS 3.3 does not support directories.", 3);
        Mutate(() => CreateEntry(path, IFileSystem.CreateMode.Directory));
    }

    public void SetAttributes(string path, bool? locked = null, string? type = null, ushort? auxType = null)
    {
        EnsureWritable();
        IFileEntry entry = Resolve(path);
        byte? fileType = type is null ? null : ParseType(type);
        bool root = entry == _fs.GetVolDirEntry();
        if (_fs is DOS && root && (locked.HasValue || fileType.HasValue || auxType.HasValue))
            throw new DiskException("unsupported_attributes", "DOS volume roots do not store file attributes.", 3);
        if (fileType.HasValue && (entry.IsDirectory ? fileType.Value != 0x0f : fileType.Value == 0x0f))
            throw new DiskException("invalid_file_type", "DIR is reserved for directories; a directory's file type cannot be changed.", 2);
        if (auxType.HasValue && (root || _fs is DOS && (fileType ?? entry.FileType) != 0x06))
            throw new DiskException("unsupported_attributes", "Auxiliary values require a ProDOS file or subdirectory, or a DOS binary file.", 3);
        if ((fileType.HasValue || auxType.HasValue) && locked != false)
            RequireAccess(entry, AccessFlags.Write);
        Mutate(() =>
        {
            if (fileType.HasValue) entry.FileType = fileType.Value;
            if (auxType.HasValue) entry.AuxType = auxType.Value;
            if (locked.HasValue)
            {
                const byte permissions = (byte)(AccessFlags.Write | AccessFlags.Rename | AccessFlags.Destroy);
                entry.Access = locked.Value ? (byte)(entry.Access & ~permissions) : (byte)(entry.Access | permissions);
            }
            entry.SaveChanges();
        });
    }

    public void RestoreFile(DiskEntry metadata, byte[] rawData, IReadOnlyList<SparseExtent> dataExtents)
        => RestoreFile(metadata, rawData, dataExtents, null, null);

    public void RestoreFile(DiskEntry metadata, byte[] rawData, IReadOnlyList<SparseExtent> dataExtents,
        byte[]? resourceData, IReadOnlyList<SparseExtent>? resourceExtents)
    {
        EnsureWritable(allowForks: true);
        ValidateRestoreName(metadata);
        if (metadata.HasResourceFork != (resourceData is not null) ||
            metadata.HasResourceFork != (resourceExtents is not null) || metadata.ResourceForkLength != resourceData?.LongLength ||
            metadata.HasResourceFork && _fs is not ProDOS)
            throw new DiskException("invalid_manifest", "Resource fork metadata and payload must agree on a ProDOS volume.", 2);
        if (metadata.IsDirectory || metadata.IsDubious || metadata.FileType == 0x0f)
            throw new DiskException("unsupported_restore", "Only undamaged data files can be restored.", 3);
        if (_fs is DOS) _ = ParseType($"0x{metadata.FileType:x2}");
        if (metadata.StoredLength != rawData.LongLength)
            throw new DiskException("invalid_manifest", "Stored length differs from the raw payload.", 2);
        ValidateExtents(rawData, dataExtents, "invalid_manifest");
        if (resourceData is not null) ValidateExtents(resourceData, resourceExtents!, "invalid_manifest");
        Mutate(() =>
        {
            string createPath = metadata.Path;
            if (_fs is DOS && !DOS_FileEntry.IsFileNameValid(metadata.Name))
            {
                IFileEntry root = _fs.GetVolDirEntry();
                if (FindTransferChild(root, metadata.Name) is not null)
                    throw new DiskException("destination_exists", $"An image entry already exists: {metadata.Name}");
                // The raw-name setter accepts legacy catalog names that CreateFile rejects.
                for (int index = 0; ; index++)
                {
                    createPath = $"A2RESTORE{index}";
                    if (FindTransferChild(root, createPath) is null) break;
                }
            }
            IFileEntry entry = CreateEntry(createPath, metadata.HasResourceFork
                ? IFileSystem.CreateMode.Extended : IFileSystem.CreateMode.File);
            entry.FileType = metadata.FileType;
            entry.SaveChanges();
            WritePart(entry, Part(true), rawData, dataExtents);
            if (resourceData is not null)
            {
                WritePart(entry, FilePart.RsrcFork, resourceData, resourceExtents!);
                if (metadata.HfsFileType is uint fileType) entry.HFSFileType = fileType;
                if (metadata.HfsCreator is uint creator) entry.HFSCreator = creator;
            }
            RestoreAttributes(entry, metadata);
            if (entry.DataLength != metadata.Length || metadata.HasResourceFork && entry.RsrcLength != metadata.ResourceForkLength)
                throw new DiskException("invalid_manifest", "Logical file length disagrees with stored payload metadata.", 2);
        });
    }

    private void WritePart(IFileEntry entry, FilePart part, byte[] bytes, IReadOnlyList<SparseExtent> extents)
    {
        using DiskFileStream file = _fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadWrite, part);
        foreach (SparseExtent extent in extents)
        {
            file.Position = extent.Offset;
            file.Write(bytes, checked((int)extent.Offset), checked((int)extent.Length));
        }
        file.SetLength(bytes.LongLength);
    }

    internal static void ValidateExtents(byte[] bytes, IReadOnlyList<SparseExtent> extents, string diagnostic)
    {
        long end = 0;
        foreach (SparseExtent extent in extents)
        {
            if (extent is null || extent.Offset < end || extent.Length <= 0 || extent.Offset > bytes.LongLength - extent.Length)
                throw new DiskException(diagnostic, "Invalid sparse extents.", 2);
            if (bytes.AsSpan(checked((int)end), checked((int)(extent.Offset - end))).IndexOfAnyExcept((byte)0) >= 0)
                throw new DiskException(diagnostic, "A sparse hole omits nonzero payload bytes.", 2);
            end = extent.Offset + extent.Length;
        }
        if (bytes.AsSpan(checked((int)end)).IndexOfAnyExcept((byte)0) >= 0)
            throw new DiskException(diagnostic, "A trailing sparse hole omits nonzero payload bytes.", 2);
    }

    public void RestoreDirectoryMetadata(DiskEntry metadata)
    {
        EnsureWritable(allowForks: true);
        ValidateRestoreName(metadata);
        if (!metadata.IsDirectory || metadata.IsDubious || _fs is DOS)
            throw new DiskException("invalid_directory_metadata", "Expected metadata for a valid ProDOS directory.", 2);
        IFileEntry entry = Resolve(metadata.Path);
        if (!entry.IsDirectory || entry == _fs.GetVolDirEntry())
            throw new DiskException("invalid_directory_metadata", "Directory metadata can only be restored on a subdirectory.", 2);
        Mutate(() => RestoreAttributes(entry, metadata));
    }

    public void Flush()
    {
        _fs.Flush();
        _disk.Flush();
        if (_writable) _stream.Flush(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _fs.Dispose(); }
        finally
        {
            try { _disk.Dispose(); }
            finally { _stream.Dispose(); }
        }
    }

    private void EnsureWritable(bool allowForks = false)
    {
        if (!_writable || _disk.IsReadOnly || _fs.IsReadOnly || IsWriteProtected)
            throw new DiskException("image_read_only", "The image is read-only or write protected.");
        if (_disk.IsDubious || _fs.IsDubious || _fs.Notes.ErrorCount != 0 || _disk.Notes.ErrorCount != 0)
            throw new DiskException("corrupt_image", "Writes to a damaged image are refused.", 4);
        if (_hybrid || _fs.FindEmbeddedVolumes() is not null)
            throw new DiskException("hybrid_filesystem", "Writes to hybrid or embedded filesystems are refused.");
        if (List(recursive: true).Any(entry => entry.IsDubious || !allowForks && entry.HasResourceFork))
            throw new DiskException("unsafe_allocation", "Writes require an undamaged volume without forked files.");
    }

    internal static void ValidateImageFile(string path, CancellationToken cancellationToken = default)
    {
        HostFiles.EnsureRegularFile(path, cancellationToken);
        FileInfo image = new(path);
        FileInfo target = new(image.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? image.FullName);
        if (target.Length > MaximumImageFileSize)
            throw new DiskException("image_too_large", "Image containers are limited to 34 MiB including metadata.", 3);
    }

    private void Mutate(Action action)
    {
        try { action(); Flush(); }
        catch (DiskException) { throw; }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        {
            throw new DiskException(exception is DiskFullException ? "disk_full" : "operation_refused",
                exception.Message, 6, exception);
        }
    }

    private static T ReadEngine<T>(Func<T> action)
    {
        try { return action(); }
        catch (DiskException) { throw; }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        {
            throw new DiskException("corrupt_file", exception.Message, 4, exception);
        }
    }

    private IFileEntry Resolve(string? path)
    {
        IFileEntry entry = _fs.GetVolDirEntry();
        string normalized = NormalizePath(path ?? "");
        if (normalized.Length == 0) return entry;
        string[] parts = _fs is DOS ? [normalized] : normalized.Split('/');
        foreach (string part in parts)
        {
            if (!entry.IsDirectory) throw new DiskException("not_a_directory", "An image path component is not a directory.");
            var matches = entry.Where(child => child.CompareFileName(part) == 0).ToArray();
            if (matches.Length != 1) throw new DiskException(matches.Length == 0 ? "file_not_found" : "ambiguous_name",
                matches.Length == 0 ? $"Image entry not found: {path}" : $"Multiple entries share the name: {path}");
            entry = matches[0];
        }
        return entry;
    }

    private void ValidateRestoreName(DiskEntry metadata)
    {
        if (metadata.RawName is null || metadata.RawName.Length == 0 ||
            string.IsNullOrEmpty(metadata.Name) || string.IsNullOrEmpty(metadata.Path))
            throw new DiskException("invalid_manifest", "Raw name metadata is required.", 2);
        string leaf = _fs is DOS ? metadata.Path : NormalizePath(metadata.Path).Split('/')[^1];
        StringComparison comparison = _fs is DOS ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string decoded;
        if (_fs is DOS)
        {
            if (metadata.RawName.Length > 30)
                throw new DiskException("invalid_manifest", "DOS raw names cannot exceed 30 bytes.", 2);
            decoded = DOS_FileEntry.GenerateCookedName(metadata.RawName);
        }
        else
        {
            byte[]? expectedRaw = ProDOS_FileEntry.GenerateRawName(metadata.Name, out _);
            if (expectedRaw is null || metadata.RawName.Length != metadata.Name.Length ||
                !metadata.RawName.AsSpan().SequenceEqual(expectedRaw.AsSpan(0, metadata.Name.Length)))
                throw new DiskException("unsupported_raw_name", "The raw ProDOS name cannot be restored without changing its stored representation.", 3);
            decoded = System.Text.Encoding.ASCII.GetString(metadata.RawName);
        }
        if (!string.Equals(leaf, metadata.Name, comparison) || !string.Equals(decoded, metadata.Name, comparison))
            throw new DiskException("invalid_manifest", "Raw name, display name, and image path disagree.", 2);
    }

    private void RestoreAttributes(IFileEntry entry, DiskEntry metadata)
    {
        if (_fs is DOS) entry.RawFileName = metadata.RawName;
        else entry.FileName = metadata.Name; // Keep ProDOS name-case flags carried by the display name.
        entry.AuxType = metadata.AuxType;
        entry.CreateWhen = metadata.Created ?? TimeStamp.NO_DATE;
        entry.ModWhen = metadata.Modified ?? TimeStamp.NO_DATE;
        entry.Access = metadata.Access;
        entry.SaveChanges();
        if (ValidDate(entry.CreateWhen) != metadata.Created || ValidDate(entry.ModWhen) != metadata.Modified)
            throw new DiskException("invalid_manifest", "The filesystem cannot represent the supplied timestamps.", 2);
    }

    private IFileEntry ResolveFile(string path, bool allowForks = false)
    {
        IFileEntry entry = Resolve(path);
        if (entry.IsDirectory) throw new DiskException("not_a_file", "The image path is a directory.");
        if (entry.IsDamaged) throw new DiskException("damaged_entry", "The file entry is damaged.", 4);
        if (entry.HasRsrcFork && !allowForks) throw new DiskException("unsupported_forks", "This operation requires a single data fork.", 3);
        return entry;
    }

    private IFileEntry CreateEntry(string path, IFileSystem.CreateMode mode)
    {
        string normalized = NormalizePath(path);
        if (normalized.Length == 0) throw new DiskException("invalid_name", "A filename is required.", 2);
        int split = _fs is DOS ? -1 : normalized.LastIndexOf('/');
        IFileEntry parent = split < 0 ? _fs.GetVolDirEntry() : Resolve(normalized[..split]);
        string name = normalized[(split + 1)..];
        RequireAccess(parent, AccessFlags.Write);
        return _fs.CreateFile(parent, name, mode);
    }

    private DiskEntry Describe(IFileEntry entry, string path)
    {
        // A damaged extended entry may have no readable extended key block.
        long length = entry.IsDamaged && entry.HasRsrcFork ? 0 : entry.DataLength;
        long storedLength = length;
        if (!entry.IsDirectory && !entry.IsDamaged)
            entry.GetPartInfo(Part(true), out storedLength, out _, out _);
        return new DiskEntry(path, entry.FileName, entry.IsDirectory, FormatType(entry.FileType),
            entry.FileType, entry.AuxType, entry.Access, (entry.Access & (byte)AccessFlags.Write) == 0,
            length, storedLength, entry.StorageSize, entry.RawFileName,
            ValidDate(entry.CreateWhen), ValidDate(entry.ModWhen), entry.IsDubious || entry.IsDamaged,
            entry.HasRsrcFork)
        {
            ResourceForkLength = entry.HasRsrcFork && !entry.IsDamaged ? entry.RsrcLength : null,
            HfsFileType = entry.HasRsrcFork && entry.HFSFileType != 0 ? entry.HFSFileType : null,
            HfsCreator = entry.HasRsrcFork && entry.HFSCreator != 0 ? entry.HFSCreator : null
        };
    }

    private void Walk(IFileEntry directory, string path, bool recursive, List<DiskEntry> result, int depth)
    {
        if (depth > 256) throw new DiskException("directory_depth", "Directory nesting exceeds the supported limit.", 4);
        foreach (IFileEntry child in directory)
        {
            string childPath = path.Length == 0 ? child.FileName : path + "/" + child.FileName;
            result.Add(Describe(child, childPath));
            if (recursive && child.IsDirectory && !child.IsDamaged) Walk(child, childPath, true, result, depth + 1);
        }
    }

    private static void CollectDelete(IFileEntry entry, bool recursive, List<IFileEntry> result, int depth)
    {
        if (depth > 256) throw new DiskException("directory_depth", "Directory nesting exceeds the supported limit.", 4);
        if (entry.IsDirectory)
        {
            if (entry.Count != 0 && !recursive) throw new DiskException("directory_not_empty", "Nonempty directories require --recursive.");
            foreach (IFileEntry child in entry) CollectDelete(child, recursive, result, depth + 1);
        }
        result.Add(entry);
    }

    private static FilePart SelectFork(IFileEntry entry, DiskFileFork fork) => fork switch
    {
        DiskFileFork.Data => FilePart.DataFork,
        DiskFileFork.Resource when entry.HasRsrcFork => FilePart.RsrcFork,
        DiskFileFork.Resource => throw new DiskException("no_resource_fork", "The file has no resource fork.", 3),
        _ => throw new ArgumentOutOfRangeException(nameof(fork))
    };

    private FilePart Part(bool raw) => raw && _fs is DOS ? FilePart.RawData : FilePart.DataFork;
    private static DateTime? ValidDate(DateTime date) =>
        date.Year >= 1900 ? DateTime.SpecifyKind(date, DateTimeKind.Unspecified) : null;

    private string NormalizePath(string path)
    {
        // DOS names are flat catalog strings, including names that contain '/' or '..'.
        if (_fs is DOS) return path == "/" ? "" : path;
        string normalized = path.Trim('/');
        if (normalized.Split('/').Any(part => part is "." or ".."))
            throw new DiskException("invalid_image_path", "Image paths cannot include dot segments.", 2);
        return normalized;
    }

    private static void RequireAccess(IFileEntry entry, AccessFlags flags)
    {
        if ((entry.Access & (byte)flags) != (byte)flags)
            throw new DiskException("file_locked", $"The entry's access flags refuse this operation: {entry.FileName}");
    }

    private static string ParseFileSystem(string fs) => fs.ToLowerInvariant() switch
    {
        "dos33" => "dos33",
        "prodos" => "prodos",
        _ => throw new DiskException("unsupported_filesystem", "Filesystem must be dos33 or prodos.", 3)
    };

    private static SectorOrder ParseOrder(string order) => order.ToLowerInvariant() switch
    {
        "dos" => SectorOrder.DOS_Sector,
        "prodos" => SectorOrder.ProDOS_Block,
        _ => throw new DiskException("invalid_order", "Order must be dos or prodos.", 2)
    };

    private byte ParseType(string type)
        => ParseFileType(type, _fs is DOS);

    internal static byte ParseFileType(string type, bool dos = false)
    {
        string upper = type.ToUpperInvariant();
        byte value = upper switch
        {
            "T" or "TXT" => 0x04,
            "I" or "INT" => 0xfa,
            "A" or "BAS" => 0xfc,
            "B" or "BIN" => 0x06,
            "S" or "F2" => 0xf2,
            "R" or "REL" => 0xfe,
            "AA" or "F3" => 0xf3,
            "BB" or "F4" => 0xf4,
            "SYS" => 0xff,
            "NON" => 0,
            _ when upper.StartsWith("0X", StringComparison.Ordinal) && byte.TryParse(upper.AsSpan(2),
                System.Globalization.NumberStyles.HexNumber, null, out byte number) => number,
            _ => throw new DiskException("invalid_file_type", "Use a known Apple file type or 0x00 through 0xff.", 2)
        };
        if (dos && value is not (0x04 or 0xfa or 0xfc or 0x06 or 0xf2 or 0xfe or 0xf3 or 0xf4))
            throw new DiskException("invalid_file_type", "This type cannot be represented in DOS 3.3.", 2);
        return value;
    }

    private string FormatType(byte type) => type switch
    {
        0x04 => _fs is DOS ? "T" : "TXT",
        0xfa => _fs is DOS ? "I" : "INT",
        0xfc => _fs is DOS ? "A" : "BAS",
        0x06 => _fs is DOS ? "B" : "BIN",
        0xf2 => _fs is DOS ? "S" : "F2",
        0xfe => _fs is DOS ? "R" : "REL",
        0xf3 => _fs is DOS ? "AA" : "F3",
        0xf4 => _fs is DOS ? "BB" : "F4",
        0x0f => "DIR",
        0xff => "SYS",
        0 => "NON",
        _ => $"0x{type:x2}"
    };
}
