using A2Utils.Core.Programs;
using A2Utils.Core.Setup;

namespace A2Utils.Core.Execution;

public sealed record ExecutionEnvironmentEvidence(string ProfilePath, string ProfileSha256, string ProfileArtifact,
    string? LockPath, string? LockSha256, string? LockArtifact);

internal sealed class ExecutionEnvironmentSnapshot
{
    private readonly byte[] _profileBytes;
    private readonly byte[]? _lockBytes;

    internal ExecutionEnvironmentSnapshot(string profilePath, byte[] profileBytes,
        DevelopmentEnvironmentProfile profile, string? lockPath, byte[]? lockBytes,
        string? lockSha256)
    {
        ProfilePath = Path.GetFullPath(profilePath);
        _profileBytes = profileBytes.ToArray();
        Profile = profile;
        ProfileSha256 = profile.InputSha256;
        LockPath = lockPath is null ? null : Path.GetFullPath(lockPath);
        _lockBytes = lockBytes?.ToArray();
        LockSha256 = lockSha256;
    }

    internal string ProfilePath { get; }
    internal DevelopmentEnvironmentProfile Profile { get; }
    internal string ProfileSha256 { get; }
    internal string? LockPath { get; }
    internal string? LockSha256 { get; }

    internal bool IsFor(ExecutionSpec spec)
        => spec.Environment is not null && PathsEqual(ProfilePath, spec.Environment)
            && (LockPath is null && spec.ToolchainLock is null
                || LockPath is not null && spec.ToolchainLock is not null && PathsEqual(LockPath, spec.ToolchainLock));

    internal void WriteProfile(Stream output) => output.Write(_profileBytes);
    internal void WriteLock(Stream output) => output.Write(_lockBytes
        ?? throw new InvalidOperationException("The environment snapshot has no lockfile."));
    internal DevelopmentEnvironmentLock ReadToolchainLock()
        => DevelopmentEnvironment.ReadJson<DevelopmentEnvironmentLock>(LockPath
            ?? throw new InvalidOperationException("The environment snapshot has no lockfile."),
            _lockBytes ?? throw new InvalidOperationException("The environment snapshot has no lockfile."), out _);

    private static bool PathsEqual(string first, string second)
        => Path.GetFullPath(first).Equals(Path.GetFullPath(second),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public static partial class ExecutionRunner
{
    internal static ExecutionEnvironmentEvidence? CaptureEnvironment(ExecutionSpec spec, string artifacts,
        CancellationToken cancellationToken)
    {
        if (spec.Environment is null) return null;
        string profilePath = Path.GetFullPath(spec.Environment);
        string? lockPath = spec.ToolchainLock is null ? null : Path.GetFullPath(spec.ToolchainLock);
        ExecutionEnvironmentSnapshot? snapshot = spec.EnvironmentSnapshot is { } loaded && loaded.IsFor(spec)
            ? loaded : null;
        byte[]? profile = snapshot is null
            ? ProgramFiles.ReadBytes(profilePath, 4 * 1024 * 1024, cancellationToken) : null;
        byte[]? locked = snapshot is null && lockPath is not null
            ? ProgramFiles.ReadBytes(lockPath, 4 * 1024 * 1024, cancellationToken) : null;
        string profileArtifact = Path.Combine(artifacts, "environment.json");
        string? lockArtifact = lockPath is null ? null : Path.Combine(artifacts, "toolchain-lock.json");
        using (FileStream output = new(profileArtifact, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (snapshot is null) output.Write(profile!);
            else snapshot.WriteProfile(output);
        }
        if (lockArtifact is not null)
        {
            using FileStream output = new(lockArtifact, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (snapshot is null) output.Write(locked!);
            else snapshot.WriteLock(output);
        }
        ExecutionEnvironmentEvidence evidence = new(profilePath,
            snapshot?.ProfileSha256 ?? ProgramFiles.Hash(profile!), profileArtifact,
            lockPath, snapshot?.LockSha256 ?? (locked is null ? null : ProgramFiles.Hash(locked)), lockArtifact);
        ValidateEnvironmentEvidence(evidence, cancellationToken);
        return evidence;
    }

    internal static void ValidateExecutionEnvironment(ExecutionSpec spec,
        ExecutionEnvironmentEvidence? evidence, CancellationToken cancellationToken)
    {
        ExecutionEnvironmentSnapshot? snapshot = spec.EnvironmentSnapshot is { } loaded && loaded.IsFor(spec)
            ? loaded : null;
        if (evidence is not null)
            ValidateEnvironmentEvidence(evidence, cancellationToken);
        else if (snapshot is not null)
        {
            if (ProgramFiles.Hash(ProgramFiles.ReadBytes(snapshot.ProfilePath, 4 * 1024 * 1024,
                    cancellationToken)) != snapshot.ProfileSha256
                || snapshot.LockPath is { } loadedLockPath
                && ProgramFiles.Hash(ProgramFiles.ReadBytes(loadedLockPath, 4 * 1024 * 1024,
                    cancellationToken)) != snapshot.LockSha256)
                throw EnvironmentChanged();
        }
        if (spec.ToolchainLock is not null)
        {
            if (snapshot is not null)
                DevelopmentEnvironment.ValidateExecutionLock(spec, snapshot.Profile,
                    snapshot.ReadToolchainLock(), cancellationToken);
            else
                DevelopmentEnvironment.ValidateExecutionLock(spec, cancellationToken);
        }
    }

    private static void ValidateEnvironmentEvidence(ExecutionEnvironmentEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (ProgramFiles.Hash(ProgramFiles.ReadBytes(evidence.ProfilePath, 4 * 1024 * 1024,
                cancellationToken)) != evidence.ProfileSha256
            || evidence.LockPath is { } evidenceLockPath
                && ProgramFiles.Hash(ProgramFiles.ReadBytes(evidenceLockPath, 4 * 1024 * 1024,
                    cancellationToken)) != evidence.LockSha256)
            throw EnvironmentChanged();
    }

    private static DiskException EnvironmentChanged()
        => new("execution.environment_changed", "The environment profile or lockfile changed after the execution specification was loaded or evidence was captured; retained artifacts identify the original inputs.", 6);
}
