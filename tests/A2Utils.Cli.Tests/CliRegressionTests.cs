// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Cli;
using A2Utils.Core.Backends;

namespace A2Utils.Cli.Tests;

public sealed class CliRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-cli-regressions-" + Guid.NewGuid().ToString("N"));

    public CliRegressionTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("B")]
    [InlineData("b")]
    [InlineData("BIN")]
    [InlineData("bin")]
    [InlineData("0x06")]
    public void Add_DosBinaryAliasWithoutLoadAddress_RefusesAndPreservesSourceAndOutput(string type)
    {
        string source = At("source.do");
        string host = At("payload.bin");
        string output = At("existing.do");
        DiskSession.Create(source, "dos33");
        byte[] original = File.ReadAllBytes(source);
        byte[] previousOutput = [0xA2, 0x55, 0xF0];
        File.WriteAllBytes(host, [1, 2, 3]);
        File.WriteAllBytes(output, previousOutput);

        var result = Run("disk", "add", source, host, "--name", "PROGRAM", "--type", type,
            "--output", output, "--overwrite", "--json");

        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        using JsonDocument error = JsonDocument.Parse(result.Error);
        Assert.Equal("invalid_arguments", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("--load-address", error.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(previousOutput, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(_directory, ".*.a2-*"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Attr_ProdosDirectoryWithChildren_ReturnsDirectoryMetadata(int children)
    {
        string source = At("source.po");
        DiskSession.Create(source, "prodos", order: "prodos");
        using (DiskSession session = DiskSession.Open(source, writable: true))
        {
            session.Mkdir("DOCS");
            for (int index = 0; index < children; index++)
            {
                session.Add($"DOCS/CHILD{index}", [1, 2, 3], "BIN");
            }
        }
        byte[] original = File.ReadAllBytes(source);

        var result = Run("disk", "attr", source, "DOCS", "--json");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement entry = document.RootElement.GetProperty("data");
        Assert.Equal("attr", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("DOCS", entry.GetProperty("path").GetString());
        Assert.Equal("DOCS", entry.GetProperty("name").GetString());
        Assert.True(entry.GetProperty("isDirectory").GetBoolean());
        Assert.Equal("DIR", entry.GetProperty("type").GetString());
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    private static (int Code, string Output, string Error) Run(params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(arguments, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string At(string name) => Path.Combine(_directory, name);

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
