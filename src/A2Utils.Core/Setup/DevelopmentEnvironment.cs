using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Setup;

public sealed record DevelopmentEnvironmentProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Machine { get; init; } = "apple2ee";
    public string MamePath { get; init; } = "";
    public string RomDirectory { get; init; } = "";
    public string? TemplateImage { get; init; }
    public string TemplateFileSystem { get; init; } = "prodos";
    public string? Cc65Path { get; init; }
    public string? Cc65Root { get; init; }
    public string? ExpectedCc65Version { get; init; }
    public double BootSeconds { get; init; } = 10;
    [JsonIgnore]
    public string InputSha256 { get; init; } = "";

    public static DevelopmentEnvironmentProfile Load(string path)
    {
        DevelopmentEnvironmentProfile profile = DevelopmentEnvironment.ReadJson<DevelopmentEnvironmentProfile>(path, out string inputHash);
        if (profile.SchemaVersion != 1 || profile.Machine is not ("apple2" or "apple2p" or "apple2e" or "apple2ee" or "apple2c")
            || profile.TemplateFileSystem is not ("dos33" or "prodos") || !double.IsFinite(profile.BootSeconds)
            || profile.BootSeconds is < 1 or > 120 || profile.MamePath is null || profile.RomDirectory is null)
            throw new DiskException("setup.invalid_profile", "The environment needs schemaVersion 1, a supported machine/filesystem, and bootSeconds 1..120.", 2);
        string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string Resolve(string value) => string.IsNullOrWhiteSpace(value) ? value : Path.GetFullPath(value, root);
        return profile with
        {
            MamePath = Resolve(profile.MamePath),
            RomDirectory = Resolve(profile.RomDirectory),
            TemplateImage = profile.TemplateImage is null ? null : Resolve(profile.TemplateImage),
            Cc65Path = profile.Cc65Path is null ? null : Resolve(profile.Cc65Path),
            Cc65Root = profile.Cc65Root is null ? null : Resolve(profile.Cc65Root),
            InputSha256 = inputHash
        };
    }
}

public sealed record EnvironmentCheck(string Code, bool Passed, string Message);
public sealed record EnvironmentCheckResult(int SchemaVersion, bool Ready, IReadOnlyList<EnvironmentCheck> Checks);
public sealed record EnvironmentFingerprint(string Role, string Path, long Length, string Sha256);
public sealed record DevelopmentEnvironmentLock(int SchemaVersion, string ProfilePath, string ProfileSha256,
    IReadOnlyList<EnvironmentFingerprint> Files);

/// <summary>Local tool discovery checks and bounded, content-addressed environment locks.</summary>
public static class DevelopmentEnvironment
{
    private const int MaximumFiles = 16384;
    private const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumTotalBytes = 8L * 1024 * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    internal static T ReadJson<T>(string path) => ReadJson<T>(path, out _);

    internal static T ReadJson<T>(string path, out string hash)
    {
        try
        {
            byte[] bytes = ProgramFiles.ReadBytes(path, 4 * 1024 * 1024);
            hash = ProgramFiles.Hash(bytes);
            using JsonDocument json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("schemaVersion", out _))
                throw new JsonException("schemaVersion is required.");
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new JsonException("Expected an object.");
        }
        catch (JsonException error) { throw new DiskException("setup.invalid_json", error.Message, 2); }
    }

    public static ExecutionSpec Apply(ExecutionSpec spec, string profilePath)
    {
        DevelopmentEnvironmentProfile profile = DevelopmentEnvironmentProfile.Load(profilePath);
        ExecutionSpec resolved = spec with
        {
            EmulatorPath = string.IsNullOrWhiteSpace(spec.EmulatorPath) ? profile.MamePath : spec.EmulatorPath,
            RomDirectory = string.IsNullOrWhiteSpace(spec.RomDirectory) ? profile.RomDirectory : spec.RomDirectory,
            Machine = string.IsNullOrWhiteSpace(spec.Machine) ? profile.Machine : spec.Machine
        };
        if (resolved.ToolchainLock is not null && (!PathsEqual(resolved.EmulatorPath, profile.MamePath)
            || !PathsEqual(resolved.RomDirectory, profile.RomDirectory)))
            throw new DiskException("setup.lock_scope", "A locked execution must use the profile's MAME executable and ROM directory; create a separate profile for overrides.", 2);
        return resolved;
    }

    public static ProjectManifest Apply(ProjectManifest manifest, string profilePath)
    {
        DevelopmentEnvironmentProfile profile = DevelopmentEnvironmentProfile.Load(profilePath);
        if (manifest.Disk is null) throw new DiskException("setup.invalid_project", "Project disk settings cannot be null.", 2);
        Cc65Options? compiler = manifest.Cc65;
        if (compiler is null && profile.Cc65Path is not null)
            compiler = new() { Compiler = profile.Cc65Path, Target = manifest.Target == "apple2enh" ? "apple2enh" : "apple2", ExpectedVersion = profile.ExpectedCc65Version };
        if (compiler is not null && compiler.ToolchainRoot is null && profile.Cc65Root is not null)
            compiler = compiler with { ToolchainRoot = profile.Cc65Root };
        string? template = manifest.Disk.Template ?? profile.TemplateImage;
        if (manifest.ToolchainLock is not null && (template is not null && (profile.TemplateImage is null || !PathsEqual(template, profile.TemplateImage))
            || compiler is not null && (profile.Cc65Path is null || !PathsEqual(compiler.Compiler, profile.Cc65Path)
                || compiler.ToolchainRoot is not null && (profile.Cc65Root is null || !PathsEqual(compiler.ToolchainRoot, profile.Cc65Root)))))
            throw new DiskException("setup.lock_scope", "A locked project must use the template and compiler distribution declared by its environment profile.", 2);
        return manifest with { Cc65 = compiler, Disk = manifest.Disk with { Template = template } };
    }

    public static async Task<EnvironmentCheckResult> CheckAsync(DevelopmentEnvironmentProfile profile,
        CancellationToken cancellationToken = default)
    {
        List<EnvironmentCheck> checks = [];
        bool emulator = File.Exists(profile.MamePath);
        bool roms = Directory.Exists(profile.RomDirectory);
        checks.Add(new("mame.path", emulator, emulator ? profile.MamePath : "Set mamePath to an existing MAME executable."));
        checks.Add(new("roms.path", roms, roms ? profile.RomDirectory : "Set romDirectory to your local, lawfully supplied ROM directory."));
        if (emulator)
        {
            var version = await Probe(profile.MamePath, ["-version"], cancellationToken);
            string found = Regex.Match(version.Output, @"\b\d+\.\d+\b", RegexOptions.CultureInvariant).Value;
            bool pinned = version.ExitCode == 0 && found == MameAdapter.ApiVersion;
            checks.Add(new("mame.version", pinned, pinned ? "MAME " + found : "Expected MAME " + MameAdapter.ApiVersion + ": " + version.Output));
            if (pinned && roms)
            {
                List<string> arguments = [profile.Machine, "-noreadconfig", "-rompath", profile.RomDirectory, "-verifyroms"];
                if (profile.Machine != "apple2c") arguments.AddRange(["-sl2", "", "-sl4", ""]);
                var verified = await Probe(profile.MamePath, arguments, cancellationToken);
                checks.Add(new("roms.verify", verified.ExitCode == 0, verified.Output));
            }
        }
        if (profile.TemplateImage is { } template)
        {
            try
            {
                RequireRegularFile(template);
                using DiskSession disk = DiskSession.Open(template, inputFs: profile.TemplateFileSystem);
                bool valid = !disk.Verify().Any(diagnostic => diagnostic.Severity == "error");
                checks.Add(new("template.verify", valid, valid ? $"{disk.Info.FileSystem} template: {template}; bootability must be verified by an execution test." : "The template has structural errors."));
            }
            catch (Exception error) when (error is DiskException or IOException or UnauthorizedAccessException)
            { checks.Add(new("template.verify", false, error.Message)); }
        }
        if (profile.Cc65Path is { } compiler)
        {
            var version = await Probe(compiler, ["--version"], cancellationToken);
            bool valid = version.ExitCode == 0 && !string.IsNullOrWhiteSpace(version.Output)
                && (profile.ExpectedCc65Version is null || version.Output.Trim() == profile.ExpectedCc65Version);
            checks.Add(new("cc65.version", valid, version.Output));
            checks.Add(new("cc65.distribution", profile.Cc65Root is not null && Directory.Exists(profile.Cc65Root)
                && new[] { "include", "asminc", "lib", "cfg" }.All(part => Directory.Exists(Path.Combine(profile.Cc65Root, part))),
                "Set cc65Root to the distribution containing include, asminc, lib, and cfg for complete dependency locking."));
        }
        return new(1, checks.All(check => check.Passed), checks);
    }

    public static DevelopmentEnvironmentLock CreateLock(string profilePath, string lockPath, CancellationToken cancellationToken = default)
    {
        string profileFile = Path.GetFullPath(profilePath);
        string destination = Path.GetFullPath(lockPath);
        DevelopmentEnvironmentProfile profile = DevelopmentEnvironmentProfile.Load(profileFile);
        ImageTransactions.EnsureDistinctPaths(profileFile, destination);
        foreach (string root in Roots(profile))
            if (IsWithin(destination, root)) throw new DiskException("setup.lock_location", "Keep the lockfile outside the ROM and toolchain directories it fingerprints.", 2);
        DevelopmentEnvironmentLock result = Snapshot(profileFile, profile, cancellationToken);
        if (result.Files.Any(file => PathsEqual(file.Path, destination)))
            throw new DiskException("setup.lock_location", "The lockfile cannot replace an environment input.", 2);
        string json = JsonSerializer.Serialize(result, JsonOptions) + "\n";
        ImageTransactions.Create(destination, false, temporary => File.WriteAllText(temporary, json, new UTF8Encoding(false)),
            temporary => _ = ReadJson<DevelopmentEnvironmentLock>(temporary), cancellationToken);
        return result;
    }

    public static void ValidateLock(string profilePath, string lockPath, CancellationToken cancellationToken = default)
        => _ = ValidateLockedProfile(profilePath, lockPath, cancellationToken);

    public static void ValidateExecutionLock(ExecutionSpec spec, CancellationToken cancellationToken = default)
    {
        if (spec.ToolchainLock is null) return;
        if (spec.Environment is null) throw new DiskException("setup.invalid_lock", "A toolchain lock requires an environment profile.", 2);
        DevelopmentEnvironmentProfile profile = ValidateLockedProfile(spec.Environment, spec.ToolchainLock, cancellationToken);
        if (!PathsEqual(spec.EmulatorPath, profile.MamePath) || !PathsEqual(spec.RomDirectory, profile.RomDirectory))
            throw new DiskException("setup.lock_scope", "The execution's MAME executable or ROM directory differs from its locked profile.", 2);
    }

    public static void ValidateProjectLock(ProjectManifest manifest, CancellationToken cancellationToken = default)
    {
        if (manifest.ToolchainLock is null) return;
        if (manifest.Environment is null) throw new DiskException("setup.invalid_lock", "A toolchain lock requires an environment profile.", 2);
        DevelopmentEnvironmentProfile profile = ValidateLockedProfile(manifest.Environment, manifest.ToolchainLock, cancellationToken);
        if (manifest.Disk.Template is { } template && (profile.TemplateImage is null || !PathsEqual(template, profile.TemplateImage))
            || manifest.Cc65 is { } compiler && (profile.Cc65Path is null || !PathsEqual(compiler.Compiler, profile.Cc65Path)
                || compiler.ToolchainRoot is null || profile.Cc65Root is null || !PathsEqual(compiler.ToolchainRoot, profile.Cc65Root)))
            throw new DiskException("setup.lock_scope", "The project's template or compiler distribution differs from its locked profile.", 2);
    }

    private static DevelopmentEnvironmentProfile ValidateLockedProfile(string profilePath, string lockPath, CancellationToken cancellationToken)
    {
        DevelopmentEnvironmentLock expected = ReadJson<DevelopmentEnvironmentLock>(lockPath);
        if (expected.SchemaVersion != 1 || string.IsNullOrWhiteSpace(expected.ProfilePath) || expected.Files is null || expected.Files.Count > MaximumFiles
            || !PathsEqual(expected.ProfilePath, Path.GetFullPath(profilePath)))
            throw new DiskException("setup.invalid_lock", "The lockfile does not identify this environment profile.", 2);
        DevelopmentEnvironmentProfile profile = DevelopmentEnvironmentProfile.Load(profilePath);
        if (profile.InputSha256 != expected.ProfileSha256)
            throw new DiskException("setup.lock_changed", "The environment profile changed after it was locked.", 6);
        DevelopmentEnvironmentLock actual = Snapshot(Path.GetFullPath(profilePath), profile, cancellationToken);
        if (actual.ProfileSha256 != expected.ProfileSha256 || !actual.Files.SequenceEqual(expected.Files))
            throw new DiskException("setup.lock_changed", "Environment files or directory membership changed. Review the changes and create a new lock before building or running.", 6);
        return profile;
    }

    private static DevelopmentEnvironmentLock Snapshot(string profilePath, DevelopmentEnvironmentProfile profile, CancellationToken cancellationToken)
    {
        List<EnvironmentFingerprint> files = [];
        HashSet<string> seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long bytes = 0;
        Add("mame", profile.MamePath);
        AddDirectory("rom", profile.RomDirectory);
        if (profile.TemplateImage is { } template) Add("template", template);
        if (profile.Cc65Path is { } compiler)
        {
            Add("cc65", compiler);
            if (profile.Cc65Root is null) throw new DiskException("setup.cc65_root", "A cc65 lock requires cc65Root for the complete compiler distribution.", 2);
        }
        if (profile.Cc65Root is { } distribution)
        {
            RequireDirectory(distribution);
            foreach (string part in new[] { "bin", "include", "asminc", "lib", "cfg" })
            {
                string folder = Path.Combine(distribution, part);
                if (part == "bin" && !Directory.Exists(folder)) continue;
                AddDirectory("cc65-" + part, folder);
            }
        }
        if (HashFile(profilePath, cancellationToken) != profile.InputSha256)
            throw new DiskException("setup.lock_changed", "The environment profile changed while its inputs were being fingerprinted.", 6);
        return new(1, profilePath, profile.InputSha256, files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());

        void Add(string role, string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireRegularFile(path);
            string full = Path.GetFullPath(path);
            if (!seen.Add(full)) return;
            FileInfo info = new(full);
            if (files.Count >= MaximumFiles || info.Length > MaximumFileBytes || (bytes += info.Length) > MaximumTotalBytes)
                throw new DiskException("setup.lock_limit", "Environment locking is limited to 16384 files, 2 GiB per file, and 8 GiB total; use dedicated tool and ROM directories.", 2);
            files.Add(new(role, full, info.Length, HashFile(full, cancellationToken)));
        }
        void AddDirectory(string role, string path)
        {
            RequireDirectory(path);
            Stack<string> pending = new();
            pending.Push(Path.GetFullPath(path));
            int directories = 0;
            while (pending.TryPop(out string? directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++directories > MaximumFiles) throw new DiskException("setup.lock_limit", "Environment directory enumeration exceeds 16384 directories.", 2);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                        throw new DiskException("setup.link", "Environment locks require physical files and directories; links are not followed: " + entry, 2);
                    if (Directory.Exists(entry)) pending.Push(entry); else Add(role, entry);
                }
            }
        }
    }

    private static IEnumerable<string> Roots(DevelopmentEnvironmentProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.RomDirectory)) yield return profile.RomDirectory;
        if (profile.Cc65Root is not null) yield return profile.Cc65Root;
    }

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        RequireRegularFile(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        int count;
        long bytes = 0;
        while ((count = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((bytes += count) > MaximumFileBytes) throw new DiskException("setup.lock_limit", "Environment file exceeded 2 GiB while hashing.", 2);
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void RequireRegularFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new DiskException("setup.missing_file", "Environment file is missing: " + path, 2);
        RejectLinks(path);
        HostFiles.EnsureRegularFile(path);
    }

    private static void RequireDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) throw new DiskException("setup.missing_directory", "Environment directory is missing: " + path, 2);
        RejectLinks(path);
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new DiskException("setup.link", "Environment locks require physical paths; resolve the link explicitly: " + current, 2);
    }

    private static bool PathsEqual(string first, string second)
        => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
            && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string path, string root)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output)> Probe(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        using Process process = new() { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return (-1, "Could not start tool.");
            Task<string> stdout = ReadOutput(process.StandardOutput, linked.Token);
            Task<string> stderr = ReadOutput(process.StandardError, linked.Token);
            await process.WaitForExitAsync(linked.Token);
            return (process.ExitCode, ((await stdout) + "\n" + (await stderr)).Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (-1, "Tool check exceeded 30 seconds."); }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException) { return (-1, error.Message); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }
    }

    private static async Task<string> ReadOutput(StreamReader reader, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }
}
