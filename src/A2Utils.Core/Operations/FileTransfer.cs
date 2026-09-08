using System.Security.Cryptography;
using System.Text.Json;
using A2Utils.Core.Backends;

namespace A2Utils.Core.Operations;

public sealed record ExtractedFile(DiskEntry Entry, string? HostFile, string? Sha256,
    IReadOnlyList<SparseExtent> DataExtents);

public sealed record ExtractionManifest(int SchemaVersion, string FileSystem,
    IReadOnlyList<ExtractedFile> Entries);

/// <summary>Transfers stored file bytes and metadata without text or BASIC conversion.</summary>
public static class FileTransfer
{
    public const string ManifestName = "a2-manifest.json";
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static ExtractionManifest Extract(DiskSession session, string destination,
        string? imagePath = null, CancellationToken cancellationToken = default)
    {
        string target = Path.GetFullPath(destination);
        RejectLinks(target);
        if (Path.Exists(target))
        {
            throw new DiskException("destination_exists", "Extraction requires a new destination directory.");
        }

        string parent = Path.GetDirectoryName(target)
            ?? throw new DiskException("invalid_destination", "The destination needs a parent directory.");
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, $".a2-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            List<ExtractedFile> files = [];
            List<DiskEntry> selected = session.List(imagePath, recursive: true).ToList();
            if (session.Info.FileSystem == "prodos" && !string.IsNullOrEmpty(imagePath))
            {
                // Include the selected directory and ancestors so a single nested file
                // can be restored into an otherwise empty volume.
                HashSet<string> included = selected.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (DiskEntry directory in session.List(recursive: true).Where(e => e.IsDirectory))
                {
                    string requested = imagePath.Trim('/');
                    if ((requested.Equals(directory.Path, StringComparison.OrdinalIgnoreCase)
                        || requested.StartsWith(directory.Path + "/", StringComparison.OrdinalIgnoreCase))
                        && included.Add(directory.Path))
                    {
                        selected.Add(directory);
                    }
                }
            }
            foreach (DiskEntry entry in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    files.Add(new(entry, null, null, []));
                    continue;
                }

                if (entry.HasResourceFork)
                {
                    throw new DiskException("unsupported_fork", $"Cannot preserve resource forks: {entry.Path}", 3);
                }
                byte[] bytes = session.ReadFile(entry.Path, raw: true);
                // A numeric prefix avoids reserved names and case collisions on every host.
                string name = $"{files.Count + 1:D4}_{SafeName(entry.Name)}.a2raw";
                File.WriteAllBytes(Path.Combine(temporary, name), bytes);
                files.Add(new(entry, name, Convert.ToHexString(SHA256.HashData(bytes)),
                    session.GetSparseExtents(entry.Path)));
            }

            ExtractionManifest manifest = new(1, session.Info.FileSystem, files);
            File.WriteAllText(Path.Combine(temporary, ManifestName),
                JsonSerializer.Serialize(manifest, JsonOptions));
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(target);
            Directory.Move(temporary, target);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    public static void Restore(DiskSession session, string manifestPath,
        CancellationToken cancellationToken = default)
    {
        string manifestFile = Path.GetFullPath(manifestPath);
        RejectLinks(manifestFile);
        ExtractionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExtractionManifest>(
                File.ReadAllText(manifestFile), JsonOptions)
                ?? throw new JsonException("The manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new DiskException("invalid_manifest", ex.Message, 2, ex);
        }

        if (manifest.SchemaVersion != 1 || manifest.Entries is null)
        {
            throw new DiskException("invalid_manifest", "Unsupported or incomplete manifest.", 2);
        }
        if (!string.Equals(manifest.FileSystem, session.Info.FileSystem, StringComparison.Ordinal))
        {
            throw new DiskException("filesystem_mismatch", "Manifest restore requires the original filesystem type.", 3);
        }

        string parent = Path.GetDirectoryName(manifestFile)!;
        List<(ExtractedFile File, byte[] Bytes)> payloads = [];
        HashSet<string> paths = new(manifest.FileSystem == "dos33" ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (ExtractedFile file in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file?.Entry is null || string.IsNullOrEmpty(file.Entry.Path)
                || !paths.Add(file.Entry.Path) || file.DataExtents is null || file.Entry.RawName is null)
            {
                throw new DiskException("invalid_manifest", "Manifest entries must have unique paths and metadata.", 2);
            }
            string expectedName = manifest.FileSystem == "dos33" ? file.Entry.Path : file.Entry.Path.Split('/')[^1];
            if (!string.Equals(expectedName, file.Entry.Name, StringComparison.Ordinal))
            {
                throw new DiskException("invalid_manifest", "An entry name conflicts with its original image path.", 2);
            }
            if (file.Entry.IsDirectory)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(file.HostFile) || file.HostFile != Path.GetFileName(file.HostFile)
                || file.HostFile.Contains(':') || file.HostFile.Contains('\\') || file.HostFile.Contains('/'))
            {
                throw new DiskException("invalid_manifest_path", "Manifest payloads must be direct sibling files.", 2);
            }
            string payloadPath = Path.Combine(parent, file.HostFile);
            RejectLinks(payloadPath);
            byte[] data = File.ReadAllBytes(payloadPath);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), file.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new DiskException("payload_hash_mismatch", $"Payload changed: {file.HostFile}", 4);
            }
            long end = 0;
            foreach (SparseExtent extent in file.DataExtents)
            {
                if (extent is null || extent.Offset < end || extent.Length <= 0 || extent.Offset > data.LongLength
                    || extent.Length > data.LongLength - extent.Offset)
                {
                    throw new DiskException("invalid_sparse_extent", "Manifest data extents are invalid.", 2);
                }
                if (data.AsSpan(checked((int)end), checked((int)(extent.Offset - end))).ContainsAnyExcept((byte)0))
                {
                    throw new DiskException("invalid_sparse_extent", "Bytes outside allocated extents must be zero.", 2);
                }
                end = extent.Offset + extent.Length;
            }
            if (data.AsSpan(checked((int)end)).ContainsAnyExcept((byte)0))
            {
                throw new DiskException("invalid_sparse_extent", "Bytes outside allocated extents must be zero.", 2);
            }
            if (data.LongLength != file.Entry.StoredLength)
            {
                throw new DiskException("payload_length_mismatch", "Stored payload length differs from metadata.", 4);
            }
            payloads.Add((file, data));
        }

        // Preflight all payload hashes before changing even the staged image.
        foreach (ExtractedFile directory in manifest.Entries.Where(f => f.Entry.IsDirectory)
                     .OrderBy(f => f.Entry.Path.Count(c => c == '/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.Mkdir(directory.Entry.Path);
        }
        foreach ((ExtractedFile file, byte[] bytes) in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.RestoreFile(file.Entry, bytes, file.DataExtents);
        }
        foreach (ExtractedFile directory in manifest.Entries.Where(f => f.Entry.IsDirectory)
                     .OrderByDescending(f => f.Entry.Path.Count(c => c == '/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.RestoreDirectoryMetadata(directory.Entry);
        }
        foreach ((ExtractedFile file, byte[] bytes) in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiskEntry restored = session.List(file.Entry.Path).Single();
            if (restored.Length != file.Entry.Length || restored.StoredLength != file.Entry.StoredLength
                || restored.FileType != file.Entry.FileType || restored.AuxType != file.Entry.AuxType
                || restored.Access != file.Entry.Access || !restored.RawName.SequenceEqual(file.Entry.RawName)
                || !session.ReadFile(file.Entry.Path, raw: true).AsSpan().SequenceEqual(bytes))
            {
                throw new DiskException("invalid_manifest", "Restored data or metadata differs from the manifest.", 2);
            }
        }
    }

    private static string SafeName(string name)
    {
        string safe = new(name.Take(80).Select(c => c is >= ' ' and <= '~'
            && !"<>:\"/\\|?*".Contains(c) ? c : '_').ToArray());
        return safe.TrimEnd(' ', '.') is { Length: > 0 } result ? result : "FILE";
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DiskException("linked_path", "File transfer paths must not traverse symbolic links or junctions.");
            }
        }
    }
}
