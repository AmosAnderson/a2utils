// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;

namespace A2Utils.Cli.Tests;

public sealed class BasicToolsWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, $"a2-basic-tools-{Guid.NewGuid():N}");

    public BasicToolsWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Check_MissingTarget_ReturnsStructuredResultAndExitTwo()
    {
        File.WriteAllText(At("source.bas"), "10 GOTO 999\n");
        var result = Run("basic", "check", At("source.bas"), "--json");
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Error);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("basic.check", json.RootElement.GetProperty("command").GetString());
        Assert.False(json.RootElement.GetProperty("data").GetProperty("valid").GetBoolean());
        var diagnostic = json.RootElement.GetProperty("data").GetProperty("diagnostics")[0];
        Assert.Equal("basic.missing_target", diagnostic.GetProperty("code").GetString());
        Assert.Equal(1, diagnostic.GetProperty("line").GetInt32());
        Assert.False(File.Exists(At("output.bas")));
    }

    [Fact]
    public void Renumber_ValidSource_WritesOutputAndMappingWithoutModifyingInput()
    {
        const string source = "10 PRINT \"10\"\n20 GOTO 10\n";
        File.WriteAllText(At("source.bas"), source);
        var result = Run("basic", "renumber", At("source.bas"), "--to", At("output.bas"), "--start", "100", "--step", "5", "--json");
        Assert.Equal(0, result.Code);
        Assert.Equal("100 PRINT \"10\"\n105 GOTO 100\n", File.ReadAllText(At("output.bas")));
        Assert.Equal(source, File.ReadAllText(At("source.bas")));
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal(105, json.RootElement.GetProperty("data").GetProperty("mapping")[1].GetProperty("newLine").GetInt32());
    }

    [Theory]
    [InlineData("10 GOTO 999\n")]
    [InlineData("10 GOTO X\n")]
    [InlineData("10 PRINT (\n")]
    public void Renumber_FailedCheck_PreservesExistingDestinationAndInput(string source)
    {
        File.WriteAllText(At("source.bas"), source);
        File.WriteAllText(At("output.bas"), "previous output");
        var result = Run("basic", "renumber", At("source.bas"), "--to", At("output.bas"), "--overwrite", "--json");
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.Equal(source, File.ReadAllText(At("source.bas")));
        Assert.Equal("previous output", File.ReadAllText(At("output.bas")));
        Assert.Empty(Directory.GetFiles(_directory, ".*.a2-*"));
    }

    [Fact]
    public void Renumber_SourceAliasAndExistingOutput_RequireSafeDestination()
    {
        File.WriteAllText(At("source.bas"), "10 END\n");
        File.WriteAllText(At("output.bas"), "previous output");
        Assert.Equal(6, Run("basic", "renumber", At("source.bas"), "--to", At("source.bas"), "--overwrite").Code);
        Assert.Equal(6, Run("basic", "renumber", At("source.bas"), "--to", At("output.bas")).Code);
        Assert.Equal("previous output", File.ReadAllText(At("output.bas")));
        Assert.Equal("10 END\n", File.ReadAllText(At("source.bas")));
        Assert.Equal(0, Run("basic", "renumber", At("source.bas"), "--to", At("output.bas"), "--overwrite").Code);
    }

    private string At(string name) => Path.Combine(_directory, name);

    [Fact]
    public void Prepare_Labels_WritesSourceAndJsonMapping()
    {
        File.WriteAllText(At("labels.bas"), "@start: HOME\nGOTO @start\n");
        var result = Run("basic", "prepare", At("labels.bas"), "--to", At("output.bas"), "--start", "100", "--step", "5", "--json");
        Assert.Equal(0, result.Code);
        Assert.Equal("100 HOME\n105 GOTO 100\n", File.ReadAllText(At("output.bas")));
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("basic.prepare", json.RootElement.GetProperty("command").GetString());
        Assert.Equal(100, json.RootElement.GetProperty("data").GetProperty("mapping")[0].GetProperty("basicLine").GetInt32());
        Assert.Equal("@start: HOME\nGOTO @start\n", File.ReadAllText(At("labels.bas")));
    }

    [Fact]
    public void Prepare_InvalidSource_PreservesExistingDestination()
    {
        File.WriteAllText(At("labels.bas"), "GOTO @missing\n");
        File.WriteAllText(At("output.bas"), "old output");
        var result = Run("basic", "prepare", At("labels.bas"), "--to", At("output.bas"), "--overwrite", "--json");
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.Equal("old output", File.ReadAllText(At("output.bas")));
        Assert.Equal("GOTO @missing\n", File.ReadAllText(At("labels.bas")));
        Assert.Empty(Directory.GetFiles(_directory, ".*.a2-*"));
    }

    [Fact]
    public void Prepare_SourceAliasAndExistingOutput_RequireSafeDestination()
    {
        File.WriteAllText(At("labels.bas"), "@start: END\n");
        File.WriteAllText(At("output.bas"), "old output");
        Assert.Equal(6, Run("basic", "prepare", At("labels.bas"), "--to", At("labels.bas"), "--overwrite").Code);
        Assert.Equal(6, Run("basic", "prepare", At("labels.bas"), "--to", At("output.bas")).Code);
        Assert.Equal("old output", File.ReadAllText(At("output.bas")));
        Assert.Equal("@start: END\n", File.ReadAllText(At("labels.bas")));
        Assert.Equal(0, Run("basic", "prepare", At("labels.bas"), "--to", At("output.bas"), "--overwrite").Code);
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
