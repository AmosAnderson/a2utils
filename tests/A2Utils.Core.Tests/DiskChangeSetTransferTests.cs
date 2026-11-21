// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class DiskChangeSetTransferTests
{
    [Fact]
    public void Apply_SequentialSameImageCopyAndMove_UsesEarlierChangesAndPreservesInput()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("input.po");
        DiskSession.Create(input, "prodos", order: "prodos", volumeName: "PLAN");
        byte[] original = File.ReadAllBytes(input);
        string planPath = WritePlan(workspace,
            new DiskChange() { Action = "add", Path = "MAIN", Hex = "A9 42 60", Type = "BIN", AuxType = 0x2000 },
            new DiskChange() { Action = "mkdir", Path = "TOOLS" },
            new DiskChange() { Action = "copy", SourcePath = "MAIN", Path = "TOOLS/COPY" },
            new DiskChange() { Action = "move", SourcePath = "MAIN", Path = "TOOLS/MOVED" });

        DiskChangePlan plan = DiskChangeSetRunner.Plan(input, planPath);
        string output = workspace.NewPath("output.po");
        DiskApplyResult result = DiskChangeSetRunner.Apply(input, planPath, output, false, false,
            expectedPlanSha256: plan.PlanSha256);

        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(plan.CandidateSha256, result.OutputSha256);
        using DiskSession disk = DiskSession.Open(output);
        Assert.Equal(new byte[] { 0xa9, 0x42, 0x60 }, disk.ReadFile("TOOLS/COPY"));
        Assert.Equal(disk.ReadFile("TOOLS/COPY"), disk.ReadFile("TOOLS/MOVED"));
        Assert.Equal(0x2000, disk.GetEntry("TOOLS/MOVED").AuxType);
        Assert.DoesNotContain(disk.List(), entry => entry.Path == "MAIN");
    }

    [Fact]
    public void Apply_ExternalRecursiveCopy_PinsImageAndPreservesMetadata()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("input.po");
        string source = workspace.NewPath("source.po");
        DiskSession.Create(input, "prodos", order: "prodos", volumeName: "TARGET");
        DiskSession.Create(source, "prodos", order: "prodos", volumeName: "SOURCE");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Mkdir("Tools");
            disk.Add("Tools/Program", [0x60], "BIN", 0x2000);
            disk.SetTimestamps("Tools/Program", new(2001, 2, 3), new(2004, 5, 6));
            disk.SetAttributes("Tools/Program", locked: true);
        }
        byte[] original = File.ReadAllBytes(source);
        string changes = WritePlan(workspace, new DiskChange()
        {
            Action = "copy",
            SourceImage = "source.po",
            SourcePath = "Tools",
            SourceOrder = "prodos",
            SourceFileSystem = "prodos",
            Path = "UTILS",
            Recursive = true,
            ExpectedSourceSha256 = ProgramFiles.Hash(original)
        });

        DiskChangePlan plan = DiskChangeSetRunner.Plan(input, changes);
        string output = workspace.NewPath("output.po");
        DiskChangeSetRunner.Apply(input, changes, output, false, false, expectedPlanSha256: plan.PlanSha256);

        Assert.Equal(source, Assert.Single(plan.Inputs).Path);
        Assert.Equal(original, File.ReadAllBytes(source));
        using DiskSession result = DiskSession.Open(output);
        DiskEntry entry = result.GetEntry("UTILS/Program");
        Assert.True(entry.IsLocked);
        Assert.Equal(0x2000, entry.AuxType);
        Assert.Equal(new DateTime(2001, 2, 3), entry.Created);
        Assert.Equal(new DateTime(2004, 5, 6), entry.Modified);
        Assert.Equal(new byte[] { 0x60 }, result.ReadFile("UTILS/Program"));
    }

    [Theory]
    [InlineData("dos33")]
    [InlineData("prodos")]
    public void Apply_TextDirectoryImport_ConvertsTextAndUsesDeterministicDates(string fileSystem)
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("input.po");
        DiskSession.Create(input, fileSystem, order: "prodos", volumeName: "PLAN");
        string directory = workspace.NewPath("files");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README"), "HELLO\n");
        string changes = WritePlan(workspace,
            new DiskChange() { Action = "import", Source = "files", Path = "/", Format = "text" });

        DiskChangePlan plan = DiskChangeSetRunner.Plan(input, changes);
        string output = workspace.NewPath("output.po");
        DiskChangeSetRunner.Apply(input, changes, output, false, false, expectedPlanSha256: plan.PlanSha256);

        Assert.Contains(plan.Inputs, item => item.Kind == "directory" && item.Path == directory);
        using DiskSession result = DiskSession.Open(output);
        Assert.Equal(AppleTextCodec.Encode("HELLO\n"u8, fileSystem), result.ReadFile("README"));
        if (fileSystem == "prodos")
            Assert.Equal(new DateTime(2000, 1, 1), result.GetEntry("README").Created);
    }

    [Fact]
    public void Apply_RecursiveImport_PreservesEmptyDirectoriesAndPinsMembership()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("input.po");
        DiskSession.Create(input, "prodos", order: "prodos", volumeName: "PLAN");
        string directory = workspace.NewPath("files");
        Directory.CreateDirectory(Path.Combine(directory, "EMPTY"));
        Directory.CreateDirectory(Path.Combine(directory, "TOOLS"));
        File.WriteAllBytes(Path.Combine(directory, "TOOLS", "PROGRAM"), [0x60]);
        string changes = WritePlan(workspace,
            new DiskChange() { Action = "mkdir", Path = "ROOT" },
            new DiskChange()
            {
                Action = "import",
                Source = "files",
                Path = "ROOT",
                Type = "BIN",
                AuxType = 0x2000,
                Recursive = true
            });

        DiskChangePlan plan = DiskChangeSetRunner.Plan(input, changes);
        string output = workspace.NewPath("output.po");
        DiskChangeSetRunner.Apply(input, changes, output, false, false, expectedPlanSha256: plan.PlanSha256);

        Assert.Equal(3, plan.Inputs.Count(item => item.Kind == "directory"));
        using DiskSession result = DiskSession.Open(output);
        Assert.True(result.GetEntry("ROOT/EMPTY").IsDirectory);
        Assert.Equal(new byte[] { 0x60 }, result.ReadFile("ROOT/TOOLS/PROGRAM"));
        Assert.Equal(0x2000, result.GetEntry("ROOT/TOOLS/PROGRAM").AuxType);
        Assert.All(result.List(recursive: true), entry =>
            Assert.Equal(new DateTime(2000, 1, 1), entry.Modified));
    }

    [Fact]
    public void Apply_ManifestRestore_PinsEveryPayloadAndPreservesStoredMetadata()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string input = workspace.NewPath("input.do");
        DiskSession.Create(input, "dos33");
        string extracted = workspace.NewPath("extracted");
        using (DiskSession disk = DiskSession.Open(source))
            FileTransfer.Extract(disk, extracted);
        string changes = WritePlan(workspace,
            new DiskChange() { Action = "restore", Source = "extracted/a2-manifest.json", Path = "/" },
            new DiskChange() { Action = "move", SourcePath = "README", Path = "NOTES" });

        DiskChangePlan plan = DiskChangeSetRunner.Plan(input, changes);
        string output = workspace.NewPath("output.do");
        DiskChangeSetRunner.Apply(input, changes, output, false, false, expectedPlanSha256: plan.PlanSha256);

        Assert.Equal(3, plan.Inputs.Count);
        using DiskSession original = DiskSession.Open(source);
        using DiskSession result = DiskSession.Open(output);
        Assert.Equal(original.ReadFile("HELLO.BIN", raw: true), result.ReadFile("HELLO.BIN", raw: true));
        Assert.Equal(original.GetEntry("HELLO.BIN").Access, result.GetEntry("HELLO.BIN").Access);
        Assert.Equal(original.ReadFile("README", raw: true), result.ReadFile("NOTES", raw: true));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("import-file")]
    [InlineData("import-membership")]
    [InlineData("restore-payload")]
    public void Apply_SourceChangesAfterCandidateValidation_PreservesImageAndExistingOutput(string kind)
    {
        using FixtureWorkspace workspace = new();
        (string input, string changes, string source) = PrepareExternal(workspace, kind);
        byte[] original = File.ReadAllBytes(input);
        string output = workspace.NewPath("output.do");
        File.WriteAllBytes(output, [7, 8, 9]);

        DiskException error = Assert.Throws<DiskException>(() => DiskChangeSetRunner.ApplyCore(
            input, changes, output, false, true, candidateValidated: () =>
            {
                if (kind == "import-membership") File.WriteAllBytes(Path.Combine(source, "NEW"), [1]);
                else
                {
                    using FileStream stream = new(source, FileMode.Open, FileAccess.ReadWrite);
                    int first = stream.ReadByte();
                    stream.Position = 0;
                    stream.WriteByte((byte)(first ^ 1));
                }
            }));

        Assert.Equal("disk_plan.source_changed", error.Code);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("import-file")]
    [InlineData("restore-payload")]
    public void Apply_OutputAliasesExternalInput_RefusesAndPreservesSource(string kind)
    {
        using FixtureWorkspace workspace = new();
        (string input, string changes, string source) = PrepareExternal(workspace, kind);
        byte[] original = File.ReadAllBytes(source);

        DiskException error = Assert.Throws<DiskException>(() =>
            DiskChangeSetRunner.Apply(input, changes, source, false, true));

        Assert.Contains(error.Code, new[] { "write.source_alias", "disk_plan.output_source_directory" });
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void Apply_OutputInsideImportDirectory_RefusesBeforeCreatingDestination()
    {
        using FixtureWorkspace workspace = new();
        (string input, string changes, string source) = PrepareExternal(workspace, "import-membership");
        string output = Path.Combine(source, "OUTPUT");

        DiskException error = Assert.Throws<DiskException>(() =>
            DiskChangeSetRunner.Apply(input, changes, output, false, false));

        Assert.Equal("disk_plan.output_source_directory", error.Code);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    [InlineData("import")]
    [InlineData("restore")]
    public void Apply_TransferCollision_PreservesOriginalAndOutput(string action)
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(input);
        string output = workspace.NewPath("output.do");
        File.WriteAllBytes(output, [7, 8, 9]);
        string directory = workspace.NewPath("files");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "README"), [1]);
        using (DiskSession disk = DiskSession.Open(input))
            FileTransfer.Extract(disk, workspace.NewPath("extracted"));
        DiskChange operation = action switch
        {
            "copy" => new DiskChange() { Action = action, SourcePath = "README", Path = "HELLO.BIN" },
            "move" => new DiskChange() { Action = action, SourcePath = "README", Path = "HELLO.BIN" },
            "import" => new DiskChange() { Action = action, Source = "files", Path = "/", Type = "T" },
            _ => new DiskChange() { Action = action, Source = "extracted/a2-manifest.json", Path = "/" }
        };
        string changes = WritePlan(workspace, operation);

        Assert.Throws<DiskException>(() => DiskChangeSetRunner.Apply(input, changes, output, false, true));

        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Plan_ExternalCopyAcrossFilesystems_RefusesImplicitMigration()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string input = workspace.NewPath("input.po");
        DiskSession.Create(input, "prodos", order: "prodos", volumeName: "PLAN");
        string changes = WritePlan(workspace, new DiskChange()
        {
            Action = "copy",
            SourceImage = Path.GetFileName(source),
            SourcePath = "README",
            Path = "README"
        });

        DiskException error = Assert.Throws<DiskException>(() => DiskChangeSetRunner.Plan(input, changes));

        Assert.Equal("filesystem_mismatch", error.Code);
    }

    private static (string Input, string Changes, string Source) PrepareExternal(
        FixtureWorkspace workspace, string kind)
    {
        string input = workspace.NewPath("input.do");
        DiskSession.Create(input, "dos33");
        if (kind == "image")
        {
            string source = workspace.CopyDisk();
            return (input, WritePlan(workspace, new DiskChange()
            {
                Action = "copy",
                SourceImage = Path.GetFileName(source),
                SourcePath = "README",
                Path = "README"
            }), source);
        }
        if (kind == "restore-payload")
        {
            string source = workspace.CopyDisk();
            string extracted = workspace.NewPath("extracted");
            ExtractionManifest manifest;
            using (DiskSession disk = DiskSession.Open(source))
                manifest = FileTransfer.Extract(disk, extracted);
            return (input, WritePlan(workspace, new DiskChange()
            {
                Action = "restore",
                Source = "extracted/a2-manifest.json",
                Path = "/"
            }), Path.Combine(extracted, manifest.Entries.First(entry => !entry.Entry.IsDirectory).HostFile!));
        }
        string directory = workspace.NewPath("files");
        Directory.CreateDirectory(directory);
        string payload = Path.Combine(directory, "README");
        File.WriteAllBytes(payload, [1, 2, 3]);
        return (input, WritePlan(workspace,
            new DiskChange() { Action = "import", Source = "files", Path = "/", Type = "T" }),
            kind == "import-membership" ? directory : payload);
    }

    private static string WritePlan(FixtureWorkspace workspace, params DiskChange[] changes)
    {
        string path = workspace.NewPath("changes.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new DiskChangeSet { Changes = changes },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return path;
    }
}
