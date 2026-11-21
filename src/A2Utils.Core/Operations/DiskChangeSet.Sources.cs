// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Operations;

public static partial class DiskChangeSetRunner
{
    private const int MaximumSourceEntries = 65535;

    private static IReadOnlyList<PreparedChange> CaptureSources(IReadOnlyList<DiskChange> changes,
        string planRoot, string imagePath, string planPath, List<DiskChangeInput> inputs,
        CancellationToken cancellationToken)
    {
        List<PreparedChange> result = [];
        long combinedBytes = 0;
        foreach (DiskChange change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateChange(change);
            string? sourceName = change.SourceImage ?? change.Source;
            if (sourceName is null)
            {
                result.Add(new(change, null));
                continue;
            }
            string sourcePath = Path.GetFullPath(sourceName, planRoot);
            List<SourceFile> files = [];
            List<string> directories = [];
            string primaryName = Path.GetFileName(sourcePath);
            if (change.Action == "import")
            {
                CaptureDirectory(sourcePath, "", 0);
            }
            else
            {
                SourceFile primary = CaptureFile(sourcePath, primaryName,
                    change.SourceImage is null ? MaximumPayloadBytes : 34 * 1024 * 1024);
                if (change.ExpectedSourceSha256 is not null &&
                    !change.ExpectedSourceSha256.Equals(primary.Fingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new DiskException("disk_plan.source_hash", $"Source hash does not match: {sourcePath}", 6);
                if (change.Action == "restore")
                {
                    ExtractionManifest manifest = FileTransfer.ParseManifest(primary.Bytes);
                    HashSet<string> captured = new(StringComparer.Ordinal) { primaryName };
                    foreach (ExtractedFile entry in manifest.Entries.Where(entry => !entry.Entry.IsDirectory))
                    {
                        CapturePayload(entry.HostFile);
                        if (entry.ResourceFork is not null) CapturePayload(entry.ResourceFork.HostFile);
                    }

                    void CapturePayload(string? name)
                    {
                        FileTransfer.ValidatePayloadName(name);
                        if (captured.Add(name!))
                            CaptureFile(Path.Combine(Path.GetDirectoryName(sourcePath)!, name!), name!, MaximumPayloadBytes);
                    }
                }
            }
            result.Add(new(change, new(primaryName, files, directories)));

            SourceFile CaptureFile(string path, string relativeName, int maximum)
            {
                CheckEntryLimit();
                ImageTransactions.ValidatePath(path);
                ImageTransactions.EnsureDistinctPaths(path, imagePath);
                ImageTransactions.EnsureDistinctPaths(path, planPath);
                byte[] bytes = ProgramFiles.ReadBytes(path,
                    (int)Math.Min(maximum, MaximumCombinedPayloadBytes - combinedBytes), cancellationToken);
                combinedBytes = checked(combinedBytes + bytes.LongLength);
                DiskChangeInput fingerprint = new(path, ProgramFiles.Hash(bytes), bytes.LongLength);
                inputs.Add(fingerprint);
                SourceFile file = new(fingerprint, relativeName, bytes);
                files.Add(file);
                return file;
            }

            void CaptureDirectory(string path, string relativeName, int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth > 256)
                    throw new DiskException("import_directory_depth", "Imported directory nesting exceeds 256 levels.", 2);
                CheckEntryLimit();
                SourceDirectoryEntry[] children = ReadSourceDirectory(path, cancellationToken);
                inputs.Add(new(path, DirectoryFingerprint(children), 0, "directory"));
                directories.Add(relativeName);
                foreach (SourceDirectoryEntry child in children)
                {
                    string childPath = Path.Combine(path, child.Name);
                    string childRelative = Path.Combine(relativeName, child.Name);
                    if (child.IsDirectory)
                    {
                        if (!change.Recursive)
                            throw new DiskException("import_recursive_required", "Host subdirectories require recursive import.", 2);
                        CaptureDirectory(childPath, childRelative, depth + 1);
                    }
                    else CaptureFile(childPath, childRelative, MaximumPayloadBytes);
                }
            }

            void CheckEntryLimit()
            {
                if (inputs.Count >= MaximumSourceEntries)
                    throw new DiskException("disk_plan.source_limit",
                        "A change set supports at most 65,535 external file and directory inputs.", 2);
            }
        }
        return result;
    }

    private static SourceDirectoryEntry[] ReadSourceDirectory(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImageTransactions.ValidatePath(path);
        if (!Directory.Exists(path))
            throw new DiskException("disk_plan.source_changed", $"The source directory is missing: {path}", 6);
        FileSystemInfo[] children = new DirectoryInfo(path)
            .EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })
            .Take(MaximumSourceEntries + 1).ToArray();
        if (children.Length > MaximumSourceEntries)
            throw new DiskException("disk_plan.source_limit", "A source directory exceeds 65,535 entries.", 2);
        return children.OrderBy(child => child.Name, StringComparer.Ordinal).Select(child =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageTransactions.ValidatePath(child.FullName);
            return new SourceDirectoryEntry(child.Name, (child.Attributes & FileAttributes.Directory) != 0);
        }).ToArray();
    }

    private static string DirectoryFingerprint(IReadOnlyList<SourceDirectoryEntry> entries)
        => ProgramFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(entries));

    private static bool IsWithinDirectory(string path, string directory)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string root = Path.GetFullPath(directory);
        string fullPath = Path.GetFullPath(path);
        return fullPath.Equals(root, comparison) ||
            fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
    }

    private static void WithMaterializedSource(PreparedSource source, Action<string> action,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(HostFiles.ResolvePhysicalDirectory(Path.GetTempPath()),
            $"a2-plan-inputs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (string directory in source.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(root, directory));
            }
            foreach (SourceFile file in source.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(root, file.RelativeName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using FileStream stream = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(file.Bytes);
            }
            action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record PreparedChange(DiskChange Change, PreparedSource? Source);
    private sealed record PreparedSource(string PrimaryName, IReadOnlyList<SourceFile> Files,
        IReadOnlyList<string> Directories);
    private sealed record SourceFile(DiskChangeInput Fingerprint, string RelativeName, byte[] Bytes);
    private sealed record SourceDirectoryEntry(string Name, bool IsDirectory);
}
