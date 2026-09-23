// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class BlockStorageExecutionTests
{
    [Theory]
    [InlineData("apple2e", "cffa202")]
    [InlineData("apple2ee", "cffa2")]
    public void Arguments_CffaProfile_SelectsCpuCompatibleSlotSevenFirmware(string machine, string card)
    {
        ExecutionSpec spec = Spec("volume.po") with { Machine = machine };
        var args = MameAdapter.CreateArguments(spec, "copy.hdv", "run.lua", "artifacts").ToArray();
        Assert.Equal(card, args[Array.IndexOf(args, "-sl7") + 1]);
        Assert.Equal(Path.GetFullPath("copy.hdv"), args[Array.IndexOf(args, "-hard1") + 1]);
        Assert.DoesNotContain("-flop1", args);
    }

    [Theory]
    [InlineData("apple2c")]
    [InlineData("apple2p")]
    [InlineData("apple2")]
    public void Validate_CffaWithoutSupportedIieSlot_RejectsMachine(string machine)
    {
        Assert.Equal("execution.storage_machine", Assert.Throws<DiskException>(() => MameAdapter.Validate(Spec("volume.po") with { Machine = machine })).Code);
    }

    [Fact]
    public void Validate_HardMountInFloppyProfile_RejectsDevice()
    {
        Assert.Equal("execution.invalid_spec", Assert.Throws<DiskException>(() => MameAdapter.Validate(Spec("volume.po") with { StorageProfile = "floppy" })).Code);
    }

    [Theory]
    [InlineData("dos", null)]
    [InlineData(null, "dos33")]
    public void Validate_DosHardDiskHint_RejectsBeforeStartingProcess(string? order, string? fileSystem)
    {
        Assert.Equal("execution.storage_format", Assert.Throws<DiskException>(() => MameAdapter.Validate(Spec("volume.po") with
        { DiskImage = "", Disks = [new("hard1", "volume.po", InputOrder: order, InputFileSystem: fileSystem)] })).Code);
    }

    [Theory]
    [InlineData("raw", ".hdv")]
    [InlineData("2mg", ".2mg")]
    public async Task Run_ProDosBlockVolume_UsesDisposableRecognizedCopyAndPreservesInput(string container, string extension)
    {
        using FixtureWorkspace workspace = new();
        string disk = workspace.NewPath("volume.po");
        DiskSession.Create(disk, "prodos", container: container, order: "prodos", blocks: 800);
        byte[] original = File.ReadAllBytes(disk);
        Directory.CreateDirectory(workspace.NewPath("roms"));
        ExecutionSpec spec = Spec(disk) with { RomDirectory = workspace.NewPath("roms"), TextContains = ["APPLE II"] };
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, workspace.NewPath("run"));
        Assert.True(result.Passed, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(original, File.ReadAllBytes(disk));
        string copy = workspace.NewPath("run/disk" + extension);
        Assert.Equal(original, File.ReadAllBytes(copy));
        Assert.Equal(ProgramFiles.Hash(original), result.InputSha256);
        Assert.Contains("-hard1", File.ReadAllText(workspace.NewPath("run/command.json")));
    }

    [Fact]
    public async Task Run_ProDosVolumeOnFloppy_RejectsBeforeEmulatorAndPreservesInput()
    {
        using FixtureWorkspace workspace = new();
        string disk = workspace.NewPath("volume.po");
        DiskSession.Create(disk, "prodos", order: "prodos", blocks: 800);
        byte[] original = File.ReadAllBytes(disk);
        Directory.CreateDirectory(workspace.NewPath("roms"));
        ExecutionResult result = await ExecutionRunner.RunAsync(Spec(disk) with
        { StorageProfile = "floppy", DiskDevice = "flop1", RomDirectory = workspace.NewPath("roms") }, workspace.NewPath("run"));
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "execution.storage_geometry");
        Assert.Null(result.EmulatorVersion);
        Assert.False(File.Exists(workspace.NewPath("run/version.stdout.txt")));
        Assert.Equal(original, File.ReadAllBytes(disk));
    }

    [Fact]
    public async Task Run_DosVolumeOnHardDisk_RejectsFilesystemBeforeEmulator()
    {
        using FixtureWorkspace workspace = new();
        string disk = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(disk);
        Directory.CreateDirectory(workspace.NewPath("roms"));
        ExecutionResult result = await ExecutionRunner.RunAsync(Spec(disk) with { RomDirectory = workspace.NewPath("roms") }, workspace.NewPath("run"));
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "execution.storage_format");
        Assert.Null(result.EmulatorVersion);
        Assert.Equal(original, File.ReadAllBytes(disk));
    }

    [Fact]
    public void ValidateCopy_TwoImgTrailingBytes_RefusesMameGeometryMismatch()
    {
        using FixtureWorkspace workspace = new();
        string disk = workspace.NewPath("volume.2mg");
        DiskSession.Create(disk, "prodos", container: "2mg", order: "prodos", blocks: 800);
        byte[] original = File.ReadAllBytes(disk);
        byte[] extra = [.. original, 1, 2, 3];
        Assert.Equal("execution.storage_format", Assert.Throws<DiskException>(() =>
            MameAdapter.ValidateStorageCopy(new("hard1", disk), disk, extra)).Code);
        Assert.Equal(original, File.ReadAllBytes(disk));
    }

    private static ExecutionSpec Spec(string disk) => new()
    {
        Name = "CFFA process contract",
        Machine = "apple2ee",
        StorageProfile = "cffa2",
        DiskDevice = "hard1",
        DiskImage = disk,
        EmulatorPath = HostPath(),
        RomDirectory = "roms",
        EmulatedSeconds = 4,
        HostTimeoutSeconds = 15
    };

    private static string HostPath()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(root!.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }
}
