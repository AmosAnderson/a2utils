// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectPreflightTests
{
    [Theory]
    [InlineData("dos33", "raw")]
    [InlineData("prodos", "raw")]
    [InlineData("prodos", "2mg")]
    public void Build_PreflightNewVolume_ReportsActualCapacityAndHashWithoutCreatingOutputParents(string fs, string container)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS\n");
        string imagePath = fs == "prodos" ? "PROGRAMS/MAIN" : "MAIN";
        string project = Save(workspace, new()
        {
            Output = "not-created/nested/build.img",
            Disk = new() { FileSystem = fs, Container = container },
            Files = [new() { Source = "main.asm", Path = imagePath, Kind = "asm" }]
        });

        ProjectBuildResult preflight = ProjectBuilder.Build(project, preflight: true);

        Assert.True(preflight.Preflight);
        Assert.False(preflight.CheckOnly);
        Assert.False(Directory.Exists(workspace.NewPath("not-created")));
        ProjectBuildPlan plan = Assert.IsType<ProjectBuildPlan>(preflight.Plan);
        Assert.Equal(new ProjectFileChange(imagePath, "add", null, 1), Assert.Single(plan.Files));
        Assert.Equal(fs == "prodos" ? new[] { "PROGRAMS" } : [], plan.CreatedDirectories);
        Assert.Equal(143360, plan.ImageSizeBytes);
        Assert.True(plan.FreeBytesAfter < plan.FreeBytesBefore);

        ProjectBuildResult built = ProjectBuilder.Build(project, workspace.NewPath("actual.img"));
        using DiskSession disk = DiskSession.Open(built.OutputPath);
        Assert.Equal(built.Sha256, preflight.Sha256);
        Assert.Equal(disk.Info.FreeBytes, plan.FreeBytesAfter);
        Assert.Equal(built.Plan!.FreeBytesBefore, plan.FreeBytesBefore);
        Assert.False(built.Preflight);
    }

    [Fact]
    public void Build_PreflightTemplateChanges_ReportsAdditionsAndReplacementsAndPreservesAllInputs()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(template);
        File.WriteAllText(workspace.NewPath("readme.txt"), "Replacement text\n");
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $3000\nRTS\n");
        string output = workspace.NewPath("existing.do");
        File.WriteAllBytes(output, [0xa2, 0xff]);
        string project = Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = [new() { Source = "readme.txt", Path = "README", Kind = "text", Replace = true },
                new() { Source = "main.asm", Path = "NEW", Kind = "asm" }]
        });
        string[] originalPaths = Directory.GetFileSystemEntries(workspace.DirectoryPath).Order().ToArray();

        ProjectBuildResult preflight = ProjectBuilder.Build(project, output, overwrite: true, preflight: true);

        Assert.Equal(originalPaths, Directory.GetFileSystemEntries(workspace.DirectoryPath).Order());
        Assert.Equal(original, File.ReadAllBytes(template));
        Assert.Equal(new byte[] { 0xa2, 0xff }, File.ReadAllBytes(output));
        ProjectBuildPlan plan = Assert.IsType<ProjectBuildPlan>(preflight.Plan);
        using DiskSession input = DiskSession.Open(template);
        Assert.Equal(input.Info.FreeBytes, plan.FreeBytesBefore);
        Assert.Equal(input.GetEntry("README").Length, plan.Files[0].PreviousLength);
        Assert.Equal("replace", plan.Files[0].Action);
        Assert.Equal("add", plan.Files[1].Action);
        Assert.Equal(ProjectBuilder.Build(project, workspace.NewPath("actual.do")).Sha256, preflight.Sha256);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("2mg")]
    public void Build_NestedProDosDirectories_NormalizesRedundantHeaderCreationDates(string container)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS\n");
        DateTime timestamp = new(2000, 1, 1, 12, 34, 0);
        string project = Save(workspace, new()
        {
            Timestamp = timestamp,
            Disk = new() { FileSystem = "prodos", Container = container },
            Files = [new() { Source = "main.asm", Path = "PROGRAMS/NESTED/MAIN", Kind = "asm" }]
        });

        ProjectBuildResult result = ProjectBuilder.Build(project, workspace.NewPath("built.img"));

        byte[] image = File.ReadAllBytes(result.OutputPath);
        int dataOffset = container == "2mg" ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(24))) : 0;
        // Independent ProDOS encoding: Jan 1, 2000 = $0021, 12:34 = $0c22.
        const uint expectedDate = 0x0c220021;
        int directoryBlock = 2;
        foreach (string name in new[] { "PROGRAMS", "NESTED" })
        {
            int directoryOffset = dataOffset + directoryBlock * 512;
            // Each freshly built parent contains a header and one child directory.
            int childOffset = directoryOffset + 4 + 39;
            Assert.Equal(name, System.Text.Encoding.ASCII.GetString(image, childOffset + 1, name.Length));
            Assert.Equal(expectedDate, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(childOffset + 0x18)));
            directoryBlock = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(childOffset + 0x11));
            int headerOffset = dataOffset + directoryBlock * 512;
            Assert.Equal(0xe0, image[headerOffset + 4] & 0xf0);
            Assert.Equal(expectedDate, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(headerOffset + 0x1c)));
        }
        using DiskSession disk = DiskSession.Open(result.OutputPath);
        Assert.Equal(timestamp, disk.GetEntry("PROGRAMS/NESTED").Created);
        Assert.Equal(new byte[] { 0x60 }, disk.ReadFile("PROGRAMS/NESTED/MAIN"));
        Assert.DoesNotContain(disk.Verify(), diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void Build_PreflightFullDisk_RefusesAndPreservesTemplateAndExistingOutput()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(template);
        File.WriteAllBytes(workspace.NewPath("large.bin"), Enumerable.Repeat((byte)0x55, 30000).ToArray());
        string project = Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = Enumerable.Range(0, 5).Select(index => new ProjectFile
            { Source = "large.bin", Path = "FILE" + index, Origin = 0x2000, Resident = false }).ToList()
        });
        string output = workspace.NewPath("existing.do");
        File.WriteAllBytes(output, [0xa2]);
        ProjectBuildResult check = ProjectBuilder.Build(project, output, checkOnly: true);
        Assert.True(check.CheckOnly);
        Assert.Null(check.Plan);
        Assert.Empty(check.Sha256);

        DiskException error = Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, output, overwrite: true, preflight: true));

        Assert.Equal("disk_full", error.Code);
        Assert.Equal(original, File.ReadAllBytes(template));
        Assert.Equal(new byte[] { 0xa2 }, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData(false, "project.entry_exists")]
    [InlineData(true, "file_locked")]
    public void Build_PreflightLockedTemplateEntry_ReportsCollisionOrAccessFailure(bool replace, string code)
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(template);
        File.WriteAllBytes(workspace.NewPath("main.bin"), [0x60]);
        string project = Save(workspace, new()
        {
            Output = "not-created/build.do",
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = [new() { Source = "main.bin", Path = "HELLO.BIN", Origin = 0x3000, Replace = replace }]
        });
        ProjectBuilder.Build(project, checkOnly: true);

        Assert.Equal(code, Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, preflight: true)).Code);

        Assert.Equal(original, File.ReadAllBytes(template));
        Assert.False(Directory.Exists(workspace.NewPath("not-created")));
    }

    [Fact]
    public void Build_PreflightInvalidProDosName_RejectsWhatSourceChecksCannotDetect()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS\n");
        string project = Save(workspace, new()
        {
            Output = "not-created/build.po",
            Files = [new() { Source = "main.asm", Path = "INVALID*NAME", Kind = "asm" }]
        });
        ProjectBuilder.Build(project, checkOnly: true);

        Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, preflight: true));

        Assert.False(Directory.Exists(workspace.NewPath("not-created")));
    }

    [Fact]
    public void Build_PreflightWriteProtectedTemplate_RefusesWithoutChangingTemplate()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.NewPath("protected.2mg");
        DiskSession.Create(template, "prodos", container: "2mg");
        byte[] original = File.ReadAllBytes(template);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(16), 0x80000000);
        File.WriteAllBytes(template, original);
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS\n");
        string project = Save(workspace, new()
        {
            Disk = new() { Template = Path.GetFileName(template) },
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }]
        });

        Assert.Equal("image_read_only", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, preflight: true)).Code);

        Assert.Equal(original, File.ReadAllBytes(template));
        Assert.False(File.Exists(workspace.NewPath("build.po")));
    }

    [Theory]
    [InlineData("main.asm")]
    [InlineData("part.inc")]
    [InlineData("project.json")]
    [InlineData("disk.do")]
    public void Build_PreflightOutputAliasesInput_RefusesAndPreservesInput(string outputName)
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\n.include \"part.inc\"\n");
        File.WriteAllText(workspace.NewPath("part.inc"), "RTS\n");
        string project = Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }]
        });
        string output = workspace.NewPath(outputName);
        byte[] original = File.ReadAllBytes(output);

        Assert.Equal("write.source_alias", Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, output, overwrite: true, preflight: true)).Code);

        Assert.Equal(original, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData(false, false, "write.destination_exists")]
    [InlineData(true, true, "write.read_only")]
    public void Build_PreflightProtectedDestination_RefusesWithoutWriting(bool overwrite, bool readOnly, string code)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS\n");
        string project = Save(workspace, new() { Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }] });
        string output = workspace.NewPath("existing.po");
        File.WriteAllBytes(output, [0xa2]);
        if (readOnly) File.SetAttributes(output, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(code, Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, output, overwrite, preflight: true)).Code);
            Assert.Equal(new byte[] { 0xa2 }, File.ReadAllBytes(output));
        }
        finally
        {
            File.SetAttributes(output, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Build_PreflightCancelled_PreservesTemplateAndOutput()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(template);
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS\n");
        string project = Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }]
        });
        string output = workspace.NewPath("existing.do");
        File.WriteAllBytes(output, [0xa2]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ProjectBuilder.Build(project, output, overwrite: true,
            cancellationToken: cancellation.Token, preflight: true));

        Assert.Equal(original, File.ReadAllBytes(template));
        Assert.Equal(new byte[] { 0xa2 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_ConflictingCheckModes_ReportsUsageError()
    {
        Assert.Equal("project.check_mode", Assert.Throws<DiskException>(() =>
            ProjectBuilder.Build("unused.json", checkOnly: true, preflight: true)).Code);
    }

    [Fact]
    public void BuildVerified_SourceChangedAfterPreflight_PreservesTemplateAndExistingOutput()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(template);
        string source = workspace.NewPath("main.asm");
        File.WriteAllText(source, ".org $2000\nRTS\n");
        string project = Save(workspace, new()
        {
            Disk = new() { FileSystem = "dos33", Template = Path.GetFileName(template) },
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }]
        });
        string output = workspace.NewPath("existing.do");
        File.WriteAllBytes(output, [0xa2]);
        ProjectBuildResult preview = ProjectBuilder.Build(project, output, overwrite: true, preflight: true);
        File.WriteAllText(source, ".org $2000\nNOP\nRTS\n");

        DiskException error = Assert.Throws<DiskException>(() => ProjectBuilder.BuildVerified(project, output, true,
            preview, CancellationToken.None));

        Assert.Equal("project.build_changed", error.Code);
        Assert.Equal(6, error.ExitCode);
        Assert.Equal(original, File.ReadAllBytes(template));
        Assert.Equal(new byte[] { 0xa2 }, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildVerified_FinalValidationFailure_PreservesExistingOutputAndRemovesStagedImage(bool externalChange)
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS\n");
        string project = Save(workspace, new() { Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }] });
        string output = workspace.NewPath("existing.po");
        File.WriteAllBytes(output, [0xa2]);
        ProjectBuildResult preview = ProjectBuilder.Build(project, output, overwrite: true, preflight: true);
        if (!externalChange) preview = preview with { Sha256 = new string('0', 64) };
        string[] originalPaths = Directory.GetFileSystemEntries(workspace.DirectoryPath).Order().ToArray();

        DiskException error = Assert.Throws<DiskException>(() => ProjectBuilder.BuildVerified(project, output, true,
            preview, CancellationToken.None, externalChange
                ? () => throw new DiskException("project.execution_changed", "Execution inputs changed.", 6) : null));

        Assert.Equal(externalChange ? "project.execution_changed" : "project.build_changed", error.Code);
        Assert.Equal(new byte[] { 0xa2 }, File.ReadAllBytes(output));
        Assert.Equal(originalPaths, Directory.GetFileSystemEntries(workspace.DirectoryPath).Order());
    }

    [Fact]
    public void BuildVerified_UnchangedPreflight_CommitsValidatedImage()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nRTS\n");
        string project = Save(workspace, new() { Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }] });
        ProjectBuildResult preview = ProjectBuilder.Build(project, preflight: true);
        bool externalChecked = false;

        ProjectBuildResult result = ProjectBuilder.BuildVerified(project, null, false, preview,
            CancellationToken.None, () => externalChecked = true);

        Assert.True(externalChecked);
        Assert.False(result.Preflight);
        Assert.Equal(preview.Sha256, result.Sha256);
        using DiskSession disk = DiskSession.Open(result.OutputPath);
        Assert.Equal(new byte[] { 0x60 }, disk.ReadFile("MAIN"));
    }

    private static string Save(FixtureWorkspace workspace, ProjectManifest manifest)
    {
        string path = workspace.NewPath("project.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, ProjectJson.Options));
        return path;
    }
}
