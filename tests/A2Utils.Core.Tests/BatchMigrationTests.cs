// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using A2Utils.Core.Backends;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class BatchMigrationTests
{
    [Fact]
    public void ApplyBatch_IndependentImages_PreservesExistingEntriesAndConvertsOnlySelectedText()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string destination = workspace.NewPath("destination.po");
        File.Copy(FixtureWorkspace.FindFixture("independent-prodos.po"), destination);
        string sourceHash = Hash(source), destinationHash = Hash(destination);
        MigrationSelection selection = new(1, [new("HELLO.BIN", "FULLDIR/PROGRAM"), new("README", "TEXT", "convert"),
            new("README", "ORIGINAL")]);
        BatchMigrationPlan plan = DiskMigration.PlanBatch(source, destination, selection);
        Assert.True(plan.CanApply);
        Assert.True(plan.FreeBytesAfter < plan.FreeBytesBefore);
        Assert.All(plan.Files, file => Assert.Empty(file.Diagnostics));
        string output = workspace.NewPath("output.po");
        BatchMigrationResult applied = DiskMigration.ApplyBatch(source, destination, selection, output, expectedPlanSha256: plan.PlanSha256);
        Assert.Equal(plan.PlanSha256, applied.Plan.PlanSha256);
        Assert.Equal(plan.CandidateSha256, Hash(output));
        Assert.Equal(sourceHash, Hash(source));
        Assert.Equal(destinationHash, Hash(destination));
        using DiskSession before = DiskSession.Open(destination);
        using DiskSession after = DiskSession.Open(output);
        Assert.Equal(new byte[] { 0, 0x7f, 0x80, 0xff, 13 }, after.ReadFile("FULLDIR/PROGRAM"));
        Assert.Equal(0x2000, after.GetEntry("FULLDIR/PROGRAM").AuxType);
        Assert.True(after.GetEntry("FULLDIR/PROGRAM").IsLocked);
        Assert.Equal("A2UTILS\r"u8.ToArray(), after.ReadFile("TEXT"));
        Assert.Equal(Convert.FromHexString("C1B2D5D4C9CCD38D"), after.ReadFile("ORIGINAL"));
        foreach (DiskEntry entry in before.List(recursive: true).Where(entry => !entry.IsDirectory))
        {
            Assert.Equivalent(entry, after.GetEntry(entry.Path), strict: true);
            Assert.Equal(before.ReadFile(entry.Path), after.ReadFile(entry.Path));
        }
    }

    [Fact]
    public void ApplyBatch_All_FlattensDirectoriesAndRefusesLossUntilAccepted()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po"), destination = workspace.CopyDisk();
        DiskSession.Create(source, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Mkdir("DIR");
            disk.Add("DIR/CODE", [0x60], "BIN", 0x2000);
            disk.SetAttributes("DIR/CODE", locked: true);
            disk.Add("ROOT", "line\r"u8.ToArray(), "TXT", 42);
        }
        BatchMigrationPlan plan = DiskMigration.PlanBatch(source, destination);
        Assert.True(plan.CanApply);
        Assert.Contains(plan.Files, item => item.File.Source == "DIR/CODE" && item.File.Destination == "CODE");
        Assert.Contains("directoryPath", plan.Files.Single(item => item.File.Source == "DIR/CODE").MetadataLosses);
        Assert.Contains("auxType", plan.Files.Single(item => item.File.Source == "ROOT").MetadataLosses);
        string original = Hash(destination);
        Assert.Equal("migration.metadata_loss", Assert.Throws<DiskException>(() =>
            DiskMigration.ApplyBatch(source, destination, inPlace: true)).Code);
        Assert.Equal(original, Hash(destination));
        BatchMigrationResult applied = DiskMigration.ApplyBatch(source, destination, inPlace: true,
            allowMetadataLoss: true, expectedPlanSha256: plan.PlanSha256);
        Assert.Equal(original, Hash(applied.Write.BackupPath!));
        Assert.Equal(plan.CandidateSha256, Hash(destination));
        using DiskSession result = DiskSession.Open(destination);
        Assert.Equal(new byte[] { 0x60 }, result.ReadFile("CODE"));
        Assert.True(result.GetEntry("CODE").IsLocked);
        Assert.Equal("line\r"u8.ToArray(), result.ReadFile("ROOT"));
    }

    [Fact]
    public void ApplyBatch_CollectiveCapacityFailure_DiscardsEarlierStagedWrites()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po"), destination = workspace.NewPath("destination.do");
        DiskSession.Create(source, "prodos", order: "prodos");
        DiskSession.Create(destination, "dos33");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Add("A", new byte[20000], "BIN", 0x2000);
            disk.Add("B", new byte[20000], "BIN", 0x2000);
        }
        using (DiskSession disk = DiskSession.Open(destination, writable: true))
        {
            disk.Add("FILL1", new byte[50000], "BIN");
            disk.Add("FILL2", new byte[50000], "BIN");
        }
        Assert.True(DiskMigration.PlanBatch(source, destination, new(1, [new("A", "A")])).CanApply);
        BatchMigrationPlan plan = DiskMigration.PlanBatch(source, destination);
        Assert.False(plan.CanApply);
        Assert.Null(plan.CandidateSha256);
        Assert.Empty(plan.Files[0].Diagnostics);
        Assert.Contains(plan.Files[1].Diagnostics, issue => issue.Code == "disk_full");
        string output = workspace.NewPath("keep.img");
        File.WriteAllText(output, "keep");
        string original = Hash(destination);
        Assert.Equal("migration.batch_blocked", Assert.Throws<DiskException>(() =>
            DiskMigration.ApplyBatch(source, destination, outputPath: output, overwrite: true, allowMetadataLoss: true)).Code);
        Assert.Equal("keep", File.ReadAllText(output));
        Assert.Equal(original, Hash(destination));
    }

    [Fact]
    public void PlanBatch_DefaultNames_ReportsFlattenedCollisionsAndLiteralDosNames()
    {
        using FixtureWorkspace workspace = new();
        string prodos = workspace.NewPath("source.po"), dos = workspace.CopyDisk();
        DiskSession.Create(prodos, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(prodos, writable: true))
        {
            disk.Mkdir("DIR");
            disk.Add("DIR/ITEM", [1], "BIN");
            disk.Add("ITEM", [2], "BIN");
        }
        using (DiskSession disk = DiskSession.Open(dos, writable: true))
            disk.Add("DIR/ENTRY", [3], "BIN");
        BatchMigrationPlan flattened = DiskMigration.PlanBatch(prodos, dos);
        Assert.False(flattened.CanApply);
        Assert.All(flattened.Files, item => Assert.Contains(item.Diagnostics, issue => issue.Code == "migration.name_collision"));
        BatchMigrationPlan literal = DiskMigration.PlanBatch(dos, prodos);
        Assert.False(literal.CanApply);
        Assert.Contains(literal.Files.Single(item => item.File.Source == "DIR/ENTRY").Diagnostics, item => item.Code == "invalid_name");
        Assert.True(DiskMigration.PlanBatch(dos, prodos, new(1, [new("DIR/ENTRY", "DIR/VALID")])).CanApply);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("destination")]
    [InlineData("source")]
    public void ApplyBatch_ChangedOrInvalidSelection_PreservesExistingOutput(string change)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk(), destination = workspace.NewPath("destination.po");
        DiskSession.Create(destination, "prodos", order: "prodos");
        string output = workspace.NewPath("keep.po");
        File.WriteAllText(output, "keep");
        MigrationSelection selected = new(1, [new("HELLO.BIN", "NEW")]);
        BatchMigrationPlan plan = DiskMigration.PlanBatch(source, destination, selected);
        if (change == "invalid") selected = new(1, [new("HELLO.BIN", "NEW", "unknown")]);
        else
        {
            using DiskSession disk = DiskSession.Open(change == "source" ? source : destination, writable: true);
            disk.Add("ADDED", [1], "BIN");
        }
        string sourceHash = Hash(source), destinationHash = Hash(destination);
        Assert.Equal(change == "invalid" ? "migration.invalid_selection" : "migration.plan_changed", Assert.Throws<DiskException>(() =>
            DiskMigration.ApplyBatch(source, destination, selected, output, overwrite: true, expectedPlanSha256: plan.PlanSha256)).Code);
        Assert.Equal(sourceHash, Hash(source));
        Assert.Equal(destinationHash, Hash(destination));
        Assert.Equal("keep", File.ReadAllText(output));
    }

    [Fact]
    public void PlanBatch_ResourceFork_RefusesLossAndRetainsOtherFileDiagnostics()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("extended.po"), destination = workspace.CopyDisk();
        DiskSession.Create(source, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Add("EXTENDED", [1], "BIN", 0x2000);
            DiskEntry metadata = disk.GetEntry("EXTENDED") with { HasResourceFork = true, ResourceForkLength = 1 };
            disk.Delete("EXTENDED");
            disk.RestoreFile(metadata, [1], [new(0, 1)], [2], [new(0, 1)]);
        }
        BatchMigrationPlan plan = DiskMigration.PlanBatch(source, destination, new(1, [new("EXTENDED", "NEW"), new("ABSENT", "MORE")]));
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Files[0].Diagnostics, issue => issue.Code == "unsupported_forks");
        Assert.NotEmpty(plan.Files[1].Diagnostics);
        Assert.Null(plan.FreeBytesAfter);
        Assert.Null(plan.CandidateSha256);
    }

    private static string Hash(string path) => ProgramFiles.Hash(File.ReadAllBytes(path));
}
