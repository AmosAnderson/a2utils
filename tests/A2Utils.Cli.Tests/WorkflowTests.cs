// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text;
using System.Text.Json;
using A2Utils.Core.Backends;

namespace A2Utils.Cli.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, $"a2-workflows-{Guid.NewGuid():N}");

    public WorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Export_BinaryPayload_OmitsDosHeaderAndSlack()
    {
        string output = At("payload.bin");
        Success("disk", "export", Fixture(), "HELLO.BIN", "--to", output);
        Assert.Equal(new byte[] { 0, 127, 128, 255, 13 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Export_DosText_ProducesReadableUtf8()
    {
        string output = At("readme.txt");
        var result = Run("disk", "export", Fixture(), "README", "--format", "text", "--to", output, "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("export", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("A2UTILS\n", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("dos33")]
    [InlineData("prodos")]
    public void Text_AddReplaceExport_RoundTripsAcrossHostNewlines(string fs)
    {
        string image = At("work.po");
        string host = At("input.txt");
        File.WriteAllText(host, "ONE\r\nTWO\nTHREE\r", new UTF8Encoding(true));
        Success("disk", "create", image, "--fs", fs);
        Success("disk", "add", image, host, "--name", "NOTES", "--format", "text", "--in-place");
        Success("disk", "export", image, "NOTES", "--format", "text", "--to", At("one.txt"));
        Assert.Equal("ONE\nTWO\nTHREE\n", File.ReadAllText(At("one.txt")));

        File.WriteAllText(host, "EDITED\n\tTAB");
        Success("disk", "replace", image, "NOTES", host, "--format", "text", "--in-place");
        Success("disk", "export", image, "NOTES", "--format", "text", "--to", At("two.txt"));
        Assert.Equal("EDITED\n\tTAB", File.ReadAllText(At("two.txt")));
        using DiskSession session = DiskSession.Open(image);
        Assert.Equal(4, session.GetEntry("NOTES").FileType);
        byte[] payload = session.ReadFile("NOTES");
        Assert.Equal(fs == "dos33" ? (byte)0x8d : (byte)0x0d, payload[6]);
    }

    [Fact]
    public void Text_ExplicitNonTextType_RefusesAndPreservesInput()
    {
        string image = At("work.do");
        string host = At("input.txt");
        File.Copy(Fixture(), image);
        File.WriteAllText(host, "HELLO\n");
        byte[] before = File.ReadAllBytes(image);
        var result = Run("disk", "add", image, host, "--name", "NEW", "--format", "text",
            "--type", "BIN", "--load-address", "0x2000", "--output", At("bad.do"));
        Assert.Equal(2, result.Code);
        Assert.False(File.Exists(At("bad.do")));
        Assert.Equal(before, File.ReadAllBytes(image));
    }

    [Fact]
    public void Copy_SameImage_PreservesRawStoredDataAndLockState()
    {
        string output = At("copy.do");
        Success("disk", "copy", Fixture(), "HELLO.BIN", "COPY", "--output", output);
        using DiskSession source = DiskSession.Open(Fixture());
        using DiskSession copied = DiskSession.Open(output);
        Assert.Equal(source.ReadFile("HELLO.BIN", raw: true), copied.ReadFile("COPY", raw: true));
        Assert.Equal(0x2000, copied.GetEntry("COPY").AuxType);
        Assert.True(copied.GetEntry("COPY").IsLocked);
        Assert.Equal(3, copied.List().Count);
    }

    [Fact]
    public void Copy_SeparateImage_PreservesSourceAndDestinationFiles()
    {
        string target = At("target.2mg");
        Success("disk", "create", target, "--fs", "dos33", "--container", "2mg", "--order", "prodos");
        byte[] original = File.ReadAllBytes(Fixture());
        Success("disk", "copy", target, "HELLO.BIN", "COPIED", "--from", Fixture(), "--in-place");
        using DiskSession session = DiskSession.Open(target);
        Assert.Equal(new byte[] { 0, 127, 128, 255, 13 }, session.ReadFile("COPIED"));
        Assert.Equal(original, File.ReadAllBytes(Fixture()));
    }

    [Fact]
    public void Copy_CrossFilesystem_RefusesImplicitConversion()
    {
        string target = At("target.po");
        Success("disk", "create", target, "--fs", "prodos");
        byte[] before = File.ReadAllBytes(target);
        var result = Run("disk", "copy", target, "HELLO.BIN", "HELLO", "--from", Fixture(), "--in-place");
        Assert.Equal(3, result.Code);
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(_directory, "*.bak"));
    }

    [Fact]
    public void Copy_OutputWouldOverwriteSource_RefusesEvenWithOverwrite()
    {
        string source = At("source.do");
        string target = At("target.do");
        File.Copy(Fixture(), source);
        Success("disk", "create", target, "--fs", "dos33");
        byte[] before = File.ReadAllBytes(source);
        var result = Run("disk", "copy", target, "README", "COPIED", "--from", source,
            "--output", source, "--overwrite");
        Assert.Equal(6, result.Code);
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void Copy_ExplicitSameSourceImage_RefusesSelfDescendant()
    {
        string image = At("work.po");
        Success("disk", "create", image, "--fs", "prodos");
        Success("disk", "mkdir", image, "PARENT", "--in-place");
        byte[] before = File.ReadAllBytes(image);
        var result = Run("disk", "copy", image, "PARENT", "PARENT/CHILD", "--from", image, "--recursive", "--in-place");
        Assert.Equal(6, result.Code);
        Assert.Contains("self_descendant", result.Error);
        Assert.Equal(before, File.ReadAllBytes(image));
    }

    [Fact]
    public void Move_BetweenDirectories_RetainsContentAndMetadata()
    {
        string image = At("work.po");
        string host = At("payload.bin");
        File.WriteAllBytes(host, [1, 2, 255]);
        Success("disk", "create", image, "--fs", "prodos");
        Success("disk", "mkdir", image, "ONE/TWO", "--parents", "--in-place");
        Success("disk", "mkdir", image, "ONE/TWO", "--parents", "--in-place");
        Success("disk", "add", image, host, "--name", "HELLO", "--type", "BIN", "--aux-type", "0x3000", "--in-place");
        Success("disk", "move", image, "HELLO", "ONE/TWO/MOVED", "--in-place");
        using DiskSession session = DiskSession.Open(image);
        Assert.Equal(new byte[] { 1, 2, 255 }, session.ReadFile("ONE/TWO/MOVED"));
        Assert.Equal(0x3000, session.GetEntry("ONE/TWO/MOVED").AuxType);
        Assert.DoesNotContain(session.List(), e => e.Name == "HELLO");
    }

    [Fact]
    public void Import_RecursiveText_CopiesTreeAndConvertsEveryFile()
    {
        string image = At("work.po");
        string host = At("host");
        Directory.CreateDirectory(Path.Combine(host, "Sub"));
        File.WriteAllText(Path.Combine(host, "README"), "ROOT\r\n");
        File.WriteAllText(Path.Combine(host, "Sub", "NOTES"), "CHILD\n");
        Success("disk", "create", image, "--fs", "prodos", "--size", "800k");
        Success("disk", "mkdir", image, "IMPORT", "--in-place");
        Success("disk", "import", image, host, "--to", "IMPORT", "--recursive", "--format", "text", "--in-place");
        Success("disk", "copy", image, "IMPORT", "COPY", "--recursive", "--in-place");
        Success("disk", "export", image, "COPY/Sub/NOTES", "--format", "text", "--to", At("text.txt"));
        Assert.Equal("CHILD\n", File.ReadAllText(At("text.txt")));
        using DiskSession session = DiskSession.Open(image);
        Assert.Equal(Encoding.ASCII.GetBytes("ROOT\r"), session.ReadFile("IMPORT/README"));
        Assert.Equal(4, session.GetEntry("COPY/Sub/NOTES").FileType);
    }

    [Fact]
    public void Import_InvalidTextInLaterFile_RollsBackEntireBatch()
    {
        string image = At("work.po");
        string host = At("host");
        Directory.CreateDirectory(host);
        File.WriteAllText(Path.Combine(host, "FIRST"), "VALID\n");
        File.WriteAllText(Path.Combine(host, "SECOND"), "UNSUPPORTED \u2603");
        Success("disk", "create", image, "--fs", "prodos");
        byte[] before = File.ReadAllBytes(image);
        var result = Run("disk", "import", image, host, "--format", "text", "--in-place");
        Assert.Equal(3, result.Code);
        Assert.Equal(before, File.ReadAllBytes(image));
        Assert.Empty(Directory.GetFiles(_directory, "*.bak"));
    }

    [Theory]
    [InlineData("B")]
    [InlineData("BIN")]
    [InlineData("0x06")]
    public void Import_DosBinaryWithoutAddress_RefusesImplicitMetadata(string type)
    {
        string image = At("work.do");
        string host = At("host");
        Directory.CreateDirectory(host);
        File.WriteAllBytes(Path.Combine(host, "HELLO"), [0, 255]);
        Success("disk", "create", image, "--fs", "dos33");
        var result = Run("disk", "import", image, host, "--type", type, "--output", At("bad.do"));
        Assert.Equal(2, result.Code);
        Assert.False(File.Exists(At("bad.do")));
    }

    private void Success(params string[] args)
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

    private static string Fixture()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "tests", "TestData", "independent-dos33.do");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Independent fixture was not found.");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
