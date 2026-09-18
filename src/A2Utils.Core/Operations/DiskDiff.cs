using System.Security.Cryptography;
using A2Utils.Core.Backends;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Operations;

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

/// <summary>Compares logical filesystem content and physical image bytes without modifying either image.</summary>
public static class DiskDiff
{
    private const int MaximumImageBytes = 34 * 1024 * 1024;
    private const int MaximumByteRanges = 4096;

    public static DiskDifference Compare(string beforePath, string afterPath,
        string? beforeOrder = null, string? beforeFileSystem = null,
        string? afterOrder = null, string? afterFileSystem = null,
        CancellationToken cancellationToken = default)
    {
        string beforeFull = Path.GetFullPath(beforePath);
        string afterFull = Path.GetFullPath(afterPath);
        byte[] beforeBytes = ProgramFiles.ReadBytes(beforeFull, MaximumImageBytes, cancellationToken);
        byte[] afterBytes = ProgramFiles.ReadBytes(afterFull, MaximumImageBytes, cancellationToken);
        string beforeHash = ProgramFiles.Hash(beforeBytes);
        string afterHash = ProgramFiles.Hash(afterBytes);
        string temporaryRoot = HostFiles.ResolvePhysicalDirectory(Path.GetTempPath());
        string beforeSnapshot = SnapshotPath(temporaryRoot, "before", beforeFull);
        string afterSnapshot = SnapshotPath(temporaryRoot, "after", afterFull);
        try
        {
            WriteSnapshot(beforeSnapshot, beforeBytes, cancellationToken);
            WriteSnapshot(afterSnapshot, afterBytes, cancellationToken);
            using DiskSession before = DiskSession.Open(beforeSnapshot, beforeOrder, beforeFileSystem);
            using DiskSession after = DiskSession.Open(afterSnapshot, afterOrder, afterFileSystem);
            DiskInfo beforeInfo = before.Info with { Path = beforeFull };
            DiskInfo afterInfo = after.Info with { Path = afterFull };
            Dictionary<string, DiskEntrySnapshot> beforeEntries = Snapshot(before, cancellationToken);
            Dictionary<string, DiskEntrySnapshot> afterEntries = Snapshot(after, cancellationToken);
            List<DiskEntryDifference> entries = [];
            foreach ((DiskEntrySnapshot? oldEntry, DiskEntrySnapshot? newEntry) in
                MatchEntries(beforeEntries.Values, afterEntries.Values,
                    beforeInfo.FileSystem, afterInfo.FileSystem))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = oldEntry?.Path ?? newEntry!.Path;
                if (oldEntry is null)
                {
                    entries.Add(new(path, "added", null, newEntry, []));
                    continue;
                }
                if (newEntry is null)
                {
                    entries.Add(new(path, "removed", oldEntry, null, []));
                    continue;
                }
                List<string> fields = ChangedFields(oldEntry, newEntry);
                if (fields.Count != 0) entries.Add(new(path, "modified", oldEntry, newEntry, fields));
            }

            (List<ByteDifference> ranges, long count, bool truncated) = CompareBytes(beforeBytes, afterBytes);
            return new(1, beforeFull, afterFull, beforeHash, afterHash,
                beforeHash.Equals(afterHash, StringComparison.OrdinalIgnoreCase), beforeInfo, afterInfo,
                entries, ranges, count, truncated);
        }
        finally
        {
            DeleteSnapshot(beforeSnapshot);
            DeleteSnapshot(afterSnapshot);
        }
    }

    private static IReadOnlyList<(DiskEntrySnapshot? Before, DiskEntrySnapshot? After)> MatchEntries(
        IEnumerable<DiskEntrySnapshot> before, IEnumerable<DiskEntrySnapshot> after,
        string beforeFileSystem, string afterFileSystem)
    {
        Dictionary<string, DiskEntrySnapshot> oldExact = before.ToDictionary(entry => entry.Path,
            StringComparer.Ordinal);
        Dictionary<string, DiskEntrySnapshot> newExact = after.ToDictionary(entry => entry.Path,
            StringComparer.Ordinal);
        List<(DiskEntrySnapshot? Before, DiskEntrySnapshot? After)> matches = [];

        foreach (string path in oldExact.Keys.Intersect(newExact.Keys, StringComparer.Ordinal).ToArray())
        {
            matches.Add((oldExact[path], newExact[path]));
            oldExact.Remove(path);
            newExact.Remove(path);
        }

        if (beforeFileSystem == "prodos" || afterFileSystem == "prodos")
        {
            var oldFolded = oldExact.Values.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            var newFolded = newExact.Values.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (string path in oldFolded.Keys.Intersect(newFolded.Keys,
                StringComparer.OrdinalIgnoreCase).ToArray())
            {
                DiskEntrySnapshot[] oldGroup = oldFolded[path];
                DiskEntrySnapshot[] newGroup = newFolded[path];
                if (oldGroup.Length != 1 || newGroup.Length != 1) continue;
                matches.Add((oldGroup[0], newGroup[0]));
                oldExact.Remove(oldGroup[0].Path);
                newExact.Remove(newGroup[0].Path);
            }
        }

        foreach (DiskEntrySnapshot entry in oldExact.Values) matches.Add((entry, null));
        foreach (DiskEntrySnapshot entry in newExact.Values) matches.Add((null, entry));
        return matches.OrderBy(match => match.Before?.Path ?? match.After!.Path,
            StringComparer.Ordinal).ToArray();
    }

    private static string SnapshotPath(string temporaryRoot, string role, string source)
        => Path.Combine(temporaryRoot,
            $"a2-diff-{role}-{Guid.NewGuid():N}-{Path.GetFileName(source)}");

    private static void WriteSnapshot(string path, byte[] bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.SequentialScan);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void DeleteSnapshot(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, DiskEntrySnapshot> Snapshot(DiskSession session,
        CancellationToken cancellationToken)
    {
        StringComparer comparer = session.Info.FileSystem == "dos33"
            ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        Dictionary<string, DiskEntrySnapshot> result = new(comparer);
        foreach (DiskEntry entry in session.List(recursive: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? payloadHash = null;
            string? storedHash = null;
            if (!entry.IsDirectory)
            {
                payloadHash = ProgramFiles.Hash(session.ReadFile(entry.Path));
                storedHash = ProgramFiles.Hash(session.ReadFile(entry.Path, raw: true));
            }
            result.Add(entry.Path, new(entry.Path, entry.IsDirectory, entry.Type, entry.AuxType,
                entry.Access, entry.Length, entry.StoredLength, entry.StorageSize, payloadHash,
                storedHash, entry.Created, entry.Modified));
        }
        return result;
    }

    private static List<string> ChangedFields(DiskEntrySnapshot before, DiskEntrySnapshot after)
    {
        List<string> fields = [];
        if (!before.Path.Equals(after.Path, StringComparison.Ordinal)) fields.Add("path");
        if (before.IsDirectory != after.IsDirectory) fields.Add("kind");
        if (before.Type != after.Type) fields.Add("type");
        if (before.AuxType != after.AuxType) fields.Add("auxType");
        if (before.Access != after.Access) fields.Add("access");
        if (before.Length != after.Length) fields.Add("length");
        if (before.StoredLength != after.StoredLength) fields.Add("storedLength");
        if (before.StorageSize != after.StorageSize) fields.Add("storageSize");
        if (before.PayloadSha256 != after.PayloadSha256) fields.Add("payload");
        if (before.StoredSha256 != after.StoredSha256) fields.Add("storedPayload");
        if (before.Created != after.Created) fields.Add("created");
        if (before.Modified != after.Modified) fields.Add("modified");
        return fields;
    }

    private static (List<ByteDifference> Ranges, long Count, bool Truncated) CompareBytes(
        ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        int common = Math.Min(before.Length, after.Length);
        List<ByteDifference> ranges = [];
        long count = Math.Abs((long)before.Length - after.Length);
        int start = -1;
        bool truncated = false;
        for (int index = 0; index < common; index++)
        {
            bool differs = before[index] != after[index];
            if (differs)
            {
                count++;
                if (start < 0) start = index;
            }
            else if (start >= 0)
            {
                AddRange(start, index - start);
                start = -1;
            }
        }
        if (start >= 0) AddRange(start, common - start);
        if (before.Length != after.Length) AddRange(common, Math.Abs((long)before.Length - after.Length));
        return (ranges, count, truncated);

        void AddRange(long offset, long length)
        {
            if (ranges.Count < MaximumByteRanges) ranges.Add(new(offset, length));
            else truncated = true;
        }
    }
}
