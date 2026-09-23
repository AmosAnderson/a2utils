// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Graphics;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

/// <summary>Compares an Apple display-memory page with a PNG encoded by the repository's deterministic codecs.</summary>
public sealed record GraphicsMemoryAssertion(string ExpectedImage, string Mode, int Page = 1, string Bank = "cpu");

public sealed record ExecutionGraphicsMemoryResult(string Mode, int Page, string Bank, string ExpectedImage,
    string ExpectedSha256, bool Passed, int MismatchedBytes, string? FirstMismatch,
    VisualComparisonResult? Pixels, string ExpectedPreview, string? ActualPreview, string? DifferenceImage)
{
    public int? StepIndex { get; init; }
    public string? StepName { get; init; }
}

internal sealed record PreparedGraphicsExecution(ExecutionSpec Spec,
    IReadOnlyList<PreparedGraphicsAssertion> Assertions);

internal sealed record PreparedGraphicsSegment(string Bank, int Address, byte[] Bytes);

internal sealed record PreparedGraphicsAssertion(GraphicsMemoryAssertion Source, string SourcePath,
    string SourceSha256, IReadOnlyList<PreparedGraphicsSegment> Segments, byte[] ExpectedBytes,
    int? StepIndex, string? StepName);

/// <summary>Expands image expectations into exact memory assertions and creates reviewable preview/difference artifacts.</summary>
internal static class ExecutionGraphicsMemory
{
    public static PreparedGraphicsExecution Prepare(ExecutionSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.GraphicsMemory is null) throw Invalid("graphicsMemory cannot be null.");
        if (spec.GraphicsMemory.Count > 16) throw Invalid("graphicsMemory accepts at most 16 display-page assertions.");

        List<PreparedGraphicsAssertion> prepared = [];
        List<MemoryAssertion> memory = spec.Memory?.ToList() ?? throw Invalid("memory cannot be null.");
        foreach (GraphicsMemoryAssertion assertion in spec.GraphicsMemory)
        {
            PreparedGraphicsAssertion item = PrepareOne(assertion, spec.Machine, null, null, cancellationToken);
            prepared.Add(item);
            memory.AddRange(item.Segments.Select(segment => new MemoryAssertion(segment.Address,
                Convert.ToHexString(segment.Bytes), segment.Bank)));
        }

        if (spec.Steps is null) throw Invalid("steps cannot be null.");
        ExecutionStep[] steps = new ExecutionStep[spec.Steps.Count];
        for (int index = 0; index < steps.Length; index++)
        {
            ExecutionStep step = spec.Steps[index] ?? throw Invalid("steps cannot contain null entries.");
            if (step.Condition is not { } condition)
            {
                steps[index] = step;
                continue;
            }
            if (condition.GraphicsMemory is null || condition.GraphicsMemory.Count > 8)
                throw Invalid("A step condition accepts at most 8 graphicsMemory assertions.");
            List<MemoryAssertion> stepMemory = condition.Memory?.ToList() ?? throw Invalid("Condition memory cannot be null.");
            foreach (GraphicsMemoryAssertion assertion in condition.GraphicsMemory)
            {
                PreparedGraphicsAssertion item = PrepareOne(assertion, spec.Machine, index, step.Name, cancellationToken);
                prepared.Add(item);
                stepMemory.AddRange(item.Segments.Select(segment => new MemoryAssertion(segment.Address,
                    Convert.ToHexString(segment.Bytes), segment.Bank)));
            }
            steps[index] = step with
            {
                Condition = condition with { Memory = stepMemory, GraphicsMemory = [] }
            };
        }

        return new(spec with { Memory = memory, GraphicsMemory = [], Steps = steps }, prepared);
    }

    public static ExecutionResult Attach(ExecutionResult result, PreparedGraphicsExecution prepared,
        CancellationToken cancellationToken = default)
    {
        if (prepared.Assertions.Count == 0) return result;
        List<ProgramDiagnostic> diagnostics = result.Diagnostics.ToList();
        List<ExecutionGraphicsMemoryResult> comparisons = [];
        for (int index = 0; index < prepared.Assertions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparedGraphicsAssertion assertion = prepared.Assertions[index];
            IReadOnlyList<ExecutionMemory> ranges;
            IReadOnlyDictionary<int, string>? flat = null;
            if (assertion.StepIndex is { } stepIndex)
            {
                ExecutionCheckpoint? checkpoint = result.Checkpoints.FirstOrDefault(item => item.Index == stepIndex);
                ranges = checkpoint?.Memory ?? [];
            }
            else
            {
                ranges = result.BankMemory;
                flat = result.Memory;
            }
            comparisons.Add(EvaluateOne(result.ArtifactDirectory, index, assertion, ranges, flat, diagnostics, cancellationToken));
        }
        bool passed = result.Passed && diagnostics.All(diagnostic => diagnostic.Severity != "error");
        return result with
        {
            Passed = passed,
            Diagnostics = diagnostics,
            GraphicsMemory = comparisons,
            Artifacts = Directory.GetFiles(result.ArtifactDirectory, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).ToArray()
        };
    }

    private static PreparedGraphicsAssertion PrepareOne(GraphicsMemoryAssertion assertion, string machine,
        int? stepIndex, string? stepName, CancellationToken cancellationToken)
    {
        if (assertion is null || string.IsNullOrWhiteSpace(assertion.ExpectedImage))
            throw Invalid("A graphicsMemory assertion needs an expectedImage PNG path.");
        if (assertion.Mode is not ("lores" or "hires" or "hires-color" or "dhires-mono" or "dhires-color"))
            throw Invalid("graphicsMemory mode must be lores, hires, hires-color, dhires-mono, or dhires-color.");
        if (assertion.Page is not (1 or 2)) throw Invalid("graphicsMemory page must be 1 or 2.");
        if (assertion.Bank is not ("cpu" or "main" or "aux"))
            throw Invalid("graphicsMemory bank must be cpu, main, or aux.");

        string source = Path.GetFullPath(assertion.ExpectedImage);
        byte[] png = ProgramFiles.ReadBytes(source, 32 * 1024 * 1024, cancellationToken);
        string hash = ProgramFiles.Hash(png);
        bool doubleHires = assertion.Mode.StartsWith("dhires-", StringComparison.Ordinal);
        int address = assertion.Page == 1 ? 0x2000 : 0x4000;
        if (doubleHires)
        {
            if (!AppleIIeMemory.IsIIeMachine(machine))
                throw Invalid("Double-hires graphicsMemory assertions require an Apple IIe or Apple IIc machine.");
            if (assertion.Bank != "cpu")
                throw Invalid("Double-hires graphicsMemory assertions select main and auxiliary banks automatically; omit bank or use cpu.");
            byte[] encoded = DoubleHiresGraphics.Encode(PngCodec.Decode(png),
                assertion.Mode == "dhires-mono" ? "mono" : "color", "aux-main");
            PreparedGraphicsSegment[] segments =
            [
                new("aux", address, encoded[..8192]),
                new("main", address, encoded[8192..])
            ];
            return new(assertion, source, hash, segments, encoded, stepIndex, stepName);
        }

        address = assertion.Mode == "lores"
            ? (assertion.Page == 1 ? 0x0400 : 0x0800)
            : address;
        byte[] bytes = AppleGraphics.EncodePng(png, assertion.Mode);
        if (!AppleIIeMemory.IsValidRange(assertion.Bank, address, bytes.Length)
            || assertion.Bank != "cpu" && !AppleIIeMemory.IsIIeMachine(machine))
            throw Invalid("The graphicsMemory page is outside the selected machine or memory bank.");
        return new(assertion, source, hash, [new(assertion.Bank, address, bytes)], bytes, stepIndex, stepName);
    }

    private static ExecutionGraphicsMemoryResult EvaluateOne(string artifacts, int index,
        PreparedGraphicsAssertion assertion, IReadOnlyList<ExecutionMemory> ranges,
        IReadOnlyDictionary<int, string>? flat, List<ProgramDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        List<byte> actual = [];
        string? firstMismatch = null;
        int mismatched = 0;
        bool complete = true;
        foreach (PreparedGraphicsSegment segment in assertion.Segments)
        {
            string? hex = segment.Bank == "cpu" ? flat?.GetValueOrDefault(segment.Address)
                ?? ranges.FirstOrDefault(range => range.Bank == "cpu" && range.Address == segment.Address)?.Hex
                : ranges.FirstOrDefault(range => range.Bank == segment.Bank && range.Address == segment.Address)?.Hex;
            byte[]? bytes = null;
            try { if (hex is not null) bytes = Convert.FromHexString(hex); }
            catch (FormatException) { }
            if (bytes is null || bytes.Length != segment.Bytes.Length)
            {
                complete = false;
                mismatched += segment.Bytes.Length;
                firstMismatch ??= $"{segment.Bank}:${segment.Address:X4}";
                continue;
            }
            actual.AddRange(bytes);
            for (int offset = 0; offset < bytes.Length; offset++)
                if (bytes[offset] != segment.Bytes[offset])
                {
                    mismatched++;
                    firstMismatch ??= $"{segment.Bank}:${segment.Address + offset:X4}";
                }
        }

        string prefix = assertion.StepIndex is { } step
            ? $"checkpoint-{step + 1:D3}-graphics-{index + 1:D3}"
            : $"graphics-memory-{index + 1:D3}";
        string expectedPreview = Path.Combine(artifacts, prefix + "-expected.png");
        RasterImage expectedImage = Decode(assertion.ExpectedBytes, assertion.Source.Mode);
        File.WriteAllBytes(expectedPreview, PngCodec.Encode(expectedImage));
        string? actualPreview = null;
        string? difference = null;
        VisualComparisonResult? pixels = null;
        if (complete && actual.Count == assertion.ExpectedBytes.Length)
        {
            RasterImage actualImage = Decode(actual.ToArray(), assertion.Source.Mode);
            pixels = VisualComparison.Compare(expectedImage, actualImage);
            actualPreview = Path.Combine(artifacts, prefix + "-actual.png");
            difference = Path.Combine(artifacts, prefix + "-diff.png");
            File.WriteAllBytes(actualPreview, PngCodec.Encode(actualImage));
            File.WriteAllBytes(difference, PngCodec.Encode(pixels.Diff));
        }
        cancellationToken.ThrowIfCancellationRequested();

        bool unchanged;
        try
        {
            unchanged = ProgramFiles.Hash(ProgramFiles.ReadBytes(assertion.SourcePath, 32 * 1024 * 1024, cancellationToken))
                == assertion.SourceSha256;
        }
        catch (Exception exception) when (exception is DiskException or IOException or UnauthorizedAccessException)
        {
            unchanged = false;
        }
        bool passed = unchanged && complete && mismatched == 0;
        if (!passed)
        {
            string symbol = assertion.StepIndex is { } stepIndex
                ? $"step:{stepIndex + 1}:{assertion.Source.Mode}:page{assertion.Source.Page}"
                : $"{assertion.Source.Mode}:page{assertion.Source.Page}";
            diagnostics.Add(new(unchanged ? "execution.graphics_memory" : "execution.graphics_changed", "error",
                unchanged
                    ? $"Display memory differed from the encoded PNG in {mismatched} byte(s)."
                    : "The expected graphics PNG changed during execution; comparison evidence was retained.",
                Symbol: symbol, Expected: "0 differing bytes", Actual: $"{mismatched} differing bytes"));
        }

        return new(assertion.Source.Mode, assertion.Source.Page,
            assertion.Source.Mode.StartsWith("dhires-", StringComparison.Ordinal) ? "aux+main" : assertion.Source.Bank,
            assertion.SourcePath, assertion.SourceSha256, passed, mismatched, firstMismatch, pixels,
            expectedPreview, actualPreview, difference)
        {
            StepIndex = assertion.StepIndex,
            StepName = assertion.StepName
        };
    }

    private static RasterImage Decode(byte[] bytes, string mode)
        => mode switch
        {
            "dhires-mono" => DoubleHiresGraphics.Decode(bytes, "mono", "aux-main"),
            "dhires-color" => DoubleHiresGraphics.Decode(bytes, "color", "aux-main"),
            _ => AppleGraphics.Decode(bytes, mode)
        };

    private static DiskException Invalid(string message) => new("execution.invalid_spec", message, 2);
}
