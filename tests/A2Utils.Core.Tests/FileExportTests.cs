// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class FileExportTests
{
    [Fact]
    public void Export_DosBinary_WritesLogicalPayloadWithoutHeaderOrSectorSlack()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        byte[] before = File.ReadAllBytes(source);
        string destination = workspace.NewPath("hello.bin");
        using (DiskSession disk = DiskSession.Open(source))
        {
            ImageWriteResult result = FileExport.Export(disk, "HELLO.BIN", destination);
            Assert.Equal(destination, result.OutputPath);
            Assert.Null(result.BackupPath);
        }

        Assert.Equal(new byte[] { 0x00, 0x7f, 0x80, 0xff, 0x0d }, File.ReadAllBytes(destination));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void Export_DosText_ConvertsHighAsciiAndCrToUtf8Lf()
    {
        using FixtureWorkspace workspace = new();
        using DiskSession disk = DiskSession.Open(workspace.CopyDisk());
        string destination = workspace.NewPath("readme.txt");

        FileExport.Export(disk, "README", destination, format: "text");

        Assert.Equal("A2UTILS\n"u8.ToArray(), File.ReadAllBytes(destination));
    }

    [Fact]
    public void Export_TextInBinaryMode_LeavesTextEncodingUntouched()
    {
        using FixtureWorkspace workspace = new();
        using DiskSession disk = DiskSession.Open(workspace.CopyDisk());
        string destination = workspace.NewPath("readme.bin");

        FileExport.Export(disk, "README", destination, format: "binary");

        Assert.Equal(new byte[] { 0xc1, 0xb2, 0xd5, 0xd4, 0xc9, 0xcc, 0xd3, 0x8d },
            File.ReadAllBytes(destination));
    }

    [Fact]
    public void Export_ExistingDestination_RequiresExplicitOverwrite()
    {
        using FixtureWorkspace workspace = new();
        using DiskSession disk = DiskSession.Open(workspace.CopyDisk());
        string destination = workspace.NewPath("existing.bin");
        File.WriteAllBytes(destination, [0xab]);

        DiskException error = Assert.Throws<DiskException>(() => FileExport.Export(disk, "HELLO.BIN", destination));
        Assert.Equal("write.destination_exists", error.Code);
        Assert.Equal(new byte[] { 0xab }, File.ReadAllBytes(destination));

        FileExport.Export(disk, "HELLO.BIN", destination, overwrite: true);
        Assert.Equal(new byte[] { 0x00, 0x7f, 0x80, 0xff, 0x0d }, File.ReadAllBytes(destination));
    }

    [Fact]
    public void Export_TextRequestedForBinary_RefusesWithoutReplacingDestination()
    {
        using FixtureWorkspace workspace = new();
        using DiskSession disk = DiskSession.Open(workspace.CopyDisk());
        string destination = workspace.NewPath("existing.txt");
        File.WriteAllBytes(destination, [0xab]);

        DiskException error = Assert.Throws<DiskException>(() =>
            FileExport.Export(disk, "HELLO.BIN", destination, format: "text", overwrite: true));

        Assert.Equal("text.unsupported_file_type", error.Code);
        Assert.Equal(new byte[] { 0xab }, File.ReadAllBytes(destination));
        Assert.Equal(2, Directory.GetFiles(workspace.DirectoryPath).Length);
    }

    [Fact]
    public void Export_UnsupportedFormat_ValidatesBeforeLookingUpEntry()
    {
        using FixtureWorkspace workspace = new();
        using DiskSession disk = DiskSession.Open(workspace.CopyDisk());

        DiskException error = Assert.Throws<DiskException>(() =>
            FileExport.Export(disk, "MISSING", workspace.NewPath("output.txt"), format: "invalid"));

        Assert.Equal("export.unsupported_format", error.Code);
        Assert.Single(Directory.GetFiles(workspace.DirectoryPath));
    }

    [Fact]
    public void Export_SourceAsDestination_RefusesEvenWithOverwrite()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        byte[] before = File.ReadAllBytes(source);
        using (DiskSession disk = DiskSession.Open(source))
        {
            Assert.Throws<DiskException>(() => FileExport.Export(disk, "HELLO.BIN", source, overwrite: true));
        }

        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void Export_CancelledOperation_PreservesExistingDestination()
    {
        using FixtureWorkspace workspace = new();
        using DiskSession disk = DiskSession.Open(workspace.CopyDisk());
        string destination = workspace.NewPath("existing.txt");
        File.WriteAllBytes(destination, [0xab]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => FileExport.Export(disk, "README", destination,
            format: "text", overwrite: true, cancellationToken: cancellation.Token));

        Assert.Equal(new byte[] { 0xab }, File.ReadAllBytes(destination));
        Assert.Equal(2, Directory.GetFiles(workspace.DirectoryPath).Length);
    }

    [Fact]
    public void Export_ProDosTextWithUnsupportedControl_RefusesWithoutCreatingOutput()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po");
        DiskSession.Create(source, "prodos", order: "prodos");
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Add("CONTROL", [0x41, 0x00, 0x42], "TXT");
        }

        using DiskSession reopened = DiskSession.Open(source);
        DiskException error = Assert.Throws<DiskException>(() =>
            FileExport.Export(reopened, "CONTROL", workspace.NewPath("output.txt"), format: "text"));

        Assert.Equal("text.unsupported_character", error.Code);
        Assert.Single(Directory.GetFiles(workspace.DirectoryPath));
    }

    [Theory]
    [InlineData("dos33", "dos", "T")]
    [InlineData("prodos", "prodos", "TXT")]
    public void ImportExport_EncodedText_RoundTripsNormalizedUtf8(string fileSystem, string order, string type)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.dsk");
        DiskSession.Create(source, fileSystem, order: order);
        using (DiskSession disk = DiskSession.Open(source, writable: true))
        {
            disk.Add("TEXT", AppleTextCodec.Encode("One\r\n\tTwo\nThree\rFour"u8, fileSystem), type);
        }

        using DiskSession reopened = DiskSession.Open(source);
        string destination = workspace.NewPath("export.txt");
        FileExport.Export(reopened, "TEXT", destination, format: "text");

        Assert.Equal("One\n\tTwo\nThree\nFour"u8.ToArray(), File.ReadAllBytes(destination));
    }

    [Fact]
    public void Export_Directory_RefusesWithoutCreatingOutput()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("source.po");
        DiskSession.Create(source, "prodos", order: "prodos");
        using DiskSession disk = DiskSession.Open(source);

        DiskException error = Assert.Throws<DiskException>(() =>
            FileExport.Export(disk, "/", workspace.NewPath("output.bin")));

        Assert.Equal("export.not_a_file", error.Code);
        Assert.Single(Directory.GetFiles(workspace.DirectoryPath));
    }
}
