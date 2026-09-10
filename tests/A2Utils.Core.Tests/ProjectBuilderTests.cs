using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Graphics;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectBuilderTests
{
    [Theory]
    [InlineData("dos33")]
    [InlineData("prodos")]
    public void Build_MixedSources_PreservesInferredAddressesAndDeterministicBytes(string fs)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nstart: LDA #$2A\nRTS\n");
        File.WriteAllText(workspace.NewPath("hello.bas"), "10 PRINT \"HELLO\"\n20 END\n");
        File.WriteAllText(workspace.NewPath("readme.txt"), "A2 build\n");
        ProjectManifest manifest = new()
        {
            Disk = new() { FileSystem = fs },
            Files = [new() { Source = "main.asm", Path = "PROGRAM", Kind = "asm" },
                new() { Source = "hello.bas", Path = "HELLO", Kind = "basic" },
                new() { Source = "readme.txt", Path = "README", Kind = "text" }]
        };
        string project = Save(workspace, manifest);
        ProjectBuildResult first = ProjectBuilder.Build(project, workspace.NewPath("first.po"));
        ProjectBuildResult second = ProjectBuilder.Build(project, workspace.NewPath("second.po"));
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(File.ReadAllBytes(first.OutputPath), File.ReadAllBytes(second.OutputPath));
        using DiskSession disk = DiskSession.Open(first.OutputPath);
        Assert.Equal(0x2000, disk.GetEntry("PROGRAM").AuxType);
        Assert.Equal(new byte[] { 0xa9, 0x2a, 0x60 }, disk.ReadFile("PROGRAM"));
        Assert.Contains("HELLO", ApplesoftBasic.Decompile(disk.ReadFile("HELLO")));
        Assert.Equal(0x2000, first.Files[0].Symbols["start"]);
        if (fs == "prodos") Assert.Equal(manifest.Timestamp, disk.GetEntry("PROGRAM").Modified);
    }

    [Fact]
    public void Build_DeclaredLoadAddressDisagrees_RefusesAndPreservesOutput()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS");
        ProjectManifest manifest = new() { Files = [new() { Source = "main.asm", Path = "PROGRAM", Kind = "asm", AuxType = 0x3000 }] };
        string output = workspace.NewPath("output.po");
        File.WriteAllBytes(output, [1, 2, 3]);
        Assert.Equal("project.metadata_conflict", Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, manifest), output, true)).Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_RawAuxAddressParticipatesInMemoryCheck_RejectsOverlap()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("one.bin"), [1, 2, 3]);
        ProjectManifest manifest = new()
        {
            Files = [new() { Source = "one.bin", Path = "ONE", AuxType = 0x2000 },
            new() { Source = "one.bin", Path = "TWO", Origin = 0x2002 }]
        };
        DiskException error = Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, manifest)));
        Assert.Equal("project.memory_overlap", error.Code);
        Assert.Equal("TWO", Assert.Single(error.Diagnostics).Symbol);
    }

    [Fact]
    public void Build_CustomReserveNamedLikeDisplayPage_StillRejectsLoresOverlap()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("screen.png"), PngCodec.Encode(new(40, 48, new byte[40 * 48 * 3])));
        string project = Save(workspace, new()
        {
            Files = [new() { Source = "screen.png", Path = "SCREEN", Kind = "lores", Origin = 8192 }],
            Reserve = [new("display page 1", 8192, 1024)]
        });
        Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, checkOnly: true)).Code);
    }

    [Fact]
    public void Build_ImportedBasic_AppliesWorkspaceReservationAndInfersAuxAddress()
    {
        using FixtureWorkspace workspace = new();
        byte[] basic = ApplesoftBasic.Compile("10 END");
        File.WriteAllBytes(workspace.NewPath("basic.bin"), basic);
        File.WriteAllBytes(workspace.NewPath("routine.bin"), [0x60]);
        string project = Save(workspace, new()
        {
            BasicWorkspaceBytes = 1024,
            Files = [new() { Source = "basic.bin", Path = "BASIC", Type = "BAS", AuxType = 0x0801 },
                new() { Source = "routine.bin", Path = "ROUTINE", Origin = (ushort)(0x0801 + basic.Length) }]
        });
        Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, checkOnly: true)).Code);
    }

    [Theory]
    [InlineData("0x6", 6)]
    [InlineData("INT", 250)]
    [InlineData("NON", 0)]
    [InlineData("REL", 254)]
    public void Build_FileTypeAliases_UseAdapterMetadataRules(string type, int expectedType)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        ProjectBuildResult result = ProjectBuilder.Build(Save(workspace, new()
        { Files = [new() { Source = "data.bin", Path = "DATA", Type = type, AuxType = 0x2000 }] }));
        using DiskSession disk = DiskSession.Open(result.OutputPath);
        Assert.Equal(expectedType, disk.GetEntry("DATA").FileType);
        Assert.Equal(new byte[] { 1, 2, 3 }, disk.ReadFile("DATA"));
        if (expectedType == 6) Assert.Equal(0x2000, result.Files[0].Origin);
    }

    [Fact]
    public void Build_AppleSingleAuxConflict_RefusesAndPreservesPreviousOutput()
    {
        using FixtureWorkspace workspace = new();
        byte[] program = Convert.FromHexString(
            "0005160000020000000000000000000000000000000000000002" +
            "0000000B0000003200000008000000010000003A00000003" +
            "00C3000400000000A92A60");
        File.WriteAllBytes(workspace.NewPath("data.as"), program);
        string project = Save(workspace, new()
        { Files = [new() { Source = "data.as", Path = "DATA", Kind = "applesingle", AuxType = 42 }] });
        string output = workspace.NewPath("output.po");
        File.WriteAllBytes(output, [0xa2]);
        Assert.Equal("project.metadata_conflict", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, output, true)).Code);
        Assert.Equal(new byte[] { 0xa2 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_BasicMissingTarget_ReportsSourceAndDoesNotWrite()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("bad.bas"), "10 GOTO 999");
        string project = Save(workspace, new() { Files = [new() { Source = "bad.bas", Path = "BAD", Kind = "basic" }] });
        DiskException error = Assert.Throws<DiskException>(() => ProjectBuilder.Build(project));
        Assert.Equal("project.basic_check", error.Code);
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.BasicLine == 10 && diagnostic.File == workspace.NewPath("bad.bas"));
        Assert.False(File.Exists(workspace.NewPath("build.po")));
    }

    [Fact]
    public void Build_TemplateAddition_PreservesInputAndUnrelatedLockedPayload()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(template);
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $3000\nRTS");
        string project = Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template), TemplateSha256 = ProgramFiles.Hash(original) },
            Files = [new() { Source = "main.asm", Path = "NEW", Kind = "asm" }]
        });
        ProjectBuildResult result = ProjectBuilder.Build(project);
        Assert.Equal(original, File.ReadAllBytes(template));
        using DiskSession input = DiskSession.Open(template);
        using DiskSession output = DiskSession.Open(result.OutputPath);
        Assert.Equal(input.ReadFile("HELLO.BIN", raw: true), output.ReadFile("HELLO.BIN", raw: true));
        Assert.True(output.GetEntry("HELLO.BIN").IsLocked);
        Assert.Equal("template-preserved-unverified", result.Bootability);
    }

    [Fact]
    public void Build_FullDiskFailure_PreservesTemplateAndPreviousOutput()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(template);
        File.WriteAllBytes(workspace.NewPath("large.bin"), Enumerable.Repeat((byte)0x55, 30000).ToArray());
        ProjectManifest manifest = new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = Enumerable.Range(0, 5).Select(index => new ProjectFile
            { Source = "large.bin", Path = "FILE" + index, Origin = 0x2000, Resident = false }).ToList()
        };
        string output = workspace.NewPath("output.do");
        File.WriteAllBytes(output, [0xa2, 0xff]);
        Assert.Throws<DiskException>(() => ProjectBuilder.Build(Save(workspace, manifest), output, true));
        Assert.Equal(original, File.ReadAllBytes(template));
        Assert.Equal(new byte[] { 0xa2, 0xff }, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_ExistingLockedEntry_RefusesReplacementWithoutChangingTemplate(bool replace)
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(template);
        File.WriteAllBytes(workspace.NewPath("new.bin"), [0x60]);
        string project = Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = [new() { Source = "new.bin", Path = "HELLO.BIN", Origin = 0x3000, Replace = replace }]
        });
        Assert.Throws<DiskException>(() => ProjectBuilder.Build(project));
        Assert.Equal(original, File.ReadAllBytes(template));
    }

    [Fact]
    public void Build_ChangedFileType_StoresCorrectNewDosHeader()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        File.WriteAllBytes(workspace.NewPath("new.bin"), [0x60]);
        string project = Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = [new() { Source = "new.bin", Path = "README", Origin = 0x3000, Replace = true }]
        });
        ProjectBuildResult result = ProjectBuilder.Build(project);
        using DiskSession disk = DiskSession.Open(result.OutputPath);
        Assert.Equal(new byte[] { 0x60 }, disk.ReadFile("README"));
        Assert.Equal(0x3000, disk.GetEntry("README").AuxType);
        Assert.Equal(6, disk.GetEntry("README").FileType);
    }

    [Fact]
    public void Build_Subdirectories_ProducesIdenticalImagesAndDates()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS");
        string project = Save(workspace, new() { Files = [new() { Source = "main.asm", Path = "SUB/DEEP/PROG", Kind = "asm" }] });
        ProjectBuildResult first = ProjectBuilder.Build(project, workspace.NewPath("a.po"));
        ProjectBuildResult second = ProjectBuilder.Build(project, workspace.NewPath("b.po"));
        Assert.Equal(first.Sha256, second.Sha256);
        using DiskSession disk = DiskSession.Open(first.OutputPath);
        Assert.Equal(new DateTime(2000, 1, 1), disk.GetEntry("SUB/DEEP").Created);
        Assert.DoesNotContain(disk.Verify(), diagnostic => diagnostic.Severity == "error");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"files\":null}")]
    [InlineData("{\"schemaVersion\":1,\"typo\":42}")]
    public void Build_InvalidSchema_ReportsUsageError(string json)
    {
        using FixtureWorkspace workspace = new();
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, json);
        Assert.Equal(2, Assert.Throws<DiskException>(() => ProjectBuilder.Build(project)).ExitCode);
    }

    [Theory]
    [InlineData("{\"name\":\"buffer\",\"start\":8192}")]
    [InlineData("{\"name\":\"buffer\",\"length\":512}")]
    [InlineData("{\"start\":8192,\"length\":512}")]
    [InlineData("{\"name\":null,\"start\":8192,\"length\":512}")]
    public void Build_IncompleteMemoryReservation_RefusesInsteadOfIgnoringIt(string reservation)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS");
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, "{\"schemaVersion\":1,\"files\":[{\"source\":\"main.asm\",\"path\":\"MAIN\",\"kind\":\"asm\"}],\"reserve\":[" + reservation + "]}");
        Assert.Equal("project.schema", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, checkOnly: true)).Code);
        Assert.False(File.Exists(workspace.NewPath("build.po")));
    }

    [Fact]
    public void Build_SymbolicBasic_StoresPreparedProgramAndPhysicalSourceMap()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("hello.bas"), "@start: PRINT \"HELLO\"\nGOTO @done\n@done:\nEND\n");
        ProjectBuildResult result = ProjectBuilder.Build(Save(workspace, new()
        { Files = [new() { Source = "hello.bas", Path = "HELLO", Kind = "basic-labels" }] }));
        using DiskSession disk = DiskSession.Open(result.OutputPath);
        Assert.Contains("20 GOTO 30", ApplesoftBasic.Decompile(disk.ReadFile("HELLO")));
        IReadOnlyList<BasicPreparedLine> mapping = Assert.IsAssignableFrom<IReadOnlyList<BasicPreparedLine>>(result.Files[0].SourceMap);
        Assert.Equal(4, mapping[2].SourceLine);
        Assert.Equal("done", Assert.Single(mapping[2].Labels));
    }

    [Fact]
    public void Build_Startup_GeneratesLauncherAndMatchesProgramCaseInsensitively()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(template);
        File.WriteAllText(workspace.NewPath("hello.bas"), "10 END");
        ProjectBuildResult result = ProjectBuilder.Build(Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = [new() { Source = "hello.bas", Path = "DEMO", Kind = "basic" }],
            Startup = new() { Program = "demo" }
        }));
        Assert.Equal(original, File.ReadAllBytes(template));
        using DiskSession disk = DiskSession.Open(result.OutputPath);
        Assert.Contains("RUN DEMO", ApplesoftBasic.Decompile(disk.ReadFile("HELLO")));
    }

    [Theory]
    [InlineData(false, "project.startup_origin")]
    [InlineData(true, "project.duplicate_path")]
    public void Build_InvalidStartup_ReportsSpecificErrorAndPreservesOutput(bool duplicate, string code)
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        File.WriteAllText(workspace.NewPath("hello.bas"), "10 END");
        ProjectFile program = new() { Source = "hello.bas", Path = "DEMO", Kind = "basic", Origin = 0x3000 };
        string project = Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = duplicate ? [program, program] : [program],
            Startup = new() { Program = "DEMO" }
        });
        string output = workspace.NewPath("output.do");
        File.WriteAllBytes(output, [0xa2]);
        Assert.Equal(code, Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, output, true)).Code);
        Assert.Equal(new byte[] { 0xa2 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_CheckOnly_ReturnsMetadataWithoutWritingImage()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS");
        ProjectBuildResult result = ProjectBuilder.Build(Save(workspace, new()
        { Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }] }), checkOnly: true);
        Assert.Equal(1, Assert.Single(result.Files).Length);
        Assert.False(File.Exists(result.OutputPath));
    }

    [Fact]
    public void Build_OutputAliasesIncludedSource_RefusesBeforeReplacement()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\n.include \"part.inc\"");
        string include = workspace.NewPath("part.inc");
        File.WriteAllText(include, "RTS");
        string project = Save(workspace, new() { Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }] });
        Assert.Equal("write.source_alias", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, include, true)).Code);
        Assert.Equal("RTS", File.ReadAllText(include));
    }

    [Fact]
    public void Build_IncompatibleCpu_RefusesTarget()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nSTZ $10");
        string project = Save(workspace, new() { Cpu = "65c02", Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }] });
        Assert.Equal("project.cpu_target", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project)).Code);
    }

    private static string Save(FixtureWorkspace workspace, ProjectManifest manifest)
    {
        string path = workspace.NewPath("project.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, ProjectJson.Options));
        return path;
    }
}
