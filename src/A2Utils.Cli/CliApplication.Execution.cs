using System.CommandLine;
using A2Utils.Core;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddExecutionCommands(RootCommand root)
    {
        foreach (string name in new[] { "run", "test" })
        {
            Command command = new(name, name == "run"
                ? "Run a versioned Apple II execution specification through external MAME."
                : "Run a versioned suite of Apple II execution specifications.");
            Argument<string> spec = new(name == "run" ? "SPEC" : "SUITE");
            Option<string> artifacts = new("--artifacts") { Required = true, Description = "New output directory for isolated disks, observations and results." };
            command.Arguments.Add(spec);
            command.Options.Add(artifacts);
            command.SetAction(parse =>
            {
                string directory = Path.GetFullPath(parse.GetValue(artifacts)!);
                ImageTransactions.ValidatePath(directory);
                if (name == "run")
                {
                    ExecutionResult run = ExecutionRunner.Run(ExecutionSpec.Load(parse.GetValue(spec)!), directory, _cancellationToken);
                    Result("run", run, $"{run.Name}: {(run.Passed ? "passed" : "failed")} ({run.StopReason})\nArtifacts: {run.ArtifactDirectory}");
                    return ExecutionExitCode(run);
                }
                ExecutionSuite suite = ExecutionSuite.Load(parse.GetValue(spec)!);
                // Parse every case before executing any case.
                ExecutionSpec[] cases = suite.Tests.Select(ExecutionSpec.Load).ToArray();
                foreach (ExecutionSpec item in cases)
                {
                    MameAdapter.Validate(item);
                    if (item.Until is null && item.Memory.Count == 0 && item.Registers.Count == 0 && item.TextContains.Count == 0)
                        throw new DiskException("execution.invalid_suite", "Every test case needs at least one assertion or completion condition.", 2);
                }
                if (Directory.Exists(directory) || File.Exists(directory))
                    throw new DiskException("execution.artifacts_exist", "Use a new artifact directory for each suite.", 2);
                Directory.CreateDirectory(directory);
                List<ExecutionResult> results = [];
                foreach (ExecutionSpec item in cases)
                {
                    ExecutionResult run = ExecutionRunner.Run(item, Path.Combine(directory, $"case-{results.Count + 1:D3}"), _cancellationToken);
                    results.Add(run);
                    if (run.StopReason == "cancelled") break;
                }
                ExecutionSuiteResult summary = new(1, results.Count == cases.Length && results.All(r => r.Passed), results);
                File.WriteAllText(Path.Combine(directory, "suite-result.json"), System.Text.Json.JsonSerializer.Serialize(summary, ExecutionSpec.JsonOptions));
                Result("test", summary, $"{results.Count(r => r.Passed)}/{cases.Length} execution tests passed.\nArtifacts: {directory}");
                return results.Select(ExecutionExitCode).DefaultIfEmpty(1).Max();
            });
            root.Subcommands.Add(command);
        }
    }

    private static int ExecutionExitCode(ExecutionResult result)
        => result.Passed ? 0 : result.StopReason == "cancelled" ? 6 : 1;
}
