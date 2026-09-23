// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Execution;

namespace A2Utils.Cli.Tests;

public sealed class CpuExecutionWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-cli-cpu-" + Guid.NewGuid().ToString("N"));

    public CpuExecutionWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Run_CpuRoutineWithoutExternalTools_ReturnsPassingJson()
    {
        File.WriteAllText(At("routine.asm"), "lda #$2a\nsta $0300\nrts\n");
        File.WriteAllText(At("case.json"), JsonSerializer.Serialize(new ExecutionSpec
        {
            Engine = "cpu",
            Machine = "apple2e",
            Routine = new() { Source = "routine.asm" },
            Memory = [new(0x0300, "2A")],
            Trace = true
        }, ExecutionSpec.JsonOptions));
        using StringWriter output = new();
        using StringWriter error = new();

        int code = CliApplication.Run(["run", At("case.json"), "--artifacts", At("run"), "--json"], output, error);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("passed").GetBoolean());
        Assert.Equal("routine_return", data.GetProperty("stopReason").GetString());
        Assert.Equal("cpu-1", data.GetProperty("emulatorVersion").GetString());
        Assert.True(File.Exists(At("run/trace.tsv")));
    }

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
