using System.Diagnostics;
using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class MultiDiskExecutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-multidisk-" + Guid.NewGuid().ToString("N"));

    public MultiDiskExecutionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Run_TwoDisksAndSavedFileAssertions_VerifiesCopiesAndPreservesBothInputs()
    {
        ExecutionSpec spec = Spec("save-file") with
        {
            DiskAssertions =
            [
                new("flop2", "SAVE", Hex: Convert.ToHexString("AFTER!"u8), Type: "BIN", AuxType: 8192, Length: 6),
                new("flop2", "UNCHANGED", Sha256: ProgramFiles.Hash("KEEP"u8)),
                new("flop2", "ABSENT", Exists: false)
            ]
        };
        Dictionary<string, string> originals = HashInputs(spec);
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(2, result.Disks.Count);
        Assert.Equal(originals, HashInputs(spec));
        foreach (ExecutionDiskResult disk in result.Disks)
        {
            Assert.Equal(originals[disk.Device], disk.InputSha256);
            Assert.Equal(ProgramFiles.Hash(File.ReadAllBytes(disk.ArtifactPath)), disk.OutputSha256);
            Assert.NotEqual(disk.InputSha256, disk.OutputSha256);
            Assert.Contains(disk.ArtifactPath, result.Artifacts);
        }
        using DiskSession saved = DiskSession.Open(result.Disks.Single(disk => disk.Device == "flop2").ArtifactPath);
        Assert.Equal("AFTER!"u8.ToArray(), saved.ReadFile("SAVE"));
        using JsonDocument command = JsonDocument.Parse(File.ReadAllText(At("run/command.json")));
        string[] arguments = command.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(At("run/disk-flop1.dsk"), arguments[Array.IndexOf(arguments, "-flop1") + 1]);
        Assert.Equal(At("run/disk-flop2.po"), arguments[Array.IndexOf(arguments, "-flop2") + 1]);
        Assert.All(spec.Disks, disk => Assert.DoesNotContain(disk.Image, arguments));
    }

    [Fact]
    public async Task Run_SavedPayloadAndMetadataMismatch_ReportsFailuresDespitePassingMachineObservations()
    {
        ExecutionSpec spec = Spec("save-file") with
        {
            TextContains = ["HELLO APPLE II"],
            DiskAssertions = [new("flop2", "SAVE", Sha256: ProgramFiles.Hash("BEFORE"u8), Type: "TXT", AuxType: 0, Length: 7),
                new("flop2", "MISSING"), new("flop2", "UNCHANGED", Exists: false)]
        };
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));
        Assert.False(result.Passed);
        Assert.Equal("emulated_limit", result.StopReason);
        Assert.Equal(6, result.Diagnostics.Count(diagnostic => diagnostic.Code == "execution.disk_assertion"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Expected == ProgramFiles.Hash("BEFORE"u8)
            && diagnostic.Actual == ProgramFiles.Hash("AFTER!"u8));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Expected == "present" && diagnostic.Actual == "absent");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Expected == "absent" && diagnostic.Actual == "present");
    }

    [Fact]
    public async Task Run_CorruptedSavedDisk_FailsVerificationAndRetainsEvidence()
    {
        ExecutionSpec spec = Spec("corrupt-disk");
        Dictionary<string, string> originals = HashInputs(spec);
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "execution.disk_assertion" && diagnostic.Symbol == "flop2");
        Assert.Equal(originals, HashInputs(spec));
        Assert.Equal(ProgramFiles.Hash([0]), result.Disks.Single(disk => disk.Device == "flop2").OutputSha256);
    }

    [Fact]
    public async Task Run_SecondDiskHashMismatch_RefusesLaunchAndPreservesInputs()
    {
        ExecutionSpec original = Spec("save-file");
        Dictionary<string, string> originals = HashInputs(original);
        ExecutionSpec spec = original with
        {
            Disks = [original.Disks[0], original.Disks[1] with { ExpectedSha256 = new string('0', 64) }]
        };
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));
        Assert.False(result.Passed);
        Assert.Equal("disk_hash_mismatch", result.StopReason);
        Assert.Null(result.EmulatorVersion);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "execution.disk_hash_mismatch"
            && diagnostic.Actual == originals["flop2"]);
        Assert.False(File.Exists(At("run/version.stdout.txt")));
        Assert.Equal(originals, HashInputs(spec));
    }

    [Theory]
    [InlineData("failure", "emulator_error")]
    [InlineData("hang", "host_timeout")]
    public async Task Run_EmulatorFailureOrTimeout_PreservesEveryInput(string mode, string reason)
    {
        ExecutionSpec spec = Spec(mode) with { HostTimeoutSeconds = mode == "hang" ? 3 : 30 };
        Dictionary<string, string> originals = HashInputs(spec);
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));
        Assert.False(result.Passed);
        Assert.Equal(reason, result.StopReason);
        Assert.Equal(originals, HashInputs(spec));
        Assert.All(result.Disks, disk => Assert.Equal(disk.InputSha256, disk.OutputSha256));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "execution.disk_assertion");
        if (mode == "hang") AssertChildExited(At("run/child.pid"));
    }

    [Fact]
    public async Task Run_Cancellation_PreservesEveryInputAndReturnsHashes()
    {
        ExecutionSpec spec = Spec("cancel");
        Dictionary<string, string> originals = HashInputs(spec);
        using CancellationTokenSource cancellation = new();
        Task<ExecutionResult> pending = ExecutionRunner.RunAsync(spec, At("run"), cancellation.Token);
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!File.Exists(At("run/child.pid")) && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        cancellation.Cancel();
        ExecutionResult result = await pending;
        Assert.Equal("cancelled", result.StopReason);
        Assert.Equal(originals, HashInputs(spec));
        Assert.All(result.Disks, disk => Assert.Equal(disk.InputSha256, disk.OutputSha256));
        AssertChildExited(At("run/child.pid"));
    }

    [Fact]
    public async Task Run_MissingSecondDisk_RejectsBeforeCreatingArtifacts()
    {
        ExecutionSpec original = Spec("pass");
        ExecutionSpec spec = original with { Disks = [original.Disks[0], original.Disks[1] with { Image = At("missing.po") }] };
        DiskException error = await Assert.ThrowsAsync<DiskException>(() => ExecutionRunner.RunAsync(spec, At("run")));
        Assert.Equal("execution.disk_missing", error.Code);
        Assert.False(Directory.Exists(At("run")));
    }

    [Theory]
    [InlineData("duplicate-device")]
    [InlineData("duplicate-path")]
    [InlineData("legacy-and-mounts")]
    [InlineData("unknown-assertion-device")]
    [InlineData("duplicate-assertion")]
    [InlineData("absent-with-payload")]
    [InlineData("invalid-hash")]
    [InlineData("invalid-type")]
    [InlineData("invalid-hex")]
    [InlineData("empty-path")]
    [InlineData("long-path")]
    [InlineData("control-path")]
    public async Task Run_InvalidMountsOrAssertions_RejectsBeforeCreatingArtifacts(string condition)
    {
        ExecutionSpec spec = Spec("pass");
        spec = condition switch
        {
            "duplicate-device" => spec with { Disks = [spec.Disks[0], spec.Disks[1] with { Device = "flop1" }] },
            "duplicate-path" => spec with { Disks = [spec.Disks[0], spec.Disks[1] with { Image = spec.Disks[0].Image }] },
            "legacy-and-mounts" => spec with { DiskImage = spec.Disks[0].Image },
            "unknown-assertion-device" => spec with { DiskAssertions = [new("hard1", "SAVE")] },
            "duplicate-assertion" => spec with { DiskAssertions = [new("flop2", "SAVE"), new("flop2", "SAVE")] },
            "absent-with-payload" => spec with { DiskAssertions = [new("flop2", "SAVE", Exists: false, Hex: "00")] },
            "invalid-hash" => spec with { Disks = [spec.Disks[0] with { ExpectedSha256 = "bad" }, spec.Disks[1]] },
            "invalid-type" => spec with { DiskAssertions = [new("flop2", "SAVE", Type: "bad")] },
            "invalid-hex" => spec with { DiskAssertions = [new("flop2", "SAVE", Hex: "F")] },
            "long-path" => spec with { DiskAssertions = [new("flop2", new string('A', 4097))] },
            "control-path" => spec with { DiskAssertions = [new("flop2", "SAVE\n")] },
            _ => spec with { DiskAssertions = [new("flop2", "")] }
        };
        await Assert.ThrowsAsync<DiskException>(() => ExecutionRunner.RunAsync(spec, At("run")));
        Assert.False(Directory.Exists(At("run")));
    }

    [Fact]
    public void Load_MountsAndAssertions_RoundTripsAndResolvesImagePaths()
    {
        File.WriteAllText(At("spec.json"), """
            {"schemaVersion":1,"emulatorPath":"mame","romDirectory":"roms","machine":"apple2ee",
             "disks":[{"device":"flop1","image":"boot.dsk"},{"device":"flop2","image":"data.po","verify":true}],
             "diskAssertions":[{"device":"flop2","path":"SAVE","hex":"00","type":"BIN","auxType":8192,"length":1}]}
            """);
        ExecutionSpec spec = ExecutionSpec.Load(At("spec.json"));
        Assert.Equal(At("boot.dsk"), spec.Disks[0].Image);
        Assert.Equal(At("data.po"), spec.Disks[1].Image);
        Assert.True(spec.Disks[1].Verify);
        MameAdapter.Validate(spec);
        ExecutionSpec roundTrip = JsonSerializer.Deserialize<ExecutionSpec>(JsonSerializer.Serialize(spec, ExecutionSpec.JsonOptions), ExecutionSpec.JsonOptions)!;
        Assert.Equal(spec.Disks, roundTrip.Disks);
        Assert.Equal(spec.DiskAssertions, roundTrip.DiskAssertions);
    }

    [Fact]
    public void EvaluateDisks_DosBinary_ComparesLogicalPayloadWithoutDosHeader()
    {
        ExecutionSpec original = Spec("pass");
        string dos = At("data.do");
        DiskSession.Create(dos, "dos33");
        using (DiskSession session = DiskSession.Open(dos, writable: true)) session.Add("SAVE", [1, 2, 3], "B", 8192);
        ExecutionSpec spec = original with
        {
            Disks = [new("flop2", dos, Verify: true)],
            DiskAssertions = [new("flop2", "SAVE", Hex: "010203", Type: "BIN", AuxType: 8192, Length: 3)]
        };
        Assert.Empty(ExecutionRunner.EvaluateDisks(spec, new Dictionary<string, string> { ["flop2"] = dos }));
    }

    [Fact]
    public void EvaluateDisks_DosCaseDistinctNames_InspectsBothCatalogEntries()
    {
        using FixtureWorkspace workspace = new();
        string dos = workspace.CopyDisk();
        byte[] bytes = File.ReadAllBytes(dos);
        SetDosCatalogName(bytes, 0, "SAVE");
        SetDosCatalogName(bytes, 1, "save");
        File.WriteAllBytes(dos, bytes);
        ExecutionSpec spec = Spec("pass") with
        {
            Disks = [new("flop2", dos, Verify: true)],
            DiskAssertions =
            [
                new("flop2", "SAVE", Hex: "007F80FF0D", Type: "BIN", Length: 5),
                new("flop2", "save", Hex: "C1B2D5D4C9CCD38D", Type: "TXT", Length: 8)
            ]
        };

        Assert.Empty(ExecutionRunner.EvaluateDisks(spec, new Dictionary<string, string> { ["flop2"] = dos }));
        Assert.Equal(bytes, File.ReadAllBytes(dos));
    }

    [Theory]
    [InlineData("A/../FILE/")]
    [InlineData("/LEADING")]
    [InlineData("A\\B")]
    [InlineData("..")]
    [InlineData("A//B")]
    public void EvaluateDisks_DosLiteralCatalogName_DoesNotInterpretHostPathComponents(string name)
    {
        using FixtureWorkspace workspace = new();
        string dos = workspace.CopyDisk();
        byte[] bytes = File.ReadAllBytes(dos);
        SetDosCatalogName(bytes, 0, name);
        File.WriteAllBytes(dos, bytes);
        ExecutionSpec spec = Spec("pass") with
        {
            Disks = [new("flop2", dos, Verify: true)],
            DiskAssertions = [new("flop2", name, Hex: "007F80FF0D", Type: "BIN", AuxType: 8192, Length: 5)]
        };

        Assert.Empty(ExecutionRunner.EvaluateDisks(spec, new Dictionary<string, string> { ["flop2"] = dos }));
        Assert.Equal(bytes, File.ReadAllBytes(dos));
    }

    private static void SetDosCatalogName(byte[] bytes, int entry, string name)
    {
        int offset = (17 * 16 + 15) * 256 + 0x0b + entry * 35 + 3;
        bytes.AsSpan(offset, 30).Fill(0xa0);
        for (int index = 0; index < name.Length; index++) bytes[offset + index] = (byte)(name[index] | 0x80);
    }

    private ExecutionSpec Spec(string mode)
    {
        File.WriteAllText(At("boot.dsk"), mode);
        string data = At("data.po");
        DiskSession.Create(data, "prodos", order: "prodos");
        using (DiskSession session = DiskSession.Open(data, writable: true))
        {
            session.Add("SAVE", "BEFORE"u8.ToArray(), "BIN", 8192);
            session.Add("UNCHANGED", "KEEP"u8.ToArray(), "BIN", 12288);
        }
        return new()
        {
            Name = "multi-disk contract test",
            Machine = "apple2ee",
            EmulatorPath = HostPath(),
            RomDirectory = _directory,
            Disks = [new("flop1", At("boot.dsk"), ProgramFiles.Hash(File.ReadAllBytes(At("boot.dsk")))),
                new("flop2", data, ProgramFiles.Hash(File.ReadAllBytes(data)), Verify: true)],
            EmulatedSeconds = 3,
            HostTimeoutSeconds = 30
        };
    }

    private static Dictionary<string, string> HashInputs(ExecutionSpec spec)
        => spec.Disks.ToDictionary(disk => disk.Device, disk => ProgramFiles.Hash(File.ReadAllBytes(disk.Image)));

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

    private static void AssertChildExited(string pidPath)
    {
        Assert.True(File.Exists(pidPath));
        try
        {
            using Process child = Process.GetProcessById(int.Parse(File.ReadAllText(pidPath)));
            Assert.True(child.WaitForExit(5000));
        }
        catch (ArgumentException) { }
    }

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
