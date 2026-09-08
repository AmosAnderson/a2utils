using System.Buffers.Binary;
using System.Runtime.InteropServices;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class ImageTransactionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "a2utils-transactions-" + Guid.NewGuid().ToString("N"));

    public ImageTransactionsTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Write_OutputPath_PreservesSourceAndCommitsEditedCopy()
    {
        string source = Write("source.do", [1, 2, 3]);
        string output = Path.Combine(_directory, "output.do");
        bool validated = false;

        ImageWriteResult result = ImageTransactions.Write(source, output, false, false,
            temporary => File.WriteAllBytes(temporary, [4, 5, 6]),
            temporary => validated = File.ReadAllBytes(temporary).SequenceEqual(new byte[] { 4, 5, 6 }));

        Assert.True(validated);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(output));
        Assert.Equal(output, result.OutputPath);
        Assert.Null(result.BackupPath);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Write_InPlace_CreatesBackupWithOriginalBytes()
    {
        string source = Write("source.do", [1, 2, 3]);

        ImageWriteResult first = ImageTransactions.Write(source, null, true, false,
            temporary => File.WriteAllBytes(temporary, [4, 5, 6]));
        ImageWriteResult second = ImageTransactions.Write(source, null, true, false,
            temporary => File.WriteAllBytes(temporary, [7, 8, 9]));

        Assert.NotNull(first.BackupPath);
        Assert.NotNull(second.BackupPath);
        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(first.BackupPath));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(second.BackupPath));
        Assert.Equal([7, 8, 9], File.ReadAllBytes(source));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_EditFailure_PreservesInputAndExistingDestination(bool inPlace)
    {
        string source = Write("source.do", [1, 2, 3]);
        string output = Write("output.do", [7, 8, 9]);

        Assert.Throws<IOException>(() => ImageTransactions.Write(source, inPlace ? null : output, inPlace, true,
            temporary =>
            {
                File.WriteAllBytes(temporary, [4, 5, 6]);
                throw new IOException("Simulated disk full.");
            }));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.Equal([7, 8, 9], File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(_directory, "*.bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Write_ValidationFailure_DoesNotCreateDestination()
    {
        string source = Write("source.do", [1, 2, 3]);
        string output = Path.Combine(_directory, "output.do");

        Assert.Throws<InvalidDataException>(() => ImageTransactions.Write(source, output, false, false,
            temporary => File.WriteAllBytes(temporary, [4, 5, 6]),
            _ => throw new InvalidDataException("Invalid disk.")));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.False(File.Exists(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Write_CancellationAfterValidation_DoesNotCommit()
    {
        string source = Write("source.do", [1, 2, 3]);
        using CancellationTokenSource cancellation = new();

        Assert.Throws<OperationCanceledException>(() => ImageTransactions.Write(source, null, true, false,
            temporary => File.WriteAllBytes(temporary, [4, 5, 6]),
            _ => cancellation.Cancel(), cancellation.Token));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(_directory, "*.bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Write_SourceChangesDuringEdit_RefusesCommitAndRetainsExternalChange()
    {
        string source = Write("source.do", [1, 2, 3]);
        string output = Path.Combine(_directory, "output.do");

        DiskException error = Assert.Throws<DiskException>(() => ImageTransactions.Write(source, output, false, false,
            temporary =>
            {
                File.WriteAllBytes(temporary, [4, 5, 6]);
                File.WriteAllBytes(source, [7, 8, 9]);
            }));

        Assert.Equal("write.concurrent_change", error.Code);
        Assert.Equal([7, 8, 9], File.ReadAllBytes(source));
        Assert.False(File.Exists(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Write_DestinationChangesDuringEdit_RefusesCommitAndRetainsExternalChange()
    {
        string source = Write("source.do", [1, 2, 3]);
        string output = Write("output.do", [4, 5, 6]);

        DiskException error = Assert.Throws<DiskException>(() => ImageTransactions.Write(source, output, false, true,
            _ => File.WriteAllBytes(output, [7, 8, 9])));

        Assert.Equal("write.concurrent_change", error.Code);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.Equal([7, 8, 9], File.ReadAllBytes(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Write_DestinationAppearsDuringEdit_RefusesCommit()
    {
        string source = Write("source.do", [1, 2, 3]);
        string output = Path.Combine(_directory, "output.do");

        DiskException error = Assert.Throws<DiskException>(() => ImageTransactions.Write(source, output, false, true,
            _ => File.WriteAllBytes(output, [7, 8, 9])));

        Assert.Equal("write.concurrent_change", error.Code);
        Assert.Equal([7, 8, 9], File.ReadAllBytes(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Write_SamePathAsOutput_RequiresInPlace()
    {
        string source = Write("source.do", [1, 2, 3]);

        DiskException error = Assert.Throws<DiskException>(() =>
            ImageTransactions.Write(source, Path.Combine(_directory, ".", "source.do"), false, true, _ => { }));

        Assert.Equal("write.source_alias", error.Code);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
    }

    [Fact]
    public void Write_WindowsHardLinkAlias_RefusesCommit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string source = Write("source.do", [1, 2, 3]);
        string alias = Path.Combine(_directory, "alias.do");
        Assert.True(CreateHardLink(alias, source, IntPtr.Zero));

        DiskException error = Assert.Throws<DiskException>(() =>
            ImageTransactions.Write(source, alias, false, true, _ => { }));

        Assert.Equal("write.source_alias", error.Code);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
    }

    [Fact]
    public void Write_UnixHardLinkOutput_RetainsInputInodeBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string source = Write("source.do", [1, 2, 3]);
        string alias = Path.Combine(_directory, "alias.do");
        Assert.Equal(0, Link(source, alias));

        ImageTransactions.Write(source, alias, false, true, temporary => File.WriteAllBytes(temporary, [4, 5, 6]));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(alias));
    }

    [Fact]
    public void Write_UnixSymbolicLink_RefusesCommit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string source = Write("source.do", [1, 2, 3]);
        string alias = Path.Combine(_directory, "alias.do");
        File.CreateSymbolicLink(alias, source);

        DiskException error = Assert.Throws<DiskException>(() =>
            ImageTransactions.Write(alias, null, true, false, _ => { }));

        Assert.Equal("write.link_refused", error.Code);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("output.do", true)]
    public void Write_InvalidDestinationChoice_ReportsUsageError(string? output, bool inPlace)
    {
        string source = Write("source.do", [1, 2, 3]);

        DiskException error = Assert.Throws<DiskException>(() => ImageTransactions.Write(source, output, inPlace, false, _ => { }));

        Assert.Equal(2, error.ExitCode);
    }

    [Fact]
    public void Create_ExistingDestinationWithoutOverwrite_RefusesBeforeCreation()
    {
        string output = Write("output.do", [1, 2, 3]);
        bool called = false;

        DiskException error = Assert.Throws<DiskException>(() => ImageTransactions.Create(output, false, _ => called = true));

        Assert.Equal("write.destination_exists", error.Code);
        Assert.False(called);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(output));
    }

    [Fact]
    public void Create_ValidationFailure_PreservesExistingDestination()
    {
        string output = Write("output.do", [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => ImageTransactions.Create(output, true,
            temporary => File.WriteAllBytes(temporary, [4, 5, 6]),
            _ => throw new InvalidDataException("Invalid disk.")));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Create_ValidImage_CommitsNewFile()
    {
        string output = Path.Combine(_directory, "output.do");

        ImageTransactions.Create(output, false, temporary => File.WriteAllBytes(temporary, [1, 2, 3]));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Convert_DosToProdosAndBack_PreservesAllSectorsAndMatchesIndependentVector()
    {
        byte[] original = SectorVector();
        string source = Write("source.do", original);
        string reordered = Path.Combine(_directory, "reordered.po");
        string restored = Path.Combine(_directory, "restored.do");

        ImageConverter.Convert(source, reordered, "raw", "prodos", "dos");
        ImageConverter.Convert(reordered, restored, "raw", "dos", "prodos");

        byte[] actual = File.ReadAllBytes(reordered);
        // ProDOS blocks 0..7 pair DOS sectors (0,14), (13,12), ... (3,2), (1,15).
        int[] dosSectorsInProdosOrder = [0, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 15];
        for (int track = 0; track < 35; track++)
        {
            for (int sector = 0; sector < 16; sector++)
            {
                Assert.Equal((byte)track, actual[(track * 16 + sector) * 256]);
                Assert.Equal((byte)dosSectorsInProdosOrder[sector], actual[(track * 16 + sector) * 256 + 1]);
            }
        }

        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(original, File.ReadAllBytes(restored));
    }

    [Fact]
    public void Convert_RawToTwoImgAndBack_PreservesPayload()
    {
        byte[] original = SectorVector();
        string source = Write("source.do", original);
        string wrapped = Path.Combine(_directory, "wrapped.2mg");
        string restored = Path.Combine(_directory, "restored.do");

        ImageConverter.Convert(source, wrapped, "2mg", "prodos", "dos");
        ImageConverter.Convert(wrapped, restored, "raw", "dos", allowMetadataLoss: true);

        Assert.Equal(original, File.ReadAllBytes(restored));
        byte[] container = File.ReadAllBytes(wrapped);
        Assert.Equal("2IMG"u8.ToArray(), container[..4]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(12)));
    }

    [Theory]
    [InlineData(1600)]
    [InlineData(65535)]
    [InlineData(65536)]
    public void Convert_LargeProdosPayloadRoundTrip_PreservesEveryBlock(int blocks)
    {
        byte[] original = new byte[blocks * 512];
        new Random(0xA2).NextBytes(original);
        // A 32 MiB host image may contain a 65,535-block volume and an unused final block.
        if (blocks == 65536)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(2 * 512 + 0x29), 65535);
            original.AsSpan(original.Length - 512).Fill(0xA2);
        }
        string source = Write("source.po", original);
        string wrapped = Path.Combine(_directory, "wrapped.2mg");
        string copied = Path.Combine(_directory, "copied.2mg");
        string restored = Path.Combine(_directory, "restored.po");

        ImageConverter.Convert(source, wrapped, "2mg", "prodos", "prodos");
        ImageConverter.Convert(wrapped, copied, "2mg", "prodos");
        ImageConverter.Convert(copied, restored, "raw", "prodos", allowMetadataLoss: true);

        Assert.Equal(original, File.ReadAllBytes(restored));
        Assert.Equal(original.Length + 64, new FileInfo(wrapped).Length);
        byte[] header = new byte[64];
        using FileStream stream = File.OpenRead(wrapped);
        stream.ReadExactly(header);
        Assert.Equal((uint)blocks, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)));
        Assert.Equal((uint)original.Length, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)));
    }

    [Theory]
    [InlineData("dos", "prodos")]
    [InlineData("prodos", "dos")]
    public void Convert_LargePayloadWithDosOrder_RefusesWithoutOutput(string inputOrder, string outputOrder)
    {
        string source = Write("source.po", new byte[1600 * 512]);
        string output = Path.Combine(_directory, "output.2mg");

        DiskException error = Assert.Throws<DiskException>(() =>
            ImageConverter.Convert(source, output, "2mg", outputOrder, inputOrder));

        Assert.Equal("image.geometry_unsupported", error.Code);
        Assert.False(File.Exists(output));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(279 * 512)]
    [InlineData(1600 * 512 + 1)]
    [InlineData(65537 * 512)]
    public void Convert_UnsupportedPayloadLength_RefusesWithoutOutput(int length)
    {
        string source = Write("source.po", new byte[length]);
        string output = Path.Combine(_directory, "output.2mg");

        DiskException error = Assert.Throws<DiskException>(() =>
            ImageConverter.Convert(source, output, "2mg", "prodos", "prodos"));

        Assert.Equal("image.geometry_unsupported", error.Code);
        Assert.False(File.Exists(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Convert_TwoImgToRawWithoutMetadataConsent_RefusesAndPreservesSource()
    {
        byte[] original = TwoImgWithMetadata();
        string source = Write("source.2mg", original);
        string output = Path.Combine(_directory, "output.do");

        DiskException error = Assert.Throws<DiskException>(() => ImageConverter.Convert(source, output, "raw", "dos"));

        Assert.Equal("convert.metadata_loss", error.Code);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Convert_TwoImgOrderRoundTrip_PreservesHeaderCommentCreatorGapsAndTrailingBytes()
    {
        byte[] original = TwoImgWithMetadata();
        string source = Write("source.2mg", original);
        string reordered = Path.Combine(_directory, "reordered.2mg");
        string restored = Path.Combine(_directory, "restored.2mg");

        ImageConverter.Convert(source, reordered, "2mg", "prodos");
        ImageConverter.Convert(reordered, restored, "2mg", "dos");

        Assert.Equal(original, File.ReadAllBytes(restored));
    }

    [Theory]
    [InlineData(24, uint.MaxValue)]
    [InlineData(28, uint.MaxValue)]
    [InlineData(32, 65u)]
    [InlineData(36, uint.MaxValue)]
    [InlineData(40, 143440u)]
    public void Convert_InvalidTwoImgRanges_RefusesWithoutOutput(int field, uint value)
    {
        byte[] invalid = TwoImgWithMetadata();
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(field), value);
        string source = Write("invalid.2mg", invalid);
        string output = Path.Combine(_directory, "output.2mg");

        DiskException error = Assert.Throws<DiskException>(() => ImageConverter.Convert(source, output, "2mg", "prodos"));

        Assert.Equal(4, error.ExitCode);
        Assert.Equal(invalid, File.ReadAllBytes(source));
        Assert.False(File.Exists(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Convert_UnknownFilesystemWithoutInputOrder_RefusesAmbiguousRawImage()
    {
        string source = Write("unknown.dsk", new byte[143360]);
        string output = Path.Combine(_directory, "output.po");

        Assert.Throws<DiskException>(() => ImageConverter.Convert(source, output, "raw", "prodos"));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Convert_TwoImgConflictingInputOrder_RefusesOverride()
    {
        string source = Write("source.2mg", TwoImgWithMetadata());
        string output = Path.Combine(_directory, "output.2mg");

        DiskException error = Assert.Throws<DiskException>(() => ImageConverter.Convert(source, output, "2mg", "dos", "prodos"));

        Assert.Equal("image.order_conflict", error.Code);
        Assert.False(File.Exists(output));
    }

    private static byte[] SectorVector()
    {
        byte[] payload = new byte[143360];
        new Random(0xA2).NextBytes(payload);
        for (int track = 0; track < 35; track++)
        {
            for (int sector = 0; sector < 16; sector++)
            {
                payload[(track * 16 + sector) * 256] = (byte)track;
                payload[(track * 16 + sector) * 256 + 1] = (byte)sector;
            }
        }

        return payload;
    }

    private static byte[] TwoImgWithMetadata()
    {
        const int offset = 80;
        const int payloadLength = 143360;
        byte[] image = new byte[offset + payloadLength + 96];
        new Random(0x2A).NextBytes(image);
        "2IMGTEST"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(8), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 0x800001FE);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 280);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), offset + payloadLength + 8);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(40), offset + payloadLength + 32);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), 32);
        SectorVector().CopyTo(image, offset);
        return image;
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private void AssertNoTemporaryFiles() => Assert.Empty(Directory.GetFiles(_directory, ".*.a2-*"));

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string source, string destination);
}
