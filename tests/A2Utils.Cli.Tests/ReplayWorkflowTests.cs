// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Text.Json;
using A2Utils.Core.Execution;

namespace A2Utils.Cli.Tests;

public sealed class ReplayWorkflowTests
{
    [Fact]
    public void Replay_DefaultAndAlternativeExpectations_ReportVerdictAndEvidence()
    {
        string root = Path.Combine(TestPaths.TemporaryRoot, "a2-cli-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "spec.json"), JsonSerializer.Serialize(new ExecutionSpec()
            {
                Engine = "cpu",
                Registers = [new("A", 42)]
            }, ExecutionSpec.JsonOptions));
            File.WriteAllText(Path.Combine(root, "observations.tsv"), "A2EXEC1\nSTOP\temulated_limit\nTIME\t0\nREG\tA\t42\nEND\n");
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(0, CliApplication.Run(["replay", root, "--json"], output, error));
            using JsonDocument success = JsonDocument.Parse(output.ToString());
            Assert.Equal("execution.replay", success.RootElement.GetProperty("command").GetString());
            Assert.True(success.RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
            string checks = Path.Combine(root, "checks.json");
            File.WriteAllText(checks, """{"schemaVersion":1,"registers":[{"name":"A","value":3}]}""");
            output.GetStringBuilder().Clear();
            Assert.Equal(1, CliApplication.Run(["replay", root, "--expect", checks, "--json"], output, error));
            using JsonDocument failure = JsonDocument.Parse(output.ToString());
            Assert.Equal("replay.mismatch", failure.RootElement.GetProperty("data").GetProperty("diagnostics")[0].GetProperty("code").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
