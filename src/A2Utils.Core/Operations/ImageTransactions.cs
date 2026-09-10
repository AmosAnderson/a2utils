using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace A2Utils.Core.Operations;

public sealed record ImageWriteResult(string OutputPath, string? BackupPath);

/// <summary>Stages complete files beside the destination before replacing a directory entry.</summary>
public static class ImageTransactions
{
    /// <summary>Validates a host path against the same link policy used for image transactions.</summary>
    public static void ValidatePath(string path) => CheckPath(Path.GetFullPath(path));

    /// <summary>Rejects linked paths and destinations that identify the source file.</summary>
    public static void EnsureDistinctPaths(string sourcePath, string destinationPath)
    {
        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationPath);
        CheckPath(source);
        CheckPath(destination);
        if (PathsEqual(source, destination) || WindowsFilesEqual(source, destination))
        {
            throw new DiskException("write.source_alias", "The destination refers to the source file.");
        }
    }

    public static ImageWriteResult Write(
        string inputPath,
        string? outputPath,
        bool inPlace,
        bool overwrite,
        Action<string> editTemporary,
        Action<string>? validate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editTemporary);
        if (inPlace == !string.IsNullOrWhiteSpace(outputPath))
        {
            throw new DiskException("write.destination_required", "Specify either an output path or in-place mode.", 2);
        }

        string source = Path.GetFullPath(inputPath);
        string destination = inPlace ? source : Path.GetFullPath(outputPath!);
        CheckPath(source);
        CheckPath(destination);
        if (!inPlace && (PathsEqual(source, destination) || WindowsFilesEqual(source, destination)))
        {
            throw new DiskException("write.source_alias", "The output path refers to the input; use explicit in-place mode.");
        }

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The input image does not exist.", source);
        }

        if (inPlace && (File.GetAttributes(source) & FileAttributes.ReadOnly) != 0)
        {
            throw new DiskException("write.read_only", "The input image is read-only.");
        }

        Fingerprint original = Capture(source);
        Fingerprint? destinationOriginal = inPlace ? original : PrepareDestination(destination, overwrite);
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = TemporaryPath(destination);
        try
        {
            using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream staged = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(staged);
                staged.Flush(flushToDisk: true);
            }

            EnsureUnchanged(source, original);
            editTemporary(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            validate?.Invoke(temporary);
            Flush(temporary);
            CheckPath(source);
            CheckPath(destination);
            EnsureUnchanged(source, original);
            if (!inPlace)
            {
                EnsureDestinationUnchanged(destination, destinationOriginal);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (inPlace)
            {
                string backup = source + "." + Guid.NewGuid().ToString("N") + ".bak";
                File.Replace(temporary, source, backup);
                return new ImageWriteResult(destination, backup);
            }

            // Rename replaces the directory entry, never truncating a possibly hard-linked inode.
            File.Move(temporary, destination, overwrite && destinationOriginal is not null);
            return new ImageWriteResult(destination, null);
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    public static ImageWriteResult Create(
        string outputPath,
        bool overwrite,
        Action<string> createTemporary,
        Action<string>? validate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createTemporary);
        string destination = Path.GetFullPath(outputPath);
        CheckPath(destination);
        Fingerprint? original = PrepareDestination(destination, overwrite);
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = TemporaryPath(destination);
        try
        {
            createTemporary(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            validate?.Invoke(temporary);
            Flush(temporary);
            CheckPath(destination);
            EnsureDestinationUnchanged(destination, original);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite && original is not null);
            return new ImageWriteResult(destination, null);
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    private static void Flush(string temporary)
    {
        CheckPath(temporary);
        using FileStream staged = new(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        staged.Flush(flushToDisk: true);
    }

    private static Fingerprint? PrepareDestination(string destination, bool overwrite)
    {
        if (!Directory.Exists(Path.GetDirectoryName(destination)))
        {
            throw new DirectoryNotFoundException("The output directory does not exist.");
        }

        if (!File.Exists(destination))
        {
            return null;
        }

        if (!overwrite)
        {
            throw new DiskException("write.destination_exists", "The destination already exists; specify overwrite explicitly.");
        }

        if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
        {
            throw new DiskException("write.read_only", "The destination is read-only.");
        }

        return Capture(destination);
    }

    private static void EnsureDestinationUnchanged(string path, Fingerprint? original)
    {
        if (original is null)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new DiskException("write.concurrent_change", "The destination appeared while preparing the operation.");
            }
        }
        else
        {
            EnsureUnchanged(path, original);
        }
    }

    private static void EnsureUnchanged(string path, Fingerprint original)
    {
        if (!File.Exists(path) || Capture(path) != original)
        {
            throw new DiskException("write.concurrent_change", "An input or destination changed while preparing the operation.");
        }
    }

    private static Fingerprint Capture(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        DateTime modified = File.GetLastWriteTimeUtc(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        return new Fingerprint(length, modified, hash);
    }

    private static string TemporaryPath(string destination) =>
        Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileNameWithoutExtension(destination) +
            ".a2-" + Guid.NewGuid().ToString("N") + Path.GetExtension(destination));

    private static bool PathsEqual(string first, string second) =>
        string.Equals(first, second, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool WindowsFilesEqual(string first, string second)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(first) || !File.Exists(second))
        {
            return false;
        }

        using SafeFileHandle firstHandle = File.OpenHandle(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using SafeFileHandle secondHandle = File.OpenHandle(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(firstHandle, out FileIdentity firstIdentity) ||
            !GetFileInformationByHandle(secondHandle, out FileIdentity secondIdentity))
        {
            throw new IOException("Could not establish whether input and output refer to the same file.");
        }

        return firstIdentity.VolumeSerialNumber == secondIdentity.VolumeSerialNumber &&
            firstIdentity.FileIndexHigh == secondIdentity.FileIndexHigh &&
            firstIdentity.FileIndexLow == secondIdentity.FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static void CheckPath(string path)
    {
        FileSystemInfo current = new FileInfo(path);
        while (true)
        {
            current.Refresh();
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new DiskException("write.link_refused", "Image writes do not accept symbolic links or linked parent directories.");
            }

            string? parent = Path.GetDirectoryName(current.FullName);
            if (parent is null || parent == current.FullName)
            {
                break;
            }

            current = new DirectoryInfo(parent);
        }
    }

    private static void DeleteTemporary(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (IOException)
        {
            // Preserve the original operation's failure if cleanup is prevented by the host.
        }
        catch (UnauthorizedAccessException)
        {
            // A leftover file has a unique name and is never mistaken for committed output.
        }
    }

    private sealed record Fingerprint(long Length, DateTime Modified, string Hash);
}
