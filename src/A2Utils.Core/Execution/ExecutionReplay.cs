// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

/// <summary>Versioned expectations for a captured final observation or one checkpoint.</summary>
public sealed record ReplayExpectations
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<MemoryAssertion> Memory { get; init; } = [];
    public IReadOnlyList<MemoryAssertion> MemoryNotEqual { get; init; } = [];
    public IReadOnlyList<RegisterAssertion> Registers { get; init; } = [];
    public IReadOnlyList<string> TextContains { get; init; } = [];
    public IReadOnlyList<string> TextNotContains { get; init; } = [];
    public IReadOnlyList<GraphicsMemoryAssertion> GraphicsMemory { get; init; } = [];
}

public sealed record ExecutionReplayResult(int SchemaVersion, string ArtifactDirectory, int? Checkpoint,
    string ArchivedSpecSha256, string ExpectationFile, string ExpectationSha256,
    string ObservationFile, string ObservationSha256,
    string? ScreenSha256, string RecordedStopReason, int Assertions, bool Passed,
    IReadOnlyList<ProgramDiagnostic> Diagnostics)
{
    public IReadOnlyList<ReplayGraphicsInput> GraphicsInputs { get; init; } = [];
}

public sealed record ReplayGraphicsInput(string Path, string Sha256);

/// <summary>Rechecks captured evidence without starting an engine or changing a run directory.</summary>
public static partial class ExecutionReplay
{
    public static ExecutionReplayResult Check(string artifactDirectory, string? expectationPath = null,
        int? checkpoint = null, CancellationToken cancellationToken = default)
    {
        if (checkpoint is < 1 or > 128) throw Invalid("Checkpoint must be a one-based step number from 1 to 128.");
        string root = Path.GetFullPath(artifactDirectory);
        string specPath = Path.Combine(root, "spec.json");
        byte[] specBytes = ReadEvidence(specPath, 4 * 1024 * 1024, cancellationToken);
        // Deserialize only. ExecutionSpec.Load resolves profiles, locks, and other live inputs.
        ExecutionSpec spec = Deserialize<ExecutionSpec>(specBytes, "replay.invalid_evidence");
        if (spec.SchemaVersion != 1 || spec.Engine is not ("cpu" or "mame"))
            throw new DiskException("replay.invalid_evidence", "Unknown archived spec version or execution engine.", 4);
        string prefix = checkpoint is { } n ? $"checkpoint-{n:D3}" : "observations";
        string observationPath = Path.Combine(root, prefix + ".tsv");
        byte[] observationBytes = ReadEvidence(observationPath, 1024 * 1024, cancellationToken);
        string expectationFile = expectationPath is null ? specPath : Path.GetFullPath(expectationPath);
        byte[] expectationBytes = expectationPath is null ? specBytes :
            ProgramFiles.ReadBytes(expectationFile, 4 * 1024 * 1024, cancellationToken);
        ReplayExpectations expected = expectationPath is null ? FromSpec(spec, checkpoint)
            : Deserialize<ReplayExpectations>(expectationBytes, "replay.invalid_expectations");
        Validate(expected);
        string textPath = Path.Combine(root, checkpoint is null ? "screen.txt" : prefix + ".txt");
        byte[]? screen = spec.Engine == "mame" && File.Exists(textPath)
            ? ReadEvidence(textPath, 32768, cancellationToken) : null;
        ExecutionObservation observation;
        try
        {
            observation = ExecutionRunner.ParseObservation(ProgramFiles.DecodeText(observationBytes),
                screen is null ? "" : ProgramFiles.DecodeText(screen), spec.Engine);
        }
        catch (Exception ex) when (ex is InvalidDataException or DiskException { Code: "program.invalid_utf8" })
        {
            throw new DiskException("replay.invalid_evidence", ex.Message, 4, ex);
        }
        Dictionary<(string Bank, int Address), byte> observed = [];
        foreach (ExecutionMemory capture in observation.BankMemory)
        {
            int address = capture.Address;
            foreach (byte value in Convert.FromHexString(capture.Hex))
            {
                var key = (capture.Bank, Address: address++);
                if (!observed.TryAdd(key, value) && observed[key] != value)
                    throw new DiskException("replay.invalid_evidence", $"Conflicting captured bytes at {key.Bank}:${key.Address:X4}.", 4);
            }
        }

        List<ProgramDiagnostic> diagnostics = [];
        List<ReplayGraphicsInput> graphicsInputs = [];
        int count = 0;
        foreach (MemoryAssertion assertion in expected.Memory) CheckMemory(assertion, equal: true);
        foreach (MemoryAssertion assertion in expected.MemoryNotEqual) CheckMemory(assertion, equal: false);
        foreach (RegisterAssertion assertion in expected.Registers)
        {
            count++;
            if (!observation.Registers.TryGetValue(assertion.Name, out long value))
                Missing(assertion.Name, "The register was not captured.");
            else if (value != assertion.Value)
                Mismatch(assertion.Name, assertion.Value.ToString(CultureInfo.InvariantCulture),
                    value.ToString(CultureInfo.InvariantCulture));
        }
        foreach (string text in expected.TextContains) CheckText(text, present: true);
        foreach (string text in expected.TextNotContains) CheckText(text, present: false);
        foreach (GraphicsMemoryAssertion graphics in expected.GraphicsMemory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(graphics.ExpectedImage)) throw Invalid("graphicsMemory requires an expectedImage PNG.");
            string path = Path.GetFullPath(graphics.ExpectedImage, Path.GetDirectoryName(expectationFile)!);
            PreparedGraphicsAssertion prepared = ExecutionGraphicsMemory.PrepareOne(graphics with { ExpectedImage = path },
                AppleIIeMemory.IsIIeMachine(spec.Machine), null, null, cancellationToken);
            graphicsInputs.Add(new(prepared.SourcePath, prepared.SourceSha256));
            count++;
            foreach (PreparedGraphicsSegment segment in prepared.Segments)
                CompareMemory(segment.Bank, segment.Address, segment.Bytes, equal: true);
        }
        if (count == 0) throw Invalid("No observation expectations were selected. Supply an expectation file with --expect.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(1, root, checkpoint, ProgramFiles.Hash(specBytes), expectationFile, ProgramFiles.Hash(expectationBytes),
            observationPath, ProgramFiles.Hash(observationBytes), screen is null ? null : ProgramFiles.Hash(screen),
            observation.StopReason, count, diagnostics.Count == 0, diagnostics)
        { GraphicsInputs = graphicsInputs };

        void CheckMemory(MemoryAssertion assertion, bool equal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assertion.Hex is null) throw Invalid("Memory assertions need hexadecimal bytes.");
            byte[] bytes;
            try { bytes = Convert.FromHexString(assertion.Hex.Replace(" ", "", StringComparison.Ordinal)); }
            catch (FormatException) { throw Invalid("Memory hex must contain complete hexadecimal bytes."); }
            bool valid = assertion.Bank == "cpu"
                ? assertion.Address >= 0 && bytes.Length > 0 && (long)assertion.Address + bytes.Length <= 65536
                : AppleIIeMemory.IsValidRange(assertion.Bank, assertion.Address, bytes.Length);
            if (!valid) throw Invalid("The memory expectation is outside its address space.");
            count++;
            CompareMemory(assertion.Bank, assertion.Address, bytes, equal);
        }

        void CompareMemory(string bank, int address, byte[] bytes, bool equal)
        {
            // A negative assertion also requires the complete range to have been captured.
            int firstDifference = -1;
            byte differingValue = 0;
            for (int offset = 0; offset < bytes.Length; offset++)
            {
                if (!observed.TryGetValue((bank, address + offset), out byte value))
                {
                    Missing($"{bank}:${address + offset:X4}", "The requested memory byte was not captured.");
                    return;
                }
                if (firstDifference < 0 && value != bytes[offset])
                {
                    firstDifference = offset;
                    differingValue = value;
                }
            }
            if (equal && firstDifference >= 0)
                Mismatch($"{bank}:${address + firstDifference:X4}", $"{bytes[firstDifference]:X2}", $"{differingValue:X2}");
            if (!equal && firstDifference < 0)
                Mismatch($"{bank}:${address:X4}", "different bytes", "identical bytes");
        }

        void CheckText(string text, bool present)
        {
            count++;
            if (screen is null) Missing("screen", "Screen text was not captured.");
            else if (observation.ScreenText.Contains(text, StringComparison.Ordinal) != present)
                Mismatch("screen", (present ? "contains " : "excludes ") + text, present ? "absent" : "present");
        }

        void Missing(string symbol, string message) =>
            diagnostics.Add(new("replay.missing_evidence", "error", message, Symbol: symbol));
        void Mismatch(string symbol, string wanted, string actual) =>
            diagnostics.Add(new("replay.mismatch", "error", $"Observation differs for {symbol}.",
                Symbol: symbol, Expected: wanted, Actual: actual));
    }

    private static ReplayExpectations FromSpec(ExecutionSpec spec, int? checkpoint)
    {
        if (checkpoint is null)
        {
            if (spec.GraphicsMemory is not { Count: 0 } || spec.SymbolicMemory is not { Count: 0 })
                throw Invalid("The archived spec contains unresolved image or symbolic expectations. Supply --expect.");
            return new()
            {
                Memory = spec.Memory,
                Registers = spec.Registers,
                TextContains = spec.TextContains,
                TextNotContains = spec.TextNotContains
            };
        }
        if (spec.Steps is null || checkpoint > spec.Steps.Count || spec.Steps[checkpoint.Value - 1]?.Condition is not { } step)
            throw Invalid("The selected step has no archived observation expectations. Supply --expect to check a captured step.");
        if (step.GraphicsMemory is not { Count: 0 } || step.SymbolicMemory is not { Count: 0 })
            throw Invalid("The archived step contains unresolved image or symbolic expectations. Supply --expect.");
        return new()
        {
            Memory = step.Memory,
            MemoryNotEqual = step.MemoryNotEqual,
            Registers = step.Registers,
            TextContains = step.TextContains,
            TextNotContains = step.TextNotContains
        };
    }

    private static void Validate(ReplayExpectations expected)
    {
        if (expected.SchemaVersion != 1 || expected.Memory is null || expected.MemoryNotEqual is null ||
            expected.Registers is null || expected.TextContains is null || expected.TextNotContains is null || expected.GraphicsMemory is null ||
            expected.Memory.Count + expected.MemoryNotEqual.Count > 1024 || expected.Registers.Count > 128 ||
            expected.TextContains.Count + expected.TextNotContains.Count > 256 || expected.GraphicsMemory.Count > 16)
            throw Invalid("Expected bounded, non-null arrays in a version 1 replay expectation file.");
        if (expected.Memory.Concat(expected.MemoryNotEqual).Any(m => m is null) || expected.GraphicsMemory.Any(g => g is null) ||
            expected.Registers.Any(r => r?.Name is null || !RegisterName().IsMatch(r.Name) || r.Value is < 0 or > 65535) ||
            expected.TextContains.Concat(expected.TextNotContains).Any(s => string.IsNullOrEmpty(s) || s.Length > 32768))
            throw Invalid("Replay expectations require valid memory, graphics, register, and text values.");
    }

    private static T Deserialize<T>(byte[] bytes, string code) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(bytes, ExecutionSpec.JsonOptions) ?? throw new JsonException("Expected a JSON object."); }
        catch (JsonException ex) { throw new DiskException(code, ex.Message, code == "replay.invalid_evidence" ? 4 : 2, ex); }
    }

    private static byte[] ReadEvidence(string path, int limit, CancellationToken cancellationToken)
    {
        try { return ProgramFiles.ReadBytes(path, limit, cancellationToken); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new DiskException("replay.missing_evidence", $"Missing archived file: {Path.GetFileName(path)}", 4, ex);
        }
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegisterName();

    private static DiskException Invalid(string message) => new("replay.invalid_expectations", message, 2);
}
