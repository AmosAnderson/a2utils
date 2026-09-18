using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

/// <summary>Runs external MAME against a disposable disk copy with independent host and emulated limits.</summary>
public static partial class ExecutionRunner
{
    public static ExecutionResult Run(ExecutionSpec spec, string artifactDirectory, CancellationToken cancellationToken = default)
        => RunAsync(spec, artifactDirectory, cancellationToken).GetAwaiter().GetResult();

    public static async Task<ExecutionResult> RunAsync(ExecutionSpec spec, string artifactDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        PreparedGraphicsExecution graphics = ExecutionGraphicsMemory.Prepare(spec, cancellationToken);
        spec = graphics.Spec;
        ValidatePrepared(spec);
        ValidateExecutionEnvironment(spec, null, cancellationToken);
        if (spec.Engine == "cpu")
        {
            ExecutionResult cpuResult = await CpuExecutionEngine.RunAsync(spec, artifactDirectory, cancellationToken);
            cpuResult = ExecutionGraphicsMemory.Attach(cpuResult, graphics, cancellationToken);
            string cpuResultPath = Path.Combine(cpuResult.ArtifactDirectory, "result.json");
            cpuResult = cpuResult with { Artifacts = cpuResult.Artifacts.Append(cpuResultPath).Distinct().Order(StringComparer.Ordinal).ToArray() };
            await File.WriteAllTextAsync(cpuResultPath, JsonSerializer.Serialize(cpuResult, ExecutionSpec.JsonOptions), CancellationToken.None);
            return cpuResult;
        }
        cancellationToken.ThrowIfCancellationRequested();
        string artifacts = Path.GetFullPath(artifactDirectory);
        ImageTransactions.ValidatePath(artifacts);
        if (Directory.Exists(artifacts) || File.Exists(artifacts))
            throw new DiskException("execution.artifacts_exist", "Use a new artifact directory for each execution.", 2);
        IReadOnlyList<ExecutionDisk> mounts = spec.GetDisks();
        foreach (ExecutionDisk mount in mounts)
        {
            if (!File.Exists(mount.Image))
                throw new DiskException("execution.disk_missing", $"The disk image for {mount.Device} does not exist: {mount.Image}", 2);
            HostFiles.EnsureRegularFile(mount.Image, cancellationToken);
        }
        if (!Directory.Exists(spec.RomDirectory)) throw new DiskException("execution.rom_directory_missing", "The specified ROM directory does not exist.", 2);
        Directory.CreateDirectory(artifacts);
        string? version = null;
        string? hash = null;
        ExecutionObservation? observation = null;
        List<ProgramDiagnostic> diagnostics = [];
        List<ExecutionDiskResult> diskResults = [];
        List<ExecutionCheckpoint> checkpoints = [];
        string reason = "emulator_error";
        PreparedRoutine? routine = null;
        ExecutionAudioResult? audio = null;
        ExecutionEnvironmentEvidence? environment = null;
        ExecutionVisualInput? visualInput = null;
        ExecutionScreenshotResult? screenshotComparison = null;
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(spec.HostTimeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdog.Token);
        try
        {
            environment = CaptureEnvironment(spec, artifacts, linked.Token);
            Dictionary<string, string> copiedDisks = new(StringComparer.Ordinal);
            foreach (ExecutionDisk mount in mounts)
            {
                byte[] snapshot = ProgramFiles.ReadBytes(mount.Image, 64 * 1024 * 1024, linked.Token);
                string disk = Path.Combine(artifacts, (spec.Disks.Count == 0 ? "disk" : "disk-" + mount.Device) + MameAdapter.StorageCopyExtension(mount, snapshot));
                string inputHash = ProgramFiles.Hash(snapshot);
                hash ??= inputHash;
                diskResults.Add(new(mount.Device, Path.GetFullPath(mount.Image), disk, inputHash, null));
                if (mount.ExpectedSha256 is { } expected && !inputHash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new DiskException("execution.disk_hash_mismatch", $"Input disk hash for {mount.Device} does not match expectedSha256.")
                    {
                        Diagnostics = [new("execution.disk_hash_mismatch", "error", $"Input disk changed for {mount.Device}.",
                            Symbol: mount.Device, Expected: expected, Actual: inputHash)]
                    };
                await using FileStream copy = new(disk, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                await copy.WriteAsync(snapshot, linked.Token);
                copy.Position = 0;
                string copyHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(copy, linked.Token));
                if (copyHash != inputHash)
                    throw new DiskException("execution.disk_copy_mismatch", $"The isolated copy does not match the input snapshot for {mount.Device}.");
                await copy.DisposeAsync();
                MameAdapter.ValidateStorageCopy(mount, disk, snapshot);
                copiedDisks.Add(mount.Device, disk);
            }
            visualInput = ExecutionVisual.Prepare(spec, artifacts, linked.Token);
            if (spec.Routine is not null) routine = RoutineHarness.Prepare(spec, artifacts, linked.Token);
            string script = Path.Combine(artifacts, "run.lua");
            await File.WriteAllTextAsync(script, MameAdapter.CreateScript(spec, artifacts, routine), new UTF8Encoding(false), linked.Token);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "spec.json"),
                JsonSerializer.Serialize(spec, ExecutionSpec.JsonOptions), linked.Token);
            IReadOnlyList<string> arguments = MameAdapter.CreateArguments(spec, copiedDisks, script, artifacts);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "command.json"),
                JsonSerializer.Serialize(new { executable = Path.GetFullPath(spec.EmulatorPath), arguments }, ExecutionSpec.JsonOptions), linked.Token);

            ProcessCapture probe = await RunProcessAsync(spec.EmulatorPath, ["-version"], artifacts, "version", linked.Token);
            ValidateExecutionEnvironment(spec, environment, linked.Token);
            version = VersionNumber().Match(probe.StandardOutput).Value;
            if (probe.ExitCode != 0 || version != spec.ExpectedVersion)
            {
                reason = "version_mismatch";
                diagnostics.Add(new("execution.version_mismatch", "error", "The emulator version probe did not match the pinned MAME API.",
                    Expected: spec.ExpectedVersion, Actual: string.IsNullOrEmpty(version) ? probe.StandardOutput.Trim() : version));
            }
            else
            {
                if (routine is not null) RoutineHarness.ValidateInputs(routine, linked.Token);
                ProcessCapture run = await RunProcessAsync(spec.EmulatorPath, arguments, artifacts, "emulator", linked.Token);
                ValidateExecutionEnvironment(spec, environment, linked.Token);
                string errorFile = Path.Combine(artifacts, "adapter-error.txt");
                string observations = Path.Combine(artifacts, "observations.tsv");
                if (run.StandardError.Contains("WRONG CHECKSUMS", StringComparison.OrdinalIgnoreCase)
                    || run.StandardError.Contains("NO GOOD DUMP KNOWN", StringComparison.OrdinalIgnoreCase)
                    || run.StandardError.Contains("NEEDS REDUMP", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "rom_mismatch";
                    diagnostics.Add(new("execution.rom_mismatch", "error", "MAME reported an unverified or mismatched ROM. See emulator.stderr.txt."));
                }
                else if (run.ExitCode != 0)
                {
                    reason = "emulator_error";
                    diagnostics.Add(new("execution.emulator_error", "error",
                        $"MAME exited with code {run.ExitCode}. See emulator.stderr.txt for ROM or machine errors."));
                }
                else if (File.Exists(errorFile))
                {
                    reason = "adapter_error";
                    diagnostics.Add(new("execution.adapter_error", "error", ReadBounded(errorFile, 65536)));
                }
                else if (!File.Exists(observations))
                {
                    reason = "missing_observations";
                    diagnostics.Add(new("execution.missing_observations", "error", "MAME exited without completing the observation script."));
                }
                else
                {
                    observation = ParseObservation(ReadBounded(observations, 1024 * 1024),
                        ReadBounded(Path.Combine(artifacts, "screen.txt"), 32768));
                    if (spec.DecodeIIeText || spec.TextColumns == 80)
                    {
                        observation = DecodeText(spec, observation);
                        await File.WriteAllTextAsync(Path.Combine(artifacts, "screen.txt"), observation.ScreenText, linked.Token);
                        await File.WriteAllTextAsync(Path.Combine(artifacts, "text-screen.json"),
                            JsonSerializer.Serialize(observation.TextScreen, ExecutionSpec.JsonOptions), linked.Token);
                    }
                    reason = observation.StopReason;
                    diagnostics.AddRange(Evaluate(spec, observation));
                    ReadCheckpoints(spec, observation, artifacts, checkpoints);
                    diagnostics.AddRange(EvaluateDisks(spec, copiedDisks, linked.Token));
                    audio = EvaluateAudio(spec, artifacts, diagnostics, linked.Token);
                    screenshotComparison = EvaluateScreenshot(visualInput, artifacts, diagnostics, linked.Token);
                    if (routine is not null) RoutineHarness.ValidateInputs(routine, linked.Token);
                    if (spec.Screenshot && !File.Exists(Path.Combine(artifacts, "screen.png")))
                        diagnostics.Add(new("execution.screenshot_missing", "error", "MAME did not create the requested screen.png artifact."));
                }
            }
            ValidateExecutionEnvironment(spec, environment, linked.Token);
        }
        catch (OperationCanceledException)
        {
            reason = cancellationToken.IsCancellationRequested ? "cancelled" : "host_timeout";
            diagnostics.Add(new("execution." + reason, "error", reason == "cancelled"
                ? "Execution was cancelled; the emulator process tree was terminated."
                : "Execution exceeded hostTimeoutSeconds; the emulator process tree was terminated."));
        }
        catch (Win32Exception ex)
        {
            reason = "emulator_unavailable";
            diagnostics.Add(new("execution.emulator_unavailable", "error", "Could not start the specified MAME executable: " + ex.Message));
        }
        catch (DiskException ex)
        {
            reason = ex.Code is "execution.disk_hash_mismatch" or "execution.disk_copy_mismatch" ? ex.Code[10..] : "adapter_error";
            diagnostics.AddRange(ex.Diagnostics.Count > 0 ? ex.Diagnostics : [new(ex.Code, "error", ex.Message)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            reason = "adapter_error";
            diagnostics.Add(new("execution.adapter_error", "error", ex.Message));
        }
        for (int index = 0; index < diskResults.Count; index++)
        {
            ExecutionDiskResult disk = diskResults[index];
            if (!File.Exists(disk.ArtifactPath)) continue;
            try
            {
                byte[] bytes = ProgramFiles.ReadBytes(disk.ArtifactPath, 64 * 1024 * 1024);
                diskResults[index] = disk with { OutputSha256 = ProgramFiles.Hash(bytes) };
            }
            catch (Exception ex) when (ex is DiskException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new("execution.disk_artifact", "error", $"Cannot hash the resulting {disk.Device} disk: {ex.Message}"));
            }
        }
        bool passed = observation is not null && diagnostics.All(d => d.Severity != "error");
        ExecutionResult result = new(1, spec.Name, passed, reason, version, observation?.EmulatedSeconds,
            observation?.Registers ?? new Dictionary<string, long>(), observation?.Memory ?? new Dictionary<int, string>(),
            observation?.ScreenText, hash, artifacts,
            Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray(), diagnostics)
        {
            Disks = diskResults,
            BankMemory = observation?.BankMemory ?? [],
            Steps = observation?.Steps ?? [],
            Checkpoints = checkpoints,
            Cycles = observation?.Cycles,
            Audio = audio,
            Environment = environment,
            ScreenshotComparison = screenshotComparison,
            Debug = observation?.Debug,
            TextScreen = observation?.TextScreen,
            Video = observation?.Video ?? new Dictionary<string, int>()
        };
        result = ExecutionGraphicsMemory.Attach(result, graphics, cancellationToken);
        string resultPath = Path.Combine(artifacts, "result.json");
        result = result with { Artifacts = result.Artifacts.Append(resultPath).ToArray() };
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, ExecutionSpec.JsonOptions));
        return result;
    }

    public static IReadOnlyList<ProgramDiagnostic> Evaluate(ExecutionSpec spec, ExecutionObservation observation)
    {
        Validate(spec);
        List<ProgramDiagnostic> diagnostics = [];
        EvaluateInstrumentation(spec, observation, diagnostics);
        if (spec.CheckBasicRuntime) diagnostics.AddRange(Basic.ApplesoftTools.RuntimeDiagnostics(observation.ScreenText));
        if (spec.Steps.Count != 0)
        {
            foreach (ExecutionStepResult step in observation.Steps)
            {
                if (step.Index >= spec.Steps.Count || step.Status != "pass")
                    diagnostics.Add(new("execution.step_" + (step.Status == "timeout" ? "timeout" : "failed"), "error",
                        $"Interaction step {step.Index + 1} did not pass.", Symbol: step.Index < spec.Steps.Count ? spec.Steps[step.Index].Name : null));
            }
            if (observation.Steps.Count != spec.Steps.Count)
                diagnostics.Add(new("execution.sequence_incomplete", "error", "The ordered interaction sequence did not complete.",
                    Expected: spec.Steps.Count.ToString(CultureInfo.InvariantCulture), Actual: observation.Steps.Count.ToString(CultureInfo.InvariantCulture)));
        }
        else if (observation.Steps.Count != 0)
            diagnostics.Add(new("execution.sequence_unexpected", "error", "The emulator returned steps for a run without a sequence."));
        if (spec.Until is not null && observation.StopReason != "completion_condition")
            diagnostics.Add(new("execution.completion_timeout", "error", "The completion byte was not observed before the emulated deadline."));
        if (spec.Debug is { } debug && (observation.Debug is null || observation.Debug.SteppedInstructions != debug.StepInstructions))
            diagnostics.Add(new("execution.debug_timeout", "error", "The debug trigger and requested instruction steps did not complete before the deadline."));
        if (observation.Debug is { } evidence)
        {
            ExecutionDebugStop trigger = evidence.Trigger;
            bool validTrigger = spec.Debug is { } requested && trigger.Index >= 0 &&
                (trigger.Kind == "breakpoint" && trigger.Index < requested.Breakpoints.Count &&
                    requested.Breakpoints[trigger.Index].Address == trigger.Address && trigger.Value is null && trigger.ProgramCounter == trigger.Address
                    && observation.EmulatedSeconds >= requested.Breakpoints[trigger.Index].AfterSeconds
                    || trigger.Kind == "watchpoint" && trigger.Index < requested.Watchpoints.Count &&
                    trigger.Address >= requested.Watchpoints[trigger.Index].Address &&
                    trigger.Address < (long)requested.Watchpoints[trigger.Index].Address! + requested.Watchpoints[trigger.Index].Length
                    && trigger.Value is >= 0 and <= 255 && observation.EmulatedSeconds >= requested.Watchpoints[trigger.Index].AfterSeconds)
                && observation.StopReason == (requested.StepInstructions > 0 ? "debug_steps" : trigger.Kind)
                && observation.Registers.TryGetValue("PC", out long pc) && pc is >= 0 and <= 65535;
            if (!validTrigger || spec.Debug is not null && evidence.History.Count > spec.Debug.HistoryInstructions)
                diagnostics.Add(new("execution.debug_evidence", "error", "The emulator debug evidence does not match a requested trigger or history limit."));
        }
        foreach (MemoryAssertion assertion in spec.Memory)
        {
            string expected = Convert.ToHexString(MameAdapter.ParseHex(assertion.Hex));
            string? actual = assertion.Bank == "cpu" ? observation.Memory.GetValueOrDefault(assertion.Address)
                : observation.BankMemory.FirstOrDefault(m => m.Bank == assertion.Bank && m.Address == assertion.Address)?.Hex;
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new("execution.memory_assertion", "error", $"Memory assertion failed in {assertion.Bank} at ${assertion.Address:X4}.",
                    Symbol: (assertion.Bank == "cpu" ? "" : assertion.Bank + ":") + "$" + assertion.Address.ToString("X4"), Expected: expected, Actual: actual ?? "missing"));
        }
        foreach (MemoryCapture range in spec.ObserveMemory)
        {
            string? actual = range.Bank == "cpu" ? observation.Memory.GetValueOrDefault(range.Address)
                : observation.BankMemory.FirstOrDefault(m => m.Bank == range.Bank && m.Address == range.Address)?.Hex;
            if (actual?.Length != range.Length * 2)
                diagnostics.Add(new("execution.memory_missing", "error", $"Requested {range.Bank} memory capture at ${range.Address:X4} is missing or incomplete."));
        }
        foreach (RegisterAssertion assertion in spec.Registers)
        {
            bool present = observation.Registers.TryGetValue(assertion.Name, out long actual);
            if (!present || assertion.Value != actual)
                diagnostics.Add(new("execution.register_assertion", "error", $"Register assertion failed for {assertion.Name}.",
                    Symbol: assertion.Name, Expected: assertion.Value.ToString(CultureInfo.InvariantCulture),
                    Actual: present ? actual.ToString(CultureInfo.InvariantCulture) : "missing"));
        }
        foreach (string text in spec.TextContains)
        {
            if (!observation.ScreenText.Contains(text, StringComparison.Ordinal))
                diagnostics.Add(new("execution.text_assertion", "error", "Expected text was absent from the selected text page.",
                    Expected: text, Actual: observation.ScreenText));
        }
        foreach (string text in spec.TextNotContains)
            if (observation.ScreenText.Contains(text, StringComparison.Ordinal))
                diagnostics.Add(new("execution.text_absent_assertion", "error", "Unexpected text was present on the selected page.", Expected: "absent: " + text, Actual: observation.ScreenText));
        return diagnostics;
    }

    /// <summary>Checks saved logical files and optional filesystem structure after the emulator has released its disk copies.</summary>
    public static IReadOnlyList<ProgramDiagnostic> EvaluateDisks(ExecutionSpec spec,
        IReadOnlyDictionary<string, string> copiedDisks, CancellationToken cancellationToken = default)
    {
        Validate(spec);
        List<ProgramDiagnostic> diagnostics = [];
        foreach (ExecutionDisk mount in spec.GetDisks())
        {
            DiskFileAssertion[] assertions = spec.DiskAssertions.Where(assertion => assertion.Device == mount.Device).ToArray();
            if (!mount.Verify && assertions.Length == 0) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!copiedDisks.TryGetValue(mount.Device, out string? disk))
                    throw new DiskException("execution.disk_missing", "The resulting disk copy is missing.");
                ImageTransactions.ValidatePath(disk);
                HostFiles.EnsureRegularFile(disk, cancellationToken);
                using DiskSession session = DiskSession.Open(disk, mount.InputOrder, mount.InputFileSystem);
                if (mount.Verify)
                {
                    if (session.Info.IsDubious)
                        diagnostics.Add(new("execution.disk_structure", "error", $"{mount.Device}: The resulting filesystem is dubious.", Symbol: mount.Device));
                    foreach (DiskDiagnostic diagnostic in session.Verify())
                        diagnostics.Add(new("execution.disk_structure", diagnostic.Severity,
                            $"{mount.Device}: {diagnostic.Code}: {diagnostic.Message}", Symbol: mount.Device));
                }
                foreach (DiskFileAssertion assertion in assertions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { EvaluateFile(session, assertion, diagnostics); }
                    catch (DiskException ex)
                    {
                        diagnostics.Add(new("execution.disk_assertion", "error", $"{mount.Device}:{assertion.Path}: {ex.Message}",
                            Symbol: mount.Device + ":" + assertion.Path));
                    }
                }
            }
            catch (Exception ex) when (ex is DiskException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new("execution.disk_assertion", "error", $"Cannot inspect resulting {mount.Device} disk: {ex.Message}",
                    Symbol: mount.Device));
            }
        }
        return diagnostics;
    }

    /// <summary>Validates an execution specification for its selected engine.</summary>
    public static void Validate(ExecutionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ValidatePrepared(ExecutionGraphicsMemory.Prepare(spec).Spec);
    }

    private static void ValidatePrepared(ExecutionSpec spec)
    {
        if (spec.Engine == "mame") MameAdapter.Validate(spec);
        else if (spec.Engine == "cpu") CpuExecutionEngine.Validate(spec);
        else throw new DiskException("execution.invalid_spec", "engine must be mame or cpu.", 2);
    }

    private static void EvaluateFile(DiskSession session, DiskFileAssertion assertion, List<ProgramDiagnostic> diagnostics)
    {
        string symbol = assertion.Device + ":" + assertion.Path;
        void Compare(string field, string expected, string actual)
        {
            if (expected != actual)
                diagnostics.Add(new("execution.disk_assertion", "error", $"Saved file {field} differs for {symbol}.",
                    Symbol: symbol, Expected: expected, Actual: actual));
        }
        DiskEntry? entry;
        try { entry = session.GetEntry(assertion.Path); }
        catch (DiskException ex) when (ex.Code == "file_not_found") { entry = null; }
        Compare("existence", assertion.Exists ? "present" : "absent", entry is null ? "absent" : "present");
        if (!assertion.Exists || entry is null) return;
        if (assertion.Type is { } type)
            Compare("type", $"0x{DiskSession.ParseFileType(type):x2}", $"0x{entry.FileType:x2}");
        if (assertion.AuxType is { } aux)
            Compare("auxType", aux.ToString(CultureInfo.InvariantCulture), entry.AuxType.ToString(CultureInfo.InvariantCulture));
        if (assertion.Length is { } length)
            Compare("length", length.ToString(CultureInfo.InvariantCulture), entry.Length.ToString(CultureInfo.InvariantCulture));
        if (assertion.Sha256 is not null || assertion.Hex is not null)
        {
            byte[] bytes = session.ReadFile(assertion.Path);
            if (assertion.Sha256 is { } hash) Compare("sha256", hash.ToLowerInvariant(), ProgramFiles.Hash(bytes));
            if (assertion.Hex is { } hex)
            {
                byte[] expected = MameAdapter.ParseHex(hex);
                if (!expected.AsSpan().SequenceEqual(bytes))
                    diagnostics.Add(new("execution.disk_assertion", "error", $"Saved file payload differs for {symbol}.",
                        Symbol: symbol, Expected: DescribePayload(expected), Actual: DescribePayload(bytes)));
            }
        }
    }

    private static string DescribePayload(ReadOnlySpan<byte> bytes)
        => bytes.Length <= 256 ? Convert.ToHexString(bytes)
            : $"{Convert.ToHexString(bytes[..256])}... ({bytes.Length} bytes; sha256 {ProgramFiles.Hash(bytes)})";

    private static async Task<ProcessCapture> RunProcessAsync(string executable, IReadOnlyList<string> arguments,
        string directory, string logName, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        Task<string> output = CaptureAsync(process.StandardOutput, Path.Combine(directory, logName + ".stdout.txt"), cancellationToken);
        Task<string> error = CaptureAsync(process.StandardError, Path.Combine(directory, logName + ".stderr.txt"), cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);
            return new(process.ExitCode, output.Result, error.Result);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            // Observe cancelled reads so no background tasks outlive this execution.
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, string path, CancellationToken cancellationToken)
    {
        const int limit = 4 * 1024 * 1024;
        await using StreamWriter writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        StringBuilder captured = new();
        char[] buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            int retained = Math.Min(count, limit - captured.Length);
            if (retained > 0)
            {
                captured.Append(buffer, 0, retained);
                await writer.WriteAsync(buffer.AsMemory(0, retained), cancellationToken);
            }
        }
        return captured.ToString();
    }

    private static string ReadBounded(string path, long maximum)
    {
        if (new FileInfo(path).Length > maximum) throw new InvalidDataException("Emulator artifact exceeds the allowed size: " + Path.GetFileName(path));
        return File.ReadAllText(path);
    }

    [GeneratedRegex(@"\b0\.\d{3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumber();
    [GeneratedRegex("^[0-9a-fA-F]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HexBytes();
    private sealed record ProcessCapture(int ExitCode, string StandardOutput, string StandardError);
}

public sealed record ExecutionObservation(string StopReason, double EmulatedSeconds,
    IReadOnlyDictionary<string, long> Registers, IReadOnlyDictionary<int, string> Memory, string ScreenText)
{
    public IReadOnlyList<ExecutionMemory> BankMemory { get; init; } = [];
    public ExecutionCycleResult? Cycles { get; init; }
    public ExecutionDebugResult? Debug { get; init; }
    public IReadOnlyDictionary<string, string> TextPages { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, int> Video { get; init; } = new Dictionary<string, int>();
    public AppleIIeTextScreen? TextScreen { get; init; }
    public IReadOnlyList<ExecutionStepResult> Steps { get; init; } = [];
}
