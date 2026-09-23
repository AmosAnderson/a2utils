// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Operations;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectImporterTests
{
    [Theory]
    [InlineData(0x0801)]
    [InlineData(0x1001)]
    public void Import_BasicWithExistingLineReferences_PreservesOriginAndPayload(int origin)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po");
        byte[] program = ApplesoftBasic.Compile("10 GOTO 100\n", (ushort)origin);
        DiskSession.Create(source, "prodos");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Add("PROGRAM", program, "BAS", (ushort)origin);
            disk.Flush();
        }

        ProjectImportResult imported = ProjectImporter.Import(source, workspace.NewPath("imported"));
        ProjectBuildResult build = ProjectBuilder.Build(imported.ProjectPath);

        Assert.Equal("basic", Assert.Single(imported.Files).Kind);
        using DiskSession rebuilt = DiskSession.Open(build.OutputPath);
        Assert.Equal((ushort)origin, rebuilt.GetEntry("PROGRAM").AuxType);
        Assert.Equal(program, rebuilt.ReadFile("PROGRAM"));
    }

    [Theory]
    [InlineData("dos33", "HELLO\r")]
    [InlineData("prodos", "HELLO\n")]
    public void Import_TextThatCannotRoundTrip_PreservesOriginalBytes(string fileSystem, string contents)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.img");
        byte[] payload = System.Text.Encoding.ASCII.GetBytes(contents);
        DiskSession.Create(source, fileSystem);
        using (DiskSession disk = DiskSession.Open(source, inputFs: fileSystem, writable: true))
        {
            disk.Add("MESSAGE", payload, "TXT");
            disk.Flush();
        }

        ProjectImportResult imported = ProjectImporter.Import(source, workspace.NewPath("imported"),
            inputFileSystem: fileSystem);
        ProjectBuildResult build = ProjectBuilder.Build(imported.ProjectPath);

        Assert.Equal("binary", Assert.Single(imported.Files).Kind);
        Assert.Contains(imported.Diagnostics, message => message.Contains("would change the original bytes", StringComparison.Ordinal));
        using DiskSession rebuilt = DiskSession.Open(build.OutputPath);
        Assert.Equal(payload, rebuilt.ReadFile("MESSAGE"));
    }

    [Fact]
    public void Import_BasicWithZeroAuxType_PreservesOriginalMetadataAsBinary()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po");
        byte[] program = ApplesoftBasic.Compile("10 END\n");
        DiskSession.Create(source, "prodos");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Add("PROGRAM", program, "BAS", 0);
            disk.Flush();
        }

        ProjectImportResult imported = ProjectImporter.Import(source, workspace.NewPath("imported"));
        ProjectBuildResult build = ProjectBuilder.Build(imported.ProjectPath);

        Assert.Equal("binary", Assert.Single(imported.Files).Kind);
        using DiskSession rebuilt = DiskSession.Open(build.OutputPath);
        Assert.Equal(0, rebuilt.GetEntry("PROGRAM").AuxType);
        Assert.Equal(program, rebuilt.ReadFile("PROGRAM"));
    }

    [Fact]
    public void Import_DosImage_CreatesPinnedBuildableProjectAndPreservesLockedEntries()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk(name: $"import-{Guid.NewGuid():N}.do");
        string destination = workspace.NewPath("imported");

        ProjectImportResult imported = ProjectImporter.Import(source, destination);

        Assert.True(File.Exists(imported.ProjectPath));
        Assert.True(File.Exists(imported.TemplatePath));
        Assert.Contains(imported.Files, file => file.ImagePath == "README" && file.Editable && file.Kind == "text");
        Assert.Contains(imported.Files, file => file.ImagePath == "HELLO.BIN" && !file.Editable);
        ProjectBuildResult build = ProjectBuilder.Build(imported.ProjectPath);
        using DiskSession original = DiskSession.Open(source);
        using DiskSession rebuilt = DiskSession.Open(build.OutputPath);
        Assert.Equal(original.ReadFile("HELLO.BIN", raw: true), rebuilt.ReadFile("HELLO.BIN", raw: true));
        Assert.Equal(original.ReadFile("README"), rebuilt.ReadFile("README"));
    }

    [Fact]
    public void Import_WithDisassembly_ExportsLoadableBinaryAsAssemblyReference()
    {
        using FixtureWorkspace workspace = new();

        ProjectImportResult imported = ProjectImporter.Import(workspace.CopyDisk(),
            workspace.NewPath("imported"), disassembleBinaries: true);

        ImportedProjectFile binary = Assert.Single(imported.Files, file => file.ImagePath == "HELLO.BIN");
        Assert.Equal("asm", binary.Kind);
        Assert.Contains(".org $2000", File.ReadAllText(Path.Combine(imported.Directory,
            binary.SourcePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Import_SourceChangesAfterCapture_RefusesCommitAndCleansSnapshot()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk(name: $"import-mutation-{Guid.NewGuid():N}.do");
        string alternate = workspace.NewPath("alternate.do");
        ImageTransactions.Write(source, alternate, inPlace: false, overwrite: false, temporary =>
        {
            using DiskSession disk = DiskSession.Open(temporary, writable: true);
            disk.Replace("README", "MUTATED\r"u8.ToArray());
        });
        byte[] alternateBytes = File.ReadAllBytes(alternate);
        string destination = workspace.NewPath("imported");
        string? snapshotPath = null;

        DiskException error = Assert.Throws<DiskException>(() => ProjectImporter.ImportForTest(
            source, destination, snapshot =>
            {
                snapshotPath = snapshot;
                File.WriteAllBytes(source, alternateBytes);
            }, disassembleBinaries: true));

        Assert.Equal("project.import_changed", error.Code);
        Assert.NotNull(snapshotPath);
        Assert.False(File.Exists(snapshotPath));
        Assert.False(Directory.Exists(destination));
        Assert.Equal(alternateBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public void Build_EmptyProjectWithTemplate_ProducesPreservationOnlyCopy()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, """
            {
              "schemaVersion": 1,
              "output": "copy.do",
              "disk": { "fileSystem": "dos33", "template": "disk.do" },
              "files": []
            }
            """);

        ProjectBuildResult build = ProjectBuilder.Build(project);

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(build.OutputPath));
    }
}
