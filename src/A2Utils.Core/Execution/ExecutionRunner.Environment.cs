using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

public sealed record ExecutionEnvironmentEvidence(string ProfilePath, string ProfileSha256, string ProfileArtifact,
    string? LockPath, string? LockSha256, string? LockArtifact);

public static partial class ExecutionRunner
{
    private static ExecutionEnvironmentEvidence? CaptureEnvironment(ExecutionSpec spec, string artifacts, CancellationToken cancellationToken)
    {
        if (spec.Environment is null) return null;
        string profilePath = Path.GetFullPath(spec.Environment);
        byte[] profile = ProgramFiles.ReadBytes(profilePath, 4 * 1024 * 1024, cancellationToken);
        string? lockPath = spec.ToolchainLock is null ? null : Path.GetFullPath(spec.ToolchainLock);
        byte[]? locked = lockPath is null ? null : ProgramFiles.ReadBytes(lockPath, 4 * 1024 * 1024, cancellationToken);
        string profileArtifact = Path.Combine(artifacts, "environment.json");
        string? lockArtifact = locked is null ? null : Path.Combine(artifacts, "toolchain-lock.json");
        using (FileStream output = new(profileArtifact, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(profile);
        if (locked is not null)
            using (FileStream output = new(lockArtifact!, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(locked);
        ExecutionEnvironmentEvidence evidence = new(profilePath, ProgramFiles.Hash(profile), profileArtifact,
            lockPath, locked is null ? null : ProgramFiles.Hash(locked), lockArtifact);
        ValidateEnvironmentEvidence(evidence, cancellationToken);
        return evidence;
    }

    private static void ValidateEnvironmentEvidence(ExecutionEnvironmentEvidence? evidence, CancellationToken cancellationToken)
    {
        if (evidence is null) return;
        if (ProgramFiles.Hash(ProgramFiles.ReadBytes(evidence.ProfilePath, 4 * 1024 * 1024, cancellationToken)) != evidence.ProfileSha256
            || evidence.LockPath is { } lockPath && ProgramFiles.Hash(ProgramFiles.ReadBytes(lockPath, 4 * 1024 * 1024, cancellationToken)) != evidence.LockSha256)
            throw new DiskException("execution.environment_changed", "The environment profile or lockfile changed after execution evidence was captured; retained artifacts identify the original inputs.", 6);
    }
}
