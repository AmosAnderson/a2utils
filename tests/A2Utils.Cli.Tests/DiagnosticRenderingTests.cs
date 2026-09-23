// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;

namespace A2Utils.Cli.Tests;

public sealed class DiagnosticRenderingTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-diagnostic-cli-" + Guid.NewGuid().ToString("N"));

    public DiagnosticRenderingTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Build_BasicError_ShowsSourceAndBasicLineAndSpecificMessage()
    {
        WriteBasicProject("10 GOTO 20\n");
        var result = Run("build", At("project.json"));
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("project.basic_check", result.Error);
        Assert.Contains(At("main.bas"), result.Error);
        Assert.Contains("source line 1", result.Error);
        Assert.Contains("column 9", result.Error);
        Assert.Contains("BASIC line 10", result.Error);
        Assert.Contains("error basic.missing_target: Target line 20 does not exist.", result.Error);
        Assert.Equal(1, Count(result.Error, "basic.missing_target"));
        Assert.False(File.Exists(At("build.po")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_AdvisoryWarning_ShowsOnceIncludingInQuietMode(bool quiet)
    {
        WriteBasicProject("10 COUNT=1:COLOUR=2\n20 END\n");
        string[] arguments = quiet ? ["build", At("project.json"), "--check", "--quiet"] : ["build", At("project.json"), "--check"];
        var result = Run(arguments);
        Assert.Equal(0, result.Code);
        Assert.Contains(At("main.bas"), result.Error);
        Assert.Contains("source line 1", result.Error);
        Assert.Contains("BASIC line 10", result.Error);
        Assert.Contains("warning basic.variable_collision", result.Error);
        Assert.Equal(1, Count(result.Error, "basic.variable_collision"));
        if (quiet) Assert.Empty(result.Output);
        else Assert.Contains("Checked 1 files", result.Output);
    }

    [Fact]
    public void Build_JsonWarning_RemainsStructuredWithoutStderrText()
    {
        WriteBasicProject("10 COUNT=1:COLOUR=2\n20 END\n");
        var result = Run("build", At("project.json"), "--check", "--json", "--verbose");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("basic.variable_collision", Assert.Single(json.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public void Build_JsonError_RemainsOneStructuredEnvelope()
    {
        WriteBasicProject("10 GOTO 20\n");
        var result = Run("build", At("project.json"), "--json", "--verbose");
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        using JsonDocument json = JsonDocument.Parse(result.Error);
        JsonElement error = json.RootElement.GetProperty("error");
        Assert.Equal("project.basic_check", error.GetProperty("code").GetString());
        JsonElement diagnostic = Assert.Single(error.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("basic.missing_target", diagnostic.GetProperty("code").GetString());
        Assert.Equal(At("main.bas"), diagnostic.GetProperty("file").GetString());
    }

    [Fact]
    public void Assembly_ErrorAlreadyDescribedByException_DoesNotRepeatSpecificMessage()
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nBNE missing\n");
        var result = Run("asm", "compile", At("main.asm"), "--to", At("main.bin"));
        Assert.Equal(2, result.Code);
        Assert.Contains(At("main.asm"), result.Error);
        Assert.Contains("source line 2", result.Error);
        Assert.Equal(1, Count(result.Error, "Undefined symbol"));
    }

    [Fact]
    public void BasicCheck_ExistingPlainRenderer_DoesNotDuplicateDiagnostics()
    {
        File.WriteAllText(At("main.bas"), "10 GOTO 20\n");
        var result = Run("basic", "check", At("main.bas"));
        Assert.Equal(2, result.Code);
        Assert.Equal(1, Count(result.Error, "basic.missing_target"));
    }

    private void WriteBasicProject(string source)
    {
        File.WriteAllText(At("main.bas"), source);
        File.WriteAllText(At("project.json"), """{"schemaVersion":1,"files":[{"source":"main.bas","path":"MAIN","kind":"basic"}]}""");
    }

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;
    private string At(string name) => Path.Combine(_directory, name);
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
