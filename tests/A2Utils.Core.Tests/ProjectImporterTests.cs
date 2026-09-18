using A2Utils.Core.Backends;
using A2Utils.Core.Operations;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectImporterTests
{
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
