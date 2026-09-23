// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Diagnostics;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class DirectoryImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-directory-import-" + Guid.NewGuid().ToString("N"));

    public DirectoryImportTests()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Host);
    }

    [Fact]
    public void Import_RecursiveProdosTree_PreservesNamesHierarchyAndPayloads()
    {
        string source = CreateDisk("prodos");
        using (DiskSession image = DiskSession.Open(source, writable: true))
        {
            image.Mkdir("DEST");
            image.Add("UNCHANGED", [99], "BIN");
        }
        byte[] original = File.ReadAllBytes(source);
        Directory.CreateDirectory(Path.Combine(Host, "Docs"));
        Directory.CreateDirectory(Path.Combine(Host, "Empty"));
        File.WriteAllBytes(Path.Combine(Host, "Hello.Bin"), [0, 127, 255]);
        File.WriteAllBytes(Path.Combine(Host, "Docs", "Readme"), [1, 2, 3, 4]);

        DirectoryImportResult result = Import(source, "DEST", "BIN", recursive: true, auxType: 0x2000);

        Assert.Equal(new DirectoryImportResult(2, 2, 7), result);
        Assert.Equal(original, File.ReadAllBytes(source));
        using DiskSession output = DiskSession.Open(Output);
        Assert.True(output.GetEntry("DEST/Empty").IsDirectory);
        Assert.Equal("Hello.Bin", output.GetEntry("DEST/Hello.Bin").Name);
        Assert.Equal("Docs", output.GetEntry("DEST/Docs").Name);
        Assert.Equal(new byte[] { 0, 127, 255 }, output.ReadFile("DEST/Hello.Bin"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, output.ReadFile("DEST/Docs/Readme"));
        Assert.Equal(new byte[] { 99 }, output.ReadFile("UNCHANGED"));
        Assert.Equal((ushort)0x2000, output.GetEntry("DEST/Hello.Bin").AuxType);
        Assert.DoesNotContain(output.Verify(), diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void Import_FlatDosDirectory_ImportsEveryPayloadWithExplicitMetadata()
    {
        string source = CreateDisk("dos33");
        File.WriteAllBytes(Path.Combine(Host, "FIRST"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(Host, "SECOND"), [4, 5]);

        DirectoryImportResult result = Import(source, "", "B", auxType: 0x2000);

        Assert.Equal(new DirectoryImportResult(2, 0, 5), result);
        using DiskSession output = DiskSession.Open(Output);
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ReadFile("FIRST"));
        Assert.Equal(new byte[] { 4, 5 }, output.ReadFile("SECOND"));
        Assert.Equal((ushort)0x2000, output.GetEntry("FIRST").AuxType);
    }

    [Theory]
    [InlineData("dos33", false, "unsupported_directories")]
    [InlineData("dos33", true, "unsupported_directories")]
    [InlineData("prodos", false, "import_recursive_required")]
    public void Import_UnrepresentableOrUnrequestedHierarchy_RefusesEntireBatch(string fileSystem, bool recursive, string code)
    {
        string source = CreateDisk(fileSystem);
        byte[] original = File.ReadAllBytes(source);
        File.WriteAllBytes(Path.Combine(Host, "FIRST"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(Host, "NESTED"));

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "", "BIN", recursive: recursive));

        Assert.Equal(code, error.Code);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData("THIS.NAME.IS.TOO.LONG")]
    [InlineData("1INVALID")]
    [InlineData("BAD NAME")]
    public void Import_InvalidProdosName_RefusesWithoutTruncatingOrRenaming(string name)
    {
        string source = CreateDisk("prodos");
        byte[] original = File.ReadAllBytes(source);
        File.WriteAllBytes(Path.Combine(Host, "A.VALID"), [1]);
        File.WriteAllBytes(Path.Combine(Host, name), [2]);

        Assert.Throws<DiskException>(() => Import(source, "", "BIN"));

        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_ExistingCaseInsensitiveName_RefusesAndPreservesExistingFile()
    {
        string source = CreateDisk("prodos");
        using (DiskSession image = DiskSession.Open(source, writable: true))
        {
            image.Add("Readme", [1, 2, 3], "BIN");
        }
        byte[] original = File.ReadAllBytes(source);
        File.WriteAllBytes(Path.Combine(Host, "README"), [4, 5, 6]);

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "", "BIN"));

        Assert.Equal("import_name_conflict", error.Code);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_MissingDestination_RefusesWithoutCreatingParents()
    {
        string source = CreateDisk("prodos");
        byte[] original = File.ReadAllBytes(source);
        File.WriteAllBytes(Path.Combine(Host, "README"), [1]);

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "MISSING/DEST", "BIN"));

        Assert.Equal("file_not_found", error.Code);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_TransformFailure_ReadsBeforeAnyDiskMutation()
    {
        string source = CreateDisk("prodos");
        Directory.CreateDirectory(Path.Combine(Host, "DOCS"));
        File.WriteAllBytes(Path.Combine(Host, "A"), [1]);
        File.WriteAllBytes(Path.Combine(Host, "B"), [2]);
        using DiskSession image = DiskSession.Open(source, writable: true);
        int transformed = 0;

        Assert.Throws<InvalidDataException>(() => DirectoryImport.Import(image, Host, "", "BIN",
            recursive: true, transform: bytes => ++transformed == 2 ? throw new InvalidDataException("Bad text.") : bytes));

        Assert.Equal(2, transformed);
        Assert.Empty(image.List(recursive: true));
    }

    [Fact]
    public void Import_PayloadTransform_AppliesToEveryFile()
    {
        string source = CreateDisk("prodos");
        File.WriteAllBytes(Path.Combine(Host, "FIRST"), [1, 2]);
        File.WriteAllBytes(Path.Combine(Host, "SECOND"), [3]);

        DirectoryImportResult result = Import(source, "", "BIN", transform: bytes => bytes.Select(value => (byte)(value + 1)).ToArray());

        Assert.Equal(3, result.BytesImported);
        using DiskSession output = DiskSession.Open(Output);
        Assert.Equal(new byte[] { 2, 3 }, output.ReadFile("FIRST"));
        Assert.Equal(new byte[] { 4 }, output.ReadFile("SECOND"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_PayloadsExceedCapacity_RefusesBeforeMutation(bool transformed)
    {
        string source = CreateDisk("prodos");
        byte[] original = File.ReadAllBytes(source);
        File.WriteAllBytes(Path.Combine(Host, "BIG"), new byte[transformed ? 1 : 143361]);

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "", "BIN",
            transform: transformed ? _ => new byte[143361] : null));

        Assert.Equal("import_too_large", error.Code);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_DiskAllocationFailure_RollsBackWholeBatch()
    {
        string source = CreateDisk("prodos");
        byte[] original = File.ReadAllBytes(source);
        File.WriteAllBytes(Path.Combine(Host, "FIRST"), Enumerable.Repeat((byte)0xAA, 100).ToArray());
        File.WriteAllBytes(Path.Combine(Host, "SECOND"), Enumerable.Repeat((byte)0x55, 143260).ToArray());

        Assert.Throws<DiskException>(() => Import(source, "", "BIN"));

        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(Output));
        Assert.Empty(Directory.GetFiles(_directory, ".*.a2-*"));
    }

    [Fact]
    public void Import_CancelledDuringRead_RollsBackWholeBatch()
    {
        string source = CreateDisk("prodos");
        byte[] original = File.ReadAllBytes(source);
        File.WriteAllBytes(Path.Combine(Host, "FIRST"), [1]);
        using CancellationTokenSource cancellation = new();

        Assert.Throws<OperationCanceledException>(() => Import(source, "", "BIN", transform: bytes =>
        {
            cancellation.Cancel();
            return bytes;
        }, cancellationToken: cancellation.Token));

        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_HostTreeChangesDuringRead_RefusesSnapshot()
    {
        string source = CreateDisk("prodos");
        byte[] original = File.ReadAllBytes(source);
        File.WriteAllBytes(Path.Combine(Host, "FIRST"), [1]);

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "", "BIN", transform: bytes =>
        {
            File.WriteAllBytes(Path.Combine(Host, "ADDED"), [2]);
            return bytes;
        }));

        Assert.Equal("import_concurrent_change", error.Code);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_UnixHostLink_RefusesOutsidePayload()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        string source = CreateDisk("prodos");
        string outside = Path.Combine(_directory, "outside");
        File.WriteAllBytes(outside, [1, 2, 3]);
        File.CreateSymbolicLink(Path.Combine(Host, "LINK"), outside);

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "", "BIN"));

        Assert.Equal("import_link_refused", error.Code);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_UnixCaseCollidingHostFiles_RefusesProdosBatch()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        string source = CreateDisk("prodos");
        File.WriteAllBytes(Path.Combine(Host, "File"), [1]);
        File.WriteAllBytes(Path.Combine(Host, "FILE"), [2]);

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "", "BIN"));

        Assert.Equal("import_name_conflict", error.Code);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_EmptyDirectoryWithInvalidType_RefusesMetadata()
    {
        string source = CreateDisk("prodos");

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "", "INVALID"));

        Assert.Equal("invalid_file_type", error.Code);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_TooManyDosEntries_RefusesBeforeReadingPayloads()
    {
        string source = CreateDisk("dos33");
        for (int index = 0; index < 106; index++)
        {
            File.WriteAllBytes(Path.Combine(Host, $"FILE{index:D3}"), []);
        }

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "", "T"));

        Assert.Equal("import_too_many_entries", error.Code);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void Import_UnixNamedPipe_RefusesWithoutOpeningPipe()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        string source = CreateDisk("prodos");
        ProcessStartInfo start = new("/usr/bin/mkfifo") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(Host, "PIPE"));
        using Process createPipe = Process.Start(start)!;
        createPipe.WaitForExit();
        Assert.Equal(0, createPipe.ExitCode);

        DiskException error = Assert.Throws<DiskException>(() => Import(source, "", "BIN"));

        Assert.Equal("unsupported_host_entry", error.Code);
        Assert.False(File.Exists(Output));
    }

    private DirectoryImportResult Import(string source, string destination, string type, ushort auxType = 0,
        bool recursive = false, Func<byte[], byte[]>? transform = null, CancellationToken cancellationToken = default)
    {
        DirectoryImportResult? result = null;
        ImageTransactions.Write(source, Output, false, false, temporary =>
        {
            using DiskSession image = DiskSession.Open(temporary, writable: true);
            result = DirectoryImport.Import(image, Host, destination, type, auxType, recursive, transform, cancellationToken);
        }, temporary =>
        {
            using DiskSession image = DiskSession.Open(temporary);
            Assert.DoesNotContain(image.Verify(), diagnostic => diagnostic.Severity == "error");
        }, cancellationToken);
        return result!;
    }

    private string CreateDisk(string fileSystem)
    {
        string path = Path.Combine(_directory, "source.po");
        DiskSession.Create(path, fileSystem, order: "prodos");
        return path;
    }

    private string Host => Path.Combine(_directory, "host");
    private string Output => Path.Combine(_directory, "output.po");

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
