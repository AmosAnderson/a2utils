using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class IndependentProdosTests
{
    private const string FixtureHash = "806223a7a89ea1581cb4628151b6e83a3d8db706dab306f60a1aec5efd544c0e";

    [Fact]
    public void Open_IndependentProdosFixture_ReadsKnownCatalogAllocationAndStorageBoundaries()
    {
        string path = FixtureWorkspace.FindFixture("independent-prodos.po");
        byte[] original = File.ReadAllBytes(path);
        Assert.Equal(FixtureHash, Convert.ToHexStringLower(SHA256.HashData(original)));
        using (DiskSession session = DiskSession.Open(path))
        {
            Assert.Equal("prodos", session.Info.FileSystem);
            Assert.Equal("INDEPENDENT", session.Info.VolumeName);
            Assert.Equal(231 * 512, session.Info.FreeBytes);
            Assert.False(session.Info.IsDubious);
            Assert.DoesNotContain(session.Verify(), diagnostic => diagnostic.Severity is "error" or "warning");
            Assert.Equal(new[] { "SEED", "EDGE512", "SAP513", "SPARSE", "TREE", "FULLDIR", "GROWNDIR" },
                session.List().Select(entry => entry.Name));
            Assert.Equal(32, session.List(recursive: true).Count);
            Assert.Equal(12, session.List("FULLDIR").Count);
            Assert.Equal(13, session.List("GROWNDIR").Count);
            Assert.Equal(new byte[] { 0x8C }, session.ReadFile("GROWNDIR/G12"));
            Assert.Equal(new byte[] { 0, 0x7F, 0x80, 0xFF, 0x0D }, session.ReadFile("SEED"));
            Assert.Equal(Enumerable.Range(0, 512).Select(value => (byte)value), session.ReadFile("EDGE512"));
            Assert.Equal(Enumerable.Repeat((byte)0xA5, 512).Append((byte)0x6B), session.ReadFile("SAP513"));
            byte[] sparse = new byte[1537];
            sparse.AsSpan(0, 512).Fill(0x3C);
            sparse[^1] = 0xD7;
            Assert.Equal(sparse, session.ReadFile("SPARSE"));
            Assert.Equal(new[] { new SparseExtent(0, 512), new SparseExtent(1536, 1) }, session.GetSparseExtents("SPARSE"));
            byte[] tree = new byte[131073];
            tree.AsSpan(0, 512).Fill(0x11);
            tree.AsSpan(255 * 512, 512).Fill(0xFE);
            tree[^1] = 0x42;
            Assert.Equal(tree, session.ReadFile("TREE"));
            Assert.Equal(new[] { new SparseExtent(0, 512), new SparseExtent(255 * 512, 513) }, session.GetSparseExtents("TREE"));
            DiskEntry seed = session.GetEntry("SEED");
            Assert.True(seed.IsLocked);
            Assert.Equal(0x21, seed.Access);
            Assert.Equal(0x2000, seed.AuxType);
            Assert.Equal(new DateTime(1993, 7, 16, 14, 35, 0), seed.Created);
            Assert.Equal(new DateTime(1994, 2, 3, 8, 9, 0), seed.Modified);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExtractRestore_IndependentProdosFixture_PreservesPayloadsSparseFilesAndMetadata()
    {
        using FixtureWorkspace workspace = new();
        string source = FixtureWorkspace.FindFixture("independent-prodos.po");
        string extraction = workspace.NewPath("extracted");
        using (DiskSession disk = DiskSession.Open(source)) FileTransfer.Extract(disk, extraction);
        string target = workspace.NewPath("restored.po");
        ImageTransactions.Create(target, false, staged =>
        {
            DiskSession.Create(staged, "prodos", order: "prodos");
            using DiskSession disk = DiskSession.Open(staged, writable: true);
            FileTransfer.Restore(disk, Path.Combine(extraction, FileTransfer.ManifestName));
        }, Validate);
        using DiskSession original = DiskSession.Open(source);
        using DiskSession restored = DiskSession.Open(target);
        Assert.Equal(original.List(recursive: true).Count, restored.List(recursive: true).Count);
        foreach (DiskEntry entry in original.List(recursive: true))
        {
            DiskEntry actual = restored.GetEntry(entry.Path);
            Assert.Equal(entry.Name, actual.Name);
            Assert.Equal(entry.FileType, actual.FileType);
            Assert.Equal(entry.AuxType, actual.AuxType);
            Assert.Equal(entry.Access, actual.Access);
            Assert.Equal(entry.Created, actual.Created);
            Assert.Equal(entry.Modified, actual.Modified);
            Assert.Equal(entry.RawName, actual.RawName);
            if (!entry.IsDirectory)
            {
                Assert.Equal(entry.Length, actual.Length);
                Assert.Equal(original.ReadFile(entry.Path), restored.ReadFile(entry.Path));
                Assert.Equal(original.GetSparseExtents(entry.Path), restored.GetSparseExtents(entry.Path));
            }
        }
    }

    [Fact]
    public void Write_IndependentProdosFixture_GrowsDirectoryAndFileWhilePreservingUnrelatedBlocks()
    {
        using FixtureWorkspace workspace = new();
        string source = FixtureWorkspace.FindFixture("independent-prodos.po");
        string output = workspace.NewPath("changed.po");
        byte[] original = File.ReadAllBytes(source);
        byte[] replacement = new byte[131073];
        replacement.AsSpan(0, 512).Fill(0x67);
        replacement[^1] = 0x68;
        // Cross the sapling/tree EOF boundary without requiring a larger physical volume.
        ImageTransactions.Write(source, output, false, false, staged =>
        {
            using DiskSession disk = DiskSession.Open(staged, writable: true);
            disk.Delete("SPARSE");
            disk.Add("FULLDIR/EXTRA", [0xAA, 0xBB], "BIN", 0x4000);
            disk.Replace("SAP513", replacement);
        }, Validate);
        using (DiskSession disk = DiskSession.Open(output))
        {
            Assert.Equal(13, disk.List("FULLDIR").Count);
            Assert.Equal(replacement, disk.ReadFile("SAP513"));
            Assert.Equal(0x2000, disk.GetEntry("SAP513").AuxType);
            Assert.Equal(new DateTime(1994, 2, 3, 8, 9, 0), disk.GetEntry("SAP513").Modified);
        }
        byte[] changed = File.ReadAllBytes(output);
        foreach (int block in new[] { 0, 1, 32, 33, 38, 39, 40, 41, 42, 44, 55, 56, 57, 69, 180, 201 })
            Assert.Equal(original.AsSpan(block * 512, 512).ToArray(), changed.AsSpan(block * 512, 512).ToArray());
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void Write_IndependentFullRoot_RefusesCatalogGrowthAndRetainsOriginal()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("full-root.po");
        byte[] bytes = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-prodos.po"));
        for (int index = 7; index < 51; index++)
        {
            int entry = RootEntryOffset(index);
            bytes.AsSpan(RootEntryOffset(1), 39).CopyTo(bytes.AsSpan(entry));
            bytes.AsSpan(entry + 1, 15).Clear();
            string name = $"FILL{index:00}";
            bytes[entry] = (byte)(0x10 | name.Length);
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes, entry + 1);
            int dataBlock = 70 + index - 7;
            Write16(bytes, entry + 0x11, dataBlock);
            bytes.AsSpan(dataBlock * 512, 512).Fill((byte)index);
            bytes[6 * 512 + dataBlock / 8] &= (byte)~(0x80 >> (dataBlock % 8));
        }
        Write16(bytes, 2 * 512 + 0x25, 51);
        File.WriteAllBytes(source, bytes);
        Validate(source);
        string output = workspace.NewPath("refused.po");
        Assert.Throws<DiskException>(() => ImageTransactions.Write(source, output, false, false, staged =>
        {
            using DiskSession disk = DiskSession.Open(staged, writable: true);
            disk.Add("EXTRA", [1], "BIN");
        }, Validate));
        Assert.False(File.Exists(output));
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_IndependentMaximumProdosVolume_PreservesHighBlockAndUnusedHostTail(bool trailingBlock)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("maximum.po");
        byte[] image = CreateMaximumVolume(trailingBlock);
        File.WriteAllBytes(source, image);
        using (DiskSession disk = DiskSession.Open(source))
        {
            Assert.Equal(new byte[] { 0x81, 0xFE }, disk.ReadFile("HIGH"));
            Assert.Equal(65535 - 65, disk.Info.FreeBytes / 512);
        }
        string output = workspace.NewPath("modified.po");
        ImageTransactions.Write(source, output, false, false, staged =>
        {
            using DiskSession disk = DiskSession.Open(staged, writable: true);
            disk.Add("ADDED", [1, 2, 3], "BIN", 0x4000);
            disk.Rename("HIGH", "HIGH.RENAMED");
        }, Validate);
        using (DiskSession disk = DiskSession.Open(output))
        {
            Assert.Equal(new byte[] { 0x81, 0xFE }, disk.ReadFile("HIGH.RENAMED"));
            Assert.Equal(new byte[] { 1, 2, 3 }, disk.ReadFile("ADDED"));
        }
        byte[] changed = File.ReadAllBytes(output);
        Assert.Equal(image.AsSpan(65534 * 512).ToArray(), changed.AsSpan(65534 * 512).ToArray());
        Assert.Equal(SHA256.HashData(image), SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public async Task Read_MalformedIndependentCorpus_CompletesWithinDeadlineAndPreservesInputs()
    {
        using FixtureWorkspace workspace = new();
        byte[] baseline = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-prodos.po"));
        int[] offsets = [2 * 512 + 2, 5 * 512 + 2, 2 * 512 + 0x23, 2 * 512 + 0x24,
            2 * 512 + 0x27, 2 * 512 + 0x29, RootEntryOffset(0) + 0x11,
            RootEntryOffset(2) + 0x15, 34 * 512 + 256, 38 * 512, 39 * 512 + 255,
            43 * 512 + 2, 56 * 512 + 2, 180 * 512 + 2, 6 * 512 + 4];
        List<byte[]> corpus = [];
        foreach (int offset in offsets)
        {
            byte[] corrupted = (byte[])baseline.Clone();
            corrupted[offset] ^= 0xFF;
            corpus.Add(corrupted);
        }
        byte[] rootCycle = (byte[])baseline.Clone();
        Write16(rootCycle, 5 * 512 + 2, 2);
        corpus.Add(rootCycle);
        byte[] directoryCycle = (byte[])baseline.Clone();
        Write16(directoryCycle, 180 * 512 + 2, 56);
        corpus.Add(directoryCycle);
        byte[] crossLink = (byte[])baseline.Clone();
        Write16(crossLink, RootEntryOffset(0) + 0x11, 33);
        corpus.Add(crossLink);
        corpus.Add(baseline[..^1]);
        using CancellationTokenSource overall = new(TimeSpan.FromSeconds(60));
        for (int index = 0; index < corpus.Count; index++)
        {
            string path = workspace.NewPath($"mutation-{index:00}.po");
            File.WriteAllBytes(path, corpus[index]);
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo(ProbeHostPath())
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "--probe-disk", path }
                }
            };
            Assert.True(process.Start());
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail($"Malformed disk case {index} exceeded the process deadline.");
            }
            Assert.True(process.ExitCode == 0, $"Malformed disk case {index}: {await stdout}\n{await stderr}");
            Assert.Equal(corpus[index], File.ReadAllBytes(path));
        }
        Assert.Equal(FixtureHash, Convert.ToHexStringLower(SHA256.HashData(
            File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-prodos.po")))));
    }

    private static byte[] CreateMaximumVolume(bool trailingBlock)
    {
        // Rewrite documented bitmap/header fields directly; never format via DiskArc or A2Utils.
        byte[] baseline = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-prodos.po"));
        byte[] image = new byte[(trailingBlock ? 65536 : 65535) * 512];
        baseline.CopyTo(image, 0);
        image.AsSpan(6 * 512, 16 * 512).Fill(0xFF);
        Write16(image, 2 * 512 + 0x29, 65535);
        for (int block = 0; block < 65536; block++)
        {
            bool used = block < 22 || block >= 65534
                || block < 280 && (baseline[6 * 512 + block / 8] & (0x80 >> (block % 8))) == 0;
            if (used) image[6 * 512 + block / 8] &= (byte)~(0x80 >> (block % 8));
        }
        int entry = RootEntryOffset(7);
        image.AsSpan(RootEntryOffset(1), 39).CopyTo(image.AsSpan(entry));
        image.AsSpan(entry + 1, 15).Clear();
        image[entry] = 0x14;
        "HIGH"u8.CopyTo(image.AsSpan(entry + 1));
        Write16(image, entry + 0x11, 65534);
        image[entry + 0x15] = 2;
        image[entry + 0x16] = 0;
        image[entry + 0x17] = 0;
        image[65534 * 512] = 0x81;
        image[65534 * 512 + 1] = 0xFE;
        Write16(image, 2 * 512 + 0x25, 8);
        if (trailingBlock) image.AsSpan(65535 * 512).Fill(0xA7);
        return image;
    }

    private static int RootEntryOffset(int index) => (2 + (index + 1) / 13) * 512 + 4 + (index + 1) % 13 * 39;
    private static void Write16(byte[] bytes, int offset, int value)
        => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));

    private static void Validate(string path)
    {
        using DiskSession disk = DiskSession.Open(path);
        Assert.False(disk.Info.IsDubious);
        Assert.DoesNotContain(disk.Verify(), diagnostic => diagnostic.Severity is "error" or "warning");
    }

    private static string ProbeHostPath()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(root!.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(path), "Build the disk parser probe helper: " + path);
        return path;
    }
}
