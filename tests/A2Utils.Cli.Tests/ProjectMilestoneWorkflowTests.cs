// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Projects;

namespace A2Utils.Cli.Tests;

public sealed class ProjectMilestoneWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-cli-project-milestone-" + Guid.NewGuid().ToString("N"));

    public ProjectMilestoneWorkflowTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(At("main.asm"), ".org $2000\nrts\n");
        File.WriteAllText(At("project.json"), JsonSerializer.Serialize(new ProjectManifest
        {
            Output = "out/build.po",
            Files = [new() { Source = "main.asm", Kind = "asm", Path = "MAIN" }]
        }, ProjectJson.Options));
    }

    [Fact]
    public void Build_Preflight_ReportsPlanWithoutCreatingOutputDirectory()
    {
        var result = Run("build", At("project.json"), "--preflight", "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("preflight").GetBoolean());
        Assert.Equal(64, data.GetProperty("sha256").GetString()!.Length);
        Assert.Equal("add", data.GetProperty("plan").GetProperty("files")[0].GetProperty("action").GetString());
        Assert.True(data.GetProperty("plan").GetProperty("freeBytesBefore").GetInt64() > data.GetProperty("plan").GetProperty("freeBytesAfter").GetInt64());
        Assert.False(Directory.Exists(At("out")));
    }

    [Theory]
    [InlineData("--check", "--preflight")]
    [InlineData("--check", "--test")]
    [InlineData("--preflight", "--test")]
    public void Build_ConflictingModes_RefusesBeforeWriting(string first, string second)
    {
        var result = Run("build", At("project.json"), first, second, "--json");
        Assert.Equal(2, result.Code);
        Assert.Contains("project.mode", result.Error);
        Assert.False(Directory.Exists(At("out")));
    }

    [Fact]
    public void Build_TestWithoutArtifacts_ReportsRequiredOption()
    {
        var result = Run("build", At("project.json"), "--test", "--json");
        Assert.Equal(2, result.Code);
        Assert.Contains("project.artifacts", result.Error);
        Assert.False(Directory.Exists(At("out")));
    }

    [Fact]
    public void Build_TestWithoutExecutionSettings_RefusesAndCreatesNoOutput()
    {
        var result = Run("build", At("project.json"), "--test", "--artifacts", At("evidence"), "--json");
        Assert.Equal(2, result.Code);
        Assert.Contains("project.execution_required", result.Error);
        Assert.False(Directory.Exists(At("out")));
        Assert.False(Directory.Exists(At("evidence")));
    }

    [Fact]
    public void Build_ArtifactsWithoutTest_RefusesUnusedOption()
    {
        var result = Run("build", At("project.json"), "--artifacts", At("evidence"), "--json");
        Assert.Equal(2, result.Code);
        Assert.Contains("project.artifacts", result.Error);
    }

    [Theory]
    [InlineData("APPLE II", 0)]
    [InlineData("MISSING", 1)]
    public void Build_Test_EmitsCombinedResultAndMappedExitStatus(string expectedText, int expectedCode)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string host = Path.Combine(root!.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.WriteAllText(At("os.dsk"), "pass");
        File.WriteAllText(At("suite.json"), JsonSerializer.Serialize(new ExecutionSuite { Tests = ["case.json"] }, ProjectJson.Options));
        File.WriteAllText(At("case.json"), JsonSerializer.Serialize(new ExecutionSpec
        {
            Machine = "apple2e",
            EmulatorPath = host,
            RomDirectory = _directory,
            Disks = [new("flop1", "os.dsk")],
            TextContains = [expectedText],
            EmulatedSeconds = 3
        }, ProjectJson.Options));
        File.WriteAllText(At("project.json"), JsonSerializer.Serialize(new ProjectManifest
        {
            Output = "out/build.po",
            Execution = new("suite.json", "flop2"),
            Files = [new() { Source = "main.asm", Kind = "asm", Path = "MAIN" }]
        }, ProjectJson.Options));
        var result = Run("build", At("project.json"), "--test", "--artifacts", At("evidence"), "--json");
        Assert.Equal(expectedCode, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("build.test", json.RootElement.GetProperty("command").GetString());
        Assert.Equal(expectedCode == 0, json.RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
        Assert.True(File.Exists(At("out/build.po")));
        Assert.True(File.Exists(At("evidence/project-result.json")));
        Assert.Equal("pass", File.ReadAllText(At("os.dsk")));
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        StringWriter output = new(), error = new();
        int code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, true);
}
