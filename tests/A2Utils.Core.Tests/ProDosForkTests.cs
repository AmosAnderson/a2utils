// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class ProDosForkTests
{
    [Fact]
    public void ExtractRestore_IndependentSparseAndEmptyForks_PreservesBothForksAndCatalog()
    {
        using FixtureWorkspace workspace = new();
        string source = CreateForkFixture(workspace);
        byte[] original = File.ReadAllBytes(source);
        string extracted = workspace.NewPath("extracted");
        ExtractionManifest manifest;
        using (DiskSession disk = DiskSession.Open(source))
        {
            Assert.DoesNotContain(disk.Verify(), d => d.Severity == "error");
            var operations = disk.Info.Capabilities.Operations;
            Assert.True(operations.Single(c => c.Operation == "resourceForks").Supported);
            Assert.True(operations.Single(c => c.Operation == "extractAll").Supported);
            Assert.False(operations.Single(c => c.Operation == "edit").Supported);
            manifest = FileTransfer.Extract(disk, extracted, "FULLDIR");
        }
        Assert.Equal(2, manifest.SchemaVersion);
        ExtractedFile file = manifest.Entries.Single(f => f.Entry.Name == "F00");
        ExtractedFork resource = Assert.IsType<ExtractedFork>(file.ResourceFork);
        Assert.Equal(1025, file.Entry.ResourceForkLength);
        byte[] expected = new byte[1025];
        expected.AsSpan(0, 512).Fill(0x7c);
        expected[^1] = 0xab;
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(extracted, resource.HostFile)));
        Assert.Equal(ProgramFiles.Hash(expected), resource.Sha256, ignoreCase: true);
        Assert.Equal(new[] { new SparseExtent(0, 512), new SparseExtent(1024, 1) }, resource.DataExtents);
        Assert.Equal(0, manifest.Entries.Single(f => f.Entry.Name == "F01").Entry.ResourceForkLength);
        Assert.Null(manifest.Entries.Single(f => f.Entry.Name == "F02").Entry.ResourceForkLength);

        string input = workspace.NewPath("blank.po");
        DiskSession.Create(input, "prodos", order: "prodos");
        string planPath = workspace.NewPath("restore.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            changes = new[] { new DiskChange { Action = "restore", Path = "/", Source = "extracted/" + FileTransfer.ManifestName } }
        }, FileTransfer.JsonOptions));
        DiskChangePlan plan = DiskChangeSetRunner.Plan(input, planPath);
        Assert.Contains(plan.Inputs, i => i.Path == Path.Combine(extracted, resource.HostFile));
        string restored = workspace.NewPath("restored.po");
        DiskChangeSetRunner.Apply(input, planPath, restored, false, false, expectedPlanSha256: plan.PlanSha256);

        using DiskSession result = DiskSession.Open(restored);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.ReadFile("FULLDIR/F00", DiskFileFork.Data));
        Assert.Equal(expected, result.ReadFile("FULLDIR/F00", DiskFileFork.Resource));
        Assert.Equal(resource.DataExtents, result.GetSparseExtents("FULLDIR/F00", DiskFileFork.Resource));
        DiskEntry restoredEntry = result.GetEntry("FULLDIR/F00");
        Assert.Equal(file.Entry.Access, restoredEntry.Access);
        Assert.Equal(file.Entry.AuxType, restoredEntry.AuxType);
        Assert.Equal(file.Entry.Created, restoredEntry.Created);
        Assert.Equal(file.Entry.Modified, restoredEntry.Modified);
        Assert.Equal(0x54455854u, restoredEntry.HfsFileType);
        Assert.Equal(0x74747874u, restoredEntry.HfsCreator);
        Assert.True(result.GetEntry("FULLDIR/F01").HasResourceFork);
        Assert.Empty(result.ReadFile("FULLDIR/F01", DiskFileFork.Resource));
        Assert.Equal("no_resource_fork", Assert.Throws<DiskException>(() =>
            result.ReadFile("FULLDIR/F02", DiskFileFork.Resource)).Code);
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("hash", "payload_hash_mismatch")]
    [InlineData("length", "payload_length_mismatch")]
    [InlineData("extent", "invalid_sparse_extent")]
    [InlineData("version", "invalid_manifest")]
    [InlineData("missing", "invalid_manifest")]
    [InlineData("alias", "invalid_manifest")]
    public void Restore_InvalidResourcePayload_PreservesInputAndExistingOutput(string damage, string code)
    {
        using FixtureWorkspace workspace = new();
        string source = CreateForkFixture(workspace);
        string extracted = workspace.NewPath("extracted");
        using (DiskSession disk = DiskSession.Open(source))
            FileTransfer.Extract(disk, extracted, "FULLDIR/F00");
        string manifestPath = Path.Combine(extracted, FileTransfer.ManifestName);
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        JsonNode file = manifest["entries"]![0]!;
        JsonNode fork = file["resourceFork"]!;
        switch (damage)
        {
            case "hash": fork["sha256"] = "changed"; break;
            case "length": file["entry"]!["resourceForkLength"] = 1; break;
            case "extent": fork["dataExtents"] = new JsonArray(); break;
            case "version": manifest["schemaVersion"] = 1; break;
            case "missing": file.AsObject().Remove("resourceFork"); break;
            case "alias": fork["hostFile"] = (string?)file["hostFile"]; break;
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        string input = workspace.NewPath("blank.po");
        DiskSession.Create(input, "prodos", order: "prodos");
        byte[] before = File.ReadAllBytes(input);
        string output = workspace.NewPath("keep.po");
        File.WriteAllBytes(output, [0x7e]);
        Assert.Equal(code, Assert.Throws<DiskException>(() => DiskEditor.Edit(input,
            disk => FileTransfer.Restore(disk, manifestPath, outputPath: output), output, overwrite: true)).Code);
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Equal(new byte[] { 0x7e }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Compare_ResourceOnlyChange_IdentifiesForkWithoutChangingDisks()
    {
        using FixtureWorkspace workspace = new();
        string source = CreateForkFixture(workspace);
        byte[] bytes = File.ReadAllBytes(source);
        bytes[83 * 512] ^= 0xff;
        string changed = workspace.NewPath("changed.po");
        File.WriteAllBytes(changed, bytes);
        DiskEntryDifference difference = Assert.Single(DiskDiff.Compare(source, changed).Entries);
        Assert.Equal("FULLDIR/F00", difference.Path);
        Assert.Equal(new[] { "resourceFork" }, difference.ChangedFields);
        Assert.NotEqual(difference.Before!.ResourceForkSha256, difference.After!.ResourceForkSha256);
        Assert.Equal(bytes, File.ReadAllBytes(changed));
        Assert.Equal("unsafe_allocation", Assert.Throws<DiskException>(() => DiskSession.Open(source, writable: true)).Code);
    }

    [Fact]
    public void Extract_DamagedExtendedKeyBlock_ReportsDamageAndPublishesNoFiles()
    {
        using FixtureWorkspace workspace = new();
        string source = CreateForkFixture(workspace);
        byte[] bytes = File.ReadAllBytes(source);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(43 * 512 + 4 + 39 + 0x11), 0xffff);
        File.WriteAllBytes(source, bytes);
        using DiskSession disk = DiskSession.Open(source);
        Assert.True(disk.GetEntry("FULLDIR/F00").IsDubious);
        Assert.Null(disk.GetEntry("FULLDIR/F00").ResourceForkLength);
        string extracted = workspace.NewPath("extract");
        Assert.Equal("damaged_entry", Assert.Throws<DiskException>(() => FileTransfer.Extract(disk, extracted)).Code);
        Assert.False(Directory.Exists(extracted));
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    // Construct extended directory entries, fork descriptors, and allocation bits directly
    // from the documented ProDOS layout. All modifications are on a workspace copy.
    private static string CreateForkFixture(FixtureWorkspace workspace)
    {
        byte[] bytes = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-prodos.po"));
        for (int block = 80; block <= 85; block++)
        {
            int map = 6 * 512 + block / 8;
            byte bit = (byte)(0x80 >> (block % 8));
            Assert.NotEqual(0, bytes[map] & bit);
            bytes[map] &= (byte)~bit;
            bytes.AsSpan(block * 512, 512).Clear();
        }
        for (int index = 0; index < 2; index++)
        {
            int entry = 43 * 512 + 4 + (index + 1) * 39;
            bytes[entry] = 0x53; // Extended file, three-character name.
            int key = index == 0 ? 80 : 84;
            Write16(entry + 0x11, key);
            Write16(entry + 0x13, index == 0 ? 5 : 3);
            Write24(entry + 0x15, 512);
            WriteFork(key * 512, 1, 44 + index, 1, index == 0 ? 3 : 0);
            if (index == 0)
            {
                WriteFork(key * 512 + 256, 2, 81, 3, 1025);
                bytes[key * 512 + 8] = 18;
                bytes[key * 512 + 9] = 1;
                "TEXTttxt"u8.CopyTo(bytes.AsSpan(key * 512 + 10));
                bytes[entry + 0x1e] = 0x21;
            }
            else WriteFork(key * 512 + 256, 1, 85, 1, 0);
        }
        new byte[] { 1, 2, 3 }.CopyTo(bytes, 44 * 512);
        bytes[81 * 512] = 82;
        bytes[81 * 512 + 2] = 83;
        bytes.AsSpan(82 * 512, 512).Fill(0x7c);
        bytes[83 * 512] = 0xab;
        string path = workspace.NewPath("forked.po");
        File.WriteAllBytes(path, bytes);
        return path;

        void WriteFork(int offset, byte storage, int key, int blocks, int length)
        {
            bytes[offset] = storage;
            Write16(offset + 1, key);
            Write16(offset + 3, blocks);
            Write24(offset + 5, length);
        }
        void Write16(int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);
        void Write24(int offset, int value)
        {
            Write16(offset, value);
            bytes[offset + 2] = (byte)(value >> 16);
        }
    }
}
