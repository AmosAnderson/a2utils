// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Diagnostics;
using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;
using A2Utils.Core.Setup;

namespace A2Utils.Core.Tests;

public sealed class ExecutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-execution-" + Guid.NewGuid().ToString("N"));

    public ExecutionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Run_ProcessContract_CapturesAssertionsAndProtectsInputImage()
    {
        ExecutionSpec spec = Spec("pass") with
        {
            Memory = [new(768, "2a")],
            Registers = [new("A", 42)],
            TextContains = ["APPLE II"]
        };
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("result"));
        Assert.True(result.Passed, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("0.289", result.EmulatorVersion);
        Assert.Equal("pass", File.ReadAllText(spec.DiskImage));
        Assert.Contains("modified disposable copy", File.ReadAllText(At("result/disk.dsk")));
        Assert.Equal(42, result.Registers["A"]);
        Assert.Equal("2A", result.Memory[768]);
        Assert.Equal(64, result.InputSha256!.Length);
        Assert.Contains(At("result/result.json"), result.Artifacts);
        Assert.All(result.Artifacts, path => Assert.StartsWith(At("result"), path));
    }

    [Theory]
    [InlineData("wrong-version", "version_mismatch")]
    [InlineData("failure", "emulator_error")]
    [InlineData("missing", "missing_observations")]
    [InlineData("malformed", "adapter_error")]
    [InlineData("adapter-error", "adapter_error")]
    [InlineData("wrong-rom", "rom_mismatch")]
    public async Task Run_ProcessFailure_ReportsFailureWithoutModifyingSource(string mode, string reason)
    {
        ExecutionSpec spec = Spec(mode);
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("result"));
        Assert.False(result.Passed);
        Assert.Equal(reason, result.StopReason);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal(mode, File.ReadAllText(spec.DiskImage));
    }

    [Fact]
    public async Task Run_MissingExecutable_ReportsActionableFailure()
    {
        ExecutionResult result = await ExecutionRunner.RunAsync(Spec("pass") with { EmulatorPath = At("missing-mame.exe") }, At("result"));
        Assert.False(result.Passed);
        Assert.Equal("emulator_unavailable", result.StopReason);
    }

    [Fact]
    public async Task Run_HostDeadline_KillsParentAndChildProcesses()
    {
        ExecutionResult result = await ExecutionRunner.RunAsync(Spec("hang") with { HostTimeoutSeconds = 3 }, At("result"));
        Assert.Equal("host_timeout", result.StopReason);
        Assert.False(result.Passed);
        AssertExited(At("result/parent.pid"));
        AssertExited(At("result/child.pid"));
    }

    [Fact]
    public async Task Run_Cancellation_KillsParentAndChildProcesses()
    {
        using CancellationTokenSource cancellation = new();
        Task<ExecutionResult> task = ExecutionRunner.RunAsync(Spec("cancel"), At("result"), cancellation.Token);
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!File.Exists(At("result/child.pid")) && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        cancellation.Cancel();
        ExecutionResult result = await task;
        Assert.Equal("cancelled", result.StopReason);
        AssertExited(At("result/parent.pid"));
        AssertExited(At("result/child.pid"));
    }

    [Fact]
    public async Task Run_VersionProbeHangs_HostDeadlineAppliesBeforeMachineLaunch()
    {
        ExecutionResult result = await ExecutionRunner.RunAsync(Spec("hang-version") with { HostTimeoutSeconds = 1 }, At("result"));
        Assert.Equal("host_timeout", result.StopReason);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task Run_ExistingArtifactDirectory_RefusesOverwrite()
    {
        Directory.CreateDirectory(At("result"));
        File.WriteAllText(At("result/keep.txt"), "preserve me");
        DiskException error = await Assert.ThrowsAsync<DiskException>(() => ExecutionRunner.RunAsync(Spec("pass"), At("result")));
        Assert.Equal("execution.artifacts_exist", error.Code);
        Assert.Equal("preserve me", File.ReadAllText(At("result/keep.txt")));
    }

    [Fact]
    public async Task Run_FailedAssertions_ExposesExpectedAndActualValues()
    {
        ExecutionSpec spec = Spec("pass") with
        {
            Memory = [new(768, "FF")],
            Registers = [new("A", 5)],
            TextContains = ["GOODBYE"],
            Until = new(768, 1)
        };
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("result"));
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, d => d.Code == "execution.memory_assertion" && d.Expected == "FF" && d.Actual == "2A");
        Assert.Contains(result.Diagnostics, d => d.Code == "execution.register_assertion" && d.Expected == "5" && d.Actual == "42");
        Assert.Contains(result.Diagnostics, d => d.Code == "execution.text_assertion");
        Assert.Contains(result.Diagnostics, d => d.Code == "execution.completion_timeout");
    }

    [Fact]
    public void CreateArguments_PathsWithSpaces_RemainDistinctAndUseCopiedDisk()
    {
        ExecutionSpec spec = Spec("pass");
        IReadOnlyList<string> args = MameAdapter.CreateArguments(spec, At("copy disk.dsk"), At("run script.lua"), At("artifacts path"));
        Assert.Equal(At("copy disk.dsk"), args[args.ToList().IndexOf("-flop1") + 1]);
        Assert.DoesNotContain(spec.DiskImage, args);
        Assert.Contains("-noreadconfig", args);
        Assert.Contains("-seconds_to_run", args);
        Assert.Contains("-video", args);
    }

    [Fact]
    public void CreateScript_KeyboardLuaPayload_IsEncodedAsData()
    {
        ExecutionSpec spec = Spec("pass") with { Keys = [new(1, "\"); os.execute('bad'); --\n")], Until = new(768, 42), Trace = true };
        string lua = MameAdapter.CreateScript(spec, At("result"));
        Assert.DoesNotContain("os.execute", lua);
        Assert.Contains("\\034", lua);
        Assert.Contains("emu.add_machine_frame_notifier", lua);
        Assert.Contains("machine.natkeyboard:post", lua);
        Assert.Contains("now >= deadline", lua);
        Assert.Contains("machine:exit()", lua);
        Assert.Contains("local trace_enabled = true", lua);
    }

    [Theory]
    [InlineData(0xc000, "00")]
    [InlineData(0xbfff, "0000")]
    [InlineData(65535, "0000")]
    [InlineData(-1, "00")]
    [InlineData(768, "G0")]
    public void Validate_InvalidMemoryRange_RejectsBeforeExecution(int address, string hex)
    {
        Assert.Throws<DiskException>(() => MameAdapter.Validate(Spec("pass") with { Memory = [new(address, hex)] }));
    }

    [Fact]
    public void Load_RelativePaths_AreResolvedAgainstSpecification()
    {
        File.WriteAllText(At("spec.json"), """{"schemaVersion":1,"emulatorPath":"mame.exe","romDirectory":"roms","diskImage":"disk.dsk","machine":"apple2ee"}""");
        ExecutionSpec spec = ExecutionSpec.Load(At("spec.json"));
        Assert.Equal(At("disk.dsk"), spec.DiskImage);
        Assert.Equal(At("roms"), spec.RomDirectory);
        Assert.Equal(At("mame.exe"), spec.EmulatorPath);
    }

    [Fact]
    public void Load_EnvironmentChangesAfterRead_AppliesAndReportsTheSameBytes()
    {
        string environment = At("environment.json");
        File.WriteAllText(environment, JsonSerializer.Serialize(new DevelopmentEnvironmentProfile
        {
            Machine = "apple2ee",
            MamePath = "old-mame",
            RomDirectory = "old-roms"
        }, DevelopmentEnvironment.JsonOptions));
        File.WriteAllText(At("spec.json"),
            """{"schemaVersion":1,"environment":"environment.json","diskImage":"disk.dsk"}""");
        string? appliedHash = null;

        ExecutionSpec spec = ExecutionSpec.Load(At("spec.json"), (path, hash) =>
        {
            if (path != environment) return;
            appliedHash = hash;
            File.WriteAllText(environment, JsonSerializer.Serialize(new DevelopmentEnvironmentProfile
            {
                Machine = "apple2p",
                MamePath = "new-mame",
                RomDirectory = "new-roms"
            }, DevelopmentEnvironment.JsonOptions));
        });

        Assert.Equal("apple2ee", spec.Machine);
        Assert.Equal(At("old-mame"), spec.EmulatorPath);
        Assert.NotNull(appliedHash);
        Assert.NotEqual(appliedHash, ProgramFiles.Hash(File.ReadAllBytes(environment)));
    }

    [Fact]
    public void Load_UnknownProperty_RejectsMisspelledAssertions()
    {
        File.WriteAllText(At("spec.json"), """{"textContain":["PASS"]}""");
        Assert.Throws<DiskException>(() => ExecutionSpec.Load(At("spec.json")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"memory\":[{\"hex\":\"FF\"}]}")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    public void Load_MissingRequiredOrDuplicateProperties_RejectsAmbiguousInput(string json)
    {
        File.WriteAllText(At("spec.json"), json);
        Assert.Throws<DiskException>(() => ExecutionSpec.Load(At("spec.json")));
    }

    private ExecutionSpec Spec(string mode)
    {
        File.WriteAllText(At("input.dsk"), mode);
        return new()
        {
            Name = "contract test",
            EmulatorPath = HostPath(),
            Machine = "apple2ee",
            RomDirectory = _directory,
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
        string path = Path.Combine(root!.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(path), "Build the execution process-contract helper: " + path);
        return path;
    }

    private static void AssertExited(string pidFile)
    {
        Assert.True(File.Exists(pidFile), "The helper must have started before asserting process-tree cleanup.");
        int pid = int.Parse(File.ReadAllText(pidFile));
        try
        {
            using Process process = Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(5000), "Execution left a child process alive.");
        }
        catch (ArgumentException) { }
    }

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
