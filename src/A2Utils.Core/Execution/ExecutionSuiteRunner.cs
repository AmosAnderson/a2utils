// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Execution;

public sealed record ExecutionSuiteRunOptions
{
    public IReadOnlyList<string> Filters { get; init; } = [];
    public int Jobs { get; init; } = 1;
    public string? RerunFailedPath { get; init; }
    public bool WriteProgress { get; init; }
    public bool CreateRunSubdirectory { get; init; }
}

public sealed record ExecutionSuiteCaseInfo(
    int Index, string Id, string Path, string Name, bool Selected);

public sealed record ExecutionSuitePlan(
    int SchemaVersion, string SuitePath, int Discovered, int Planned,
    IReadOnlyList<ExecutionSuiteCaseInfo> Cases);

public sealed record ExecutionSuiteCounts(
    int Planned, int Completed, int Passed, int Failed, int Cancelled, int NotRun);

public sealed record ExecutionSuiteCaseResult(
    int Index, string Id, string Path, string Name, bool Selected, string Status,
    string? ArtifactDirectory);

public sealed record ExecutionSuiteEvent(
    int SchemaVersion, long Sequence, string Event, ExecutionSuiteCounts Counts)
{
    public int? Index { get; init; }
    public string? Id { get; init; }
    public string? Path { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? ArtifactDirectory { get; init; }
}

/// <summary>Plans and runs isolated execution-suite cases with stable case identities.</summary>
public static class ExecutionSuiteRunner
{
    private const int MaximumJobs = 16;
    private const int MaximumFilters = 32;
    private static readonly JsonSerializerOptions JsonLinesOptions = new(ExecutionSpec.JsonOptions)
    {
        WriteIndented = false
    };

    public static ExecutionSuitePlan Plan(string suitePath, ExecutionSuiteRunOptions? options = null)
        => LoadSelection(suitePath, options ?? new(), null, ValidateStandalone).Plan;

    public static ExecutionSuiteResult Run(string suitePath, string artifactDirectory,
        ExecutionSuiteRunOptions? options = null, CancellationToken cancellationToken = default)
        => RunAsync(suitePath, artifactDirectory, options, cancellationToken).GetAwaiter().GetResult();

    public static async Task<ExecutionSuiteResult> RunAsync(string suitePath, string artifactDirectory,
        ExecutionSuiteRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ExecutionSuiteRunOptions effective = options ?? new();
        ExecutionSuiteSelection selection = LoadSelection(suitePath, effective, null, ValidateStandalone);
        string artifacts = ResolveArtifactDirectory(artifactDirectory, effective.CreateRunSubdirectory);
        return await RunSelectionAsync(selection, artifacts, effective, static (item, directory, token) =>
            ExecutionRunner.RunAsync(item.Spec, directory, token), null, cancellationToken);
    }

    internal static ExecutionSuiteSelection LoadSelection(string suitePath, ExecutionSuiteRunOptions options,
        Action<string, string>? inputLoaded, Action<ExecutionSpec> validate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        string fullSuite = Path.GetFullPath(suitePath);
        ExecutionSuite suite = ExecutionSuite.Load(fullSuite, inputLoaded);
        string suiteDirectory = Path.GetDirectoryName(fullSuite)!;
        List<ExecutionSuiteCase> cases = [];
        for (int offset = 0; offset < suite.Tests.Count; offset++)
        {
            string path = suite.Tests[offset];
            ExecutionSpec spec = ExecutionSpec.Load(path, inputLoaded);
            validate(spec);
            string relative = Path.GetRelativePath(suiteDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            cases.Add(new(offset + 1, $"case-{offset + 1:D3}", path, relative, spec));
        }

        HashSet<int>? previousFailures = options.RerunFailedPath is null
            ? null : LoadFailedCases(options.RerunFailedPath, cases);
        bool HasFilter(ExecutionSuiteCase item) => options.Filters.Count == 0 ||
            options.Filters.Any(filter => MatchesFilter(item, filter));
        ExecutionSuiteCase[] selected = cases.Where(item => HasFilter(item) &&
            (previousFailures is null || previousFailures.Contains(item.Index))).ToArray();
        if (options.Filters.Count != 0 && selected.Length == 0)
            throw new DiskException("execution.filter_empty", "No suite cases match the requested filter and rerun selection.", 2);

        ExecutionSuiteCaseInfo[] information = cases.Select(item => new ExecutionSuiteCaseInfo(
            item.Index, item.Id, item.RelativePath, item.Spec.Name, selected.Contains(item))).ToArray();
        ExecutionSuitePlan plan = new(1, fullSuite, cases.Count, selected.Length, information);
        return new(plan, cases, selected);
    }

    internal static string ResolveArtifactDirectory(string artifactDirectory, bool createRunSubdirectory)
    {
        string requested = Path.GetFullPath(artifactDirectory);
        ImageTransactions.ValidatePath(requested);
        if (!createRunSubdirectory) return requested;
        if (File.Exists(requested))
            throw new DiskException("execution.artifacts_parent", "The automatic run-directory parent is a file.", 2);
        string name = $"run-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        string resolved = Path.Combine(requested, name);
        ImageTransactions.ValidatePath(resolved);
        return resolved;
    }

    internal static async Task<ExecutionSuiteResult> RunSelectionAsync(ExecutionSuiteSelection selection,
        string artifactDirectory, ExecutionSuiteRunOptions options,
        Func<ExecutionSuiteCase, string, CancellationToken, Task<ExecutionResult>> execute,
        Action<string>? initializeArtifacts, CancellationToken cancellationToken)
    {
        string artifacts = Path.GetFullPath(artifactDirectory);
        ValidateNewArtifactDirectory(artifacts);
        Directory.CreateDirectory(artifacts);
        initializeArtifacts?.Invoke(artifacts);

        ExecutionResult?[] results = new ExecutionResult?[selection.Cases.Count];
        string[] statuses = selection.Cases.Select(item => selection.Selected.Contains(item) ? "not-run" : "excluded").ToArray();
        object stateLock = new();
        int next = -1;
        int stop = 0;
        ExceptionDispatchInfo? failure = null;
        using ProgressWriter? progress = options.WriteProgress
            ? new(Path.Combine(artifacts, "events.jsonl")) : null;

        ExecutionSuiteCounts SnapshotUnsafe()
        {
            int passed = statuses.Count(status => status == "passed");
            int failed = statuses.Count(status => status == "failed");
            int cancelled = statuses.Count(status => status == "cancelled");
            int completed = passed + failed + cancelled;
            return new(selection.Selected.Count, completed, passed, failed, cancelled,
                selection.Selected.Count - completed);
        }

        ExecutionSuiteCounts Snapshot()
        {
            lock (stateLock)
            {
                return SnapshotUnsafe();
            }
        }

        void Emit(string eventName, ExecutionSuiteCase? item = null, string? status = null,
            string? caseArtifacts = null)
        {
            lock (stateLock)
            {
                progress?.Write(new(1, 0, eventName, SnapshotUnsafe())
                {
                    Index = item?.Index,
                    Id = item?.Id,
                    Path = item?.RelativePath,
                    Name = item?.Spec.Name,
                    Status = status,
                    ArtifactDirectory = caseArtifacts
                });
            }
        }

        Emit("suite-started", caseArtifacts: artifacts);
        async Task Worker()
        {
            while (true)
            {
                ExecutionSuiteCase item;
                lock (stateLock)
                {
                    if (stop != 0 || cancellationToken.IsCancellationRequested) return;
                    int selectedIndex = ++next;
                    if (selectedIndex >= selection.Selected.Count) return;
                    item = selection.Selected[selectedIndex];
                }
                string caseArtifacts = Path.Combine(artifacts, item.Id);
                Emit("case-started", item, "running", caseArtifacts);
                ExecutionResult run;
                try
                {
                    run = await execute(item, caseArtifacts, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (stateLock)
                    {
                        stop = 1;
                        statuses[item.Index - 1] = "cancelled";
                        Emit("case-completed", item, "cancelled", caseArtifacts);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    lock (stateLock)
                    {
                        failure ??= ExceptionDispatchInfo.Capture(ex);
                        stop = 1;
                    }
                    return;
                }

                string status = run.Passed ? "passed" : run.StopReason == "cancelled" ? "cancelled" : "failed";
                lock (stateLock)
                {
                    results[item.Index - 1] = run;
                    statuses[item.Index - 1] = status;
                    if (status == "cancelled") stop = 1;
                    Emit("case-completed", item, status, run.ArtifactDirectory);
                }
            }
        }

        try
        {
            Task[] workers = Enumerable.Range(0, Math.Min(options.Jobs, Math.Max(1, selection.Selected.Count)))
                .Select(_ => Worker()).ToArray();
            await Task.WhenAll(workers);
            failure?.Throw();
        }
        catch
        {
            Emit("suite-aborted", status: "failed", caseArtifacts: artifacts);
            throw;
        }

        ExecutionSuiteCounts counts = Snapshot();
        bool cancelledSuite = cancellationToken.IsCancellationRequested || counts.Cancelled != 0;
        bool passedSuite = !cancelledSuite && counts.Completed == counts.Planned && counts.Failed == 0;
        ExecutionSuiteCaseResult[] caseResults = selection.Cases.Select(item => new ExecutionSuiteCaseResult(
            item.Index, item.Id, item.RelativePath, item.Spec.Name, selection.Selected.Contains(item),
            statuses[item.Index - 1], results[item.Index - 1]?.ArtifactDirectory)).ToArray();
        ExecutionSuiteResult summary = new(1, passedSuite, results.OfType<ExecutionResult>().ToArray())
        {
            SuitePath = selection.Plan.SuitePath,
            ArtifactDirectory = artifacts,
            Counts = counts,
            Cases = caseResults,
            Cancelled = cancelledSuite
        };
        File.WriteAllText(Path.Combine(artifacts, "suite-result.json"),
            JsonSerializer.Serialize(summary, ExecutionSpec.JsonOptions));
        Emit("suite-completed", status: passedSuite ? "passed" : cancelledSuite ? "cancelled" : "failed",
            caseArtifacts: artifacts);
        return summary;
    }

    private static void ValidateStandalone(ExecutionSpec spec)
    {
        ExecutionRunner.Validate(spec);
        if (!ProjectWorkflow.HasAssertions(spec))
            throw new DiskException("execution.invalid_suite", "Every test case needs at least one assertion or completion condition.", 2);
    }

    private static void ValidateOptions(ExecutionSuiteRunOptions options)
    {
        if (options.Jobs is < 1 or > MaximumJobs)
            throw new DiskException("execution.jobs", $"Suite jobs must be between 1 and {MaximumJobs}.", 2);
        if (options.Filters is null || options.Filters.Count > MaximumFilters ||
            options.Filters.Any(filter => string.IsNullOrWhiteSpace(filter) || filter.Length > 256))
            throw new DiskException("execution.filter", $"Use at most {MaximumFilters} nonempty filters of at most 256 characters.", 2);
        if (options.RerunFailedPath is not null && string.IsNullOrWhiteSpace(options.RerunFailedPath))
            throw new DiskException("execution.rerun_failed", "The previous suite result path cannot be empty.", 2);
    }

    private static HashSet<int> LoadFailedCases(string path, IReadOnlyList<ExecutionSuiteCase> cases)
    {
        string resultPath = Path.GetFullPath(path);
        if (Directory.Exists(resultPath)) resultPath = Path.Combine(resultPath, "suite-result.json");
        ExecutionSuiteResult previous;
        try
        {
            string json = ProgramFiles.DecodeText(ProgramFiles.ReadBytes(resultPath, 64 * 1024 * 1024));
            previous = JsonSerializer.Deserialize<ExecutionSuiteResult>(json, ExecutionSpec.JsonOptions)
                ?? throw new JsonException("Previous suite result must be an object.");
        }
        catch (Exception ex) when (ex is JsonException or DiskException)
        {
            throw new DiskException("execution.rerun_failed", "Cannot read the previous suite result: " + ex.Message, 2);
        }

        HashSet<int> selected = [];
        if (previous.Cases is { Count: > 0 })
        {
            foreach (ExecutionSuiteCaseResult failed in previous.Cases.Where(item => item.Status == "failed"))
            {
                ExecutionSuiteCase? match = failed.Index > 0 && failed.Index <= cases.Count
                    ? cases[failed.Index - 1] : null;
                if (match is null || !match.Id.Equals(failed.Id, StringComparison.Ordinal) ||
                    !match.RelativePath.Equals(failed.Path, HostPathComparison) ||
                    !match.Spec.Name.Equals(failed.Name, StringComparison.Ordinal))
                    throw new DiskException("execution.rerun_failed", $"Previous failed case is missing or ambiguous: {failed.Path} ({failed.Name}).", 2);
                selected.Add(match.Index);
            }
            return selected;
        }

        if (previous.Tests is null)
            throw new DiskException("execution.rerun_failed", "Previous suite result has no case results.", 2);
        foreach (ExecutionResult failed in previous.Tests.Where(item => !item.Passed && item.StopReason != "cancelled"))
        {
            ExecutionSuiteCase[] matches = cases.Where(item => item.Spec.Name.Equals(failed.Name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new DiskException("execution.rerun_failed", $"Legacy failed case name is missing or ambiguous: {failed.Name}.", 2);
            selected.Add(matches[0].Index);
        }
        return selected;
    }

    private static bool MatchesFilter(ExecutionSuiteCase item, string filter)
    {
        string pattern = filter;
        bool nameOnly = pattern.StartsWith("name:", StringComparison.OrdinalIgnoreCase);
        bool pathOnly = pattern.StartsWith("path:", StringComparison.OrdinalIgnoreCase);
        if (nameOnly || pathOnly) pattern = pattern[5..];
        if (pattern.Length == 0)
            throw new DiskException("execution.filter", "A name: or path: filter needs a pattern.", 2);
        if (!pattern.Contains('*') && !pattern.Contains('?')) pattern = "*" + pattern + "*";
        return !pathOnly && WildcardMatch(item.Spec.Name, pattern) ||
            !nameOnly && WildcardMatch(item.RelativePath, pattern);
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        int valueIndex = 0, patternIndex = 0, star = -1, retry = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' ||
                char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                star = patternIndex++;
                retry = valueIndex;
            }
            else if (star >= 0)
            {
                patternIndex = star + 1;
                valueIndex = ++retry;
            }
            else return false;
        }
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }

    internal static void ValidateNewArtifactDirectory(string artifacts)
    {
        ImageTransactions.ValidatePath(artifacts);
        if (Directory.Exists(artifacts) || File.Exists(artifacts))
            throw new DiskException("execution.artifacts_exist", "Use a new artifact directory for each suite.", 2);
        for (string? parent = Path.GetDirectoryName(artifacts); parent is not null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent))
                throw new DiskException("execution.artifacts_parent", "The artifact directory has a parent that is a file.", 2);
    }

    internal sealed record ExecutionSuiteCase(
        int Index, string Id, string Path, string RelativePath, ExecutionSpec Spec);

    internal sealed record ExecutionSuiteSelection(
        ExecutionSuitePlan Plan, IReadOnlyList<ExecutionSuiteCase> Cases,
        IReadOnlyList<ExecutionSuiteCase> Selected);

    private static StringComparison HostPathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class ProgressWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _lock = new();
        private long _sequence;

        public ProgressWriter(string path)
            => _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));

        public void Write(ExecutionSuiteEvent value)
        {
            lock (_lock)
            {
                ExecutionSuiteEvent sequenced = value with { Sequence = ++_sequence };
                _writer.WriteLine(JsonSerializer.Serialize(sequenced, JsonLinesOptions));
                _writer.Flush();
            }
        }

        public void Dispose() => _writer.Dispose();
    }
}
