// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Security.Cryptography;
using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Projects;

public sealed record ProjectExecutionSettings(string Suite, string DiskDevice = "flop1");
public sealed record ProjectWorkflowResult(int SchemaVersion, bool Passed, ProjectBuildResult Build,
    ExecutionSuiteResult Tests, string ArtifactDirectory)
{
    public bool Cancelled { get; init; }
}

/// <summary>Preflights and builds a project, then runs its build-bound execution suite.</summary>
public static class ProjectWorkflow
{
    public static ProjectWorkflowResult Run(string manifestPath, string artifactDirectory,
        string? outputPath = null, bool overwrite = false, CancellationToken cancellationToken = default)
        => Run(manifestPath, artifactDirectory, outputPath, overwrite, new(), cancellationToken);

    public static ProjectWorkflowResult Run(string manifestPath, string artifactDirectory,
        string? outputPath, bool overwrite, ExecutionSuiteRunOptions suiteOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suiteOptions);
        string artifacts = ExecutionSuiteRunner.ResolveArtifactDirectory(artifactDirectory,
            suiteOptions.CreateRunSubdirectory);
        ExecutionSuiteRunner.ValidateNewArtifactDirectory(artifacts);
        ResolvedProjectManifest resolved = ProjectResolver.Load(manifestPath, outputPath,
            cancellationToken);
        ProjectBuildResult preview = ProjectBuilder.BuildResolved(resolved, overwrite,
            cancellationToken: cancellationToken, preflight: true);
        ProjectExecutionSettings settings = preview.Execution
            ?? throw new DiskException("project.execution_required", "Project testing requires execution.suite in the manifest.", 2);
        EnsureOutsideArtifacts(preview.OutputPath);
        foreach (BuildInput input in preview.Inputs) EnsureOutsideArtifacts(input.Path);
        Dictionary<string, CapturedInput> executionInputs = new(PathComparer);
        ExecutionSuiteRunner.ExecutionSuiteSelection selection = ExecutionSuiteRunner.LoadSelection(
            settings.Suite, suiteOptions,
            (path, hash) => CaptureKnown(path, hash, 4 * 1024 * 1024), source =>
            {
                if (!HasAssertions(source))
                    throw new DiskException("execution.invalid_suite", "Every test case needs an assertion, completion condition, or disk verification.", 2);
                _ = BuildExecution.Bind(source, preview, settings.DiskDevice);
            });
        foreach (ExecutionSuiteRunner.ExecutionSuiteCase item in selection.Selected)
        {
            ExecutionSpec spec = BuildExecution.Bind(item.Spec, preview, settings.DiskDevice);
            if (spec.Engine == "mame")
            {
                if (!Directory.Exists(spec.RomDirectory))
                    throw new DiskException("execution.rom_directory_missing", "The specified ROM directory does not exist.", 2);
                EnsureOutsideArtifacts(spec.RomDirectory);
                Capture(spec.EmulatorPath, maximum: null);
            }
            if (spec.Environment is not null) Capture(spec.Environment);
            if (spec.ToolchainLock is not null)
            {
                Capture(spec.ToolchainLock);
                Setup.DevelopmentEnvironment.ValidateLock(spec.Environment!, spec.ToolchainLock, cancellationToken);
                Capture(spec.Environment!);
                Capture(spec.ToolchainLock);
            }
            if (ExecutionVisual.ValidateExpected(spec, cancellationToken) is { } screenshot)
                CaptureKnown(screenshot.Path, screenshot.Sha256, 32 * 1024 * 1024);
            PreparedGraphicsExecution graphics = ExecutionGraphicsMemory.Prepare(spec,
                cancellationToken);
            foreach (PreparedGraphicsAssertion assertion in graphics.Assertions)
                CaptureKnown(assertion.SourcePath, assertion.SourceSha256, 32 * 1024 * 1024);
            if (spec.Routine is not null)
            {
                PreparedRoutine routine = RoutineHarness.Assemble(spec, cancellationToken);
                foreach (var input in routine.InputHashes)
                    if (Capture(input.Key, Assembly.Assembler.MaximumSourceLength) != input.Value)
                        throw new DiskException("project.execution_changed", $"Routine input changed during preflight: {input.Key}", 6);
            }
            foreach (ExecutionDisk disk in spec.GetDisks().Where(disk => disk.Device != settings.DiskDevice))
            {
                string hash = Capture(disk.Image, 64 * 1024 * 1024);
                if (disk.ExpectedSha256 is { } expected && !expected.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new DiskException("execution.disk_hash", $"Execution input hash does not match: {disk.Image}", 6);
            }
        }
        CheckExecutionInputs();
        ProjectBuildResult build = ProjectBuilder.BuildVerified(resolved, overwrite, preview,
            cancellationToken, CheckExecutionInputs);
        if (build.Sha256 != preview.Sha256 || !build.Inputs.SequenceEqual(preview.Inputs))
            throw new DiskException("project.build_changed", "The build changed after preflight; execution was refused. The validated build output is retained.", 6);
        CheckExecutionInputs();

        async Task<ExecutionResult> Execute(ExecutionSuiteRunner.ExecutionSuiteCase item,
            string caseArtifacts, CancellationToken token)
        {
            CheckExecutionInputs();
            ExecutionSpec spec = BuildExecution.Bind(item.Spec, build, settings.DiskDevice);
            // Pin every auxiliary OS image as well as the newly built image.
            spec = spec with
            {
                Disks = spec.GetDisks().Select(disk => disk.Device == settings.DiskDevice ? disk
                    : disk with { ExpectedSha256 = executionInputs[Path.GetFullPath(disk.Image)].Sha256 }).ToArray()
            };
            ExecutionResult run = await ExecutionRunner.RunAsync(spec, caseArtifacts, token);
            if (!token.IsCancellationRequested && run.StopReason != "cancelled") CheckExecutionInputs();
            run = BuildExecution.Annotate(item.Spec, build, run);
            string? sourceTrace = BuildExecution.WriteSourceTrace(build, run);
            string locations = Path.Combine(run.ArtifactDirectory, "source-locations.json");
            WriteJson(locations, BuildExecution.Locate(build, run, item.Spec));
            run = run with
            {
                Artifacts = run.Artifacts.Concat(sourceTrace is null ? [locations] : new[] { sourceTrace, locations }).ToArray()
            };
            WriteJson(Path.Combine(run.ArtifactDirectory, "result.json"), run);
            return run;
        }

        ExecutionSuiteResult tests = ExecutionSuiteRunner.RunSelectionAsync(selection, artifacts, suiteOptions,
            Execute, directory =>
            {
                WriteJson(Path.Combine(directory, "build.json"), build);
                WriteJson(Path.Combine(directory, "execution-inputs.json"), executionInputs
                    .Select(pair => new BuildInput(pair.Key, pair.Value.Sha256)).ToArray());
            }, cancellationToken).GetAwaiter().GetResult();
        bool cancelled = tests.Cancelled;
        ProjectWorkflowResult result = new(1, tests.Passed, build, tests, artifacts)
        {
            Cancelled = cancelled
        };
        WriteJson(Path.Combine(artifacts, "project-result.json"), result);
        return result;

        string Capture(string path, int? maximum = 4 * 1024 * 1024)
        {
            string full = Path.GetFullPath(path);
            EnsureOutsideArtifacts(full);
            ImageTransactions.EnsureDistinctPaths(full, preview.OutputPath);
            executionInputs.TryGetValue(full, out CapturedInput? previous);
            if (previous?.MaximumBytes is { } previousLimit && (maximum is null || previousLimit < maximum))
                maximum = previousLimit;
            string hash = HashExecutionInput(full, maximum, cancellationToken);
            if (previous is not null && previous.Sha256 != hash)
                throw new DiskException("project.execution_changed", $"Execution input changed: {full}", 6);
            executionInputs[full] = new(hash, maximum);
            return hash;
        }

        void CaptureKnown(string path, string hash, int? maximum)
        {
            string full = Path.GetFullPath(path);
            EnsureOutsideArtifacts(full);
            ImageTransactions.EnsureDistinctPaths(full, preview.OutputPath);
            if (executionInputs.TryGetValue(full, out CapturedInput? previous) &&
                previous.Sha256 != hash)
                throw new DiskException("project.execution_changed",
                    $"Execution input changed while loading: {full}", 6);
            executionInputs[full] = new(hash, maximum);
        }

        void CheckExecutionInputs()
        {
            foreach (var input in executionInputs)
                if (HashExecutionInput(input.Key, input.Value.MaximumBytes, cancellationToken) != input.Value.Sha256)
                    throw new DiskException("project.execution_changed", $"Execution input changed: {input.Key}", 6);
        }

        void EnsureOutsideArtifacts(string path)
        {
            string full = Path.GetFullPath(path);
            ImageTransactions.EnsureDistinctPaths(full, artifacts);
            if (full.StartsWith(artifacts + Path.DirectorySeparatorChar, PathComparison))
                throw new DiskException("execution.artifacts_alias", "Project outputs and inputs must be outside the new execution artifact directory.", 6);
        }
    }

    public static bool HasAssertions(ExecutionSpec spec) => spec.Until is not null || spec.SymbolicUntil is not null || spec.Debug is not null || spec.Routine is not null || spec.Cycles is not null || spec.ScreenshotAssertion is not null || spec.GraphicsMemory.Count != 0 ||
        spec.Audio is { NonSilent: not null } or { MinRms: not null } or { MaxRms: not null } or { MinPeak: not null } or { MaxPeak: not null } ||
        spec.CheckBasicRuntime || spec.Steps.Any(step => step?.Condition is not null) || spec.TextNotContains.Count != 0 ||
        spec.Memory.Count != 0 || spec.SymbolicMemory.Count != 0 ||
        spec.Registers.Count != 0 || spec.TextContains.Count != 0 || spec.DiskAssertions.Count != 0 ||
        spec.GetDisks().Any(disk => disk.Verify);

    // Emulator binaries are routinely larger than disk images. Stream their hashes
    // while retaining the configured limits for specifications and auxiliary images.
    internal static string HashExecutionInput(string path, int? maximumBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImageTransactions.ValidatePath(path);
        HostFiles.EnsureRegularFile(path, cancellationToken);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = input.Length;
        if (maximumBytes is { } maximum && length > maximum)
            throw new DiskException("program.too_large", $"Input exceeds {maximum} bytes: {path}", 3);
        string hash = Convert.ToHexStringLower(SHA256.HashDataAsync(input, cancellationToken).GetAwaiter().GetResult());
        if (input.Length != length || input.Position != length)
            throw new DiskException("project.execution_changed", $"Execution input changed while hashing: {path}", 6);
        cancellationToken.ThrowIfCancellationRequested();
        return hash;
    }

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, ProjectJson.Options));
    private sealed record CapturedInput(string Sha256, int? MaximumBytes);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
