// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

/// <summary>Opt-in content-addressed storage for complete, validated project images.</summary>
public static class ProjectBuildCache
{
    private const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private const int MaximumImageBytes = 34 * 1024 * 1024;
    private const int MaximumInputBytes = 64 * 1024 * 1024;
    private const int MaximumInputs = 8192;
    private const long MaximumCombinedInputBytes = 512L * 1024 * 1024;
    private const int MaximumEntriesPerManifest = 1024;

    public static ProjectBuildResult Build(string manifestPath, string cacheDirectory,
        string? outputPath = null, bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ResolvedProjectManifest resolved = ProjectResolver.Load(manifestPath, outputPath,
            cancellationToken);
        string cache = PrepareCache(cacheDirectory);
        string destination = resolved.OutputPath;
        EnsureOutsideCache(destination, cache);
        EnsureInputsOutsideCache([new BuildInput(resolved.ManifestPath,
            ProgramFiles.Hash(resolved.ManifestBytes))], cache);
        ImageTransactions.EnsureDistinctPaths(resolved.ManifestPath, destination);

        string manifestId = HashText(NormalizePath(resolved.ManifestPath));
        string entries = Path.Combine(cache, "entries", manifestId);
        string objects = Path.Combine(cache, "objects");
        PrepareDirectory(entries);
        PrepareDirectory(objects);

        Dictionary<string, InputObservation> observed = new(PathComparer);
        ProjectBuildResult? hit = TryRestore(resolved, destination, cache, entries,
            objects, overwrite, observed, cancellationToken);
        if (hit is not null) return hit;

        ProjectBuildResult preview = ProjectBuilder.BuildResolved(resolved, overwrite,
            cancellationToken: cancellationToken, preflight: true);
        EnsureOutsideCache(preview.OutputPath, cache);
        EnsureInputsOutsideCache(preview.Inputs, cache);
        ImageTransactions.EnsureDistinctPaths(resolved.ManifestPath, preview.OutputPath);
        ProjectBuildResult built = ProjectBuilder.BuildVerified(resolved,
            overwrite, preview, cancellationToken);
        if (!built.ToolVersion.Equals(CurrentToolVersion, StringComparison.Ordinal))
            throw Error("version", "The completed build did not report the current tool version.", 6);
        if (!InputsMatch(built.Inputs, null, cancellationToken))
            return built with { CacheHit = false };

        string contentKey = ComputeContentKey(resolved.ManifestPath, built);
        CacheEntry entry = new(1, resolved.ManifestPath, contentKey,
            built.Sha256.ToLowerInvariant(), built with { CacheHit = false });
        if (built.Inputs.Count > MaximumInputs)
            return built with { CacheHit = false };
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(entry, ProjectJson.Options);
        if (metadata.Length > MaximumMetadataBytes)
            return built with { CacheHit = false };

        string metadataPath = Path.Combine(entries, contentKey + ".json");
        using FileStream? cacheLock = TryAcquireWriteLock(entries, cancellationToken);
        if (cacheLock is null) return built with { CacheHit = false };
        int entryCount = Directory.EnumerateFiles(entries, "*.json", SearchOption.TopDirectoryOnly)
            .Take(MaximumEntriesPerManifest + 1).Count();
        if (!File.Exists(metadataPath) && entryCount >= MaximumEntriesPerManifest)
            return built with { CacheHit = false };

        string objectPath = Path.Combine(objects, built.Sha256.ToLowerInvariant() + ".img");
        StoreObject(built.OutputPath, objectPath, built, cancellationToken);
        StoreMetadata(metadataPath, entry, metadata, cancellationToken);
        return built with { CacheHit = false };
    }

    private static FileStream? TryAcquireWriteLock(string entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return new FileStream(Path.Combine(entries, ".write.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException)
        {
            // A concurrent writer can populate the immutable cache; this build remains valid uncached.
            return null;
        }
    }

    private static ProjectBuildResult? TryRestore(ResolvedProjectManifest resolved,
        string destination, string cache,
        string entries, string objects, bool overwrite,
        Dictionary<string, InputObservation> observed, CancellationToken cancellationToken)
    {
        string[] candidates = Directory.EnumerateFiles(entries, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal).Take(MaximumEntriesPerManifest + 1).ToArray();
        if (candidates.Length > MaximumEntriesPerManifest)
            throw Error("limit", $"A project cache may contain at most {MaximumEntriesPerManifest} entries per manifest.");

        foreach (string metadataPath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string expectedKey = Path.GetFileNameWithoutExtension(metadataPath);
            if (!IsSha256(expectedKey)) continue;
            CacheEntry? entry = ReadMetadata(metadataPath, cancellationToken);
            if (entry?.Build?.Inputs is { } declaredInputs)
                EnsureInputsOutsideCache(declaredInputs, cache);
            if (!IsValidEntry(entry, resolved.ManifestPath, expectedKey)) continue;
            BuildInput? manifestInput = entry!.Build.Inputs.SingleOrDefault(input =>
                PathsEqual(input.Path, resolved.ManifestPath));
            if (manifestInput is null || !manifestInput.Sha256.Equals(
                ProgramFiles.Hash(resolved.ManifestBytes), StringComparison.OrdinalIgnoreCase) ||
                entry.Build.Target != resolved.Profile.Name || entry.Build.Cpu != resolved.Cpu ||
                entry.Build.FileSystem != resolved.FileSystem ||
                !MatchesResolvedInput(entry.Build.Inputs, resolved.EnvironmentInput) ||
                !MatchesResolvedInput(entry.Build.Inputs, resolved.ToolchainLockInput) ||
                !MatchesResolvedInput(entry.Build.Inputs, resolved.TemplateInput))
                continue;
            if (!InputsMatch(entry.Build.Inputs, observed, cancellationToken)) continue;
            if (!CurrentInputSetMatches(resolved, entry.Build, destination, cancellationToken)) continue;

            string objectPath = Path.Combine(objects, entry.ImageSha256 + ".img");
            if (!File.Exists(objectPath)) continue;
            foreach (BuildInput input in entry.Build.Inputs)
                ImageTransactions.EnsureDistinctPaths(input.Path, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                ImageTransactions.Write(objectPath, destination, inPlace: false, overwrite,
                    static _ => { }, temporary =>
                    {
                        ValidateImage(temporary, entry.Build, cancellationToken);
                        if (!InputsMatch(entry.Build.Inputs, null, cancellationToken))
                            throw Error("stale", "Project inputs changed while restoring a cached build.", 6);
                        if (!CurrentInputSetMatches(resolved, entry.Build, destination, cancellationToken))
                            throw Error("stale", "Project input paths changed while restoring a cached build.", 6);
                    }, cancellationToken);
            }
            catch (DiskException exception) when (exception.Code is "project.cache_corrupt" or "project.cache_stale")
            {
                continue;
            }

            return entry.Build with
            {
                OutputPath = destination,
                CheckOnly = false,
                Preflight = false,
                CacheHit = true
            };
        }

        return null;
    }

    private static bool CurrentInputSetMatches(ResolvedProjectManifest resolved,
        ProjectBuildResult build, string destination, CancellationToken cancellationToken)
    {
        foreach (ProjectAssetReport asset in build.Assets)
        {
            foreach (BuildInput output in asset.Outputs)
            {
                string path = Path.GetFullPath(output.Path, resolved.ProjectRoot);
                ImageTransactions.ValidatePath(path);
                ImageTransactions.EnsureDistinctPaths(path, destination);
                if (File.Exists(path) || Directory.Exists(path))
                    throw new DiskException("project.asset_collision",
                        "Generated asset collides with an existing path: " + output.Path, 2);
            }
        }
        if (!resolved.Manifest.Files.Any(file => file.Kind == "cc65")) return true;
        HashSet<string> inputs = build.Inputs.Select(input => input.Path).ToHashSet(PathComparer);
        // Added headers can shadow older includes even when every previously captured
        // file is unchanged. Enumerate the same project and toolchain trees as compilation.
        return Cc65Compiler.EnumerateInputPaths(resolved.Manifest.Cc65!, resolved.ProjectRoot,
            cancellationToken).All(inputs.Contains);
    }

    private static bool MatchesResolvedInput(IReadOnlyList<BuildInput> inputs,
        ProjectInputSnapshot? snapshot)
    {
        if (snapshot is null) return true;
        BuildInput? input = inputs.SingleOrDefault(item => PathsEqual(item.Path, snapshot.Path));
        return input is not null && input.Sha256.Equals(snapshot.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool InputsMatch(IReadOnlyList<BuildInput> inputs,
        Dictionary<string, InputObservation>? observed, CancellationToken cancellationToken)
    {
        long combinedBytes = 0;
        foreach (BuildInput input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InputObservation actual;
            if (observed is not null && observed.TryGetValue(input.Path,
                out InputObservation? prior))
            {
                actual = prior;
            }
            else
            {
                try
                {
                    actual = HashInput(input.Path, cancellationToken);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    actual = new(null, 0);
                }
                if (observed is not null) observed[input.Path] = actual;
            }
            if (actual.Length > MaximumCombinedInputBytes - combinedBytes ||
                !input.Sha256.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            combinedBytes += actual.Length;
        }
        return true;
    }

    private static void StoreObject(string source, string objectPath,
        ProjectBuildResult build, CancellationToken cancellationToken)
    {
        if (File.Exists(objectPath))
        {
            try
            {
                ValidateImage(objectPath, build, cancellationToken);
                return;
            }
            catch (DiskException exception) when (exception.Code == "project.cache_corrupt")
            {
                // Atomically replace the invalid object below.
            }
        }

        try
        {
            ImageTransactions.Write(source, objectPath, inPlace: false, overwrite: true,
                static _ => { }, temporary => ValidateImage(temporary, build, cancellationToken),
                cancellationToken);
        }
        catch (DiskException exception) when (exception.Code == "write.concurrent_change")
        {
            // Another writer may have committed the same immutable object.
            ValidateImage(objectPath, build, cancellationToken);
        }
    }

    private static void StoreMetadata(string path, CacheEntry entry, byte[] json,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            CacheEntry? existing = ReadMetadata(path, cancellationToken);
            if (IsValidEntry(existing, entry.ManifestPath, entry.ContentKey)) return;
        }
        try
        {
            ImageTransactions.Create(path, overwrite: true,
                temporary => File.WriteAllBytes(temporary, json), temporary =>
                {
                    CacheEntry? staged = ReadMetadata(temporary, cancellationToken);
                    if (!IsValidEntry(staged, entry.ManifestPath, entry.ContentKey))
                        throw Error("metadata", "Staged project-cache metadata failed validation.", 6);
                }, cancellationToken);
        }
        catch (DiskException exception) when (exception.Code is "write.concurrent_change" or "write.destination_exists")
        {
            CacheEntry? existing = ReadMetadata(path, cancellationToken);
            if (!IsValidEntry(existing, entry.ManifestPath, entry.ContentKey)) throw;
        }
    }

    private static CacheEntry? ReadMetadata(string path,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] json = ProgramFiles.ReadBytes(path, MaximumMetadataBytes,
                cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = ProjectJson.Options.MaxDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            ProjectBuilder.RejectDuplicateProperties(document.RootElement);
            CacheEntry? entry = JsonSerializer.Deserialize<CacheEntry>(json, ProjectJson.Options);
            if (entry?.Build?.Files is null) return entry;
            List<BuiltFile> files = new(entry.Build.Files.Count);
            foreach (BuiltFile? file in entry.Build.Files)
            {
                if (file is null) return null;
                files.Add(file with { SourceMap = RehydrateSourceMap(file) });
            }
            return entry with { Build = entry.Build with { Files = files } };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (DiskException exception) when (exception.Code is "program.too_large" or "program.invalid_utf8" or "project.schema")
        {
            return null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static object? RehydrateSourceMap(BuiltFile file)
    {
        if (file.SourceMap is not JsonElement element)
        {
            if (file.SourceMap is null && file.Kind is not ("asm" or "basic" or "basic-labels" or "cc65"))
                return null;
            throw new JsonException($"Cached source map for '{file.Path}' has an invalid representation.");
        }

        return file.Kind switch
        {
            "asm" => element.Deserialize<AssemblySourceMapEntry[]>(ProjectJson.Options)
                ?? throw new JsonException("Cached assembly source map cannot be null."),
            "basic" or "basic-labels" => element.Deserialize<BasicPreparedLine[]>(ProjectJson.Options)
                ?? throw new JsonException("Cached BASIC source map cannot be null."),
            "cc65" => element.Deserialize<Cc65SourceMap>(ProjectJson.Options)
                ?? throw new JsonException("Cached cc65 source map cannot be null."),
            _ when element.ValueKind == JsonValueKind.Null => null,
            _ => throw new JsonException($"Source kind '{file.Kind}' cannot contain cached source-map data.")
        };
    }

    private static bool IsValidEntry(CacheEntry? entry, string manifestPath,
        string expectedContentKey)
    {
        if (entry is null || entry.SchemaVersion != 1 || entry.Build is null ||
            string.IsNullOrWhiteSpace(entry.ManifestPath) ||
            !Path.IsPathFullyQualified(entry.ManifestPath) ||
            string.IsNullOrEmpty(entry.ContentKey) || string.IsNullOrEmpty(entry.ImageSha256) ||
            !entry.ContentKey.Equals(expectedContentKey, StringComparison.Ordinal) ||
            !IsSha256(entry.ContentKey) || !IsSha256(entry.ImageSha256) ||
            !IsSha256(entry.Build.Sha256) ||
            !entry.ImageSha256.Equals(entry.Build.Sha256, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(entry.Build.ToolVersion) ||
            !entry.Build.ToolVersion.Equals(CurrentToolVersion, StringComparison.Ordinal) ||
            entry.Build.CheckOnly || entry.Build.Preflight || entry.Build.CacheHit ||
            entry.Build.Inputs is null || entry.Build.Inputs.Count is 0 or > MaximumInputs ||
            entry.Build.Files is null || entry.Build.Memory is null || entry.Build.Diagnostics is null ||
            entry.Build.Assets is null || string.IsNullOrWhiteSpace(entry.Build.OutputPath) ||
            !Path.IsPathFullyQualified(entry.Build.OutputPath) ||
            string.IsNullOrWhiteSpace(entry.Build.Target) || string.IsNullOrWhiteSpace(entry.Build.Cpu) ||
            entry.Build.FileSystem is not ("dos33" or "prodos"))
            return false;

        try
        {
            if (!PathsEqual(entry.ManifestPath, manifestPath)) return false;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        HashSet<string> paths = new(PathComparer);
        bool hasManifest = false;
        foreach (BuildInput input in entry.Build.Inputs)
        {
            if (input is null || !Path.IsPathFullyQualified(input.Path) || !IsSha256(input.Sha256))
                return false;
            string full;
            try { full = Path.GetFullPath(input.Path); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
            if (!PathsEqual(full, input.Path) || !paths.Add(full)) return false;
            if (PathsEqual(full, manifestPath)) hasManifest = true;
        }
        if (!hasManifest) return false;
        try
        {
            return ComputeContentKey(entry.ManifestPath, entry.Build)
                .Equals(entry.ContentKey, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static InputObservation HashInput(string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImageTransactions.ValidatePath(path);
        HostFiles.EnsureRegularFile(path, cancellationToken);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = input.Length;
        if (length > MaximumInputBytes) return new(null, length);
        string hash = Convert.ToHexStringLower(SHA256.HashDataAsync(input,
            cancellationToken).GetAwaiter().GetResult());
        if (input.Length != length || input.Position != length)
            throw Error("input_changed", "A project input changed while checking the build cache: " + path, 6);
        cancellationToken.ThrowIfCancellationRequested();
        return new(hash, length);
    }

    private static void ValidateImage(string path, ProjectBuildResult build,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = ProgramFiles.ReadBytes(path, MaximumImageBytes, cancellationToken);
            string hash = ProgramFiles.Hash(bytes);
            if (!hash.Equals(build.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Error("corrupt", "Cached project image SHA-256 does not match its metadata.", 6);
            using DiskSession session = DiskSession.Open(path, inputFs: build.FileSystem);
            DiskInfo info = session.Info;
            if (info.FileSystem != build.FileSystem || info.IsDubious ||
                session.Verify().Any(diagnostic => diagnostic.Severity == "error"))
                throw Error("corrupt", "Cached project image failed structural verification.", 6);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiskException exception) when (exception.Code != "project.cache_corrupt")
        {
            throw Error("corrupt", "Cached project image failed validation: " + exception.Message, 6,
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error("corrupt", "Cached project image could not be validated: " + exception.Message, 6,
                exception);
        }
    }

    private static string ComputeContentKey(string manifestPath, ProjectBuildResult build)
    {
        ProjectBuildResult canonical = build with
        {
            OutputPath = "",
            CacheHit = false,
            Inputs = build.Inputs
                .OrderBy(input => NormalizePath(input.Path), StringComparer.Ordinal)
                .Select(input => new BuildInput(NormalizePath(input.Path),
                    input.Sha256.ToLowerInvariant())).ToArray()
        };
        CacheKey key = new(1, NormalizePath(manifestPath), canonical);
        return ProgramFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(key, ProjectJson.Options));
    }

    private static string PrepareCache(string cacheDirectory)
    {
        string cache = Path.GetFullPath(cacheDirectory);
        ImageTransactions.ValidatePath(cache);
        if (File.Exists(cache)) throw Error("path", "The project cache path is an existing file.");
        PrepareDirectory(cache);
        PrepareDirectory(Path.Combine(cache, "entries"));
        PrepareDirectory(Path.Combine(cache, "objects"));
        return cache;
    }

    private static void PrepareDirectory(string path)
    {
        ImageTransactions.ValidatePath(path);
        Directory.CreateDirectory(path);
        ImageTransactions.ValidatePath(path);
    }

    private static void EnsureOutsideCache(string destination, string cache)
    {
        string full = Path.GetFullPath(destination);
        ImageTransactions.ValidatePath(full);
        if (PathsEqual(full, cache) || full.StartsWith(cache + Path.DirectorySeparatorChar,
            PathComparison))
            throw Error("alias", "The project output must be outside the build cache.", 6);
    }

    private static void EnsureInputsOutsideCache(IReadOnlyList<BuildInput> inputs,
        string cache)
    {
        foreach (BuildInput input in inputs)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Path)) continue;
            string full;
            try { full = Path.GetFullPath(input.Path); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (PathsEqual(full, cache) || full.StartsWith(cache + Path.DirectorySeparatorChar,
                PathComparison))
                throw Error("input_alias", "Project build inputs must be outside the build cache.", 6);
        }
    }

    private static string HashText(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? full.ToUpperInvariant() : full;
    }

    private static bool PathsEqual(string first, string second)
        => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), PathComparison);

    private static string CurrentToolVersion => typeof(ProjectBuildCache).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static DiskException Error(string code, string message, int exitCode = 2,
        Exception? innerException = null)
        => new("project.cache_" + code, message, exitCode, innerException);

    private sealed record CacheEntry(int SchemaVersion, string ManifestPath,
        string ContentKey, string ImageSha256, ProjectBuildResult Build);
    private sealed record CacheKey(int SchemaVersion, string ManifestPath,
        ProjectBuildResult Build);
    private sealed record InputObservation(string? Sha256, long Length);
}
