using System.Text.Json;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class RuntimeMemoryTests
{
    [Fact]
    public void Check_CompilerPayloadBssAndSavedZeroPage_UnionsPhysicalFootprintWithoutFalseSystemConflict()
    {
        ProjectFile item = new() { Path = "C.PROGRAM", Kind = "cc65" };
        BuiltFile info = Built(item, 0x2000, 16) with
        {
            RuntimeMemory = [new("CODE", 0x2000, 16), new("BSS", 0x2008, 32, "main", "bss"),
                new("ZEROPAGE", 0x80, 26, "main", "zero-page"), new("C stack", 0x8e00, 0x800, "main", "stack")]
        };
        var memory = ProjectRuntimeMemory.Check(new(), [(item, info)]);
        Assert.Equal(3, memory.Count);
        Assert.Equal(40, memory.Single(region => region.Start == 0x2000).Length);
        Assert.Equal(40 + 26 + 0x800, memory.Sum(region => region.Length));
    }

    [Theory]
    [InlineData(0x7f, 1)]
    [InlineData(0x99, 2)]
    public void Check_CompilerZeroPageOutsideSavedWorkspace_PreservesSystemReservation(int start, int length)
    {
        ProjectFile item = new() { Path = "C.PROGRAM", Kind = "cc65" };
        BuiltFile info = Built(item, 0x2000, 16) with { RuntimeMemory = [new("ZEROPAGE", start, length, "main", "zero-page")] };
        Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() => ProjectRuntimeMemory.Check(new(), [(item, info)])).Code);
    }

    [Fact]
    public void Check_DeclaredZeroPageReservation_StillConflictsWithCompilerWorkspace()
    {
        ProjectFile item = new() { Path = "C.PROGRAM", Kind = "cc65" };
        BuiltFile info = Built(item, 0x2000, 16) with { RuntimeMemory = [new("ZEROPAGE", 0x80, 26, "main", "zero-page")] };
        Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() => ProjectRuntimeMemory.Check(
            new() { Reserve = [new("interrupt scratch", 0x80, 4)] }, [(item, info)])).Code);
    }

    [Theory]
    [InlineData("level", "level", true)]
    [InlineData("level", "menu", false)]
    [InlineData("level", null, false)]
    [InlineData(null, null, false)]
    public void Check_OverlayGroups_AllowOnlyNamedMutuallyExclusiveMembers(string? firstGroup, string? secondGroup, bool permitted)
    {
        ProjectFile first = new() { Path = "ONE", OverlayGroup = firstGroup };
        ProjectFile second = new() { Path = "TWO", OverlayGroup = secondGroup };
        var files = new[] { (first, Built(first, 0x2000, 16)), (second, Built(second, 0x2008, 16)) };
        if (permitted) Assert.Equal(2, ProjectRuntimeMemory.Check(new(), files).Count);
        else Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() => ProjectRuntimeMemory.Check(new(), files)).Code);
    }

    [Fact]
    public void Check_HeapCollidesWithOwnProgram_RejectsDistinctAllocation()
    {
        ProjectFile item = new() { Path = "APP", RuntimeMemory = [new("heap", 0x2008, 256, "main", "heap")] };
        BuiltFile info = Built(item, 0x2000, 32) with { RuntimeMemory = item.RuntimeMemory };
        Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() => ProjectRuntimeMemory.Check(new(), [(item, info)])).Code);
    }

    [Fact]
    public void Check_SoftwareStackCollidesWithBss_RejectsLinkerFootprint()
    {
        ProjectFile item = new() { Path = "APP", Kind = "cc65" };
        BuiltFile info = Built(item, 0x2000, 32) with
        { RuntimeMemory = [new("BSS", 0x3000, 512, "main", "bss"), new("stack", 0x3100, 512, "main", "stack")] };
        Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() => ProjectRuntimeMemory.Check(new(), [(item, info)])).Code);
    }

    [Fact]
    public void CompilerFootprint_ExportedStackAndCustomSegmentBanks_InfersBoundedRuntimeRegions()
    {
        Cc65Result compiled = new([], "test", "", "", [])
        {
            Symbols = new Dictionary<string, int> { ["__HIMEM__"] = 0x9600, ["__STACKSIZE__"] = 0x800 },
            Segments = [new("CODE", 0x2000, 16, "data"), new("AUXBSS", 0x4000, 256, "bss"), new("EXEHDR", 0x7c9, 58, "header")]
        };
        var regions = ProjectRuntimeMemory.FromCompiler(compiled, new() { SegmentBanks = new() { ["AUXBSS"] = "aux" } }, new());
        Assert.Equal(3, regions.Count);
        Assert.Equal("aux", regions.Single(region => region.Name == "AUXBSS").MemoryBank);
        Assert.Equal(0x8e00, regions.Single(region => region.Kind == "stack").Start);
    }

    [Theory]
    [InlineData("basic-system", true)]
    [InlineData("system", false)]
    [InlineData("auto", false)]
    public void Check_ProdosRuntime_ReservesBasicSystemOnlyWhenSelected(string runtime, bool conflict)
    {
        ProjectFile item = new() { Path = "APP" };
        var files = new[] { (item, Built(item, 0xa000, 16)) };
        ProjectManifest manifest = new() { Runtime = runtime };
        if (conflict) Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() => ProjectRuntimeMemory.Check(manifest, files)).Code);
        else Assert.Single(ProjectRuntimeMemory.Check(manifest, files));
    }

    [Fact]
    public void Build_RuntimeHeapFailure_PreservesExistingImage()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("code.bin"), [0x60, 0xea]);
        string output = workspace.NewPath("old.po");
        File.WriteAllBytes(output, [4, 5, 6]);
        ProjectManifest manifest = new()
        {
            Files = [new() { Source = "code.bin", Path = "APP", Origin = 0x2000,
                RuntimeMemory = [new("heap", 0x2001, 16, "main", "heap")] }]
        };
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, JsonSerializer.Serialize(manifest, ProjectJson.Options));
        Assert.Equal("project.memory_overlap", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, output, overwrite: true)).Code);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(output));
    }

    private static BuiltFile Built(ProjectFile file, int origin, int length) => new(file.Path, file.Kind, "BIN",
        (ushort)origin, origin, origin, length, "", true, new Dictionary<string, int>(), null)
    { MemoryBank = file.MemoryBank, OverlayGroup = file.OverlayGroup };
}
