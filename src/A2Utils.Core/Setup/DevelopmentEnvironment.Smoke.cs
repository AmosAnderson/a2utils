// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text;
using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Setup;

public sealed record EnvironmentSmokeEvidence(string ArtifactDirectory, string ResultPath,
    string ProfilePath, string ProfileSha256, string ProfileArtifact, string Language,
    string? LockPath, string? ProjectPath, string? WorkflowResultPath, bool Passed, bool Cancelled,
    IReadOnlyList<BuildInput> GeneratedInputs);

public static partial class DevelopmentEnvironment
{
    /// <summary>Checks a profile, then builds and executes original mailbox code against its OS template.</summary>
    public static Task<EnvironmentCheckResult> SmokeAsync(string profilePath, string artifactDirectory,
        CancellationToken cancellationToken = default)
        => SmokeCoreAsync(profilePath, artifactDirectory, null, cancellationToken);

    internal static Task<EnvironmentCheckResult> SmokeForTestAsync(string profilePath, string artifactDirectory,
        Action? workflowPrepared, CancellationToken cancellationToken = default)
        => SmokeCoreAsync(profilePath, artifactDirectory, workflowPrepared, cancellationToken);

    private static async Task<EnvironmentCheckResult> SmokeCoreAsync(string profilePath, string artifactDirectory,
        Action? workflowPrepared, CancellationToken cancellationToken)
    {
        string profileFile = Path.GetFullPath(profilePath);
        byte[] profileBytes = ProgramFiles.ReadBytes(profileFile, 4 * 1024 * 1024, cancellationToken);
        DevelopmentEnvironmentProfile profile = DevelopmentEnvironmentProfile.Load(profileFile, profileBytes);
        string artifacts = Path.GetFullPath(artifactDirectory);
        ImageTransactions.ValidatePath(artifacts);
        if (Directory.Exists(artifacts) || File.Exists(artifacts))
            throw new DiskException("setup.smoke_destination", "Environment smoke evidence requires a new directory.", 2);
        foreach (string root in Roots(profile))
            if (IsWithin(artifacts, root))
                throw new DiskException("setup.smoke_location", "Keep smoke evidence outside the ROM and compiler distribution directories.", 2);
        cancellationToken.ThrowIfCancellationRequested();
        string parent = Path.GetDirectoryName(artifacts)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".a2-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try { Directory.Move(staging, artifacts); }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging); }

        string profileArtifact = Path.Combine(artifacts, "environment.json");
        File.WriteAllBytes(profileArtifact, profileBytes);
        string language = profile.Cc65Path is null ? "asm" : "c";
        string resultPath = Path.Combine(artifacts, "smoke-result.json");
        string? lockPath = null;
        string? projectPath = null;
        string? workflowResult = null;
        List<BuildInput> generated = [new(profileArtifact, profile.InputSha256)];
        List<EnvironmentCheck> checks = [];
        bool workflowPassed = false;
        bool cancelled = false;
        try
        {
            EnvironmentCheckResult precheck = await CheckAsync(profile, cancellationToken);
            checks.AddRange(precheck.Checks);
            if (profile.TemplateImage is null)
                checks.Add(new("smoke.template", false,
                    "Smoke execution requires templateImage booting to an Applesoft prompt; ProDOS requires BASIC.SYSTEM."));
            if (profile.Machine == "apple2")
                checks.Add(new("smoke.machine", false, "Smoke projects require apple2p, apple2e, apple2ee, or apple2c."));
            string proposedLock = Path.Combine(artifacts, "environment.lock.json");
            DevelopmentEnvironmentLock locked = CreateLock(profileFile, proposedLock, cancellationToken);
            lockPath = proposedLock;
            generated.Add(new(lockPath, ProgramFiles.Hash(ProgramFiles.ReadBytes(lockPath, 4 * 1024 * 1024, cancellationToken))));
            if (locked.ProfileSha256 != profile.InputSha256)
                throw new DiskException("setup.lock_changed", "The environment profile changed before smoke execution.", 6);
            if (checks.All(check => check.Passed))
            {
                string projectDirectory = Path.Combine(artifacts, "project");
                ProjectStarterResult starter = ProjectStarter.Create(projectDirectory, language, profileFile, cancellationToken);
                projectPath = Path.Combine(projectDirectory, "project.a2.json");
                ProjectManifest manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(projectPath), ProjectJson.Options)!;
                WriteJson(projectPath, manifest with { ToolchainLock = lockPath }, ProjectJson.Options);
                string executionPath = Path.Combine(projectDirectory, "execution.json");
                ExecutionSpec execution = JsonSerializer.Deserialize<ExecutionSpec>(File.ReadAllBytes(executionPath), ExecutionSpec.JsonOptions)!;
                WriteJson(executionPath, execution with
                {
                    Name = "environment smoke mailbox",
                    ToolchainLock = lockPath
                }, ExecutionSpec.JsonOptions);
                generated.AddRange(starter.Files.Select(path =>
                    new BuildInput(path, ProgramFiles.Hash(ProgramFiles.ReadBytes(path, 4 * 1024 * 1024, cancellationToken)))));
                ValidateLock(profileFile, lockPath, cancellationToken);
                workflowPrepared?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                ProjectWorkflowResult workflow = ProjectWorkflow.Run(projectPath, Path.Combine(artifacts, "execution"),
                    cancellationToken: cancellationToken);
                workflowResult = Path.Combine(workflow.ArtifactDirectory, "project-result.json");
                workflowPassed = workflow.Passed && !workflow.Cancelled;
                cancelled = workflow.Cancelled;
                checks.Add(new("smoke.workflow", workflowPassed, workflowPassed
                    ? $"The {language} starter was built, loaded from the template, and observed writing 2A A5 5A to $0300-$0302."
                    : $"The {language} starter did not complete its mailbox assertions. Inspect {workflowResult}."));
            }
        }
        catch (Exception error) when (error is DiskException or IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            cancelled = error is OperationCanceledException;
            checks.Add(new(error is DiskException disk ? disk.Code : cancelled ? "smoke.cancelled" : "smoke.failed",
                false, cancelled ? "Environment smoke execution was cancelled; collected evidence is retained." : error.Message));
        }

        // Retain failure evidence too, but never report success against changed inputs.
        // These final checks also run after cancellation and execution/compilation failures.
        try
        {
            if (HashFile(profileFile, CancellationToken.None) != profile.InputSha256)
                throw new DiskException("setup.lock_changed", "The environment profile changed during smoke execution.", 6);
            if (lockPath is not null && File.Exists(lockPath)) ValidateLock(profileFile, lockPath);
            foreach (BuildInput input in generated)
                if (HashFile(input.Path, CancellationToken.None) != input.Sha256)
                    throw new DiskException("setup.smoke_input_changed", "A generated smoke input changed during execution: " + input.Path, 6);
        }
        catch (Exception error) when (error is DiskException or IOException or UnauthorizedAccessException)
        {
            checks.Add(new(error is DiskException disk ? disk.Code : "smoke.input_failed", false, error.Message));
        }
        bool passed = workflowPassed && checks.All(check => check.Passed);
        EnvironmentCheckResult result = new(1, passed, checks)
        {
            Smoke = new(artifacts, resultPath, profileFile, profile.InputSha256, profileArtifact, language,
                lockPath, projectPath, workflowResult, passed, cancelled, generated)
        };
        ImageTransactions.Create(resultPath, false,
            temporary => WriteJson(temporary, result, JsonOptions),
            temporary => _ = ReadJson<EnvironmentCheckResult>(temporary));
        return result;
    }

    private static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, options) + "\n", new UTF8Encoding(false));
}
