// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectImporterTests
{
    [Theory]
    [InlineData("prodos", false)]
    [InlineData("prodos", true)]
    [InlineData("dos33", false)]
    [InlineData("dos33", true)]
    public void Import_BinaryOutsideMainRam_PreservesReferenceAndCreatesBuildableProject(string fileSystem, bool disassemble)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.img");
        byte[] payload = [0x60, 0x2a];
        DiskSession.Create(source, fileSystem);
        using (DiskSession disk = DiskSession.Open(source, inputFs: fileSystem, writable: true))
        {
            disk.Add("BANKED", payload, "BIN", 0xd000);
            disk.Flush();
        }
        byte[] original = File.ReadAllBytes(source);

        ProjectImportResult imported = ProjectImporter.Import(source, workspace.NewPath("imported"),
            disassembleBinaries: disassemble, inputFileSystem: fileSystem);
        ProjectBuildResult built = ProjectBuilder.Build(imported.ProjectPath);

        ImportedProjectFile file = Assert.Single(imported.Files);
        Assert.False(file.Editable);
        Assert.StartsWith("reference/", file.SourcePath, StringComparison.Ordinal);
        Assert.Contains("load range", file.Reason, StringComparison.Ordinal);
        Assert.NotEmpty(imported.Diagnostics);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(imported.Directory, file.SourcePath)));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(original, File.ReadAllBytes(built.OutputPath));
        using DiskSession rebuilt = DiskSession.Open(built.OutputPath, inputFs: fileSystem);
        Assert.Equal(0xd000, rebuilt.GetEntry("BANKED").AuxType);
        Assert.Equal(payload, rebuilt.ReadFile("BANKED"));
    }

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
    public void Import_DosTextWithRecordsAfterLogicalEof_PreservesStoredBytes()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        using (DiskSession disk = DiskSession.Open(source, writable: true))
            disk.Add("RECORDS", [0xc1, 0x00, 0xc2], "TXT");
        byte[] original = File.ReadAllBytes(source);
        byte[] stored;
        using (DiskSession disk = DiskSession.Open(source))
        {
            Assert.Equal(new byte[] { 0xc1 }, disk.ReadFile("RECORDS"));
            stored = disk.ReadFile("RECORDS", raw: true);
        }

        ProjectImportResult imported = ProjectImporter.Import(source, workspace.NewPath("imported"));
        ImportedProjectFile file = Assert.Single(imported.Files, item => item.ImagePath == "RECORDS");
        Assert.False(file.Editable);
        Assert.Contains("records after its logical EOF", file.Reason, StringComparison.Ordinal);
        Assert.Equal(stored, File.ReadAllBytes(Path.Combine(imported.Directory, file.SourcePath)));
        ProjectBuildResult built = ProjectBuilder.Build(imported.ProjectPath);
        using DiskSession rebuilt = DiskSession.Open(built.OutputPath);
        Assert.Equal(stored, rebuilt.ReadFile("RECORDS", raw: true));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void Build_LegacyDosTextReplacement_PreservesRecordsUntilPayloadChanges()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        byte[] records = new byte[601];
        records[0] = 0xc1;
        records[^1] = 0xc2;
        using (DiskSession disk = DiskSession.Open(template, writable: true))
            disk.Add("RECORDS", records, "TXT");
        byte[] original = File.ReadAllBytes(template);
        byte[] stored;
        using (DiskSession disk = DiskSession.Open(template))
            stored = disk.ReadFile("RECORDS", raw: true);
        string source = workspace.NewPath("records.txt");
        File.WriteAllText(source, "A");
        ProjectManifest manifest = new()
        {
            Output = "unchanged.do",
            Disk = new() { FileSystem = "dos33", Template = "disk.do", TemplateSha256 = ProgramFiles.Hash(original) },
            Files = [new() { Source = "records.txt", Path = "RECORDS", Kind = "text", Type = "0x04", Replace = true, Resident = false }]
        };
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, System.Text.Json.JsonSerializer.Serialize(manifest, ProjectJson.Options));

        ProjectBuildResult preflight = ProjectBuilder.Build(project, preflight: true);
        Assert.False(File.Exists(preflight.OutputPath));
        Assert.Equal(preflight.Plan!.FreeBytesBefore, preflight.Plan.FreeBytesAfter);
        ProjectBuildResult built = ProjectBuilder.Build(project);
        Assert.Equal(preflight.Sha256, built.Sha256);
        using (DiskSession rebuilt = DiskSession.Open(built.OutputPath))
            Assert.Equal(stored, rebuilt.ReadFile("RECORDS", raw: true));

        File.WriteAllText(source, "Z");
        ProjectBuildResult edited = ProjectBuilder.Build(project, workspace.NewPath("edited.do"));
        using DiskSession changed = DiskSession.Open(edited.OutputPath);
        byte[] changedStored = changed.ReadFile("RECORDS", raw: true);
        Assert.Equal(0xda, changedStored[0]);
        Assert.False(changedStored.AsSpan(1).ContainsAnyExcept((byte)0));
        Assert.Equal(original, File.ReadAllBytes(template));
    }

    [Fact]
    public void Import_DataFileExceedingSourceLimit_PreservesEntryInBuildableTemplate()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("large.po");
        byte[] payload = new byte[4 * 1024 * 1024 + 1];
        payload[0] = 0x41;
        payload[^1] = 0x42;
        DiskSession.Create(source, "prodos", blocks: 10000, order: "prodos");
        using (DiskSession disk = DiskSession.Open(source, inputOrder: "prodos", writable: true))
            disk.Add("DATA", payload, "0xf2", 0x1234);
        byte[] original = File.ReadAllBytes(source);

        ProjectImportResult imported = ProjectImporter.Import(source, workspace.NewPath("imported"), inputOrder: "prodos");
        ImportedProjectFile file = Assert.Single(imported.Files);
        Assert.False(file.Editable);
        Assert.Contains("4 MiB", file.Reason, StringComparison.Ordinal);
        Assert.True(payload.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(imported.Directory, file.SourcePath))));
        ProjectBuildResult built = ProjectBuilder.Build(imported.ProjectPath);
        Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(built.OutputPath)));
        Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(source)));
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
