using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;
using A2Utils.Core.Setup;

namespace A2Utils.Core.Tests;

public sealed class ExecutionEnvironmentEvidenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-environment-evidence-" + Guid.NewGuid().ToString("N"));
    public ExecutionEnvironmentEvidenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Run_LockedEnvironment_RetainsExactProfileAndLockEvidence()
    {
        ExecutionSpec spec = Spec("pass");
        byte[] profile = File.ReadAllBytes(spec.Environment!);
        byte[] locked = File.ReadAllBytes(spec.ToolchainLock!);
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("result"));
        Assert.True(result.Passed, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
        AssertEvidence(result, spec, profile, locked);
    }

    [Fact]
    public async Task Run_ProfileAndLockReplacedDuringProbe_RejectsEvenWhenNewLockIsValid()
    {
        ExecutionSpec spec = Spec("environment-change");
        byte[] profile = File.ReadAllBytes(spec.Environment!);
        byte[] locked = File.ReadAllBytes(spec.ToolchainLock!);
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("result"));
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, item => item.Code == "execution.environment_changed");
        DevelopmentEnvironment.ValidateLock(spec.Environment!, spec.ToolchainLock!);
        AssertEvidence(result, spec, profile, locked);
        Assert.False(File.Exists(At("result/emulator.stdout.txt")));
        Assert.Equal("environment-change", File.ReadAllText(spec.DiskImage));
    }

    private void AssertEvidence(ExecutionResult result, ExecutionSpec spec, byte[] profile, byte[] locked)
    {
        ExecutionEnvironmentEvidence evidence = Assert.IsType<ExecutionEnvironmentEvidence>(result.Environment);
        Assert.Equal(spec.Environment, evidence.ProfilePath);
        Assert.Equal(spec.ToolchainLock, evidence.LockPath);
        Assert.Equal(ProgramFiles.Hash(profile), evidence.ProfileSha256);
        Assert.Equal(ProgramFiles.Hash(locked), evidence.LockSha256);
        Assert.Equal(At("result/environment.json"), evidence.ProfileArtifact);
        Assert.Equal(At("result/toolchain-lock.json"), evidence.LockArtifact);
        Assert.Equal(profile, File.ReadAllBytes(evidence.ProfileArtifact));
        Assert.Equal(locked, File.ReadAllBytes(evidence.LockArtifact!));
        Assert.Contains(evidence.ProfileArtifact, result.Artifacts);
        Assert.Contains(evidence.LockArtifact!, result.Artifacts);
    }

    private ExecutionSpec Spec(string mode)
    {
        Directory.CreateDirectory(At("roms"));
        File.WriteAllText(At("roms/test.rom"), "contract ROM fingerprint");
        File.WriteAllText(At("input.dsk"), mode);
        DevelopmentEnvironmentProfile profile = new() { MamePath = HostPath(), RomDirectory = At("roms") };
        File.WriteAllText(At("environment.json"), JsonSerializer.Serialize(profile, DevelopmentEnvironment.JsonOptions));
        DevelopmentEnvironment.CreateLock(At("environment.json"), At("environment.lock.json"));
        return new()
        {
            Environment = At("environment.json"),
            ToolchainLock = At("environment.lock.json"),
            EmulatorPath = profile.MamePath,
            RomDirectory = profile.RomDirectory,
            Machine = profile.Machine,
            DiskImage = At("input.dsk"),
            EmulatedSeconds = 3,
            HostTimeoutSeconds = 30
        };
    }

    private static string HostPath()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(root.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(path), "Build the execution process-contract helper: " + path);
        return path;
    }

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, true);
}
