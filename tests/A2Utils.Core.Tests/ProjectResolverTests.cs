// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Graphics;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;
using A2Utils.Core.Setup;

namespace A2Utils.Core.Tests;

public sealed class ProjectResolverTests
{
    [Fact]
    public void Inspect_ValidProject_ReturnsHashedDependenciesMemoryAndDiskPlanWithoutOutput()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("main.asm");
        string project = workspace.NewPath("project.json");
        File.WriteAllText(source, ".org $2000\nstart: LDA #$2A\nRTS\n");
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Target = "apple2enh",
            Output = "out/demo.po",
            Files = [new() { Source = "main.asm", Path = "TOOLS/MAIN", Kind = "asm" }]
        }, ProjectJson.Options));

        ProjectResolutionResult result = ProjectResolver.Inspect(project);

        Assert.Equal("65c02", result.Settings.Cpu);
        Assert.Equal(Path.GetFullPath(project), result.ManifestPath);
        Assert.Equal(Path.Combine(workspace.DirectoryPath, "out", "demo.po"), result.OutputPath);
        Assert.Contains(result.Dependencies, dependency => dependency.Role == "manifest"
            && dependency.Sha256?.Length == 64);
        Assert.Contains(result.Dependencies, dependency => dependency.Role == "source"
            && dependency.Path == source && dependency.Sha256?.Length == 64);
        ProjectResolvedFile file = Assert.Single(result.Files);
        Assert.Equal(0x2000, file.Origin);
        Assert.Equal("TOOLS/MAIN", file.ImagePath);
        Assert.Contains(result.Memory, region => region.Name == "TOOLS/MAIN");
        Assert.Equal("add", Assert.Single(result.DiskPlan.Entries).Action);
        Assert.Contains("TOOLS", result.DiskPlan.Directories);
        Assert.False(File.Exists(result.OutputPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(result.OutputPath)));
    }

    [Fact]
    public void Resolve_InvalidJson_ProvidesJsonPointerAndPhase()
    {
        using FixtureWorkspace workspace = new();
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project,
            """{"schemaVersion":1,"files":[{"source":5,"path":"MAIN"}]}""");

        DiskException error = Assert.Throws<DiskException>(() => ProjectResolver.Inspect(project));

        ProgramDiagnostic diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("project.schema", diagnostic.Code);
        Assert.Equal("$.files[0].source", diagnostic.JsonPointer);
        Assert.Equal("resolve", diagnostic.Phase);
        Assert.Equal("a2.project", diagnostic.Tool);
        Assert.NotNull(diagnostic.Line);
        Assert.NotNull(diagnostic.Column);
    }

    [Fact]
    public void Inspect_Template_ReportsDetectedDiskAndActualEntryActionsWithoutMutation()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.NewPath("template.2mg");
        string source = workspace.NewPath("main.bin");
        string project = workspace.NewPath("project.json");
        DiskSession.Create(template, "prodos", container: "2mg", order: "prodos");
        using (DiskSession disk = DiskSession.Open(template, inputFs: "prodos", writable: true))
        {
            disk.Mkdir("TOOLS");
            disk.Add("TOOLS/MAIN", [0x60], "BIN", 0x2000);
            disk.Flush();
        }
        File.WriteAllBytes(source, [0xa9, 0x2a, 0x60]);
        string before = ProgramFiles.Hash(File.ReadAllBytes(template));
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Output = "out/demo.2mg",
            Disk = new() { Template = "template.2mg", FileSystem = "prodos", Container = "raw" },
            Files = [new()
            {
                Source = "main.bin",
                Path = "tools/main",
                Kind = "binary",
                Origin = 0x2000,
                Replace = true
            }]
        }, ProjectJson.Options));

        ProjectResolutionResult result = ProjectResolver.Inspect(project);

        Assert.Equal("2mg", result.Disk.Container);
        Assert.Equal("prodos", result.Disk.Order);
        Assert.Equal("replace", Assert.Single(result.DiskPlan.Entries).Action);
        Assert.Empty(result.DiskPlan.Directories);
        Assert.Equal(before, ProgramFiles.Hash(File.ReadAllBytes(template)));
        Assert.False(Directory.Exists(Path.Combine(workspace.DirectoryPath, "out")));
    }

    [Fact]
    public void Inspect_BootOnlyProject_ReportsBootPayloadAndSectorMutation()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("boot.bin");
        string project = workspace.NewPath("project.json");
        byte[] payload = [0xa9, 0x2a, 0x60];
        File.WriteAllBytes(source, payload);
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Output = "out/boot.do",
            Disk = new() { FileSystem = "dos33", Order = "dos", Blocks = 280 },
            Boot = new() { Source = "boot.bin", Kind = "binary", Sectors = 2 }
        }, ProjectJson.Options));

        ProjectResolutionResult result = ProjectResolver.Inspect(project);

        ProjectResolvedBoot boot = Assert.IsType<ProjectResolvedBoot>(result.Boot);
        Assert.Equal(source, boot.SourcePath);
        Assert.Equal("binary", boot.Kind);
        Assert.Equal(0x0800, boot.Origin);
        Assert.Equal(2, boot.Sectors);
        Assert.Equal(payload.Length, boot.PayloadLength);
        Assert.Equal(ProgramFiles.Hash(payload), boot.PayloadSha256);
        Assert.Equal("write-sectors", boot.Action);
        Assert.Empty(result.Files);
        Assert.Equal(payload.Length, result.DiskPlan.CompiledPayloadBytes);
        Assert.Equal(512, result.DiskPlan.BootSectorBytes);
        Assert.Empty(result.DiskPlan.Entries);
        Assert.Contains(result.Dependencies, dependency => dependency.Role == "boot-source"
            && dependency.Path == source && dependency.Sha256 == boot.PayloadSha256);
        Assert.False(File.Exists(result.OutputPath));
    }

    [Theory]
    [InlineData("cpu", false)]
    [InlineData("mame", true)]
    public void Inspect_ExecutionSuite_ReportsMameOnlyWhenAnEngineNeedsIt(string engine,
        bool expectsMame)
    {
        using FixtureWorkspace workspace = new();
        string project = workspace.NewPath("project.json");
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nstart: RTS\n");
        File.WriteAllText(workspace.NewPath("routine.asm"), "RTS\n");
        File.WriteAllText(workspace.NewPath("suite.json"), JsonSerializer.Serialize(
            new ExecutionSuite { Tests = ["case.json"] }, ExecutionSpec.JsonOptions));
        File.WriteAllText(workspace.NewPath("case.json"), JsonSerializer.Serialize(
            new ExecutionSpec
            {
                Engine = engine,
                Machine = "apple2ee",
                EmulatorPath = engine == "mame" ? workspace.NewPath("mame.exe") : "",
                RomDirectory = engine == "mame" ? workspace.DirectoryPath : "",
                Routine = new() { Source = "routine.asm" }
            }, ExecutionSpec.JsonOptions));
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Target = "apple2enh",
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }],
            Execution = new("suite.json")
        }, ProjectJson.Options));

        ProjectResolutionResult result = ProjectResolver.Inspect(project);

        ProjectToolRequirement? mame = result.ToolRequirements.SingleOrDefault(tool => tool.Id == "mame");
        ProjectToolRequirement? roms = result.ToolRequirements.SingleOrDefault(tool => tool.Id == "mame-roms");
        Assert.Equal(expectsMame, mame is not null);
        Assert.Equal(expectsMame, roms is not null);
        if (mame is not null) Assert.True(mame.Required);
        if (roms is not null)
        {
            Assert.True(roms.Required);
            Assert.Equal(workspace.DirectoryPath, roms.Path);
            Assert.True(roms.Available);
        }
        Assert.Contains(result.Dependencies, dependency => dependency.Role == "execution-suite"
            && dependency.Path == workspace.NewPath("suite.json") && dependency.Sha256?.Length == 64);
        Assert.Contains(result.Dependencies, dependency => dependency.Role == "execution-spec"
            && dependency.Path == workspace.NewPath("case.json") && dependency.Sha256?.Length == 64);
        Assert.Contains(result.Dependencies, dependency => dependency.Role == "execution-routine-source"
            && dependency.Path == workspace.NewPath("routine.asm") && dependency.Sha256?.Length == 64);
    }

    [Fact]
    public void Inspect_ExecutionSuite_MissingRomDirectory_IsReportedAsUnavailableRequirement()
    {
        using FixtureWorkspace workspace = new();
        string project = workspace.NewPath("project.json");
        string missingRoms = workspace.NewPath("missing-roms");
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nstart: RTS\n");
        File.WriteAllText(workspace.NewPath("suite.json"), JsonSerializer.Serialize(
            new ExecutionSuite { Tests = ["case.json"] }, ExecutionSpec.JsonOptions));
        File.WriteAllText(workspace.NewPath("case.json"), JsonSerializer.Serialize(
            new ExecutionSpec
            {
                Engine = "mame",
                Machine = "apple2ee",
                EmulatorPath = workspace.NewPath("mame.exe"),
                RomDirectory = missingRoms
            }, ExecutionSpec.JsonOptions));
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Target = "apple2enh",
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }],
            Execution = new("suite.json")
        }, ProjectJson.Options));

        ProjectResolutionResult result = ProjectResolver.Inspect(project);

        ProjectToolRequirement roms = Assert.Single(result.ToolRequirements,
            tool => tool.Id == "mame-roms");
        Assert.True(roms.Required);
        Assert.Equal(missingRoms, roms.Path);
        Assert.Equal(false, roms.Available);
        Assert.Contains("ROM", roms.Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_ExecutionSuite_InvalidNonRoutineSteps_AreRejectedAgainstProjectPlan()
    {
        using FixtureWorkspace workspace = new();
        string project = workspace.NewPath("project.json");
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nstart: RTS\n");
        File.WriteAllText(workspace.NewPath("suite.json"), JsonSerializer.Serialize(
            new ExecutionSuite { Tests = ["case.json"] }, ExecutionSpec.JsonOptions));
        File.WriteAllText(workspace.NewPath("case.json"), JsonSerializer.Serialize(
            new ExecutionSpec
            {
                Engine = "mame",
                Machine = "apple2ee",
                EmulatorPath = workspace.NewPath("mame.exe"),
                RomDirectory = workspace.DirectoryPath,
                Steps = Enumerable.Range(0, 129).Select(index => new ExecutionStep
                {
                    Name = "step-" + index,
                    Action = "wait",
                    Seconds = 0.1
                }).ToArray()
            }, ExecutionSpec.JsonOptions));
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Target = "apple2enh",
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }],
            Execution = new("suite.json")
        }, ProjectJson.Options));

        DiskException error = Assert.Throws<DiskException>(() =>
            ProjectResolver.Inspect(project));

        Assert.Equal("execution.steps", error.Code);
    }

    [Fact]
    public void Inspect_ExecutionSuite_LargeBuildOnFloppyDevice_IsRejectedAgainstDiskPlan()
    {
        using FixtureWorkspace workspace = new();
        string project = workspace.NewPath("project.json");
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nstart: RTS\n");
        File.WriteAllText(workspace.NewPath("suite.json"), JsonSerializer.Serialize(
            new ExecutionSuite { Tests = ["case.json"] }, ExecutionSpec.JsonOptions));
        File.WriteAllText(workspace.NewPath("case.json"), JsonSerializer.Serialize(
            new ExecutionSpec
            {
                Engine = "mame",
                Machine = "apple2ee",
                EmulatorPath = workspace.NewPath("mame.exe"),
                RomDirectory = workspace.DirectoryPath
            }, ExecutionSpec.JsonOptions));
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Target = "apple2enh",
            Disk = new() { FileSystem = "prodos", Blocks = 281, Order = "prodos" },
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }],
            Execution = new("suite.json", "flop1")
        }, ProjectJson.Options));

        DiskException error = Assert.Throws<DiskException>(() =>
            ProjectResolver.Inspect(project));

        Assert.Equal("execution.storage_geometry", error.Code);
    }

    [Fact]
    public void Inspect_ExecutionSuite_TooManyGraphicsAssertions_AreRejectedBeforeTraversal()
    {
        using FixtureWorkspace workspace = new();
        string project = workspace.NewPath("project.json");
        string expected = workspace.NewPath("expected.png");
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nstart: RTS\n");
        File.WriteAllBytes(expected, PngCodec.Encode(new(40, 48, new byte[40 * 48 * 3])));
        File.WriteAllText(workspace.NewPath("suite.json"), JsonSerializer.Serialize(
            new ExecutionSuite { Tests = ["case.json"] }, ExecutionSpec.JsonOptions));
        File.WriteAllText(workspace.NewPath("case.json"), JsonSerializer.Serialize(
            new ExecutionSpec
            {
                Engine = "mame",
                Machine = "apple2ee",
                EmulatorPath = workspace.NewPath("mame.exe"),
                RomDirectory = workspace.DirectoryPath,
                GraphicsMemory = Enumerable.Range(0, 17)
                    .Select(_ => new GraphicsMemoryAssertion(expected, "lores"))
                    .ToArray()
            }, ExecutionSpec.JsonOptions));
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Target = "apple2enh",
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }],
            Execution = new("suite.json")
        }, ProjectJson.Options));

        DiskException error = Assert.Throws<DiskException>(() =>
            ProjectResolver.Inspect(project));

        Assert.Equal("execution.invalid_spec", error.Code);
        Assert.Contains("at most 16", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_EnvironmentChangesAfterLoad_RejectsMixedResolution()
    {
        using FixtureWorkspace workspace = new();
        string firstTemplate = workspace.NewPath("first.po");
        string secondTemplate = workspace.NewPath("second.po");
        string environment = workspace.NewPath("environment.json");
        string source = workspace.NewPath("main.bin");
        string project = workspace.NewPath("project.json");
        DiskSession.Create(firstTemplate, "prodos", order: "prodos");
        DiskSession.Create(secondTemplate, "prodos", order: "prodos");
        File.WriteAllBytes(source, [0x60]);
        DevelopmentEnvironmentProfile first = new() { TemplateImage = firstTemplate };
        File.WriteAllText(environment, JsonSerializer.Serialize(first,
            DevelopmentEnvironment.JsonOptions));
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Environment = "environment.json",
            Files = [new() { Source = "main.bin", Path = "MAIN", Kind = "binary", Origin = 0x2000 }]
        }, ProjectJson.Options));

        DiskException error = Assert.Throws<DiskException>(() =>
            ProjectResolver.InspectForTest(project, () => File.WriteAllText(environment,
                JsonSerializer.Serialize(first with { TemplateImage = secondTemplate },
                    DevelopmentEnvironment.JsonOptions)), null));

        Assert.Equal("project.source_changed", error.Code);
    }

    [Fact]
    public void Inspect_ToolchainLockChangesAfterLoad_RejectsMixedResolution()
    {
        using FixtureWorkspace workspace = new();
        string environment = workspace.NewPath("environment.json");
        string lockFile = workspace.NewPath("environment.lock.json");
        string emulator = workspace.NewPath("mame.exe");
        string roms = workspace.NewPath("roms");
        string source = workspace.NewPath("main.bin");
        string project = workspace.NewPath("project.json");
        Directory.CreateDirectory(roms);
        File.WriteAllBytes(emulator, [0x60]);
        File.WriteAllBytes(source, [0x60]);
        File.WriteAllText(environment, JsonSerializer.Serialize(new DevelopmentEnvironmentProfile
        {
            MamePath = emulator,
            RomDirectory = roms
        }, DevelopmentEnvironment.JsonOptions));
        DevelopmentEnvironment.CreateLock(environment, lockFile);
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Environment = "environment.json",
            ToolchainLock = "environment.lock.json",
            Files = [new() { Source = "main.bin", Path = "MAIN", Kind = "binary", Origin = 0x2000 }]
        }, ProjectJson.Options));

        DiskException error = Assert.Throws<DiskException>(() =>
            ProjectResolver.InspectForTest(project,
                () => File.WriteAllText(lockFile, "{\"schemaVersion\":1}"), null));

        Assert.Equal("project.source_changed", error.Code);
    }

    [Fact]
    public void Inspect_ManifestChangesAfterLoad_RejectsMixedResolution()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("main.bin");
        string project = workspace.NewPath("project.json");
        File.WriteAllBytes(source, [0x60]);
        ProjectManifest manifest = new()
        {
            Files = [new() { Source = "main.bin", Path = "MAIN", Kind = "binary", Origin = 0x2000 }]
        };
        File.WriteAllText(project, JsonSerializer.Serialize(manifest, ProjectJson.Options));

        DiskException error = Assert.Throws<DiskException>(() =>
            ProjectResolver.InspectForTest(project, () => File.WriteAllText(project,
                JsonSerializer.Serialize(manifest with { Output = "changed.po" },
                    ProjectJson.Options)), null));

        Assert.Equal("project.source_changed", error.Code);
    }

    [Fact]
    public void Inspect_TemplateChangesAfterCheckedBuild_UsesCheckedTemplateSnapshot()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.NewPath("template.po");
        string replacement = workspace.NewPath("replacement.po");
        string source = workspace.NewPath("main.bin");
        string project = workspace.NewPath("project.json");
        DiskSession.Create(template, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(template, inputFs: "prodos", writable: true))
        {
            disk.Add("MAIN", [0x60], "BIN", 0x2000);
            disk.Flush();
        }
        DiskSession.Create(replacement, "prodos", order: "prodos");
        File.WriteAllBytes(source, [0xa9, 0x2a, 0x60]);
        string checkedHash = ProgramFiles.Hash(File.ReadAllBytes(template));
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Disk = new() { Template = "template.po", FileSystem = "prodos" },
            Files = [new()
            {
                Source = "main.bin",
                Path = "MAIN",
                Kind = "binary",
                Origin = 0x2000,
                Replace = true
            }]
        }, ProjectJson.Options));

        ProjectResolutionResult result = ProjectResolver.InspectForTest(project, null,
            () => File.Copy(replacement, template, overwrite: true));

        Assert.Equal("replace", Assert.Single(result.DiskPlan.Entries).Action);
        Assert.Equal(checkedHash, AssertDependency(result, "disk-template", template).Sha256);
        Assert.NotEqual(checkedHash, ProgramFiles.Hash(File.ReadAllBytes(template)));
    }

    [Fact]
    public void Inspect_ExecutionSuite_PinsCaseProfileLockRoutineAssetsAndAuxiliaryDisk()
    {
        using FixtureWorkspace workspace = new();
        string project = workspace.NewPath("project.json");
        string suite = workspace.NewPath("suite.json");
        string testCase = workspace.NewPath("case.json");
        string profilePath = workspace.NewPath("environment.json");
        string lockPath = workspace.NewPath("environment.lock.json");
        string routine = workspace.NewPath("routine.asm");
        string include = workspace.NewPath("routine.inc");
        string screenshot = workspace.NewPath("expected-screen.png");
        string graphics = workspace.NewPath("expected-lores.png");
        string auxiliaryDisk = workspace.NewPath("boot.do");
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nstart: RTS\n");
        File.WriteAllText(routine, ".include \"routine.inc\"\nRTS\n");
        File.WriteAllText(include, "NOP\n");
        File.WriteAllBytes(screenshot, PngCodec.Encode(new(1, 1, new byte[3])));
        File.WriteAllBytes(graphics, PngCodec.Encode(new(40, 48, new byte[40 * 48 * 3])));
        File.WriteAllBytes(auxiliaryDisk, [1, 2, 3, 4]);
        File.WriteAllText(lockPath, "{\"schemaVersion\":1}\n");
        DevelopmentEnvironmentProfile profile = new()
        {
            Machine = "apple2ee",
            MamePath = "mame-a.exe",
            RomDirectory = "roms"
        };
        File.WriteAllText(profilePath, JsonSerializer.Serialize(profile,
            DevelopmentEnvironment.JsonOptions));
        ExecutionSpec sourceSpec = new()
        {
            Name = "dependency audit",
            Engine = "mame",
            Environment = "environment.json",
            ToolchainLock = "environment.lock.json",
            Routine = new() { Source = "routine.asm" },
            Disks =
            [
                new("flop1", "future-build.do"),
                new("flop2", "boot.do", ProgramFiles.Hash(File.ReadAllBytes(auxiliaryDisk)))
            ],
            ScreenshotAssertion = new("expected-screen.png"),
            GraphicsMemory = [new("expected-lores.png", "lores")]
        };
        File.WriteAllText(testCase, JsonSerializer.Serialize(sourceSpec, ExecutionSpec.JsonOptions));
        File.WriteAllText(suite, JsonSerializer.Serialize(new ExecutionSuite { Tests = ["case.json"] },
            ExecutionSpec.JsonOptions));
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Target = "apple2enh",
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }],
            Execution = new("suite.json")
        }, ProjectJson.Options));

        ProjectResolutionResult first = ProjectResolver.Inspect(project);

        AssertDependency(first, "execution-suite", suite);
        string caseHash = AssertDependency(first, "execution-spec", testCase).Sha256!;
        string profileHash = AssertDependency(first, "execution-environment", profilePath).Sha256!;
        AssertDependency(first, "execution-toolchain-lock", lockPath);
        AssertDependency(first, "execution-routine-source", routine);
        AssertDependency(first, "execution-routine-include", include);
        AssertDependency(first, "execution-screenshot", screenshot);
        AssertDependency(first, "execution-graphics", graphics);
        AssertDependency(first, "execution-auxiliary-disk", auxiliaryDisk);
        Assert.DoesNotContain(first.Dependencies, dependency => dependency.Path.EndsWith(
            "future-build.do", StringComparison.Ordinal));
        Assert.Equal(workspace.NewPath("mame-a.exe"), first.ToolRequirements
            .Single(tool => tool.Id == "mame").Path);

        File.WriteAllText(testCase, JsonSerializer.Serialize(sourceSpec with { Name = "changed case" },
            ExecutionSpec.JsonOptions));
        ProjectResolutionResult caseChanged = ProjectResolver.Inspect(project);
        Assert.NotEqual(caseHash, AssertDependency(caseChanged, "execution-spec", testCase).Sha256);
        Assert.Equal(profileHash, AssertDependency(caseChanged, "execution-environment", profilePath).Sha256);

        File.WriteAllText(profilePath, JsonSerializer.Serialize(profile with { MamePath = "mame-b.exe" },
            DevelopmentEnvironment.JsonOptions));
        ProjectResolutionResult profileChanged = ProjectResolver.Inspect(project);
        Assert.NotEqual(profileHash, AssertDependency(profileChanged,
            "execution-environment", profilePath).Sha256);
        Assert.Equal(workspace.NewPath("mame-b.exe"), profileChanged.ToolRequirements
            .Single(tool => tool.Id == "mame").Path);
    }

    [Fact]
    public void ProgramDiagnostic_OptionalRepairFieldsRemainAdditive()
    {
        ProgramDiagnostic diagnostic = new("sample", "error", "Replace the value.",
            "project.json", 2, 4)
        {
            EndLine = 2,
            EndColumn = 9,
            JsonPointer = "/target",
            Phase = "resolve",
            Tool = "a2.project",
            HelpUri = "docs/projects.md",
            Related = [new("Target declaration.", "project.json", 2, 4, 2, 9, "/target")],
            Fixes = [new("Use a supported target.",
                [new("project.json", "apple2e", 2, 4, 2, 9, "/target")])]
        };

        string json = JsonSerializer.Serialize(diagnostic, ProjectJson.Options);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(9, document.RootElement.GetProperty("endColumn").GetInt32());
        Assert.Equal("/target", document.RootElement.GetProperty("jsonPointer").GetString());
        Assert.Equal("apple2e", document.RootElement.GetProperty("fixes")[0]
            .GetProperty("edits")[0].GetProperty("replacement").GetString());
    }

    private static ProjectDependency AssertDependency(ProjectResolutionResult result,
        string role, string path)
    {
        ProjectDependency dependency = Assert.Single(result.Dependencies,
            item => item.Role == role && item.Path == path);
        Assert.True(dependency.Exists);
        Assert.Equal(64, dependency.Sha256?.Length);
        return dependency;
    }
}
