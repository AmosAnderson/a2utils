using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectBankedMemoryTests
{
    [Theory]
    [InlineData("main", "aux", 0x2000)]
    [InlineData("lc1", "lc2", 0xd000)]
    [InlineData("aux-lc1", "aux-lc2", 0xd000)]
    [InlineData("lc1", "aux-lc1", 0xe000)]
    [InlineData("lc2", "aux-lc2", 0xe000)]
    public void Build_SeparatePhysicalBanks_AllowsSameAddressesAndPreservesDiskMetadata(string first, string second, ushort origin)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        ProjectBuildResult result = ProjectBuilder.Build(Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33" },
            Files = [Payload("ONE", first, origin), Payload("TWO", second, origin)]
        }));
        Assert.Equal(new[] { first, second }, result.Memory.Select(region => region.MemoryBank));
        Assert.Equal(new[] { first, second }, result.Files.Select(file => file.MemoryBank));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "project.banked_loader");
        using DiskSession disk = DiskSession.Open(result.OutputPath);
        foreach (BuiltFile file in result.Files)
        {
            Assert.Equal(origin, disk.GetEntry(file.Path).AuxType);
            Assert.Equal(new byte[] { 1, 2, 3 }, disk.ReadFile(file.Path));
        }
    }

    [Theory]
    [InlineData("main", "main", 0x2000)]
    [InlineData("aux", "aux", 0x2000)]
    [InlineData("lc1", "lc2", 0xe000)]
    [InlineData("lc2", "lc1", 0xdfff)]
    [InlineData("aux-lc1", "aux-lc2", 0xe000)]
    [InlineData("aux-lc2", "aux-lc1", 0xdfff)]
    public void Build_SharedPhysicalBytes_RejectsOverlapAndPreservesExistingOutput(string first, string second, ushort origin)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        string output = workspace.NewPath("existing.do");
        File.WriteAllBytes(output, [9, 8, 7]);
        DiskException error = Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33" },
            Files = [Payload("ONE", first, origin), Payload("TWO", second, origin)]
        }), output, overwrite: true));
        Assert.Equal("project.memory_overlap", error.Code);
        Assert.Contains(first, Assert.Single(error.Diagnostics).Expected);
        Assert.Contains(second, error.Diagnostics[0].Actual);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_BankOneEndsAtSharedBoundary_DoesNotAliasBankTwo()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        ProjectBuildResult result = ProjectBuilder.Build(Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33" },
            Files = [Payload("ONE", "lc1", 0xdffd), Payload("TWO", "lc2", 0xdffd)],
            Reserve = [new("shared page", 0xe000, 256, "lc2")]
        }), checkOnly: true);
        Assert.Equal(2, result.Memory.Count);
    }

    [Theory]
    [InlineData("main", 0xbfff)]
    [InlineData("aux", 0xbfff)]
    [InlineData("lc1", 0xcfff)]
    [InlineData("lc2", 0x2000)]
    [InlineData("aux-lc1", 0x2000)]
    [InlineData("aux-lc2", 0xcfff)]
    public void Build_InvalidBankRange_RejectsEvenNonresidentPayloadWithMemoryChecksDisabled(string bank, ushort origin)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        DiskException error = Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, new()
        {
            CheckMemory = false,
            Files = [Payload("DATA", bank, origin) with { Resident = false }]
        }), checkOnly: true));
        Assert.Equal("project.memory_bank_range", error.Code);
    }

    [Theory]
    [InlineData("aux", 0x2000)]
    [InlineData("lc1", 0xd000)]
    [InlineData("lc2", 0xd000)]
    [InlineData("aux-lc1", 0xd000)]
    [InlineData("aux-lc2", 0xd000)]
    public void Build_BankedPayloadOnBaselineAppleIIPlus_RejectsUnsupportedHardware(string bank, ushort origin)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        Assert.Equal("project.memory_bank", Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, new()
        {
            Target = "apple2plus",
            CheckMemory = false,
            Files = [Payload("DATA", bank, origin)]
        }), checkOnly: true)).Code);
    }

    [Theory]
    [InlineData("lc1", 0xe000, "lc2", 0xe002, "project.memory_overlap")]
    [InlineData("aux-lc1", 0xe000, "aux-lc2", 0xe002, "project.memory_overlap")]
    [InlineData("main", 0x2000, "aux", 0xbfff, "project.memory_bank_range")]
    public void Build_BankedReservation_ValidatesPhysicalRangeAndSharedUpperMemory(string bank, ushort origin,
        string reservedBank, int reservedStart, string code)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        Assert.Equal(code, Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33" },
            Files = [Payload("DATA", bank, origin)],
            Reserve = [new("reserved", reservedStart, 3, reservedBank)]
        }), checkOnly: true)).Code);
    }

    [Theory]
    [InlineData("lc1", 0xd000)]
    [InlineData("lc2", 0xd000)]
    [InlineData("lc2", 0xe000)]
    public void Build_ProdosMainLanguageCard_ProtectsRuntimeUnlessOverlapChecksExplicitlyDisabled(string bank, ushort origin)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        ProjectManifest manifest = new() { Files = [Payload("DATA", bank, origin)] };
        Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() =>
            ProjectBuilder.Build(Save(workspace, manifest), checkOnly: true)).Code);
        Assert.Single(ProjectBuilder.Build(Save(workspace, manifest with { CheckMemory = false }), checkOnly: true).Files);
    }

    [Fact]
    public void Build_ProdosAuxiliaryPayload_ReportsRuntimeRamDiskRequirement()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        ProjectBuildResult result = ProjectBuilder.Build(Save(workspace, new()
        {
            Files = [Payload("DATA", "aux", 0x2000)]
        }), checkOnly: true);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "project.prodos_auxiliary_memory");
    }

    [Theory]
    [InlineData("BAS")]
    [InlineData("SYS")]
    [InlineData("TXT")]
    public void Build_BankedNonBinaryFile_RejectsUnsupportedLoadingSemantics(string type)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        Assert.Equal("project.memory_bank_payload", Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, new()
        {
            Files = [Payload("DATA", "aux", 0x2000) with { Type = type }]
        }), checkOnly: true)).Code);
    }

    [Theory]
    [InlineData(0x2000)]
    [InlineData(0xc000)]
    public void Build_EmptyReservation_DoesNotOccupyBytes(int start)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        ProjectBuildResult result = ProjectBuilder.Build(Save(workspace, new()
        {
            Files = [Payload("DATA", "main", 0x2000)],
            Reserve = [new("empty", start, 0)]
        }), checkOnly: true);
        Assert.Single(result.Memory);
    }

    [Fact]
    public void Build_EmptyReservationBeyondAddressSpace_RejectsSchemaOutOfRangeStart()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        Assert.Equal("project.memory_range", Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, new()
        {
            Files = [Payload("DATA", "main", 0x2000)],
            Reserve = [new("outside", 65536, 0)]
        }), checkOnly: true)).Code);
    }

    [Fact]
    public void Build_BankedStartup_RequiresMainMemoryLoaderAndPreservesTemplate()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] before = File.ReadAllBytes(template);
        File.WriteAllBytes(workspace.NewPath("data.bin"), [0x60]);
        Assert.Equal("project.startup_bank", Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = [Payload("DATA", "aux", 0x2000)],
            Startup = new() { Program = "DATA" }
        }))).Code);
        Assert.Equal(before, File.ReadAllBytes(template));
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("AUX")]
    [InlineData("")]
    [InlineData(null)]
    public void Build_UnknownBank_RejectsManifest(string? bank)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        Assert.Equal("project.memory_bank", Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, new()
        {
            Files = [Payload("DATA", bank!, 0x2000)]
        }), checkOnly: true)).Code);
    }

    [Fact]
    public void Build_DefaultBank_RemainsMainAndSerializesExplicitPhysicalBank()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        string path = workspace.NewPath("project.json");
        File.WriteAllText(path, """{"schemaVersion":1,"files":[{"source":"data.bin","path":"DATA","origin":8192}]}""");
        ProjectBuildResult result = ProjectBuilder.Build(path, checkOnly: true);
        Assert.Equal("main", Assert.Single(result.Files).MemoryBank);
        Assert.Equal("main", Assert.Single(result.Memory).MemoryBank);
        Assert.Contains("\"memoryBank\": \"main\"", JsonSerializer.Serialize(result, ProjectJson.Options));
    }

    [Fact]
    public void Targets_IieCapabilities_DescribePhysicalBanksAndExpansionRequirement()
    {
        TargetProfile profile = TargetProfiles.Get("apple2e");
        Assert.Equal(6, profile.MemoryBanks.Count);
        Assert.Equal(16384, profile.BankedMainMemoryBytes);
        Assert.Equal(65536, profile.AuxiliaryMemoryBytes);
        Assert.Contains("expansion", profile.AuxiliaryMemoryRequirement);
        Assert.True(profile.Supports80ColumnText);
        Assert.False(profile.SupportsMouseText);
        Assert.True(TargetProfiles.Get("apple2enh").SupportsMouseText);
        Assert.Single(TargetProfiles.Get("apple2plus").MemoryBanks);
        Assert.Equal("lc2", profile.MemoryBanks.Single(bank => bank.Name == "lc1").SharedUpperBank);
        Assert.Equal(0xe000, profile.MemoryBanks.Single(bank => bank.Name == "lc1").SharedUpperStart);
    }

    private static ProjectFile Payload(string path, string bank, ushort origin) =>
        new() { Source = "data.bin", Path = path, Origin = origin, MemoryBank = bank };

    private static string Save(FixtureWorkspace workspace, ProjectManifest manifest)
    {
        string path = workspace.NewPath("project.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, ProjectJson.Options));
        return path;
    }
}
