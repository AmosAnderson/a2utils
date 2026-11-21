// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2Utils.Core.Backends;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Operations;

public sealed record ExtractedFile(DiskEntry Entry, string? HostFile, string? Sha256,
    IReadOnlyList<SparseExtent> DataExtents)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExtractedFork? ResourceFork { get; init; }
}

public sealed record ExtractedFork(string HostFile, string Sha256, IReadOnlyList<SparseExtent> DataExtents);

public sealed record ExtractionManifest(int SchemaVersion, string FileSystem,
    IReadOnlyList<ExtractedFile> Entries);

/// <summary>Transfers stored file bytes and metadata without text or BASIC conversion.</summary>
public static class FileTransfer
{
    private const int MaximumManifestBytes = 32 * 1024 * 1024;
    private const int MaximumEntries = 65535;
    private const int MaximumPayloadBytes = 32 * 1024 * 1024;
    private const int MaximumCombinedPayloadBytes = 128 * 1024 * 1024;
    public const string ManifestName = "a2-manifest.json";
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions ManifestReadOptions = new(JsonOptions)
    {
        Converters = { new CatalogDateConverter() }
    };

    private sealed class CatalogDateConverter : JsonConverter<DateTime>
    {
        // Older manifests include the extracting host's UTC offset. ProDOS stores wall-clock fields.
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset().DateTime;

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Unspecified));
    }

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
            long payloadBytes = 0;
            List<DiskEntry> selected = session.List(imagePath, recursive: true).ToList();
            string fileSystem = session.Info.FileSystem;
            if (fileSystem == "prodos" && !string.IsNullOrEmpty(imagePath))
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
                if (files.Count == MaximumEntries)
                    throw new DiskException("transfer.too_large", "Extraction supports at most 65,535 entries.", 3);
                if (entry.IsDubious)
                    throw new DiskException("damaged_entry", $"Cannot preserve a damaged or dubious entry: {entry.Path}", 4);
                if (entry.IsDirectory)
                {
                    files.Add(new(entry, null, null, []));
                    continue;
                }

                long size = entry.StoredLength + (entry.ResourceForkLength ?? 0);
                if (size > MaximumCombinedPayloadBytes - payloadBytes)
                    throw new DiskException("transfer.too_large", "Extracted payloads exceed the 128 MiB restore limit.", 3);
                payloadBytes += size;
                // A numeric prefix avoids reserved names and case collisions on every host.
                string prefix = $"{files.Count + 1:D4}_{SafeName(entry.Name)}";
                ExtractedFork data = WriteFork(entry, prefix + ".a2raw", DiskFileFork.Data);
                files.Add(new(entry, data.HostFile, data.Sha256, data.DataExtents)
                {
                    ResourceFork = entry.HasResourceFork
                        ? WriteFork(entry, prefix + ".a2rsrc", DiskFileFork.Resource) : null
                });
            }

            ExtractedFork WriteFork(DiskEntry entry, string name, DiskFileFork fork)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes = entry.HasResourceFork ? session.ReadFile(entry.Path, fork) : session.ReadFile(entry.Path, raw: true);
                var extents = entry.HasResourceFork ? session.GetSparseExtents(entry.Path, fork) : session.GetSparseExtents(entry.Path);
                File.WriteAllBytes(Path.Combine(temporary, name), bytes);
                return new(name, Convert.ToHexString(SHA256.HashData(bytes)), extents);
            }

            ExtractionManifest manifest = new(files.Any(f => f.ResourceFork is not null) ? 2 : 1, fileSystem, files);
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            if (manifestBytes.Length > MaximumManifestBytes)
                throw new DiskException("transfer.too_large", "The extraction manifest exceeds the 32 MiB restore limit.", 3);
            File.WriteAllBytes(Path.Combine(temporary, ManifestName), manifestBytes);
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
        CancellationToken cancellationToken = default, string? outputPath = null)
    {
        string manifestFile = Path.GetFullPath(manifestPath);
        RejectLinks(manifestFile);
        if (outputPath is not null) ImageTransactions.EnsureDistinctPaths(manifestFile, outputPath);
        ExtractionManifest manifest = ParseManifest(
            ProgramFiles.ReadBytes(manifestFile, MaximumManifestBytes, cancellationToken));
        if (!string.Equals(manifest.FileSystem, session.Info.FileSystem, StringComparison.Ordinal))
        {
            throw new DiskException("filesystem_mismatch", "Manifest restore requires the original filesystem type.", 3);
        }

        string parent = Path.GetDirectoryName(manifestFile)!;
        List<(ExtractedFile File, byte[] Data, byte[]? Resource)> payloads = [];
        int payloadBytes = 0;
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
            StringComparison nameComparison = manifest.FileSystem == "dos33"
                ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!string.Equals(expectedName, file.Entry.Name, nameComparison))
            {
                throw new DiskException("invalid_manifest", "An entry name conflicts with its original image path.", 2);
            }
            if (file.Entry.IsDirectory)
            {
                continue;
            }
            byte[] data = ReadPayload(file.HostFile, file.Sha256, file.Entry.StoredLength, file.DataExtents);
            byte[]? resource = file.ResourceFork is { } fork
                ? ReadPayload(fork.HostFile, fork.Sha256, file.Entry.ResourceForkLength!.Value, fork.DataExtents)
                : null;
            payloads.Add((file, data, resource));
        }

        byte[] ReadPayload(string? name, string? sha256, long length, IReadOnlyList<SparseExtent> extents)
        {
            ValidatePayloadName(name);
            string payloadPath = Path.Combine(parent, name!);
            RejectLinks(payloadPath);
            if (outputPath is not null) ImageTransactions.EnsureDistinctPaths(payloadPath, outputPath);
            byte[] bytes = ProgramFiles.ReadBytes(payloadPath,
                Math.Min(MaximumPayloadBytes, MaximumCombinedPayloadBytes - payloadBytes), cancellationToken);
            payloadBytes += bytes.Length;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), sha256, StringComparison.OrdinalIgnoreCase))
                throw new DiskException("payload_hash_mismatch", $"Payload changed: {name}", 4);
            if (bytes.LongLength != length)
                throw new DiskException("payload_length_mismatch", $"Stored length differs from metadata: {name}", 4);
            DiskSession.ValidateExtents(bytes, extents, "invalid_sparse_extent");
            return bytes;
        }

        // Preflight all payload hashes before changing even the staged image.
        foreach (ExtractedFile directory in manifest.Entries.Where(f => f.Entry.IsDirectory)
                     .OrderBy(f => f.Entry.Path.Count(c => c == '/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.Mkdir(directory.Entry.Path);
        }
        foreach ((ExtractedFile file, byte[] bytes, byte[]? resource) in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.RestoreFile(file.Entry, bytes, file.DataExtents, resource, file.ResourceFork?.DataExtents);
        }
        foreach (ExtractedFile directory in manifest.Entries.Where(f => f.Entry.IsDirectory)
                     .OrderByDescending(f => f.Entry.Path.Count(c => c == '/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.RestoreDirectoryMetadata(directory.Entry);
        }
        foreach ((ExtractedFile file, byte[] bytes, byte[]? resource) in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiskEntry restored = session.List(file.Entry.Path).Single();
            byte[] restoredData = file.Entry.HasResourceFork
                ? session.ReadFile(file.Entry.Path, DiskFileFork.Data) : session.ReadFile(file.Entry.Path, raw: true);
            if (restored.ResourceForkLength != file.Entry.ResourceForkLength ||
                restored.HfsFileType.GetValueOrDefault() != file.Entry.HfsFileType.GetValueOrDefault() ||
                restored.HfsCreator.GetValueOrDefault() != file.Entry.HfsCreator.GetValueOrDefault() ||
                resource is not null && !session.ReadFile(file.Entry.Path, DiskFileFork.Resource).AsSpan().SequenceEqual(resource) ||
                restored.Length != file.Entry.Length || restored.StoredLength != file.Entry.StoredLength
                || restored.FileType != file.Entry.FileType || restored.AuxType != file.Entry.AuxType
                || restored.Access != file.Entry.Access || !restored.RawName.SequenceEqual(file.Entry.RawName)
                || !restoredData.AsSpan().SequenceEqual(bytes))
            {
                throw new DiskException("invalid_manifest", "Restored data or metadata differs from the manifest.", 2);
            }
        }
    }

    internal static ExtractionManifest ParseManifest(byte[] bytes)
    {
        ExtractionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExtractionManifest>(ProgramFiles.DecodeText(bytes), ManifestReadOptions)
                ?? throw new JsonException("The manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new DiskException("invalid_manifest", ex.Message, 2, ex);
        }
        if (manifest.SchemaVersion is not (1 or 2) || manifest.Entries is null || manifest.Entries.Count > MaximumEntries ||
            manifest.Entries.Any(file => file?.Entry is null))
            throw new DiskException("invalid_manifest", "Expected a version 1 or 2 manifest with at most 65,535 valid entries.", 2);
        foreach (ExtractedFile file in manifest.Entries)
        {
            if (!file.Entry.HasResourceFork && (file.ResourceFork is not null || file.Entry.ResourceForkLength is not null ||
                file.Entry.HfsFileType is not null || file.Entry.HfsCreator is not null) ||
                file.Entry.HasResourceFork && (manifest.SchemaVersion != 2 || manifest.FileSystem != "prodos" ||
                file.Entry.IsDirectory || file.Entry.ResourceForkLength is null or < 0 || file.ResourceFork?.DataExtents is null ||
                string.Equals(file.HostFile, file.ResourceFork.HostFile, StringComparison.OrdinalIgnoreCase)))
                throw new DiskException("invalid_manifest", "Inconsistent resource fork metadata or payload names.", 2);
        }
        return manifest;
    }

    internal static void ValidatePayloadName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)
            || name.Contains(':') || name.Contains('\\') || name.Contains('/') || name is "." or "..")
            throw new DiskException("invalid_manifest_path", "Manifest payloads must be direct sibling files.", 2);
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
