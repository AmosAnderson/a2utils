using System.Security.Cryptography;
using System.Text.Json;
using A2Utils.Cli;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

namespace A2Utils.Cli.Tests;

public sealed class CliApplicationTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, $"a2-cli-tests-{Guid.NewGuid():N}");

    public CliApplicationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Help_DiskGroup_ListsImplementedCommands()
    {
        var result = Run("disk", "--help");
        Assert.Equal(0, result.Code);
        Assert.Contains("extract", result.Output);
        Assert.Contains("convert", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Parse_UnknownOption_ReturnsUsageErrorWithoutStdout()
    {
        var result = Run("disk", "info", "example.do", "--bogus");
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("invalid_arguments", result.Error);
    }

    [Fact]
    public void Info_IndependentFixture_ReturnsVersionedJson()
    {
        var result = Run("disk", "info", Fixture(), "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("dos33", json.RootElement.GetProperty("data").GetProperty("fileSystem").GetString());
        Assert.Equal(143360, json.RootElement.GetProperty("data").GetProperty("sizeBytes").GetInt32());
    }

    [Fact]
    public void List_IndependentFixture_ReportsStoredAndLogicalLengths()
    {
        var result = Run("disk", "ls", Fixture(), "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        var entries = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        var binary = entries.Single(e => e.GetProperty("name").GetString() == "HELLO.BIN");
        Assert.Equal(5, binary.GetProperty("length").GetInt32());
        Assert.Equal(256, binary.GetProperty("storedLength").GetInt32());
    }

    [Fact]
    public void Read_MissingFile_UsesIoExitCodeAndJsonStderr()
    {
        var result = Run("disk", "info", At("missing.do"), "--json");
        Assert.Equal(5, result.Code);
        Assert.Empty(result.Output);
        using JsonDocument json = JsonDocument.Parse(result.Error);
        Assert.Equal("host_io", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Extract_ManifestRestore_PreservesRawBinaryAndMetadata()
    {
        string destination = At("extracted");
        var extracted = Run("disk", "extract", Fixture(), "--to", destination);
        Assert.Equal(0, extracted.Code);
        Assert.True(File.Exists(Path.Combine(destination, FileTransfer.ManifestName)));

        string blank = At("blank.do");
        AssertSuccess("disk", "create", blank, "--fs", "dos33");
        string restored = At("restored.do");
        AssertSuccess("disk", "add", blank, "--manifest", Path.Combine(destination, FileTransfer.ManifestName), "--output", restored);
        using DiskSession original = DiskSession.Open(Fixture());
        using DiskSession roundTrip = DiskSession.Open(restored);
        Assert.Equal(original.ReadFile("HELLO.BIN", raw: true), roundTrip.ReadFile("HELLO.BIN", raw: true));
        Assert.Equal(original.List("HELLO.BIN")[0].RawName, roundTrip.List("HELLO.BIN")[0].RawName);
        Assert.True(roundTrip.List("HELLO.BIN")[0].IsLocked);
    }

    [Fact]
    public void Extract_ExistingDirectory_RefusesOverwrite()
    {
        var result = Run("disk", "extract", Fixture(), "--to", _directory);
        Assert.Equal(6, result.Code);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Mutation_LockedFile_LeavesSourceAndDestinationUntouched()
    {
        string source = At("source.do");
        File.Copy(Fixture(), source);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var result = Run("disk", "delete", source, "HELLO.BIN", "--output", At("failed.do"));
        Assert.Equal(6, result.Code);
        Assert.False(File.Exists(At("failed.do")));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void Mutation_MissingDestination_ReturnsUsageError()
    {
        var result = Run("disk", "delete", Fixture(), "README");
        Assert.Equal(2, result.Code);
    }

    [Fact]
    public void Mutation_InPlaceRename_CreatesUsableBackup()
    {
        string source = At("source.do");
        File.Copy(Fixture(), source);
        byte[] before = File.ReadAllBytes(source);
        var result = Run("disk", "rename", source, "README", "NOTES", "--in-place", "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        string backup = json.RootElement.GetProperty("data").GetProperty("backupPath").GetString()!;
        Assert.Equal(before, File.ReadAllBytes(backup));
        using DiskSession modified = DiskSession.Open(source);
        Assert.Single(modified.List("NOTES"));
    }

    [Fact]
    public void Add_DosBinaryWithoutAddress_RefusesImplicitMetadata()
    {
        string source = At("source.do");
        string host = At("hello.bin");
        File.WriteAllBytes(host, [1, 2, 3]);
        AssertSuccess("disk", "create", source, "--fs", "dos33");
        var result = Run("disk", "add", source, host, "--name", "HELLO", "--type", "B", "--output", At("new.do"));
        Assert.Equal(2, result.Code);
        Assert.False(File.Exists(At("new.do")));
    }

    [Fact]
    public void ProDos_CreateDirectoryAddReplace_ProducesReadableVolume()
    {
        string image = At("volume.2mg");
        string host = At("hello.bin");
        File.WriteAllBytes(host, [0, 127, 255]);
        AssertSuccess("disk", "create", image, "--fs", "prodos", "--size", "800k", "--container", "2mg", "--volume-name", "TEST");
        AssertSuccess("disk", "mkdir", image, "DOCS", "--in-place");
        AssertSuccess("disk", "add", image, host, "--name", "DOCS/HELLO", "--type", "BIN", "--aux-type", "0x2000", "--in-place");
        File.WriteAllBytes(host, [17, 18]);
        AssertSuccess("disk", "replace", image, "DOCS/HELLO", host, "--in-place");
        AssertSuccess("disk", "verify", image);
        using DiskSession session = DiskSession.Open(image);
        Assert.Equal(new byte[] { 17, 18 }, session.ReadFile("DOCS/HELLO"));
        Assert.Equal(0x2000, session.List("DOCS/HELLO")[0].AuxType);
    }

    [Fact]
    public void Create_SizeAndBlocks_RejectsConflictingGeometry()
    {
        var result = Run("disk", "create", At("bad.po"), "--fs", "prodos", "--size", "800k", "--blocks", "1600");
        Assert.Equal(2, result.Code);
        Assert.False(File.Exists(At("bad.po")));
    }

    [Fact]
    public void Quiet_Info_SuppressesNormalOutput()
    {
        var result = Run("disk", "info", Fixture(), "--quiet");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Output);
    }

    [Fact]
    public void Convert_DosToProDosOrder_MatchesIndependentFixture()
    {
        string destination = At("converted.po");
        AssertSuccess("disk", "convert", Fixture(), destination, "--container", "raw", "--order", "prodos");
        Assert.Equal(File.ReadAllBytes(Fixture("independent-dos33.po")), File.ReadAllBytes(destination));
    }

    private void AssertSuccess(params string[] args)
    {
        var result = Run(args);
        Assert.True(result.Code == 0, $"Exit {result.Code}: {result.Error}");
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string At(string name) => Path.Combine(_directory, name);

    private static string Fixture(string name = "independent-dos33.do")
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "tests", "TestData", name);
            if (File.Exists(path))
            {
                return path;
            }
        }
        throw new FileNotFoundException("Unable to find the independent test fixture.");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
