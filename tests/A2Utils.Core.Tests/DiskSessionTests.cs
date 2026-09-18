using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;
using CommonUtil;
using DiskArc;
using DiskArc.Disk;

namespace A2Utils.Core.Tests;

public sealed class DiskSessionTests
{
    private static readonly byte[] BinaryPayload = [0x00, 0x7f, 0x80, 0xff, 0x0d];
    private static readonly byte[] TextPayload = [0xc1, 0xb2, 0xd5, 0xd4, 0xc9, 0xcc, 0xd3, 0x8d];

    [Fact]
    public void BootSectors_LargerSectorAddressableImage_RejectsReadAndWrite()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("forty-track.po");
        AppHook hook = new(new SimpleMessageLog());
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (IDiskImage image = UnadornedSector.CreateSectorImage(stream, 40, 16,
            Defs.SectorOrder.ProDOS_Block, hook))
        {
            image.FormatDisk(Defs.FileSystemType.ProDOS, "BOOTTEST", 254, false, hook);
            image.Flush();
            stream.Flush(true);
        }

        using DiskSession disk = DiskSession.Open(path, "prodos", "prodos", writable: true);

        Assert.Equal(40 * 16 * 256, disk.Info.SizeBytes);
        DiskException read = Assert.Throws<DiskException>(() => disk.ReadBootSectors(1));
        DiskException write = Assert.Throws<DiskException>(() => disk.WriteBootSectors(new byte[256]));
        Assert.Equal("boot.geometry", read.Code);
        Assert.Equal("boot.geometry", write.Code);
    }

    [Theory]
    [InlineData("do", "51AF1DA57DFE814CE1323390F4AD3B3E6B247F81869FC8B83D47D4F2A78EF58C")]
    [InlineData("po", "EB9B9BAF41A3C097C716AC0A2E0EA2BAC6F1424C879C24ED90C9B9D00E88E8C7")]
    public void Fixture_IndependentImage_MatchesPublishedHash(string extension, string hash)
    {
        byte[] image = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-dos33." + extension));
        Assert.Equal(143360, image.Length);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(image)));
    }

    [Theory]
    [InlineData("do")]
    [InlineData("po")]
    public void Open_IndependentDosImage_ReadsCatalogAndMetadata(string extension)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk(extension);
        using DiskSession disk = DiskSession.Open(path);

        Assert.Equal(42, disk.Info.VolumeNumber);
        Assert.Equal(143360, disk.Info.SizeBytes);
        Assert.False(disk.Info.IsDubious);
        DiskEntry binary = Assert.Single(disk.List(), entry => entry.Name == "HELLO.BIN");
        Assert.Equal(2, disk.List().Count);
        Assert.Equal((ushort)0x2000, binary.AuxType);
        Assert.True(binary.IsLocked);
        Assert.Equal(5, binary.Length);
        Assert.Equal(256, binary.StoredLength);
        Assert.Equal(512, binary.StorageSize);
        Assert.Equal(BinaryPayload, disk.ReadFile("HELLO.BIN"));
        Assert.Equal(TextPayload, disk.ReadFile("README"));
    }

    [Fact]
    public void ReadFile_RawDosBinary_PreservesHeaderAndTrailingBytes()
    {
        using FixtureWorkspace workspace = new();
        using DiskSession disk = DiskSession.Open(workspace.CopyDisk());

        byte[] raw = disk.ReadFile("HELLO.BIN", raw: true);

        Assert.Equal(256, raw.Length);
        Assert.Equal(new byte[] { 0x00, 0x20, 0x05, 0x00 }, raw[..4]);
        Assert.Equal(BinaryPayload, raw[4..9]);
        Assert.All(raw[9..], value => Assert.Equal((byte)0xcc, value));
    }

    [Fact]
    public void Open_MisleadingExtension_UsesImageContents()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk("po", "misleading.do");
        using DiskSession disk = DiskSession.Open(path);

        Assert.Equal(BinaryPayload, disk.ReadFile("HELLO.BIN"));
        Assert.Equal(TextPayload, disk.ReadFile("README"));
    }

    [Fact]
    public void Open_ReadOnlyInspection_PreservesEveryImageByte()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] before = File.ReadAllBytes(path);

        using (DiskSession disk = DiskSession.Open(path))
        {
            _ = disk.Info;
            _ = disk.List();
            _ = disk.ReadFile("HELLO.BIN");
            _ = disk.Verify();
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(252)]
    [InlineData(253)]
    [InlineData(308)]
    [InlineData(31229)]
    public void Add_DosBinaryAcrossSectorAndListBoundaries_PreservesPayload(int length)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] payload = Pattern(length);
        byte[] before = File.ReadAllBytes(path);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            disk.Add("NEW.FILE", payload, "B", 0x6000);
        }

        using DiskSession reopened = DiskSession.Open(path);
        Assert.Equal(payload, reopened.ReadFile("NEW.FILE"));
        Assert.Equal(BinaryPayload, reopened.ReadFile("HELLO.BIN"));
        Assert.Equal(TextPayload, reopened.ReadFile("README"));
        Assert.Equal((ushort)0x6000, Assert.Single(reopened.List(), item => item.Name == "NEW.FILE").AuxType);
        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(before[..(3 * 16 * 256)], after[..(3 * 16 * 256)]);
    }

    [Fact]
    public void Rename_DosFile_ChangesOnlyItsCatalogName()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] before = File.ReadAllBytes(path);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            disk.Rename("README", "RENAMED");
        }

        using DiskSession reopened = DiskSession.Open(path);
        Assert.Equal(TextPayload, reopened.ReadFile("RENAMED"));
        byte[] after = File.ReadAllBytes(path);
        int nameOffset = (17 * 16 + 15) * 256 + 0x0b + 35 + 3;
        Assert.Equal(before[..nameOffset], after[..nameOffset]);
        Assert.Equal(before[(nameOffset + 30)..], after[(nameOffset + 30)..]);
    }

    [Fact]
    public void Replace_DosBinary_PreservesTypeAddressAndUnrelatedFile()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] payload = Pattern(600);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            disk.SetAttributes("HELLO.BIN", locked: false);
            disk.Replace("HELLO.BIN", payload);
        }

        using DiskSession reopened = DiskSession.Open(path);
        Assert.Equal(payload, reopened.ReadFile("HELLO.BIN"));
        Assert.Equal(TextPayload, reopened.ReadFile("README"));
        Assert.Equal((ushort)0x2000, Assert.Single(reopened.List(), item => item.Name == "HELLO.BIN").AuxType);
    }

    [Fact]
    public void Delete_DosFile_FreesItsAllocationAndPreservesOtherFile()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        long freeBefore;
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            freeBefore = Assert.IsType<long>(disk.Info.FreeBytes);
            disk.Delete("README");
        }

        using DiskSession reopened = DiskSession.Open(path);
        Assert.Single(reopened.List());
        Assert.Equal(BinaryPayload, reopened.ReadFile("HELLO.BIN"));
        Assert.Equal(freeBefore + 512, reopened.Info.FreeBytes);
    }

    [Fact]
    public void SetAttributes_DosBinary_ChangesLockAndAddressWithoutChangingPayloadOrSlack()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            disk.SetAttributes("HELLO.BIN", locked: false, auxType: 0x4000);
        }

        using DiskSession reopened = DiskSession.Open(path);
        DiskEntry entry = Assert.Single(reopened.List(), item => item.Name == "HELLO.BIN");
        Assert.False(entry.IsLocked);
        Assert.Equal((ushort)0x4000, entry.AuxType);
        Assert.Equal(BinaryPayload, reopened.ReadFile("HELLO.BIN"));
        Assert.All(reopened.ReadFile("HELLO.BIN", raw: true)[9..], value => Assert.Equal((byte)0xcc, value));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("replace")]
    public void Mutate_LockedFile_RefusesWithoutChangingImage(string operation)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] before = File.ReadAllBytes(path);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            Assert.Throws<DiskException>(() =>
            {
                if (operation == "delete")
                {
                    disk.Delete("HELLO.BIN");
                }
                else
                {
                    disk.Replace("HELLO.BIN", [1, 2, 3]);
                }
            });
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("catalog-cycle")]
    [InlineData("list-cycle")]
    [InlineData("invalid-sector")]
    [InlineData("allocation-conflict")]
    public void Open_CorruptedImage_RefusesWritesAndPreservesInput(string corruption)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] image = File.ReadAllBytes(path);
        int catalog = (17 * 16 + 15) * 256;
        int list = (18 * 16 + 15) * 256;
        switch (corruption)
        {
            case "catalog-cycle":
                image[catalog + 2] = 15;
                break;
            case "list-cycle":
                image[list + 1] = 18;
                image[list + 2] = 15;
                break;
            case "invalid-sector":
                image[list + 13] = 16;
                break;
            case "allocation-conflict":
                image[(18 * 16 + 13) * 256 + 13] = 14;
                break;
        }

        File.WriteAllBytes(path, image);
        Assert.Throws<DiskException>(() =>
        {
            using DiskSession disk = DiskSession.Open(path, writable: true);
            disk.Add("UNSAFE", [1], "B");
        });
        Assert.Equal(image, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(143359)]
    [InlineData(143361)]
    public void Open_InvalidRawLength_ReportsDiskException(int length)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("invalid.do");
        File.WriteAllBytes(path, new byte[length]);

        Assert.Throws<DiskException>(() =>
        {
            using DiskSession disk = DiskSession.Open(path);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(131072)]
    [InlineData(131073)]
    public void Add_ProDosAcrossStorageBoundaries_PreservesBytesAndMetadata(int length)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("prodos.po");
        DiskSession.Create(path, "prodos", sizeKiB: 800, order: "prodos", volumeName: "TESTS");
        byte[] payload = Pattern(length);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            disk.Mkdir("SOURCE");
            disk.Add("SOURCE/PAYLOAD", payload, "BIN", 0x6000);
        }

        using DiskSession reopened = DiskSession.Open(path);
        Assert.Equal(payload, reopened.ReadFile("SOURCE/PAYLOAD"));
        DiskEntry file = Assert.Single(reopened.List("SOURCE"));
        Assert.Equal((ushort)0x6000, file.AuxType);
        Assert.Equal(length, file.Length);
        Assert.False(file.IsDubious);
    }

    [Fact]
    public void Delete_ProDosDirectory_RequiresExplicitRecursion()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("prodos.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            disk.Mkdir("PARENT");
            disk.Add("PARENT/CHILD", [1, 2, 3], "BIN");
            Assert.Throws<DiskException>(() => disk.Delete("PARENT"));
            Assert.Equal(new byte[] { 1, 2, 3 }, disk.ReadFile("PARENT/CHILD"));
            disk.Delete("PARENT", recursive: true);
        }

        using DiskSession reopened = DiskSession.Open(path);
        Assert.Empty(reopened.List());
    }

    [Theory]
    [InlineData("do", "po", "prodos")]
    [InlineData("po", "do", "dos")]
    public void Convert_IndependentSectorOrderVectors_MatchesEveryExpectedByte(
        string sourceExtension, string targetExtension, string targetOrder)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk(sourceExtension);
        string output = workspace.NewPath("converted." + targetExtension);
        byte[] before = File.ReadAllBytes(source);

        ImageConverter.Convert(source, output, "raw", targetOrder);

        Assert.Equal(File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-dos33." + targetExtension)),
            File.ReadAllBytes(output));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void SetAttributes_WriteProtectedTwoImg_RefusesAndPreservesImage()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("locked.2mg");
        byte[] image = TwoImgFixture(writeProtected: true);
        File.WriteAllBytes(path, image);

        Assert.Throws<DiskException>(() =>
        {
            using DiskSession disk = DiskSession.Open(path, writable: true);
            disk.SetAttributes("HELLO.BIN", locked: false);
        });

        Assert.Equal(image, File.ReadAllBytes(path));
    }

    [Fact]
    public void Convert_TwoImgWithMetadata_PreservesHeaderCommentsCreatorDataAndGaps()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("metadata.2mg");
        string output = workspace.NewPath("converted.2mg");
        byte[] image = TwoImgFixture(writeProtected: true);
        File.WriteAllBytes(source, image);

        ImageConverter.Convert(source, output, "2mg", "prodos");

        byte[] expected = (byte[])image.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(12), 1);
        File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-dos33.po")).CopyTo(expected, 64);
        Assert.Equal(expected, File.ReadAllBytes(output));
        Assert.Equal(image, File.ReadAllBytes(source));
    }

    [Fact]
    public void ExtractRestore_DosSparseBinary_PreservesHeaderSlackHolesAndMetadata()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        byte[] image = File.ReadAllBytes(source);
        int list = (18 * 16 + 15) * 256;
        image[list + 16] = 18;
        image[list + 17] = 10;
        image[(17 * 16 + 15) * 256 + 0x0b + 33] = 3;
        image[17 * 16 * 256 + 0x38 + 18 * 4] = 0x0b;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((18 * 16 + 14) * 256 + 2), 764);
        Array.Fill(image, (byte)0x66, (18 * 16 + 10) * 256, 256);
        File.WriteAllBytes(source, image);
        string extracted = workspace.NewPath("extracted");
        byte[] rawBefore;
        DiskEntry metadataBefore;
        using (DiskSession disk = DiskSession.Open(source))
        {
            rawBefore = disk.ReadFile("HELLO.BIN", raw: true);
            metadataBefore = Assert.Single(disk.List(), entry => entry.Name == "HELLO.BIN");
            Assert.Equal(768, rawBefore.Length);
            Assert.Equal(new[] { new SparseExtent(0, 256), new SparseExtent(512, 256) },
                disk.GetSparseExtents("HELLO.BIN"));
            Assert.All(rawBefore[256..512], value => Assert.Equal((byte)0, value));
            FileTransfer.Extract(disk, extracted);
        }

        string target = workspace.NewPath("restored.do");
        DiskSession.Create(target, "dos33");
        using (DiskSession disk = DiskSession.Open(target, writable: true))
        {
            FileTransfer.Restore(disk, Path.Combine(extracted, FileTransfer.ManifestName));
        }

        using DiskSession reopened = DiskSession.Open(target);
        Assert.Equal(rawBefore, reopened.ReadFile("HELLO.BIN", raw: true));
        Assert.Equal(new[] { new SparseExtent(0, 256), new SparseExtent(512, 256) },
            reopened.GetSparseExtents("HELLO.BIN"));
        DiskEntry metadataAfter = Assert.Single(reopened.List(), entry => entry.Name == "HELLO.BIN");
        Assert.Equal(metadataBefore.RawName, metadataAfter.RawName);
        Assert.Equal(metadataBefore.Access, metadataAfter.Access);
        Assert.Equal(metadataBefore.FileType, metadataAfter.FileType);
        Assert.Equal(metadataBefore.AuxType, metadataAfter.AuxType);
        Assert.Equal(metadataBefore.Length, metadataAfter.Length);
        Assert.Equal(metadataBefore.StorageSize, metadataAfter.StorageSize);
        Assert.Equal(image, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("PARENT", "NESTED", "PAYLOAD")]
    [InlineData("Parent", "Nested", "Payload")]
    public void ExtractRestore_OneNestedProDosFile_RestoresParentsAndMetadata(
        string parentName, string nestedName, string fileName)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po");
        DiskSession.Create(source, "prodos", order: "prodos");
        string extracted = workspace.NewPath("extracted");
        string directoryPath = parentName + "/" + nestedName;
        string filePath = directoryPath + "/" + fileName;
        DiskEntry original;
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Mkdir(parentName);
            disk.Mkdir(directoryPath);
            disk.Add(filePath, BinaryPayload, "BIN", 0x6000);
            disk.SetAttributes(filePath, locked: true);
            original = Assert.Single(disk.List(directoryPath));
            FileTransfer.Extract(disk, extracted, filePath);
        }

        string target = workspace.NewPath("target.po");
        DiskSession.Create(target, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(target, writable: true))
        {
            FileTransfer.Restore(disk, Path.Combine(extracted, FileTransfer.ManifestName));
        }

        using DiskSession reopened = DiskSession.Open(target);
        Assert.Equal(BinaryPayload, reopened.ReadFile(filePath));
        DiskEntry restored = Assert.Single(reopened.List(directoryPath));
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Path, restored.Path);
        Assert.Equal(original.RawName, restored.RawName);
        Assert.Equal(original.Access, restored.Access);
        Assert.Equal(original.Created, restored.Created);
        Assert.Equal(original.Modified, restored.Modified);
        Assert.Equal(original.AuxType, restored.AuxType);
    }

    [Fact]
    public void Restore_TamperedPayload_RefusesBeforeChangingAnyImageBytes()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string extracted = workspace.NewPath("extracted");
        ExtractionManifest manifest;
        using (DiskSession disk = DiskSession.Open(source))
        {
            manifest = FileTransfer.Extract(disk, extracted);
        }

        string tampered = Path.Combine(extracted, manifest.Entries.Last().HostFile!);
        File.WriteAllBytes(tampered, [1, 2, 3]);
        string target = workspace.NewPath("target.do");
        DiskSession.Create(target, "dos33");
        byte[] before = File.ReadAllBytes(target);
        using (DiskSession disk = DiskSession.Open(target, writable: true))
        {
            DiskException error = Assert.Throws<DiskException>(() =>
                FileTransfer.Restore(disk, Path.Combine(extracted, FileTransfer.ManifestName)));
            Assert.Equal("payload_hash_mismatch", error.Code);
        }

        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData("dos33", "dos", "B")]
    [InlineData("dos33", "prodos", "B")]
    [InlineData("prodos", "dos", "BIN")]
    [InlineData("prodos", "prodos", "BIN")]
    public void Create_TwoImgFileSystemAndOrderCombinations_ReopensWithPayload(
        string fileSystem, string order, string type)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("created.2mg");
        DiskSession.Create(path, fileSystem, container: "2mg", order: order);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            disk.Add("PAYLOAD", BinaryPayload, type, 0x2000);
        }

        using DiskSession reopened = DiskSession.Open(path);
        Assert.Equal(fileSystem, reopened.Info.FileSystem);
        Assert.Equal(order, reopened.Info.Order);
        Assert.Equal("2mg", reopened.Info.Container);
        Assert.Equal(BinaryPayload, reopened.ReadFile("PAYLOAD"));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("replace")]
    public void Write_OversizedDosBinary_RollsBackEveryStagedChange(string operation)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string output = workspace.NewPath("output.do");
        byte[] before = File.ReadAllBytes(source);

        Assert.Throws<DiskException>(() => ImageTransactions.Write(source, output, false, false, staged =>
        {
            using DiskSession disk = DiskSession.Open(staged, writable: true);
            if (operation == "add")
            {
                disk.Add("OVERSIZED", Pattern(65536), "B");
            }
            else
            {
                disk.SetAttributes("HELLO.BIN", locked: false);
                disk.Replace("HELLO.BIN", Pattern(65536));
            }
        }));

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(output));
        Assert.Single(Directory.GetFiles(workspace.DirectoryPath));
    }

    [Fact]
    public void Write_DosAllocationExhausted_RollsBackAllAddedFiles()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string output = workspace.NewPath("output.do");
        byte[] before = File.ReadAllBytes(source);

        DiskException error = Assert.Throws<DiskException>(() =>
            ImageTransactions.Write(source, output, false, false, staged =>
            {
                using DiskSession disk = DiskSession.Open(staged, writable: true);
                disk.Add("FIRST", Pattern(65535), "B");
                disk.Add("SECOND", Pattern(65535), "B");
            }));

        Assert.Equal("disk_full", error.Code);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(output));
        Assert.Single(Directory.GetFiles(workspace.DirectoryPath));
    }

    [Theory]
    [InlineData("duplicate-add")]
    [InlineData("duplicate-rename")]
    [InlineData("invalid-attributes")]
    [InlineData("dos-directory")]
    public void Mutate_InvalidRequest_PreservesEveryImageByte(string operation)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] before = File.ReadAllBytes(path);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            Assert.Throws<DiskException>(() =>
            {
                switch (operation)
                {
                    case "duplicate-add":
                        disk.Add("README", [1], "B");
                        break;
                    case "duplicate-rename":
                        disk.Rename("README", "HELLO.BIN");
                        break;
                    case "invalid-attributes":
                        disk.SetAttributes("README", type: "INVALID");
                        break;
                    case "dos-directory":
                        disk.Mkdir("DIRECTORY");
                        break;
                }
            });
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("empty-nonzero")]
    [InlineData("null-extent")]
    public void Restore_MaliciousSparseMap_RejectsBeforeWriting(string corruption)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string extracted = workspace.NewPath("extracted");
        ExtractionManifest original;
        using (DiskSession disk = DiskSession.Open(source))
        {
            original = FileTransfer.Extract(disk, extracted);
        }

        ExtractedFile victim = original.Entries[0];
        SparseExtent[] extents = corruption == "empty-nonzero" ? [] : [null!];
        ExtractionManifest malicious = original with
        {
            Entries = original.Entries.Select((entry, index) =>
                index == 0 ? victim with { DataExtents = extents } : entry).ToArray()
        };
        string manifestPath = Path.Combine(extracted, FileTransfer.ManifestName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(malicious, FileTransfer.JsonOptions));
        string target = workspace.NewPath("target.do");
        DiskSession.Create(target, "dos33");
        byte[] before = File.ReadAllBytes(target);
        using (DiskSession disk = DiskSession.Open(target, writable: true))
        {
            DiskException error = Assert.Throws<DiskException>(() => FileTransfer.Restore(disk, manifestPath));
            Assert.Equal("invalid_sparse_extent", error.Code);
        }

        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData("PARENT", "NESTED")]
    [InlineData("Parent", "Nested")]
    public void Restore_LockedNestedDirectories_PreservesDatesNamesPermissionsAndChildren(
        string parentName, string nestedName)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po");
        DiskSession.Create(source, "prodos", order: "prodos");
        string extracted = workspace.NewPath("extracted");
        string directoryPath = parentName + "/" + nestedName;
        string filePath = directoryPath + "/PAYLOAD";
        ExtractionManifest original;
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Mkdir(parentName);
            disk.Mkdir(directoryPath);
            disk.Add(filePath, BinaryPayload, "BIN", 0x2000);
            disk.SetAttributes(directoryPath, locked: true);
            disk.SetAttributes(parentName, locked: true);
            original = FileTransfer.Extract(disk, extracted);
        }

        // Fixed dates make omitted metadata restoration observable even within one clock minute.
        DateTime created = new(1987, 1, 2, 3, 4, 0);
        DateTime modified = new(1988, 5, 6, 7, 8, 0);
        ExtractionManifest manifest = original with
        {
            Entries = original.Entries.Select(entry => entry.Entry.IsDirectory
                ? entry with { Entry = entry.Entry with { Created = created, Modified = modified } }
                : entry).ToArray()
        };
        string manifestPath = Path.Combine(extracted, FileTransfer.ManifestName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, FileTransfer.JsonOptions));
        string target = workspace.NewPath("target.po");
        DiskSession.Create(target, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(target, writable: true))
        {
            FileTransfer.Restore(disk, manifestPath);
        }

        using DiskSession reopened = DiskSession.Open(target);
        Assert.Equal(BinaryPayload, reopened.ReadFile(filePath));
        foreach (ExtractedFile expected in manifest.Entries.Where(entry => entry.Entry.IsDirectory))
        {
            DiskEntry actual = Assert.Single(reopened.List(recursive: true), entry => entry.Path == expected.Entry.Path);
            Assert.Equal(expected.Entry.Name, actual.Name);
            Assert.Equal(expected.Entry.RawName, actual.RawName);
            Assert.Equal(expected.Entry.Access, actual.Access);
            Assert.True(actual.IsLocked);
            Assert.Equal(created, actual.Created);
            Assert.Equal(modified, actual.Modified);
        }
    }

    [Theory]
    [InlineData("raw-name")]
    [InlineData("logical-length")]
    public void Restore_ManifestMetadataConflictsWithStoredFile_RejectsWithoutPublishing(string corruption)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string extracted = workspace.NewPath("extracted");
        ExtractionManifest original;
        using (DiskSession disk = DiskSession.Open(source))
        {
            original = FileTransfer.Extract(disk, extracted);
        }

        byte[] duplicateName = original.Entries[0].Entry.RawName;
        ExtractionManifest malicious = original with
        {
            Entries = original.Entries.Select(entry => entry with
            {
                Entry = corruption == "raw-name"
                    ? entry.Entry with { RawName = duplicateName }
                    : entry.Entry with { Length = entry.Entry.Length + 1 }
            }).ToArray()
        };
        string manifestPath = Path.Combine(extracted, FileTransfer.ManifestName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(malicious, FileTransfer.JsonOptions));
        string target = workspace.NewPath("target.do");
        DiskSession.Create(target, "dos33");
        byte[] before = File.ReadAllBytes(target);
        string output = workspace.NewPath("restored.do");

        Assert.Throws<DiskException>(() => ImageTransactions.Write(target, output, false, false, staged =>
        {
            using DiskSession disk = DiskSession.Open(staged, writable: true);
            FileTransfer.Restore(disk, manifestPath);
        }));

        Assert.False(File.Exists(output));
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    private static byte[] TwoImgFixture(bool writeProtected)
    {
        byte[] payload = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-dos33.do"));
        byte[] comment = "Original fixture comment"u8.ToArray();
        byte[] creatorData = [0xde, 0xad, 0xbe, 0xef, 0x00];
        int commentOffset = 64 + payload.Length + 4;
        int creatorOffset = commentOffset + comment.Length + 3;
        byte[] image = new byte[creatorOffset + creatorData.Length + 2];
        "2IMG"u8.CopyTo(image);
        "TEST"u8.CopyTo(image.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(8), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), writeProtected ? 0x8000012au : 0x0000012au);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 280);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), (uint)commentOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36), (uint)comment.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(40), (uint)creatorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), (uint)creatorData.Length);
        payload.CopyTo(image, 64);
        Array.Fill(image, (byte)0x77, 64 + payload.Length, image.Length - 64 - payload.Length);
        comment.CopyTo(image, commentOffset);
        creatorData.CopyTo(image, creatorOffset);
        return image;
    }

    private static byte[] Pattern(int length) =>
        Enumerable.Range(0, length).Select(index => (byte)((index * 37 + 11) & 0xff)).ToArray();
}
