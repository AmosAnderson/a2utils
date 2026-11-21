// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.CommandLine;
using System.Text;
using A2Utils.Core.Execution;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddReplayCommand(RootCommand root)
    {
        Command command = new("replay", "Check expectations against observations saved by a previous run.");
        Argument<string> artifacts = new("ARTIFACTS") { Description = "A run directory containing spec.json and observation evidence." };
        Option<string?> expect = new("--expect") { Description = "Use a versioned replay expectation file. Defaults to the archived assertions." };
        Option<int?> checkpoint = new("--checkpoint") { Description = "Check a captured step by its one-based number. Defaults to the final observation." };
        command.Arguments.Add(artifacts);
        command.Options.Add(expect);
        command.Options.Add(checkpoint);
        command.SetAction(parse =>
        {
            ExecutionReplayResult result = ExecutionReplay.Check(parse.GetValue(artifacts)!, parse.GetValue(expect),
                parse.GetValue(checkpoint), _cancellationToken);
            string label = result.Checkpoint is int step ? $"Checkpoint {step}" : "Final observation";
            StringBuilder summary = new($"{label}: {(result.Passed ? "passed" : "failed")} ({result.Assertions} assertions)");
            foreach (var diagnostic in result.Diagnostics)
            {
                summary.AppendLine().Append($"{diagnostic.Code} [{diagnostic.Symbol}] {diagnostic.Message}");
                if (diagnostic.Expected is not null)
                    summary.Append($" Expected {diagnostic.Expected}, observed {diagnostic.Actual}.");
            }
            Result("execution.replay", result, summary.ToString());
            return result.Passed ? 0 : 1;
        });
        root.Subcommands.Add(command);
    }
}
