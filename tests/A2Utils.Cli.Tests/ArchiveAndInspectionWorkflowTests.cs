// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace A2Utils.Cli.Tests;

public sealed class ArchiveAndInspectionWorkflowTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(TestPaths.TemporaryRoot, "a2-archive-inspect-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Archive_ListsExtractsAndCatalogsThroughCli()
    {
        string archive = Fixture("independent-files.shk");
        var listing = Run("archive", "ls", archive, "--json");
        Assert.Equal(0, listing.Code);
        using var json = JsonDocument.Parse(listing.Output);
        Assert.Equal("archive.ls", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("resource", json.RootElement.GetProperty("data").GetProperty("entries")[0].GetProperty("parts")[1].GetProperty("name").GetString());

        string output = Path.Combine(_directory, "resource");
        var extracted = Run("archive", "extract", archive, "--index", "1", "--part", "resource", "--to", output, "--json");
        Assert.Equal(0, extracted.Code);
        using var result = JsonDocument.Parse(extracted.Output);
        Assert.Equal(4, result.RootElement.GetProperty("data").GetProperty("length").GetInt32());
        Assert.Equal(new byte[] { 0, 127, 128, 255 }, File.ReadAllBytes(output));

        var ambiguous = Run("archive", "extract", archive, "EMPTY", "--to", output, "--overwrite", "--json");
        Assert.Equal(2, ambiguous.Code);
        using var error = JsonDocument.Parse(ambiguous.Error);
        Assert.Equal("archive.ambiguous_entry", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(new byte[] { 0, 127, 128, 255 }, File.ReadAllBytes(output));
        var catalog = Run("archive", "catalog", Fixture("independent-disk.sdk"));
        Assert.Equal(0, catalog.Code);
        Assert.Contains("HELLO.BIN", catalog.Output);
    }

    [Fact]
    public void Inspect_BySectorAndByBlock_ReturnsTextAndJson()
    {
        string path = Fixture("independent-dos33.do");
        var text = Run("disk", "inspect", path, "--track", "18", "--sector", "14");
        Assert.Equal(0, text.Code);
        Assert.Contains("track 18 sector 14", text.Output);
        Assert.Contains("00 20 05 00 00 7F 80 FF 0D", text.Output);

        var block = Run("disk", "inspect", path, "--block", "144", "--json");
        Assert.Equal(0, block.Code);
        using var json = JsonDocument.Parse(block.Output);
        Assert.Equal("disk.inspect", json.RootElement.GetProperty("command").GetString());
        var ranges = json.RootElement.GetProperty("data").GetProperty("ranges");
        Assert.Equal(2, ranges.GetArrayLength());
        Assert.Equal((18 * 16 + 14) * 256, ranges[1].GetProperty("fileOffset").GetInt32());

        var invalid = Run("disk", "inspect", path, "--track", "18", "--json");
        Assert.Equal(2, invalid.Code);
        using var error = JsonDocument.Parse(invalid.Error);
        Assert.Equal("inspect.address", error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static string Fixture(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "tests", "TestData", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Unable to find the independent test fixture.", name);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
