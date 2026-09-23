// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Execution;
using A2Utils.Core.Graphics;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectBuildCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_NewCompilerInput_InvalidatesCachedBuild(bool toolchainInput)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"), "int main(void) { return 0; }\n");
        string toolchain = workspace.NewPath(".toolchain");
        Directory.CreateDirectory(Path.Combine(toolchain, "include"));
        string project = WriteProject(workspace, new()
        {
            Cc65 = ContractCompiler() with { ToolchainRoot = toolchain },
            Files = [new() { Source = "main.c", Path = "MAIN", Kind = "cc65" }]
        });
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));
        string added = toolchainInput ? Path.Combine(toolchain, "include", "added.h")
            : workspace.NewPath("added.h");
        File.WriteAllText(added, "#define VALUE 42\n");

        ProjectBuildResult rebuilt = ProjectBuildCache.Build(project, cache, workspace.NewPath("second.po"));
        ProjectBuildResult restored = ProjectBuildCache.Build(project, cache, workspace.NewPath("third.po"));

        Assert.False(rebuilt.CacheHit);
        Assert.Contains(rebuilt.Inputs, input => input.Path == added);
        Assert.True(restored.CacheHit);
        Assert.Equal(rebuilt.Inputs, restored.Inputs);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("output")]
    public void Build_CacheHitWithGeneratedPathConflict_RefusesWithoutChangingDestination(string conflict)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllBytes(workspace.NewPath("atlas.png"), PngCodec.Encode(new(7, 1, new byte[21])));
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nrts\n");
        string project = WriteProject(workspace, new()
        {
            Assets = [new() { Name = "ATLAS", Source = "atlas.png", Output = "atlas.bin", CellHeight = 1 }],
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }]
        });
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));
        string generated = workspace.NewPath("atlas.bin");
        if (conflict == "file") File.WriteAllText(generated, "keep source");
        if (conflict == "directory") Directory.CreateDirectory(generated);
        string destination = conflict == "output" ? generated : workspace.NewPath("second.po");
        if (conflict != "output") File.WriteAllText(destination, "keep destination");

        DiskException error = Assert.Throws<DiskException>(() =>
            ProjectBuildCache.Build(project, cache, destination, overwrite: true));

        Assert.Equal(conflict == "output" ? "write.source_alias" : "project.asset_collision", error.Code);
        if (conflict == "output") Assert.False(File.Exists(destination));
        else Assert.Equal("keep destination", File.ReadAllText(destination));
        if (conflict == "file") Assert.Equal("keep source", File.ReadAllText(generated));
        if (conflict == "directory") Assert.True(Directory.Exists(generated));
    }

    [Fact]
    public void Build_UnchangedInputs_RestoresValidatedContentAddressedObject()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");

        ProjectBuildResult first = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("first.po"));
        ProjectBuildResult second = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("second.po"));

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(File.ReadAllBytes(first.OutputPath), File.ReadAllBytes(second.OutputPath));
        Assert.Contains(first.Inputs, input => input.Path == workspace.NewPath("part.inc"));
        Assert.Single(Directory.GetFiles(Path.Combine(cache, "objects"), "*.img"));
        Assert.Single(Directory.GetFiles(Path.Combine(cache, "entries"), "*.json",
            SearchOption.AllDirectories));
        using DiskSession image = DiskSession.Open(second.OutputPath, inputFs: second.FileSystem);
        Assert.False(image.Info.IsDubious);
    }

    [Fact]
    public void Build_CacheHit_RehydratesAssemblySourceMapForDebugging()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));

        ProjectBuildResult restored = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("second.po"));

        Assert.True(restored.CacheHit);
        BuiltFile file = Assert.Single(restored.Files);
        AssemblySourceMapEntry[] sourceMap = Assert.IsType<AssemblySourceMapEntry[]>(file.SourceMap);
        Assert.NotEmpty(sourceMap);
        Directory.CreateDirectory(workspace.NewPath("evidence"));
        ExecutionResult run = new(1, "cached", true, "return", MameAdapter.ApiVersion, 0,
            new Dictionary<string, long> { ["PC"] = 0x2000 }, new Dictionary<int, string>(),
            "", null, workspace.NewPath("evidence"), [], []);
        Assert.NotEmpty(BuildExecution.Locate(restored, run));
    }

    [Theory]
    [InlineData("basic", "10 PRINT \"HELLO\"\n")]
    [InlineData("basic-labels", "@start: PRINT \"HELLO\"\n")]
    public void Build_CacheHit_RehydratesBasicSourceMap(string kind, string source)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.bas"), source);
        string project = WriteProject(workspace, new()
        {
            Files = [new() { Source = "main.bas", Path = "MAIN", Kind = kind }]
        });
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));

        ProjectBuildResult restored = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("second.po"));

        Assert.True(restored.CacheHit);
        BasicPreparedLine[] sourceMap = Assert.IsType<BasicPreparedLine[]>(
            Assert.Single(restored.Files).SourceMap);
        Assert.Single(sourceMap);
        Assert.Equal(10, sourceMap[0].BasicLine);
        Assert.Equal(workspace.NewPath("main.bas"), sourceMap[0].File);
    }

    [Fact]
    public void Build_CacheHit_RehydratesCc65SourceMap()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"),
            "// A2_FAKE_FEEDBACK\nint main(void) { return 0; }\n");
        string project = WriteProject(workspace, new()
        {
            Cc65 = ContractCompiler(),
            Files = [new() { Source = "main.c", Path = "MAIN", Kind = "cc65" }]
        });
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));

        ProjectBuildResult restored = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("second.po"));

        Assert.True(restored.CacheHit);
        Cc65SourceMap sourceMap = Assert.IsType<Cc65SourceMap>(
            Assert.Single(restored.Files).SourceMap);
        Assert.Equal("cc65-dbg-2.0", sourceMap.Format);
        Assert.Single(sourceMap.Entries);
        Assert.Contains("Segment list:", sourceMap.Map, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MetadataExceedsLimit_ReturnsSuccessfulUncachedBuildWithoutCacheArtifacts()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"),
            "// A2_FAKE_OVERSIZE_CACHE_METADATA\nint main(void) { return 0; }\n");
        string project = WriteProject(workspace, new()
        {
            Cc65 = ContractCompiler(),
            Files = [new() { Source = "main.c", Path = "MAIN", Kind = "cc65" }]
        });
        string cache = workspace.NewPath("cache");
        string output = workspace.NewPath("output.po");

        ProjectBuildResult result = ProjectBuildCache.Build(project, cache, output);

        Assert.False(result.CacheHit);
        Assert.True(File.Exists(output));
        Assert.Equal(result.Sha256, ProgramFiles.Hash(File.ReadAllBytes(output)));
        Assert.Empty(Directory.GetFiles(Path.Combine(cache, "objects"), "*.img"));
        Assert.Empty(Directory.GetFiles(Path.Combine(cache, "entries"), "*.json",
            SearchOption.AllDirectories));
        Assert.DoesNotContain(Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains(".a2-", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ManifestAtMetadataEntryLimit_RemainsUsableWithoutExceedingLimit()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));
        string entries = Assert.Single(Directory.GetDirectories(Path.Combine(cache, "entries")));
        for (int index = 0; index < 1023; index++)
            File.WriteAllText(Path.Combine(entries, index.ToString("x64") + ".json"), "{}");
        string originalObject = Assert.Single(Directory.GetFiles(Path.Combine(cache, "objects"),
            "*.img"));
        File.WriteAllText(workspace.NewPath("part.inc"), "lda #$2b\n");

        ProjectBuildResult second = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("second.po"));
        ProjectBuildResult third = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("third.po"));

        Assert.False(second.CacheHit);
        Assert.False(third.CacheHit);
        Assert.Equal(second.Sha256, third.Sha256);
        Assert.Equal(1024, Directory.GetFiles(entries, "*.json").Length);
        Assert.Equal(originalObject, Assert.Single(Directory.GetFiles(
            Path.Combine(cache, "objects"), "*.img")));
    }

    [Fact]
    public void Build_DependencyChangesAndReturnsToPriorContent_MissesThenHitsHistoricalEntry()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");
        string include = workspace.NewPath("part.inc");

        ProjectBuildResult original = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("original.po"));
        File.WriteAllText(include, "lda #$2b\n");
        ProjectBuildResult changed = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("changed.po"));
        File.WriteAllText(include, "lda #$2a\n");
        ProjectBuildResult restored = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("restored.po"));

        Assert.False(original.CacheHit);
        Assert.False(changed.CacheHit);
        Assert.NotEqual(original.Sha256, changed.Sha256);
        Assert.True(restored.CacheHit);
        Assert.Equal(original.Sha256, restored.Sha256);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(cache, "objects"), "*.img").Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(cache, "entries"), "*.json",
            SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Build_CorruptObject_RebuildsAndAtomicallyRepairsCache()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");
        ProjectBuildResult first = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("first.po"));
        string cachedObject = Assert.Single(Directory.GetFiles(Path.Combine(cache, "objects"), "*.img"));
        File.WriteAllBytes(cachedObject, [1, 2, 3]);

        ProjectBuildResult rebuilt = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("rebuilt.po"));

        Assert.False(rebuilt.CacheHit);
        Assert.Equal(first.Sha256, rebuilt.Sha256);
        Assert.Equal(first.Sha256, ProgramFiles.Hash(File.ReadAllBytes(cachedObject)));
        Assert.DoesNotContain(Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains(".a2-", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MetadataFromDifferentToolVersion_IsNotAHitAndIsReplaced()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));
        string metadata = Assert.Single(Directory.GetFiles(Path.Combine(cache, "entries"), "*.json",
            SearchOption.AllDirectories));
        JsonObject json = JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
        json["build"]!["toolVersion"] = "different-tool-version";
        File.WriteAllText(metadata, json.ToJsonString(new() { WriteIndented = true }));

        ProjectBuildResult result = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("second.po"));

        Assert.False(result.CacheHit);
        using JsonDocument repaired = JsonDocument.Parse(File.ReadAllText(metadata));
        Assert.Equal(result.ToolVersion, repaired.RootElement.GetProperty("build")
            .GetProperty("toolVersion").GetString());
    }

    [Fact]
    public void Build_MetadataWithUnknownProperty_IsStrictlyRejected()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));
        string metadata = Assert.Single(Directory.GetFiles(Path.Combine(cache, "entries"), "*.json",
            SearchOption.AllDirectories));
        JsonObject json = JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
        json["unexpected"] = true;
        File.WriteAllText(metadata, json.ToJsonString(new() { WriteIndented = true }));

        ProjectBuildResult result = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("second.po"));

        Assert.False(result.CacheHit);
        using JsonDocument repaired = JsonDocument.Parse(File.ReadAllText(metadata));
        Assert.False(repaired.RootElement.TryGetProperty("unexpected", out _));
    }

    [Fact]
    public void Build_TamperedBuildEvidence_InvalidatesMetadataIntegrityKey()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));
        string metadata = Assert.Single(Directory.GetFiles(Path.Combine(cache, "entries"), "*.json",
            SearchOption.AllDirectories));
        JsonObject json = JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
        json["build"]!["bootability"] = "tampered-but-syntactically-valid";
        File.WriteAllText(metadata, json.ToJsonString(new() { WriteIndented = true }));

        ProjectBuildResult result = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("second.po"));

        Assert.False(result.CacheHit);
        using JsonDocument repaired = JsonDocument.Parse(File.ReadAllText(metadata));
        Assert.Equal(result.Bootability, repaired.RootElement.GetProperty("build")
            .GetProperty("bootability").GetString());
    }

    [Fact]
    public void Build_GenuineInputInsideCache_IsRejectedBeforeOutputCommit()
    {
        using FixtureWorkspace workspace = new();
        string cache = workspace.NewPath("cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "main.asm"), ".org $2000\nrts\n");
        ProjectManifest manifest = new()
        {
            Output = "default.po",
            Files = [new() { Source = "cache/main.asm", Path = "MAIN", Kind = "asm" }]
        };
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, JsonSerializer.Serialize(manifest, ProjectJson.Options));
        string output = workspace.NewPath("output.po");

        DiskException error = Assert.Throws<DiskException>(() =>
            ProjectBuildCache.Build(project, cache, output));

        Assert.Equal("project.cache_input_alias", error.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Build_MetadataDeclaredInputInsideCache_IsRejected()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");
        _ = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));
        string metadata = Assert.Single(Directory.GetFiles(Path.Combine(cache, "entries"), "*.json",
            SearchOption.AllDirectories));
        string cachedObject = Assert.Single(Directory.GetFiles(Path.Combine(cache, "objects"), "*.img"));
        JsonObject json = JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
        JsonObject declaredSource = json["build"]!["inputs"]!.AsArray()
            .Select(node => node!.AsObject()).Single(input =>
                input["path"]!.GetValue<string>().EndsWith("main.asm", StringComparison.Ordinal));
        declaredSource["path"] = cachedObject;
        File.WriteAllText(metadata, json.ToJsonString(new() { WriteIndented = true }));
        string output = workspace.NewPath("second.po");

        DiskException error = Assert.Throws<DiskException>(() =>
            ProjectBuildCache.Build(project, cache, output));

        Assert.Equal("project.cache_input_alias", error.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Build_CacheHit_EnforcesOverwriteAndSourceAliasRules()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string cache = workspace.NewPath("cache");
        ProjectBuildResult first = ProjectBuildCache.Build(project, cache,
            workspace.NewPath("first.po"));
        string destination = workspace.NewPath("destination.po");
        File.WriteAllBytes(destination, [1, 2, 3]);

        DiskException exists = Assert.Throws<DiskException>(() =>
            ProjectBuildCache.Build(project, cache, destination));
        Assert.Equal("write.destination_exists", exists.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));

        ProjectBuildResult replaced = ProjectBuildCache.Build(project, cache, destination,
            overwrite: true);
        Assert.True(replaced.CacheHit);
        Assert.Equal(first.Sha256, ProgramFiles.Hash(File.ReadAllBytes(destination)));

        string source = workspace.NewPath("main.asm");
        string sourceText = File.ReadAllText(source);
        Assert.Equal("write.source_alias", Assert.Throws<DiskException>(() =>
            ProjectBuildCache.Build(project, cache, source, overwrite: true)).Code);
        Assert.Equal(sourceText, File.ReadAllText(source));
    }

    private static string Setup(FixtureWorkspace workspace)
    {
        File.WriteAllText(workspace.NewPath("main.asm"),
            ".org $2000\n.include \"part.inc\"\nrts\n");
        File.WriteAllText(workspace.NewPath("part.inc"), "lda #$2a\n");
        ProjectManifest manifest = new()
        {
            Output = "default.po",
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }]
        };
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, JsonSerializer.Serialize(manifest, ProjectJson.Options));
        return project;
    }

    private static string WriteProject(FixtureWorkspace workspace, ProjectManifest manifest)
    {
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, JsonSerializer.Serialize(manifest, ProjectJson.Options));
        return project;
    }

    private static Cc65Options ContractCompiler() => new()
    {
        Compiler = TestPaths.ExecutionHost(),
        ExpectedVersion = "cl65 V0.0 - contract test double"
    };
}
