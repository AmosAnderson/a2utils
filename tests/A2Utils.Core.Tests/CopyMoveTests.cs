// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class CopyMoveTests
{
    [Fact]
    public void Copy_LockedDosFile_PreservesStoredBytesTypeAndLock()
    {
        using FixtureWorkspace workspace = new();
        using DiskSession disk = DiskSession.Open(workspace.CopyDisk(), writable: true);
        DiskEntry original = disk.GetEntry("HELLO.BIN");
        byte[] raw = disk.ReadFile(original.Path, raw: true);

        disk.Copy(original.Path, "COPIED.BIN");

        DiskEntry copied = disk.GetEntry("COPIED.BIN");
        Assert.Equal(raw, disk.ReadFile(copied.Path, raw: true));
        Assert.Equal(original.FileType, copied.FileType);
        Assert.Equal(original.AuxType, copied.AuxType);
        Assert.Equal(original.Access, copied.Access);
        Assert.True(copied.IsLocked);
        Assert.Equal(3, disk.List().Count);
        Assert.Equal(raw, disk.ReadFile(original.Path, raw: true));
        Assert.DoesNotContain(disk.Verify(), diagnostic => diagnostic.Severity == "error");
    }

    [Theory]
    [InlineData("raw", "dos")]
    [InlineData("raw", "prodos")]
    [InlineData("2mg", "dos")]
    [InlineData("2mg", "prodos")]
    public void CopyFrom_IndependentDosAcrossContainers_PreservesSourceAndMetadata(string container, string order)
    {
        using FixtureWorkspace workspace = new();
        string sourcePath = workspace.CopyDisk();
        byte[] sourceImage = File.ReadAllBytes(sourcePath);
        string targetPath = workspace.NewPath("target.img");
        DiskSession.Create(targetPath, "dos33", container: container, order: order);
        using (DiskSession source = DiskSession.Open(sourcePath))
        using (DiskSession target = DiskSession.Open(targetPath, writable: true))
        {
            target.CopyFrom(source, "HELLO.BIN", "HELLO.BIN");
            Assert.Equal(source.GetEntry("HELLO.BIN").RawName, target.GetEntry("HELLO.BIN").RawName);
            Assert.Equal(source.ReadFile("HELLO.BIN", true), target.ReadFile("HELLO.BIN", true));
        }

        using DiskSession reopened = DiskSession.Open(targetPath);
        Assert.Equal(order, reopened.Info.Order);
        Assert.True(reopened.GetEntry("HELLO.BIN").IsLocked);
        Assert.Equal(sourceImage, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void Copy_RecursiveProDosDirectory_PreservesSparseDataCaseTimestampsAndLockedDirectories()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        using DiskSession disk = DiskSession.Open(path, writable: true);
        disk.EnsureDirectory("Source/SubDir");
        byte[] raw = new byte[1100];
        raw.AsSpan(0, 512).Fill(0x4a);
        raw.AsSpan(1024).Fill(0xcd);
        disk.Add("Source/SubDir/MixedCase", raw, "BIN", 0x4567);
        DiskEntry file = disk.GetEntry("Source/SubDir/MixedCase") with
        {
            Created = new DateTime(2001, 2, 3, 4, 5, 0),
            Modified = new DateTime(2005, 6, 7, 8, 9, 0),
            Access = 0x21,
            IsLocked = true
        };
        disk.Delete(file.Path);
        disk.RestoreFile(file, raw, [new(0, 512), new(1024, 76)]);
        DiskEntry directory = disk.GetEntry("Source/SubDir") with
        {
            Created = new DateTime(1998, 1, 2, 3, 4, 0),
            Modified = new DateTime(2000, 2, 3, 4, 5, 0),
            Access = 0x21,
            IsLocked = true
        };
        disk.RestoreDirectoryMetadata(directory);

        disk.Copy("Source", "Copied", recursive: true);

        DiskEntry copiedFile = disk.GetEntry("Copied/SubDir/MixedCase");
        DiskEntry copiedDirectory = disk.GetEntry("Copied/SubDir");
        Assert.Equal(raw, disk.ReadFile(copiedFile.Path, raw: true));
        Assert.Equal(disk.GetSparseExtents(file.Path), disk.GetSparseExtents(copiedFile.Path));
        Assert.Equal(file.Created, copiedFile.Created);
        Assert.Equal(file.Modified, copiedFile.Modified);
        Assert.Equal(file.Access, copiedFile.Access);
        Assert.Equal(file.AuxType, copiedFile.AuxType);
        Assert.Equal(directory.Created, copiedDirectory.Created);
        Assert.Equal(directory.Modified, copiedDirectory.Modified);
        Assert.Equal(directory.Access, copiedDirectory.Access);
        Assert.Equal(directory.RawName, copiedDirectory.RawName);
        Assert.Equal(6, disk.List(recursive: true).Count);
    }

    [Fact]
    public void CopyFrom_DifferentFilesystems_RefusesWithoutChangingDestination()
    {
        using FixtureWorkspace workspace = new();
        string targetPath = workspace.NewPath("target.po");
        DiskSession.Create(targetPath, "prodos", order: "prodos");
        byte[] before = File.ReadAllBytes(targetPath);
        using (DiskSession source = DiskSession.Open(workspace.CopyDisk()))
        using (DiskSession target = DiskSession.Open(targetPath, writable: true))
        {
            DiskException exception = Assert.Throws<DiskException>(() => target.CopyFrom(source, "HELLO.BIN", "HELLO.BIN"));
            Assert.Equal("filesystem_mismatch", exception.Code);
            Assert.Equal(3, exception.ExitCode);
        }
        Assert.Equal(before, File.ReadAllBytes(targetPath));
    }

    [Fact]
    public void CopyFrom_AllocatedZeroProDosBlock_PreservesBytesAndAttributesWhileNormalizingAllocation()
    {
        using FixtureWorkspace workspace = new();
        string sourcePath = workspace.NewPath("source.po");
        string targetPath = workspace.NewPath("target.po");
        DiskSession.Create(sourcePath, "prodos", order: "prodos");
        DiskSession.Create(targetPath, "prodos", order: "prodos");
        using (DiskSession setup = DiskSession.Open(sourcePath, writable: true))
        {
            setup.Add("Zeros", Enumerable.Repeat((byte)0x7e, 1536).ToArray(), "BIN", 0x4000);
            setup.SetAttributes("Zeros", locked: true);
        }
        byte[] sourceImage = File.ReadAllBytes(sourcePath);
        ushort indexBlock = ReadProDosKey(sourceImage, "Zeros");
        int zeroBlock = sourceImage[indexBlock * 512 + 1] | sourceImage[indexBlock * 512 + 257] << 8;
        Assert.NotEqual(0, zeroBlock);
        sourceImage.AsSpan(zeroBlock * 512, 512).Clear();
        File.WriteAllBytes(sourcePath, sourceImage);

        using (DiskSession source = DiskSession.Open(sourcePath))
        using (DiskSession target = DiskSession.Open(targetPath, writable: true))
        {
            DiskEntry original = source.GetEntry("Zeros");
            Assert.Equal([new SparseExtent(0, 1536)], source.GetSparseExtents("Zeros"));

            target.CopyFrom(source, "Zeros", "Zeros");

            DiskEntry copied = target.GetEntry("Zeros");
            Assert.Equal(source.ReadFile("Zeros", true), target.ReadFile("Zeros", true));
            Assert.Equal(original.Length, copied.Length);
            Assert.Equal(original.StoredLength, copied.StoredLength);
            Assert.Equal(original.FileType, copied.FileType);
            Assert.Equal(original.AuxType, copied.AuxType);
            Assert.Equal(original.Access, copied.Access);
            Assert.Equal(original.Created, copied.Created);
            Assert.Equal(original.Modified, copied.Modified);
            Assert.Equal(original.StorageSize - 512, copied.StorageSize);
            Assert.Equal([new SparseExtent(0, 512), new SparseExtent(1024, 512)], target.GetSparseExtents("Zeros"));
        }
        Assert.Equal(sourceImage, File.ReadAllBytes(sourcePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transfer_DirectoryIntoOwnDescendant_RefusesWithoutChanges(bool move)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        using (DiskSession setup = DiskSession.Open(path, writable: true))
        {
            setup.EnsureDirectory("Tree/Child");
        }
        byte[] before = File.ReadAllBytes(path);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            DiskException exception = Assert.Throws<DiskException>(() =>
            {
                if (move) disk.Move("Tree", "Tree/Child/New");
                else disk.Copy("Tree", "Tree/Child/New", recursive: true);
            });
            Assert.Equal("self_descendant", exception.Code);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Move_ProDosFileToAnotherDirectory_PreservesDataAllocationAndMetadata()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        byte[] data = Enumerable.Range(0, 8000).Select(index => (byte)(index * 31)).ToArray();
        DiskEntry original;
        long? freeBytes;
        ushort originalKey;
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            disk.EnsureDirectory("Old");
            disk.EnsureDirectory("New");
            disk.Add("Old/Program", data, "BIN", 0x6000);
            original = disk.GetEntry("Old/Program");
            freeBytes = disk.Info.FreeBytes;
        }
        originalKey = ReadProDosKey(File.ReadAllBytes(path), "Old", "Program");
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            disk.Move("Old/Program", "New/Renamed");

            DiskEntry moved = disk.GetEntry("New/Renamed");
            Assert.Equal(data, disk.ReadFile(moved.Path));
            Assert.Equal(original.StorageSize, moved.StorageSize);
            Assert.Equal(original.Created, moved.Created);
            Assert.Equal(original.Modified, moved.Modified);
            Assert.Equal(original.FileType, moved.FileType);
            Assert.Equal(original.AuxType, moved.AuxType);
            Assert.Equal(original.Access, moved.Access);
            Assert.Equal(freeBytes, disk.Info.FreeBytes);
            Assert.Throws<DiskException>(() => disk.GetEntry("Old/Program"));
        }
        Assert.Equal(originalKey, ReadProDosKey(File.ReadAllBytes(path), "New", "Renamed"));
    }

    [Fact]
    public void Move_ProDosDirectory_PreservesDescendants()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        using DiskSession disk = DiskSession.Open(path, writable: true);
        disk.EnsureDirectory("Old/Branch/Leaf");
        disk.EnsureDirectory("New");
        disk.Add("Old/Branch/Leaf/Data", [0, 1, 255], "BIN", 0x2000);

        disk.Move("Old/Branch", "New/Renamed");

        Assert.Equal(new byte[] { 0, 1, 255 }, disk.ReadFile("New/Renamed/Leaf/Data"));
        Assert.Empty(disk.List("Old"));
        Assert.DoesNotContain(disk.Verify(), diagnostic => diagnostic.Severity == "error");
    }

    [Theory]
    [InlineData("Old/File")]
    [InlineData("Old")]
    [InlineData("New")]
    public void Move_LockedEntryOrParent_RefusesWithoutChanges(string lockedPath)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        using (DiskSession setup = DiskSession.Open(path, writable: true))
        {
            setup.EnsureDirectory("Old");
            setup.EnsureDirectory("New");
            setup.Add("Old/File", [1, 2], "BIN");
            setup.SetAttributes(lockedPath, locked: true);
        }
        byte[] before = File.ReadAllBytes(path);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            DiskException exception = Assert.Throws<DiskException>(() => disk.Move("Old/File", "New/File"));

            Assert.Equal("file_locked", exception.Code);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transfer_ExistingCaseInsensitiveDestination_Refuses(bool move)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        using DiskSession disk = DiskSession.Open(path, writable: true);
        disk.Add("Source", [1], "BIN");
        disk.Add("Target", [2], "BIN");

        DiskException exception = Assert.Throws<DiskException>(() =>
        {
            if (move) disk.Move("Source", "TARGET");
            else disk.Copy("Source", "TARGET");
        });

        Assert.Equal("destination_exists", exception.Code);
        Assert.Equal(new byte[] { 1 }, disk.ReadFile("Source"));
        Assert.Equal(new byte[] { 2 }, disk.ReadFile("Target"));
    }

    [Fact]
    public void Copy_DirectoryWithoutRecursive_Refuses()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        using DiskSession disk = DiskSession.Open(path, writable: true);
        disk.Mkdir("Directory");

        DiskException exception = Assert.Throws<DiskException>(() => disk.Copy("Directory", "Copy"));

        Assert.Equal("recursive_required", exception.Code);
        Assert.Single(disk.List());
    }

    [Fact]
    public void CopyFrom_InsufficientSpaceTransaction_PreservesBothOriginalImages()
    {
        using FixtureWorkspace workspace = new();
        string sourcePath = workspace.NewPath("source.po");
        string destinationPath = workspace.NewPath("destination.po");
        string output = workspace.NewPath("output.po");
        DiskSession.Create(sourcePath, "prodos", sizeKiB: 800, order: "prodos");
        DiskSession.Create(destinationPath, "prodos", order: "prodos");
        using (DiskSession source = DiskSession.Open(sourcePath, writable: true))
            source.Add("Large", Enumerable.Repeat((byte)0x7f, 150000).ToArray(), "BIN");
        byte[] sourceBefore = File.ReadAllBytes(sourcePath);
        byte[] destinationBefore = File.ReadAllBytes(destinationPath);

        Assert.Throws<DiskException>(() => ImageTransactions.Write(destinationPath, output, false, false, temporary =>
        {
            using DiskSession source = DiskSession.Open(sourcePath);
            using DiskSession target = DiskSession.Open(temporary, writable: true);
            target.CopyFrom(source, "Large", "Large");
        }));

        Assert.False(File.Exists(output));
        Assert.Equal(sourceBefore, File.ReadAllBytes(sourcePath));
        Assert.Equal(destinationBefore, File.ReadAllBytes(destinationPath));
    }

    [Fact]
    public void Copy_CanceledBeforeSnapshot_PreservesImage()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] before = File.ReadAllBytes(path);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => disk.Copy("HELLO.BIN", "COPY", cancellationToken: cancellation.Token));
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Move_IndependentDosFile_PreservesPayloadAndAllocatedSpace()
    {
        using FixtureWorkspace workspace = new();
        using DiskSession disk = DiskSession.Open(workspace.CopyDisk(), writable: true);
        byte[] raw = disk.ReadFile("README", raw: true);
        long? free = disk.Info.FreeBytes;
        DiskEntry before = disk.GetEntry("README");

        disk.Move("README", "MOVED");

        Assert.Equal(raw, disk.ReadFile("MOVED", raw: true));
        Assert.Equal(before.StorageSize, disk.GetEntry("MOVED").StorageSize);
        Assert.Equal(free, disk.Info.FreeBytes);
        Assert.Throws<DiskException>(() => disk.GetEntry("README"));
    }

    [Fact]
    public void CopyFrom_RecursiveProDosBetweenImages_PreservesNestedPaths()
    {
        using FixtureWorkspace workspace = new();
        string sourcePath = workspace.NewPath("source.po");
        string targetPath = workspace.NewPath("target.2mg");
        DiskSession.Create(sourcePath, "prodos", order: "prodos");
        DiskSession.Create(targetPath, "prodos", container: "2mg", order: "prodos");
        using (DiskSession setup = DiskSession.Open(sourcePath, writable: true))
        {
            setup.EnsureDirectory("Original/Nested");
            setup.Add("Original/Nested/Program", [0, 128, 255], "BIN", 0x3456);
        }
        using (DiskSession source = DiskSession.Open(sourcePath))
        using (DiskSession target = DiskSession.Open(targetPath, writable: true))
        {
            target.CopyFrom(source, "Original", "Copy", recursive: true);
        }
        using DiskSession reopened = DiskSession.Open(targetPath);
        Assert.Equal(new byte[] { 0, 128, 255 }, reopened.ReadFile("Copy/Nested/Program"));
        Assert.Equal((ushort)0x3456, reopened.GetEntry("Copy/Nested/Program").AuxType);
    }

    [Fact]
    public void EnsureDirectory_ExcessiveDepth_RefusesBeforeCreatingAnyDirectories()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        byte[] before = File.ReadAllBytes(path);
        using (DiskSession disk = DiskSession.Open(path, writable: true))
        {
            string deepPath = string.Join('/', Enumerable.Repeat("Level", 257));
            Assert.Equal("directory_depth", Assert.Throws<DiskException>(() => disk.EnsureDirectory(deepPath)).Code);
            Assert.Empty(disk.List());
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("prodos", "0x0f")]
    [InlineData("prodos", "")]
    [InlineData("dos33", "SYS")]
    public void ValidateFileType_UnsupportedDataFileType_Refuses(string fs, string type)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, fs, order: "prodos");
        using DiskSession disk = DiskSession.Open(path);

        Assert.Equal("invalid_file_type", Assert.Throws<DiskException>(() => disk.ValidateFileType(type)).Code);
    }

    [Theory]
    [InlineData("prodos", "Bad Name")]
    [InlineData("prodos", "123")]
    [InlineData("dos33", "A\u00e9")]
    [InlineData("dos33", "TRAILING ")]
    public void ValidateFileName_UnrepresentableName_RefusesInsteadOfChangingIt(string fs, string name)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, fs, order: "prodos");
        using DiskSession disk = DiskSession.Open(path);

        Assert.Throws<DiskException>(() => disk.ValidateFileName(name));
    }

    [Fact]
    public void EnsureDirectory_ExistingFileOrLockedParent_Refuses()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.po");
        DiskSession.Create(path, "prodos", order: "prodos");
        using DiskSession disk = DiskSession.Open(path, writable: true);
        disk.Add("File", [1], "BIN");
        disk.Mkdir("Locked");
        disk.SetAttributes("Locked", locked: true);

        Assert.Equal("not_a_directory", Assert.Throws<DiskException>(() => disk.EnsureDirectory("File/Child")).Code);
        Assert.Equal("file_locked", Assert.Throws<DiskException>(() => disk.EnsureDirectory("Locked/Child")).Code);
        Assert.Equal(2, disk.List().Count);
    }

    private static ushort ReadProDosKey(byte[] image, params string[] components)
    {
        ushort directoryBlock = 2;
        foreach (string component in components)
        {
            bool found = false;
            ushort block = directoryBlock;
            for (int count = 0; block != 0 && count < 64; count++)
            {
                int start = block * 512;
                for (int index = 0; index < 13; index++)
                {
                    int entry = start + 4 + index * 39;
                    int storageType = image[entry] >> 4;
                    int length = image[entry] & 15;
                    if (storageType is 0 or 14 or 15) continue;
                    string name = System.Text.Encoding.ASCII.GetString(image, entry + 1, length);
                    if (!string.Equals(name, component, StringComparison.OrdinalIgnoreCase)) continue;
                    directoryBlock = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entry + 17));
                    found = true;
                    break;
                }
                if (found) break;
                block = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(start + 2));
            }
            Assert.True(found, $"Independent directory lookup did not find {component}.");
        }
        return directoryBlock;
    }
}
