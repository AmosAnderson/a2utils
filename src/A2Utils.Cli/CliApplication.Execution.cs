// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.CommandLine;
using A2Utils.Core;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;
using A2Utils.Core.Projects;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddExecutionCommands(RootCommand root)
    {
        Command runCommand = new("run", "Run a versioned Apple II execution specification through its configured engine.");
        Argument<string> runSpec = new("SPEC");
        Option<string> runArtifacts = new("--artifacts")
        {
            Required = true,
            Description = "New output directory for isolated disks, observations and results."
        };
        runCommand.Arguments.Add(runSpec);
        runCommand.Options.Add(runArtifacts);
        runCommand.SetAction(parse =>
        {
            string directory = Path.GetFullPath(parse.GetValue(runArtifacts)!);
            ImageTransactions.ValidatePath(directory);
            ExecutionResult run = ExecutionRunner.Run(ExecutionSpec.Load(parse.GetValue(runSpec)!), directory, _cancellationToken);
            Result("run", run, $"{run.Name}: {(run.Passed ? "passed" : "failed")} ({run.StopReason})\nArtifacts: {run.ArtifactDirectory}");
            return ExecutionExitCode(run);
        });
        root.Subcommands.Add(runCommand);

        Command testCommand = new("test", "List or run a selectable suite of Apple II execution specifications.");
        Argument<string> suite = new("SUITE");
        Option<string?> suiteArtifacts = new("--artifacts")
        {
            Description = "New suite directory, or a parent directory with --run-subdirectory. Required unless --list is used."
        };
        Option<bool> list = new("--list") { Description = "List discovered and selected cases without creating artifacts or running them." };
        Option<string[]> filters = new("--filter")
        {
            Description = "Select by case name/path glob (name:PATTERN, path:PATTERN, or either); may be repeated.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        Option<int?> jobs = new("--jobs") { Description = "Run 1-16 selected cases concurrently; default 1." };
        Option<string?> rerunFailed = new("--rerun-failed")
        {
            Description = "Select failures recorded in a previous suite-result.json file or artifact directory."
        };
        Option<bool> progress = new("--progress") { Description = "Write flushed JSONL progress to events.jsonl in the suite directory." };
        Option<bool> runSubdirectory = new("--run-subdirectory")
        {
            Description = "Create a unique immutable run-* child beneath --artifacts, which may already exist."
        };
        testCommand.Arguments.Add(suite);
        foreach (Option option in new Option[] { suiteArtifacts, list, filters, jobs, rerunFailed, progress, runSubdirectory })
            testCommand.Options.Add(option);
        testCommand.SetAction(parse =>
        {
            ExecutionSuiteRunOptions options = CreateSuiteOptions(parse.GetValue(filters), parse.GetValue(jobs),
                parse.GetValue(rerunFailed), parse.GetValue(progress), parse.GetValue(runSubdirectory));
            if (parse.GetValue(list))
            {
                if (options.WriteProgress || options.CreateRunSubdirectory)
                    throw new DiskException("execution.list_options", "--list cannot write progress or create an artifact run directory.", 2);
                ExecutionSuitePlan plan = ExecutionSuiteRunner.Plan(parse.GetValue(suite)!, options);
                string text = string.Join(Environment.NewLine, plan.Cases.Select(item =>
                    $"{item.Id} {(item.Selected ? "selected" : "excluded"),-8} {item.Name} ({item.Path})"));
                return Result("test.list", plan, text + $"\n{plan.Planned}/{plan.Discovered} cases selected.");
            }
            string? requestedArtifacts = parse.GetValue(suiteArtifacts);
            if (string.IsNullOrWhiteSpace(requestedArtifacts))
                throw new DiskException("execution.artifacts_required", "test requires --artifacts unless --list is used.", 2);
            ExecutionSuiteResult summary = ExecutionSuiteRunner.Run(parse.GetValue(suite)!, requestedArtifacts,
                options, _cancellationToken);
            ExecutionSuiteCounts counts = summary.Counts;
            Result("test", summary, $"{counts.Passed}/{counts.Planned} execution tests passed; " +
                $"{counts.Failed} failed, {counts.Cancelled} cancelled, {counts.NotRun} not run.\nArtifacts: {summary.ArtifactDirectory}");
            return ExecutionSuiteExitCode(summary);
        });
        root.Subcommands.Add(testCommand);
    }

    private static ExecutionSuiteRunOptions CreateSuiteOptions(IReadOnlyList<string>? filters, int? jobs,
        string? rerunFailedPath, bool writeProgress, bool createRunSubdirectory)
        => new()
        {
            Filters = filters ?? [],
            Jobs = jobs ?? 1,
            RerunFailedPath = rerunFailedPath,
            WriteProgress = writeProgress,
            CreateRunSubdirectory = createRunSubdirectory
        };

    private static int ExecutionExitCode(ExecutionResult result)
        => result.Passed ? 0 : result.StopReason == "cancelled" ? 6 : 1;

    private static int ExecutionSuiteExitCode(ExecutionSuiteResult result)
        => result.Cancelled ? 6 : result.Counts.Failed != 0 ? 1 : 0;
}
