// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectStartupTests
{
    [Theory]
    [InlineData("dos33")]
    [InlineData("prodos")]
    public void Build_OrderedStartup_EmitsLoadsCallsAndPreservesInputs(string fileSystem)
    {
        using FixtureWorkspace workspace = new();
        ProjectManifest manifest = CreateProject(workspace, fileSystem);
        byte[] template = File.ReadAllBytes(workspace.NewPath("template.po"));
        manifest = manifest with
        {
            Startup = new()
            {
                Steps = [new() { Program = "data" }, new() { Program = "code" },
                    new() { Action = "call", Program = "CODE" }, new() { Action = "run", Program = "BASIC" }]
            }
        };
        string project = Save(workspace, manifest);
        byte[] source = File.ReadAllBytes(workspace.NewPath("code.bin"));
        ProjectBuildResult preflight = ProjectBuilder.Build(project, preflight: true);
        Assert.False(File.Exists(preflight.OutputPath));
        ProjectBuildResult built = ProjectBuilder.Build(project);
        Assert.Equal(preflight.Sha256, built.Sha256);
        Assert.Equal(template, File.ReadAllBytes(workspace.NewPath("template.po")));
        Assert.Equal(source, File.ReadAllBytes(workspace.NewPath("code.bin")));
        using DiskSession disk = DiskSession.Open(built.OutputPath);
        byte[] expected = ApplesoftBasic.Compile("10 HIMEM:8192\n20 PRINT CHR$(4);\"BLOAD DATA\"\n30 PRINT CHR$(4);\"BLOAD CODE\"\n40 CALL 12289\n50 PRINT CHR$(4);\"RUN BASIC\"\n");
        Assert.Equal(expected, disk.ReadFile("HELLO"));
        Assert.Equal(ProgramFiles.Hash(expected), built.Files.Single(file => file.Kind == "startup").Sha256);
    }

    [Fact]
    public void Build_FinalBinaryRun_UsesExplicitEntryPointAndCachesGeneratedLauncher()
    {
        using FixtureWorkspace workspace = new();
        ProjectManifest manifest = CreateProject(workspace, "dos33") with
        {
            Startup = new() { Steps = [new() { Action = "run", Program = "CODE" }] }
        };
        string project = Save(workspace, manifest);
        ProjectResolutionResult resolution = ProjectResolver.Inspect(project);
        Assert.Contains(resolution.Files, file => file.ImagePath == "HELLO");
        string cache = workspace.NewPath("cache");
        ProjectBuildResult first = ProjectBuildCache.Build(project, cache, workspace.NewPath("first.po"));
        ProjectBuildResult hit = ProjectBuildCache.Build(project, cache, workspace.NewPath("second.po"));
        Assert.True(hit.CacheHit);
        Assert.Equal(first.Sha256, hit.Sha256);
        using (DiskSession disk = DiskSession.Open(hit.OutputPath))
            Assert.Equal(ApplesoftBasic.Compile("10 HIMEM:12288\n20 PRINT CHR$(4);\"BLOAD CODE\"\n30 CALL 12289\n40 END\n"), disk.ReadFile("HELLO"));
        Save(workspace, manifest with { Files = manifest.Files.Select(file => file.Path == "CODE" ? file with { EntryPoint = 0x3000 } : file).ToList() });
        ProjectBuildResult changed = ProjectBuildCache.Build(project, cache, workspace.NewPath("third.po"));
        Assert.False(changed.CacheHit);
        Assert.NotEqual(first.Sha256, changed.Sha256);
    }

    [Theory]
    [InlineData("unloaded", "project.startup_not_loaded")]
    [InlineData("overwritten", "project.startup_not_loaded")]
    [InlineData("run-order", "project.startup_sequence")]
    [InlineData("bank", "project.startup_bank")]
    [InlineData("workspace", "project.startup_memory")]
    [InlineData("both", "project.startup")]
    [InlineData("unknown-action", "project.startup")]
    [InlineData("missing-file", "project.startup_program")]
    public void Build_InvalidStartup_PreservesTemplateSourcesAndExistingDestination(string scenario, string code)
    {
        using FixtureWorkspace workspace = new();
        ProjectManifest manifest = CreateProject(workspace, "dos33");
        ProjectStartup startup = new() { Steps = [new() { Program = "CODE" }, new() { Action = "call", Program = "CODE" }] };
        switch (scenario)
        {
            case "unloaded": startup = new() { Steps = [new() { Action = "call", Program = "CODE" }] }; break;
            case "overwritten":
                manifest = manifest with
                {
                    CheckMemory = false,
                    Files = manifest.Files.Select(file => file.Path == "DATA" ? file with { Origin = 0x3000 } : file).ToList()
                };
                startup = new() { Steps = [new() { Program = "CODE" }, new() { Program = "DATA" }, new() { Action = "call", Program = "CODE" }] };
                break;
            case "run-order": startup = new() { Steps = [new() { Action = "run", Program = "CODE" }, new() { Program = "DATA" }] }; break;
            case "bank": manifest = manifest with { Files = manifest.Files.Select(file => file.Path == "CODE" ? file with { MemoryBank = "aux" } : file).ToList() }; break;
            case "workspace": startup = startup with { WorkspaceBytes = 65536 }; manifest = manifest with { CheckMemory = false }; break;
            case "both": startup = startup with { Program = "CODE" }; break;
            case "unknown-action": startup = new() { Steps = [new() { Action = "jump", Program = "CODE" }] }; break;
            case "missing-file": startup = new() { Steps = [new() { Program = "ABSENT" }] }; break;
        }
        string project = Save(workspace, manifest with { Startup = startup });
        string output = workspace.NewPath("existing.po");
        File.WriteAllBytes(output, [0xa2, 0x55]);
        Dictionary<string, byte[]> before = Directory.GetFiles(workspace.DirectoryPath).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal(code, Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, output, overwrite: true)).Code);
        foreach (var input in before) Assert.Equal(input.Value, File.ReadAllBytes(input.Key));
    }

    [Fact]
    public void Build_LargeFinalBasic_RejectsCollisionWithPreviouslyLoadedBinary()
    {
        using FixtureWorkspace workspace = new();
        ProjectManifest manifest = CreateProject(workspace, "dos33");
        File.WriteAllText(workspace.NewPath("main.bas"), string.Concat(Enumerable.Range(1, 100).Select(i => $"{i * 10} REM {new string('A', 80)}\n")));
        string project = Save(workspace, manifest with
        {
            CheckMemory = false,
            Startup = new() { Steps = [new() { Program = "DATA" }, new() { Action = "run", Program = "BASIC" }] }
        });
        Assert.Equal("project.startup_memory", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project)).Code);
        Assert.False(File.Exists(workspace.NewPath("build.po")));
    }

    private static ProjectManifest CreateProject(FixtureWorkspace workspace, string fileSystem)
    {
        DiskSession.Create(workspace.NewPath("template.po"), fileSystem, order: "prodos");
        File.WriteAllBytes(workspace.NewPath("data.bin"), [1, 2, 3]);
        File.WriteAllBytes(workspace.NewPath("code.bin"), [0xea, 0x60]);
        File.WriteAllText(workspace.NewPath("main.bas"), "10 END\n");
        return new()
        {
            Disk = new() { FileSystem = fileSystem, Template = "template.po" },
            Files = [new() { Source = "data.bin", Path = "DATA", Origin = 0x2000 },
                new() { Source = "code.bin", Path = "CODE", Origin = 0x3000, EntryPoint = 0x3001 },
                new() { Source = "main.bas", Path = "BASIC", Kind = "basic" }]
        };
    }

    private static string Save(FixtureWorkspace workspace, ProjectManifest manifest)
    {
        string path = workspace.NewPath("project.a2.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, ProjectJson.Options));
        return path;
    }
}
