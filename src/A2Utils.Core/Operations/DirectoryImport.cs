using System.Security.Cryptography;
using A2Utils.Core.Backends;

namespace A2Utils.Core.Operations;

public sealed record DirectoryImportResult(int FileCount, int DirectoryCount, long BytesImported);

/// <summary>Imports an exact-name host tree into a transaction's writable disk session.</summary>
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(destinationPath);
        string hostRoot = Path.GetFullPath(hostDirectory);
        RejectLinks(hostRoot);
        if (!Directory.Exists(hostRoot))
        {
            throw new DirectoryNotFoundException("The host import directory does not exist.");
        }

        string destination = destinationPath.Trim('/');
        DiskInfo info = session.Info;
        bool dos = info.FileSystem == "dos33";
        session.ValidateFileType(type);
        int entryLimit = dos ? 105 : (int)Math.Min(65535, info.SizeBytes / 512 * 13);
        if (dos && destination.Length != 0)
        {
            throw new DiskException("unsupported_directories", "DOS imports target the volume root only.", 3);
        }
        if (!session.GetEntry(destination).IsDirectory)
        {
            throw new DiskException("not_a_directory", "The image import destination must be an existing directory.");
        }

        StringComparer comparer = dos ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        HashSet<string> existing = session.List(destination, recursive: true).Select(entry => entry.Path).ToHashSet(comparer);
        HashSet<string> planned = new(comparer);
        List<HostEntry> entries = [];
        List<DirectorySnapshot> directories = [];
        long sourceBytes = 0;
        Plan(hostRoot, destination, destination.Count(character => character == '/'));

        long importedBytes = 0;
        foreach (HostEntry entry in entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(entry.HostPath);
            HostFiles.EnsureRegularFile(entry.HostPath, cancellationToken);
            EnsureFileMetadata(entry);
            using FileStream input = new(entry.HostPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != entry.Length)
            {
                throw ConcurrentChange();
            }
            byte[] bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            entry.Hash = SHA256.HashData(bytes);
            entry.Payload = transform is null ? bytes : transform(bytes)
                ?? throw new DiskException("invalid_import_transform", "The import transform returned no payload.", 2);
            importedBytes = checked(importedBytes + entry.Payload.LongLength);
            if (importedBytes > info.SizeBytes)
            {
                throw new DiskException("import_too_large", "The transformed import payloads exceed the image capacity.");
            }
            EnsureFileMetadata(entry);
        }

        // Validate the entire host snapshot before creating any image entries.
        foreach (DirectorySnapshot directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(directory.Path);
            string[] currentNames = Children(directory.Path, entryLimit).Select(child => child.Name).ToArray();
            if (!currentNames.SequenceEqual(directory.ChildNames, StringComparer.Ordinal))
            {
                throw ConcurrentChange();
            }
        }
        foreach (HostEntry entry in entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(entry.HostPath);
            HostFiles.EnsureRegularFile(entry.HostPath, cancellationToken);
            EnsureFileMetadata(entry);
            using FileStream input = new(entry.HostPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!SHA256.HashData(input).AsSpan().SequenceEqual(entry.Hash))
            {
                throw ConcurrentChange();
            }
        }

        foreach (HostEntry directory in entries.Where(entry => entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.Mkdir(directory.ImagePath);
        }
        foreach (HostEntry file in entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.Add(file.ImagePath, file.Payload!, type, auxType);
        }

        return new DirectoryImportResult(entries.Count(entry => !entry.IsDirectory),
            entries.Count(entry => entry.IsDirectory), importedBytes);

        void Plan(string hostPath, string imagePath, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(hostPath);
            if (depth > 255)
            {
                throw new DiskException("import_directory_depth", "The import would exceed the supported directory depth.");
            }

            FileSystemInfo[] children = Children(hostPath, entryLimit - entries.Count);
            directories.Add(new DirectorySnapshot(hostPath, children.Select(child => child.Name).ToArray()));
            foreach (FileSystemInfo child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= entryLimit)
                {
                    throw TooManyEntries();
                }
                RejectLinks(child.FullName);
                session.ValidateFileName(child.Name);
                string target = imagePath.Length == 0 ? child.Name : imagePath + "/" + child.Name;
                if (existing.Contains(target) || !planned.Add(target))
                {
                    throw new DiskException("import_name_conflict", $"An imported name conflicts with an existing or planned entry: {target}");
                }

                bool isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                if (isDirectory)
                {
                    if (dos)
                    {
                        throw new DiskException("unsupported_directories", "DOS directory import is flat; host subdirectories cannot be represented.", 3);
                    }
                    if (!recursive)
                    {
                        throw new DiskException("import_recursive_required", "Host subdirectories require explicit recursive import.");
                    }
                    entries.Add(new HostEntry(child.FullName, target, true, 0, child.LastWriteTimeUtc));
                    Plan(child.FullName, target, depth + 1);
                }
                else
                {
                    if (child is not FileInfo file || (child.Attributes & FileAttributes.Device) != 0)
                    {
                        throw new DiskException("unsupported_host_entry", "Import accepts regular host files and directories only.");
                    }
                    long length = file.Length;
                    sourceBytes = checked(sourceBytes + length);
                    if (sourceBytes > info.SizeBytes)
                    {
                        throw new DiskException("import_too_large", "The host import payloads exceed the image capacity.");
                    }
                    entries.Add(new HostEntry(child.FullName, target, false, length, child.LastWriteTimeUtc));
                }
            }
        }
    }

    private static FileSystemInfo[] Children(string path, int limit)
    {
        FileSystemInfo[] children = new DirectoryInfo(path)
            .EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })
            .Take(limit + 1).ToArray();
        if (children.Length > limit)
        {
            throw TooManyEntries();
        }
        return children.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
    }

    private static DiskException TooManyEntries() =>
        new("import_too_many_entries", "The import exceeds the supported entry count for this image.");

    private static void EnsureFileMetadata(HostEntry entry)
    {
        FileInfo current = new(entry.HostPath);
        if (!current.Exists || current.Length != entry.Length || current.LastWriteTimeUtc != entry.LastWrite)
        {
            throw ConcurrentChange();
        }
    }

    private static DiskException ConcurrentChange() =>
        new("import_concurrent_change", "The host import directory changed while preparing the operation.");

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new DiskException("import_link_refused", "Import paths must not traverse symbolic links or junctions.");
            }
        }
    }

    private sealed record DirectorySnapshot(string Path, string[] ChildNames);

    private sealed class HostEntry(string hostPath, string imagePath, bool isDirectory, long length, DateTime lastWrite)
    {
        public string HostPath { get; } = hostPath;
        public string ImagePath { get; } = imagePath;
        public bool IsDirectory { get; } = isDirectory;
        public long Length { get; } = length;
        public DateTime LastWrite { get; } = lastWrite;
        public byte[]? Hash { get; set; }
        public byte[]? Payload { get; set; }
    }
}
