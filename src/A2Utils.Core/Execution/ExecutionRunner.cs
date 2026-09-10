using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        MameAdapter.Validate(spec);
        cancellationToken.ThrowIfCancellationRequested();
        string artifacts = Path.GetFullPath(artifactDirectory);
        ImageTransactions.ValidatePath(artifacts);
        ImageTransactions.ValidatePath(spec.DiskImage);
        if (Directory.Exists(artifacts) || File.Exists(artifacts))
            throw new DiskException("execution.artifacts_exist", "Use a new artifact directory for each execution.", 2);
        if (!File.Exists(spec.DiskImage)) throw new DiskException("execution.disk_missing", "The specified disk image does not exist.", 2);
        HostFiles.EnsureRegularFile(spec.DiskImage, cancellationToken);
        if (!Directory.Exists(spec.RomDirectory)) throw new DiskException("execution.rom_directory_missing", "The specified ROM directory does not exist.", 2);
        Directory.CreateDirectory(artifacts);
        string? version = null;
        string? hash = null;
        ExecutionObservation? observation = null;
        List<ProgramDiagnostic> diagnostics = [];
        string reason = "emulator_error";
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(spec.HostTimeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdog.Token);
        try
        {
            string disk = Path.Combine(artifacts, "disk" + Path.GetExtension(spec.DiskImage));
            await using (FileStream input = new(spec.DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("Execution disk images are limited to 64 MiB.");
                hash = Convert.ToHexString(await SHA256.HashDataAsync(input, linked.Token)).ToLowerInvariant();
                input.Position = 0;
                await using FileStream copy = new(disk, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(copy, linked.Token);
            }
            string script = Path.Combine(artifacts, "run.lua");
            await File.WriteAllTextAsync(script, MameAdapter.CreateScript(spec, artifacts), new UTF8Encoding(false), linked.Token);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "spec.json"),
                JsonSerializer.Serialize(spec, ExecutionSpec.JsonOptions), linked.Token);
            IReadOnlyList<string> arguments = MameAdapter.CreateArguments(spec, disk, script, artifacts);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "command.json"),
                JsonSerializer.Serialize(new { executable = Path.GetFullPath(spec.EmulatorPath), arguments }, ExecutionSpec.JsonOptions), linked.Token);

            ProcessCapture probe = await RunProcessAsync(spec.EmulatorPath, ["-version"], artifacts, "version", linked.Token);
            version = VersionNumber().Match(probe.StandardOutput).Value;
            if (probe.ExitCode != 0 || version != spec.ExpectedVersion)
            {
                reason = "version_mismatch";
                diagnostics.Add(new("execution.version_mismatch", "error", "The emulator version probe did not match the pinned MAME API.",
                    Expected: spec.ExpectedVersion, Actual: string.IsNullOrEmpty(version) ? probe.StandardOutput.Trim() : version));
            }
            else
            {
                ProcessCapture run = await RunProcessAsync(spec.EmulatorPath, arguments, artifacts, "emulator", linked.Token);
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
                        ReadBounded(Path.Combine(artifacts, "screen.txt"), 4096));
                    reason = observation.StopReason;
                    diagnostics.AddRange(Evaluate(spec, observation));
                    if (spec.Screenshot && !File.Exists(Path.Combine(artifacts, "screen.png")))
                        diagnostics.Add(new("execution.screenshot_missing", "error", "MAME did not create the requested screen.png artifact."));
                }
            }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            reason = "adapter_error";
            diagnostics.Add(new("execution.adapter_error", "error", ex.Message));
        }
        bool passed = observation is not null && diagnostics.All(d => d.Severity != "error");
        ExecutionResult result = new(1, spec.Name, passed, reason, version, observation?.EmulatedSeconds,
            observation?.Registers ?? new Dictionary<string, long>(), observation?.Memory ?? new Dictionary<int, string>(),
            observation?.ScreenText, hash, artifacts,
            Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray(), diagnostics);
        string resultPath = Path.Combine(artifacts, "result.json");
        result = result with { Artifacts = result.Artifacts.Append(resultPath).ToArray() };
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, ExecutionSpec.JsonOptions));
        return result;
    }

    public static IReadOnlyList<ProgramDiagnostic> Evaluate(ExecutionSpec spec, ExecutionObservation observation)
    {
        MameAdapter.Validate(spec);
        List<ProgramDiagnostic> diagnostics = [];
        if (spec.Until is not null && observation.StopReason != "completion_condition")
            diagnostics.Add(new("execution.completion_timeout", "error", "The completion byte was not observed before the emulated deadline."));
        foreach (MemoryAssertion assertion in spec.Memory)
        {
            string expected = Convert.ToHexString(MameAdapter.ParseHex(assertion.Hex));
            observation.Memory.TryGetValue(assertion.Address, out string? actual);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new("execution.memory_assertion", "error", $"Memory assertion failed at ${assertion.Address:X4}.",
                    Symbol: "$" + assertion.Address.ToString("X4"), Expected: expected, Actual: actual ?? "missing"));
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
                diagnostics.Add(new("execution.text_assertion", "error", "Expected text was absent from the selected 40-column text page.",
                    Expected: text, Actual: observation.ScreenText));
        }
        return diagnostics;
    }

    public static ExecutionObservation ParseObservation(string text, string screenText)
    {
        string[] lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 4 || lines[0] != "A2EXEC1" || lines[^1] != "END")
            throw new InvalidDataException("The emulator observation file is incomplete or has an unsupported version.");
        Dictionary<string, long> registers = new(StringComparer.Ordinal);
        Dictionary<int, string> memory = [];
        string? reason = null;
        double? seconds = null;
        foreach (string line in lines.Skip(1).SkipLast(1))
        {
            string[] fields = line.Split('\t');
            if (fields is ["STOP", var stop] && reason is null && stop is "completion_condition" or "emulated_limit") reason = stop;
            else if (fields is ["TIME", var time] && seconds is null
                && double.TryParse(time, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) && parsed >= 0) seconds = parsed;
            else if (fields is ["REG", var register, var value] && long.TryParse(value, CultureInfo.InvariantCulture, out long number)
                && registers.TryAdd(register, number)) { }
            else if (fields is ["MEM", var address, var hex] && int.TryParse(address, CultureInfo.InvariantCulture, out int offset)
                && offset is >= 0 and <= 65535 && hex.Length % 2 == 0 && HexBytes().IsMatch(hex)
                && memory.TryAdd(offset, hex)) { }
            else throw new InvalidDataException("The emulator observation file contains an invalid or duplicate field.");
        }
        if (reason is null || seconds is null) throw new InvalidDataException("The emulator observation is missing its stop reason or time.");
        return new(reason, seconds.Value, registers, memory, screenText);
    }

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
    IReadOnlyDictionary<string, long> Registers, IReadOnlyDictionary<int, string> Memory, string ScreenText);
