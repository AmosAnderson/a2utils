// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class DiskMigrationTests
{
    [Theory]
    [InlineData(0xc3, false)]
    [InlineData(0x01, false)]
    [InlineData(0xe3, true)]
    [InlineData(0x21, true)]
    public void Plan_ProDosAccess_ReportsOnlyUnrepresentableFlags(byte access, bool losesAccess)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po");
        string destination = workspace.CopyDisk();
        DiskSession.Create(source, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Add("BIN", [1, 2], "BIN", 0x2000);
            DiskEntry entry = disk.GetEntry("BIN") with { Access = access, IsLocked = (access & 2) == 0 };
            disk.Delete("BIN");
            disk.RestoreFile(entry, [1, 2], [new(0, 2)]);
        }
        DiskMigrationPlan plan = DiskMigration.Plan(source, "BIN", destination, "NEW");
        Assert.Equal(losesAccess, plan.MetadataLosses.Contains("access"));
    }

    [Fact]
    public void Apply_IndependentDosBinary_PreservesInputsPayloadAddressAndLock()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string destination = workspace.NewPath("prodos.po");
        File.Copy(FixtureWorkspace.FindFixture("independent-prodos.po"), destination);
        byte[] sourceBefore = File.ReadAllBytes(source);
        byte[] destinationBefore = File.ReadAllBytes(destination);
        DiskMigrationPlan plan = DiskMigration.Plan(source, "HELLO.BIN", destination, "MIGRATED");
        Assert.Equal(plan.PlanSha256, DiskMigration.Plan(source, "HELLO.BIN", destination, "MIGRATED").PlanSha256);
        string output = workspace.NewPath("result.po");

        DiskMigrationResult result = DiskMigration.Apply(source, "HELLO.BIN", destination, "MIGRATED",
            output, expectedPlanSha256: plan.PlanSha256);

        using DiskSession original = DiskSession.Open(source);
        using DiskSession migrated = DiskSession.Open(output);
        Assert.Equal(original.ReadFile("HELLO.BIN"), migrated.ReadFile("MIGRATED"));
        Assert.Equal(original.GetEntry("HELLO.BIN").AuxType, migrated.GetEntry("MIGRATED").AuxType);
        Assert.True(migrated.GetEntry("MIGRATED").IsLocked);
        Assert.Equal(plan.CandidateSha256, result.Plan.CandidateSha256);
        Assert.Equal(sourceBefore, File.ReadAllBytes(source));
        Assert.Equal(destinationBefore, File.ReadAllBytes(destination));
        using DiskSession before = DiskSession.Open(destination);
        foreach (DiskEntry entry in before.List(recursive: true).Where(entry => !entry.IsDirectory))
        {
            Assert.Equivalent(before.GetEntry(entry.Path), migrated.GetEntry(entry.Path), strict: true);
            Assert.Equal(before.ReadFile(entry.Path), migrated.ReadFile(entry.Path));
        }
    }

    [Theory]
    [InlineData("BIN")]
    [InlineData("BAS")]
    [InlineData("TXT")]
    public void Apply_ProDosToDos_RequiresLossAcceptanceAndPreservesSupportedPayload(string type)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po");
        string destination = workspace.CopyDisk();
        DiskSession.Create(source, "prodos", order: "prodos");
        byte[] payload = type == "BAS" ? ApplesoftBasic.Compile("10 PRINT \"HELLO\"\n") : [65, 66, 13];
        ushort aux = type switch { "BIN" => 0x4000, "BAS" => 0x0801, _ => 0 };
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Add("PROGRAM", payload, type, aux);
            disk.SetTimestamps("PROGRAM", new(2001, 1, 1), new(2002, 1, 1));
        }
        byte[] original = File.ReadAllBytes(destination);
        string output = workspace.NewPath("out.do");
        File.WriteAllBytes(output, [42]);
        Assert.Equal("migration.metadata_loss", Assert.Throws<DiskException>(() =>
            DiskMigration.Apply(source, "PROGRAM", destination, "NEW", output, overwrite: true)).Code);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(output));

        DiskMigration.Apply(source, "PROGRAM", destination, "NEW", output, overwrite: true, allowMetadataLoss: true);

        using DiskSession result = DiskSession.Open(output);
        Assert.Equal(payload, result.ReadFile("NEW"));
        Assert.Equal(aux, result.GetEntry("NEW").AuxType);
        Assert.Equal(original, File.ReadAllBytes(destination));
    }

    [Fact]
    public void Apply_TextConversion_ChangesEncodingOnlyWhenExplicit()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.do");
        string destination = workspace.NewPath("destination.po");
        DiskSession.Create(source, "dos33");
        DiskSession.Create(destination, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
            disk.Add("TEXT", AppleTextCodec.Encode("HELLO\n"u8, "dos33"), "TXT");
        string output = workspace.NewPath("out.po");

        DiskMigration.Apply(source, "TEXT", destination, "TEXT", output, options: new() { TextMode = "convert" });

        using DiskSession result = DiskSession.Open(output);
        Assert.Equal("HELLO\r"u8.ToArray(), result.ReadFile("TEXT"));
    }

    [Fact]
    public void Apply_SourceChangedAfterCandidate_RefusesAndPreservesDestination()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string destination = workspace.NewPath("destination.po");
        DiskSession.Create(destination, "prodos", order: "prodos");
        byte[] before = File.ReadAllBytes(destination);
        string output = workspace.NewPath("out.po");
        File.WriteAllBytes(output, [7, 8]);

        DiskException error = Assert.Throws<DiskException>(() => DiskMigration.ApplyCore(source, "HELLO.BIN",
            destination, "NEW", output, overwrite: true, candidatePrepared: () =>
            {
                byte[] changed = File.ReadAllBytes(source);
                changed[0] ^= 1;
                File.WriteAllBytes(source, changed);
            }));

        Assert.Equal("migration.input_changed", error.Code);
        Assert.Equal(before, File.ReadAllBytes(destination));
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Apply_CollisionOrStalePlan_PreservesBothInputsAndExistingOutput()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string destination = workspace.NewPath("destination.po");
        DiskSession.Create(destination, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(destination, writable: true))
            disk.Add("EXISTS", [1], "BIN", 0x2000);
        byte[] before = File.ReadAllBytes(destination);
        string output = workspace.NewPath("out.po");
        File.WriteAllBytes(output, [99]);

        Assert.Throws<DiskException>(() => DiskMigration.Apply(source, "HELLO.BIN", destination, "EXISTS", output, overwrite: true));
        Assert.Equal("migration.plan_changed", Assert.Throws<DiskException>(() =>
            DiskMigration.Apply(source, "HELLO.BIN", destination, "NEW", output,
                overwrite: true, expectedPlanSha256: new string('0', 64))).Code);
        Assert.Throws<DiskException>(() => DiskMigration.Apply(source, "HELLO.BIN", destination, "NEW", source, overwrite: true));
        Assert.Equal(before, File.ReadAllBytes(destination));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Apply_InPlace_KeepsBackup()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string destination = workspace.NewPath("destination.po");
        DiskSession.Create(destination, "prodos", order: "prodos");
        byte[] before = File.ReadAllBytes(destination);

        DiskMigrationResult result = DiskMigration.Apply(source, "HELLO.BIN", destination, "NEW", inPlace: true);

        Assert.Equal(before, File.ReadAllBytes(result.Write.BackupPath!));
        using DiskSession disk = DiskSession.Open(destination);
        Assert.True(disk.GetEntry("NEW").IsLocked);
    }

    [Theory]
    [InlineData("preserve")]
    [InlineData("convert")]
    public void Plan_DosRandomAccessText_RefusesSilentRecordTruncation(string mode)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.do");
        string destination = workspace.NewPath("destination.po");
        DiskSession.Create(source, "dos33");
        DiskSession.Create(destination, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
            disk.Add("RECORDS", [0xc1, 0, 0xc2], "TXT");
        byte[] before = File.ReadAllBytes(source);
        Assert.Equal("migration.text_records", Assert.Throws<DiskException>(() =>
            DiskMigration.Plan(source, "RECORDS", destination, "TEXT", new() { TextMode = mode })).Code);
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void Apply_FullDestination_PreservesInputsAndExistingOutput()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po");
        string destination = workspace.NewPath("destination.do");
        DiskSession.Create(source, "prodos", order: "prodos");
        DiskSession.Create(destination, "dos33");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
            disk.Add("PROGRAM", new byte[20000], "BIN", 0x2000);
        using (DiskSession disk = DiskSession.Open(destination, writable: true))
        {
            disk.Add("FILL1", new byte[60000], "BIN", 0);
            disk.Add("FILL2", new byte[60000], "BIN", 0);
        }
        byte[] before = File.ReadAllBytes(destination);
        string output = workspace.NewPath("out.do");
        File.WriteAllBytes(output, [13, 37]);
        Assert.Equal("disk_full", Assert.Throws<DiskException>(() =>
            DiskMigration.Apply(source, "PROGRAM", destination, "NEW", output,
                overwrite: true, allowMetadataLoss: true)).Code);
        Assert.Equal(before, File.ReadAllBytes(destination));
        Assert.Equal(new byte[] { 13, 37 }, File.ReadAllBytes(output));
    }
}
