// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Execution;

namespace A2Utils.Cli.Tests;

public sealed class DebugWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-cli-debug-" + Guid.NewGuid().ToString("N"));

    public DebugWorkflowTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("2A", 0)]
    [InlineData("FF", 1)]
    public void Run_DebugBankContract_ReturnsStructuredEvidenceAndBehavioralExit(string expected, int expectedExit)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string host = Path.Combine(root.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        string disk = At("input.dsk");
        File.WriteAllText(disk, "debug-iie");
        ExecutionSpec spec = new()
        {
            EmulatorPath = host,
            RomDirectory = _directory,
            Machine = "apple2ee",
            DiskImage = disk,
            Debug = new() { Breakpoints = [new(8192)] },
            TextColumns = 80,
            Memory = [new(768, expected)],
            TextContains = ["A2 {MT:00}"]
        };
        File.WriteAllText(At("case.json"), JsonSerializer.Serialize(spec, ExecutionSpec.JsonOptions));
        StringWriter output = new();
        StringWriter error = new();
        int exit = CliApplication.Run(["run", At("case.json"), "--artifacts", At("result"), "--json"], output, error);
        Assert.Equal(expectedExit, exit);
        Assert.Equal("", error.ToString());
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal("breakpoint", data.GetProperty("debug").GetProperty("trigger").GetProperty("kind").GetString());
        Assert.Equal(80, data.GetProperty("textScreen").GetProperty("columns").GetInt32());
        Assert.Equal("debug-iie", File.ReadAllText(disk));
    }

    [Fact]
    public void Capabilities_DebugAndBankSupport_IsDiscoverable()
    {
        StringWriter output = new();
        Assert.Equal(0, CliApplication.Run(["capabilities", "--json"], output));
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        JsonElement workflow = json.RootElement.GetProperty("data").GetProperty("developmentWorkflow");
        Assert.True(workflow.GetProperty("breakpoints").GetBoolean());
        Assert.True(workflow.GetProperty("mouseTextCells").GetBoolean());
        Assert.Contains(workflow.GetProperty("memoryBanks").EnumerateArray(), bank => bank.GetString() == "aux-lc1");
    }

    private string At(string path) => Path.Combine(_directory, path);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
