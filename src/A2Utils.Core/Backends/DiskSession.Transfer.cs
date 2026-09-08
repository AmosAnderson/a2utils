using DiskArc;
using DiskArc.FS;
using static DiskArc.Defs;

namespace A2Utils.Core.Backends;

public sealed partial class DiskSession
{
    /// <summary>Validates one literal filename without changing case or replacing characters.</summary>
    public void ValidateFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new DiskException("invalid_name", "A filename is required.", 2);
        bool valid;
        if (_fs is DOS)
        {
            byte[]? raw = DOS_FileEntry.GenerateRawName(name);
            valid = raw is not null && DOS_FileEntry.GenerateCookedName(raw) == name;
        }
        else
        {
            valid = ProDOS_FileEntry.GenerateRawName(name, out _) is not null;
        }
        if (!valid) throw new DiskException("invalid_name",
            $"The filename cannot be represented exactly in this filesystem: {name}", 2);
    }

    /// <summary>Validates a data-file type without creating a file.</summary>
    public void ValidateFileType(string type)
    {
        if (string.IsNullOrEmpty(type) || ParseType(type) == 0x0f)
            throw new DiskException("invalid_file_type", "A supported data-file type is required; DIR is reserved for directories.", 2);
    }

    /// <summary>Creates missing ProDOS parent directories; existing directories are retained.</summary>
    public void EnsureDirectory(string path)
    {
        EnsureWritable();
        string normalized = NormalizePath(path);
        if (normalized.Length == 0) return;
        if (_fs is DOS) throw new DiskException("unsupported_directories", "DOS 3.3 does not support directories.", 3);
        string[] parts = normalized.Split('/');
        if (parts.Length > 256) throw new DiskException("directory_depth", "Directory nesting cannot exceed 256 levels.", 2);
        foreach (string part in parts) ValidateFileName(part);

        // Check existing ancestors before making any new entries.
        IFileEntry parent = _fs.GetVolDirEntry();
        int firstMissing = parts.Length;
        for (int index = 0; index < parts.Length; index++)
        {
            IFileEntry? existing = FindTransferChild(parent, parts[index]);
            if (existing is null)
            {
                RequireAccess(parent, AccessFlags.Write);
                firstMissing = index;
                break;
            }
            if (!existing.IsDirectory) throw new DiskException("not_a_directory", $"An image path component is a file: {parts[index]}");
            parent = existing;
        }

        Mutate(() =>
        {
            for (int index = firstMissing; index < parts.Length; index++)
            {
                RequireAccess(parent, AccessFlags.Write);
                parent = _fs.CreateFile(parent, parts[index], IFileSystem.CreateMode.Directory);
            }
        });
    }

    /// <summary>Copies to an exact new path within this image, preserving stored data and file attributes.</summary>
    /// <remarks>ProDOS allocated zero-filled blocks may become sparse holes; physical allocation
    /// and storage size may differ. The source is unchanged.</remarks>
    public void Copy(string sourcePath, string destinationPath, bool recursive = false,
        CancellationToken cancellationToken = default) =>
        CopyFrom(this, sourcePath, destinationPath, recursive, cancellationToken);

    /// <summary>Copies from the same filesystem type; the caller stages the destination image.</summary>
    /// <remarks>ProDOS zero-filled blocks may become sparse holes. File bytes, lengths, and
    /// supported file attributes are preserved; allocation is not required to match.</remarks>
    public void CopyFrom(DiskSession source, string sourcePath, string destinationPath,
        bool recursive = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritable();
        if ((_fs is DOS) != (source._fs is DOS))
            throw new DiskException("filesystem_mismatch",
                "Copy requires matching filesystem types; DOS/ProDOS file conversion is not implemented.", 3);
        if (source._disk.IsDubious || source._fs.IsDubious || source._disk.Notes.ErrorCount != 0 || source._fs.Notes.ErrorCount != 0)
            throw new DiskException("corrupt_image", "Copy requires an undamaged source filesystem.", 4);

        IFileEntry sourceEntry = source.Resolve(sourcePath);
        if (sourceEntry == source._fs.GetVolDirEntry())
            throw new DiskException("root_operation_refused", "Copy a named file or directory, not the volume root.");
        if (sourceEntry.IsDirectory && !recursive)
            throw new DiskException("recursive_required", "Copying a directory requires --recursive.", 2);

        (IFileEntry parent, string name, string targetPath) = ResolveTransferTarget(destinationPath);
        if (ReferenceEquals(source, this)) RejectTransferDescendant(sourceEntry, parent);
        if (FindTransferChild(parent, name) is not null)
            throw new DiskException("destination_exists", $"An image entry already exists at the destination: {destinationPath}");
        RequireAccess(parent, AccessFlags.Write);

        // Finish reading every source entry before creating any destination entries.
        // Only allocated extents are retained in memory, so sparse holes do not multiply memory use.
        List<TransferSnapshot> snapshot = [];
        CaptureTransfer(source, sourceEntry, targetPath, name, snapshot, 0, cancellationToken);
        foreach (TransferSnapshot item in snapshot)
        {
            ValidateRestoreName(item.Metadata);
            if (!item.Metadata.IsDirectory && _fs is DOS) _ = ParseType($"0x{item.Metadata.FileType:x2}");
        }

        foreach (TransferSnapshot item in snapshot.Where(item => item.Metadata.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mkdir(item.Metadata.Path);
        }
        foreach (TransferSnapshot item in snapshot.Where(item => !item.Metadata.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] raw = new byte[checked((int)item.Metadata.StoredLength)];
            int offset = 0;
            foreach (SparseExtent extent in item.Extents)
            {
                item.AllocatedData.AsSpan(offset, checked((int)extent.Length))
                    .CopyTo(raw.AsSpan(checked((int)extent.Offset)));
                offset += checked((int)extent.Length);
            }
            RestoreFile(item.Metadata, raw, item.Extents);
        }
        foreach (TransferSnapshot item in snapshot.Where(item => item.Metadata.IsDirectory).Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreDirectoryMetadata(item.Metadata);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Moves an entry inside this image without copying or reallocating its data.</summary>
    public void Move(string sourcePath, string destinationPath)
    {
        EnsureWritable();
        IFileEntry source = Resolve(sourcePath);
        if (source == _fs.GetVolDirEntry())
            throw new DiskException("root_operation_refused", "Cannot move the volume root.");
        (IFileEntry parent, string name, _) = ResolveTransferTarget(destinationPath);
        RejectTransferDescendant(source, parent);
        if (FindTransferChild(parent, name) is not null)
            throw new DiskException("destination_exists", $"An image entry already exists at the destination: {destinationPath}");
        RequireAccess(source, AccessFlags.Rename);
        RequireAccess(source.ContainingDir, AccessFlags.Write);
        RequireAccess(parent, AccessFlags.Write);
        ValidateTransferTreeDepth(source, TransferEntryPath(parent).Split('/', StringSplitOptions.RemoveEmptyEntries).Length);
        Mutate(() => _fs.MoveFile(source, parent, name));
    }

    private sealed record TransferSnapshot(DiskEntry Metadata, IReadOnlyList<SparseExtent> Extents,
        byte[] AllocatedData);

    private void CaptureTransfer(DiskSession source, IFileEntry entry, string targetPath,
        string targetName, List<TransferSnapshot> snapshot, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > 256) throw new DiskException("directory_depth", "Directory nesting exceeds the supported limit.", 4);
        if (_fs is not DOS && targetPath.Count(character => character == '/') + (entry.IsDirectory ? 1 : 0) > 256)
            throw new DiskException("directory_depth", "The copied directory tree would exceed 256 directory levels.", 2);
        if (entry.IsDamaged || entry.IsDubious)
            throw new DiskException("damaged_entry", $"Cannot copy a damaged entry: {entry.FileName}", 4);
        if (entry.HasRsrcFork)
            throw new DiskException("unsupported_forks", $"Cannot preserve resource forks: {entry.FileName}", 3);
        RequireAccess(entry, AccessFlags.Read);
        ValidateFileName(targetName);
        string sourcePath = source.TransferEntryPath(entry);
        DiskEntry metadata = source.Describe(entry, sourcePath) with
        {
            Path = targetPath,
            Name = targetName,
            RawName = targetName == entry.FileName ? entry.RawFileName : TransferRawName(targetName)
        };

        if (entry.IsDirectory)
        {
            snapshot.Add(new(metadata, [], []));
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (IFileEntry child in entry)
            {
                if (!names.Add(child.FileName)) throw new DiskException("ambiguous_name", "The source directory contains duplicate names.", 4);
                CaptureTransfer(source, child, targetPath + "/" + child.FileName,
                    child.FileName, snapshot, depth + 1, cancellationToken);
            }
            return;
        }

        byte[] raw = source.ReadFile(sourcePath, raw: true);
        IReadOnlyList<SparseExtent> extents = source.GetSparseExtents(sourcePath);
        byte[] allocated = new byte[checked((int)extents.Sum(extent => extent.Length))];
        int outputOffset = 0;
        foreach (SparseExtent extent in extents)
        {
            raw.AsSpan(checked((int)extent.Offset), checked((int)extent.Length)).CopyTo(allocated.AsSpan(outputOffset));
            outputOffset += checked((int)extent.Length);
        }
        snapshot.Add(new(metadata, extents, allocated));
    }

    private (IFileEntry Parent, string Name, string Path) ResolveTransferTarget(string path)
    {
        string normalized = NormalizePath(path);
        if (normalized.Length == 0) throw new DiskException("invalid_destination", "An exact new destination path is required.", 2);
        int split = _fs is DOS ? -1 : normalized.LastIndexOf('/');
        string name = normalized[(split + 1)..];
        ValidateFileName(name);
        IFileEntry parent = split < 0 ? _fs.GetVolDirEntry() : Resolve(normalized[..split]);
        if (!parent.IsDirectory) throw new DiskException("not_a_directory", "The destination parent is not a directory.");
        string parentPath = TransferEntryPath(parent);
        return (parent, name, parentPath.Length == 0 ? name : parentPath + "/" + name);
    }

    private static IFileEntry? FindTransferChild(IFileEntry parent, string name)
    {
        IFileEntry[] matches = parent.Where(entry => entry.CompareFileName(name) == 0).ToArray();
        if (matches.Length > 1) throw new DiskException("ambiguous_name", $"Multiple entries share the name: {name}", 4);
        return matches.SingleOrDefault();
    }

    private void RejectTransferDescendant(IFileEntry source, IFileEntry parent)
    {
        if (!source.IsDirectory) return;
        int depth = 0;
        for (IFileEntry current = parent; current != _fs.GetVolDirEntry(); current = current.ContainingDir)
        {
            if (++depth > 256) throw new DiskException("directory_depth", "Directory nesting exceeds the supported limit.", 4);
            if (current == source)
                throw new DiskException("self_descendant", "A directory cannot be copied or moved into itself or a descendant.");
        }
    }

    private static void ValidateTransferTreeDepth(IFileEntry source, int parentDepth)
    {
        if (!source.IsDirectory) return;
        if (parentDepth >= 256) throw new DiskException("directory_depth", "The moved directory tree would exceed 256 directory levels.", 2);
        foreach (IFileEntry child in source)
            ValidateTransferTreeDepth(child, parentDepth + 1);
    }

    private string TransferEntryPath(IFileEntry entry)
    {
        if (entry == _fs.GetVolDirEntry()) return "";
        if (_fs is DOS) return entry.FileName;
        List<string> names = [];
        for (IFileEntry current = entry; current != _fs.GetVolDirEntry(); current = current.ContainingDir)
        {
            if (names.Count > 256) throw new DiskException("directory_depth", "Directory nesting exceeds the supported limit.", 4);
            names.Add(current.FileName);
        }
        names.Reverse();
        return string.Join('/', names);
    }

    private byte[] TransferRawName(string name)
    {
        if (_fs is DOS) return DOS_FileEntry.GenerateRawName(name)!;
        return ProDOS_FileEntry.GenerateRawName(name, out _)![..name.Length];
    }
}
