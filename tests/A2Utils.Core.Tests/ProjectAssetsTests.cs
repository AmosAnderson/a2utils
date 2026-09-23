// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text;
using System.Text.Json;
using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Graphics;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectAssetsTests
{
    [Fact]
    public void Compile_AtlasCells_GeneratesOffsetsAndReusableAssemblyAndCInputs()
    {
        byte[] rgb = new byte[14 * 2 * 3];
        Array.Fill(rgb, (byte)255, 0, 3);
        Array.Fill(rgb, (byte)255, (14 + 13) * 3, 3);
        ProjectAssetCompilation result = ProjectAssets.Compile(new()
        {
            Name = "HERO",
            Source = "hero.png",
            Output = "generated/hero.bin",
            AssemblyInclude = "generated/hero.inc",
            CHeader = "generated/hero.h",
            MetadataOutput = "generated/hero.json",
            CellWidth = 7,
            CellHeight = 2
        }, PngCodec.Encode(new(14, 2, rgb)));

        Assert.Equal(new byte[] { 1, 0, 0, 64 }, result.Payload);
        Assert.Equal(2, result.Constants["HERO_CELL_COUNT"]);
        Assert.Equal(2, result.Constants["HERO_CELL_1_OFFSET"]);
        Assert.Contains("#define HERO_CELL_1_OFFSET 2UL", Encoding.UTF8.GetString(result.Outputs["generated/hero.h"]));
        Assert.Contains("static const unsigned char HERO_DATA[]", Encoding.UTF8.GetString(result.Outputs["generated/hero.h"]));
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("main.asm");
        File.WriteAllText(source, ".org $6000\n.include \"generated/hero.inc\"\n.word HERO_CELL_1_OFFSET\n.incbin \"generated/hero.bin\"\n");
        var generated = result.Outputs.ToDictionary(pair => workspace.NewPath(pair.Key), pair => pair.Value);
        AssemblyResult program = Assembler.AssembleFile(source, generated);
        Assert.Equal(new byte[] { 2, 0, 1, 0, 0, 64 }, program.Bytes);
        Assert.False(File.Exists(workspace.NewPath("generated/hero.bin")));
        Assert.Equal(3, program.DependencyHashes.Count);
    }

    [Fact]
    public void Build_GeneratedAssetIncludedInAssembly_IsReproducibleAndLeavesNoIntermediateFiles()
    {
        using FixtureWorkspace workspace = new();
        string png = workspace.NewPath("atlas.png");
        File.WriteAllBytes(png, PngCodec.Encode(new(7, 1, Enumerable.Repeat((byte)255, 21).ToArray())));
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $6000\n.include \"generated/atlas.inc\"\nlda #ATLAS_LENGTH\nrts\n.incbin \"generated/atlas.bin\"\n");
        string manifest = workspace.NewPath("project.json");
        ProjectManifest project = new()
        {
            Output = "output.po",
            Assets = [new() { Name = "ATLAS", Source = "atlas.png", Output = "generated/atlas.bin", AssemblyInclude = "generated/atlas.inc", CellHeight = 1 }],
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }]
        };
        File.WriteAllText(manifest, JsonSerializer.Serialize(project, ProjectJson.Options));

        ProjectBuildResult first = ProjectBuilder.Build(manifest);
        ProjectBuildResult second = ProjectBuilder.Build(manifest, workspace.NewPath("second.po"));

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Contains(first.Inputs, input => input.Path == png);
        Assert.DoesNotContain(first.Inputs, input => input.Path.EndsWith("atlas.bin", StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(first.Assets).Outputs.Count);
        Assert.False(Directory.Exists(workspace.NewPath("generated")));
        Assert.Equal(new byte[] { 0xa9, 1, 0x60, 0x7f }, ReadDiskFile(first.OutputPath, "MAIN"));
    }

    [Fact]
    public void Build_GeneratedPathCollision_PreservesSourceAndExistingDisk()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("atlas.png"), PngCodec.Encode(new(7, 1, new byte[21])));
        File.WriteAllText(workspace.NewPath("atlas.bin"), "keep source");
        File.WriteAllText(workspace.NewPath("output.po"), "keep output");
        ProjectManifest project = new()
        {
            Output = "output.po",
            Assets = [new() { Name = "A", Source = "atlas.png", Output = "atlas.bin", CellHeight = 1 }],
            Files = [new() { Source = "atlas.bin", Path = "DATA", Origin = 0x6000 }]
        };
        string manifest = workspace.NewPath("project.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(project, ProjectJson.Options));

        Assert.Throws<DiskException>(() => ProjectBuilder.Build(manifest, overwrite: true));
        Assert.Equal("keep source", File.ReadAllText(workspace.NewPath("atlas.bin")));
        Assert.Equal("keep output", File.ReadAllText(workspace.NewPath("output.po")));
    }

    [Fact]
    public void Compile_ShapesAndDhires_ExposesFormatOffsets()
    {
        ProjectAssetCompilation shape = ProjectAssets.Compile(new() { Name = "SHAPES", Source = "shapes.json", Output = "shapes.bin", Kind = "shapes" },
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"shapes\":[{\"name\":\"line\",\"commands\":[{\"direction\":\"right\"}]}]}"));
        Assert.Equal(4, shape.Constants["SHAPES_SHAPE_1_OFFSET"]);
        Assert.Equal(new byte[] { 1, 0, 4, 0, 5, 0 }, shape.Payload);
        ProjectAssetCompilation dhires = ProjectAssets.Compile(new()
        {
            Name = "SCREEN",
            Source = "screen.png",
            Output = "screen.bin",
            Kind = "dhires",
            BankOrder = "main-aux"
        }, PngCodec.Encode(new(560, 192, new byte[560 * 192 * 3])));
        Assert.Equal(16384, dhires.Payload.Length);
        Assert.Equal(0, dhires.Constants["SCREEN_MAIN_OFFSET"]);
        Assert.Equal(8192, dhires.Constants["SCREEN_AUX_OFFSET"]);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("/absolute.bin")]
    [InlineData("generated\\asset.bin")]
    [InlineData("generated//asset.bin")]
    [InlineData("asset.txt")]
    public void Compile_UnsafeOrWrongOutputPath_RejectsBeforeConversion(string path)
    {
        Assert.Throws<DiskException>(() => ProjectAssets.Compile(new() { Name = "A", Source = "a.png", Output = path }, []));
    }

    [Fact]
    public void Assemble_GeneratedIncludeOutsideSourceBoundary_Rejects()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("src/main.asm");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, ".org $6000\n.incbin \"../escape.bin\"\n");
        Assert.Throws<DiskException>(() => Assembler.AssembleFile(source,
            new Dictionary<string, byte[]> { [workspace.NewPath("escape.bin")] = [1] }));
    }

    private static byte[] ReadDiskFile(string path, string name)
    {
        using DiskSession session = DiskSession.Open(path);
        return session.ReadFile(name);
    }
}
