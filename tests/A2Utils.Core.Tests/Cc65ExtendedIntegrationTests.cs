// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class Cc65ExtendedIntegrationTests
{
    [Fact]
    public void Compile_LinkedFeedback_ReportsOriginalSourceLocationSymbolsAndSegmentFootprint()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("main.c");
        File.WriteAllText(source, "// A2_FAKE_FEEDBACK\nint main(void) { return 0; }\n");
        Cc65Result result = Cc65Compiler.Compile(source, Options(), workspace.DirectoryPath);
        Assert.Equal(0x803, result.Symbols["_main"]);
        Assert.Equal("bss", result.Segments.Single(segment => segment.Name == "BSS").Kind);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message == "controlled source warning");
        Assert.Equal(source, warning.File);
        Assert.Equal(3, warning.Line);
        Assert.Equal("warning", warning.Severity);
        Assert.Equal(HostPath(), result.CompilerPath);
        var location = Assert.Single(result.SourceMap);
        Assert.Equal(source, location.File);
        Assert.Equal(1, location.Line);
        Assert.Equal(0x803, location.Address);
        Assert.Equal("// A2_FAKE_FEEDBACK", location.Source);
        Assert.Contains("version\tmajor=2,minor=0", result.Debug);
    }

    [Fact]
    public void Build_Cc65Feedback_ImportsSymbolsBssZeroPageAndStackWithoutDoubleCounting()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"), "// A2_FAKE_FEEDBACK\nint main(void) { return 0; }\n");
        ProjectManifest manifest = new() { Cc65 = Options(), Files = [new() { Source = "main.c", Path = "APP", Kind = "cc65" }] };
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, JsonSerializer.Serialize(manifest, ProjectJson.Options));
        ProjectBuildResult result = ProjectBuilder.Build(project, checkOnly: true);
        BuiltFile file = Assert.Single(result.Files);
        Assert.Equal(0x803, file.Symbols["_main"]);
        Cc65SourceMap sourceMap = Assert.IsType<Cc65SourceMap>(file.SourceMap);
        Assert.Equal("cc65-dbg-2.0", sourceMap.Format);
        Assert.Single(sourceMap.Entries);
        ExecutionResult run = new(1, "cc65", true, "emulated_limit", "0.289", 1,
            new Dictionary<string, long> { ["PC"] = 0x803 }, new Dictionary<int, string>(), "", null,
            workspace.DirectoryPath, [], []);
        ExecutionSourceLocation location = Assert.Single(BuildExecution.Locate(result, run));
        Assert.Equal(workspace.NewPath("main.c"), location.File);
        Assert.Equal(1, location.Line);
        Assert.Contains(file.RuntimeMemory, region => region.Kind == "zero-page" && region.Start == 0x80);
        Assert.Contains(file.RuntimeMemory, region => region.Kind == "stack" && region.Start == 0x8e00);
        Assert.Equal(26 + 258 + 2048, result.Memory.Sum(region => region.Length));
    }

    [Fact]
    public void Compile_CustomConfig_IsolatesAndHashesValidatedSingleOutputConfiguration()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"), "int main(void) { return 0; }");
        string config = "MEMORY { RAM: file=%O, start=$0803,size=$1000; } SEGMENTS { CODE:load=RAM,type=ro; }";
        File.WriteAllText(workspace.NewPath("custom.cfg"), config);
        Cc65Result result = Cc65Compiler.Compile(workspace.NewPath("main.c"), Options() with { LinkerConfig = "custom.cfg" }, workspace.DirectoryPath);
        Assert.Contains(result.Inputs, input => input.Path == "custom.cfg" && input.Sha256 == ProgramFiles.Hash(System.Text.Encoding.UTF8.GetBytes(config)));
        Assert.Equal(config, File.ReadAllText(workspace.NewPath("custom.cfg")));
        Assert.False(File.Exists(workspace.NewPath("program.as")));
    }

    [Fact]
    public void Compile_UnsafeConfig_RejectsBeforeIntermediateFilesOrExternalWrites()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"), "int main(void) { return 0; }");
        File.WriteAllText(workspace.NewPath("bad.cfg"), "MEMORY { RAM:file=\"../escape\",start=$0803,size=$1000; }");
        Assert.Equal("cc65.linker_config", Assert.Throws<DiskException>(() => Cc65Compiler.Compile(
            workspace.NewPath("main.c"), Options() with { LinkerConfig = "bad.cfg" }, workspace.DirectoryPath)).Code);
        Assert.False(File.Exists(workspace.NewPath("main.o")));
    }

    [Fact]
    public void Compile_ConfigOutsideProject_RejectsPathEscape()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"), "int main(void) { return 0; }");
        Assert.Equal("cc65.path_escape", Assert.Throws<DiskException>(() => Cc65Compiler.Compile(
            workspace.NewPath("main.c"), Options() with { LinkerConfig = "../outside.cfg" }, workspace.DirectoryPath)).Code);
    }

    [Fact]
    public void Compile_ConfiguredDistribution_ReportsInstalledHeaderLibraryAndExecutableHashes()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"), "int main(void) { return 0; }");
        string toolchain = workspace.NewPath("toolchain");
        Directory.CreateDirectory(Path.Combine(toolchain, "include"));
        Directory.CreateDirectory(Path.Combine(toolchain, "lib"));
        string header = Path.Combine(toolchain, "include", "stdio.h");
        File.WriteAllText(header, "/* distribution fixture */");
        string library = Path.Combine(toolchain, "lib", "apple2.lib");
        File.WriteAllBytes(library, [1, 2, 3]);
        Cc65Result result = Cc65Compiler.Compile(workspace.NewPath("main.c"), Options() with { ToolchainRoot = toolchain }, workspace.DirectoryPath);
        Assert.Contains(result.ToolchainInputs, input => input.Path == header);
        Assert.Contains(result.ToolchainInputs, input => input.Path == library);
        Assert.Contains(result.ToolchainInputs, input => input.Path == HostPath());
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "cc65.toolchain_partial");
    }

    [Fact]
    public void Compile_GeneratedHeader_StagesAndReportsWithoutCreatingHostFile()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"), "#include \"assets.h\"\nint main(void) { return 0; }");
        string header = workspace.NewPath("generated/assets.h");
        Cc65Result result = Cc65Compiler.Compile(workspace.NewPath("main.c"), Options() with { Includes = ["generated"] }, workspace.DirectoryPath,
            generatedInputs: new Dictionary<string, byte[]> { [header] = "#define TILE_BYTES 16\n"u8.ToArray() });
        Assert.Contains(result.Inputs, input => input.Path == "generated/assets.h");
        Assert.False(File.Exists(header));
    }

    private static Cc65Options Options() => new() { Compiler = HostPath(), ExpectedVersion = "cl65 V0.0 - contract test double" };

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
