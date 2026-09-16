using A2Utils.Core.Graphics;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

public sealed record ScreenshotAssertion(string ExpectedImage, int ChannelTolerance = 0,
    double MaxDifferentFraction = 0, ImageCrop? Crop = null);
public sealed record ExecutionScreenshotResult(string ExpectedSha256, string ActualSha256,
    VisualComparisonResult Comparison, string DifferenceImage);
public sealed record ExecutionVisualInput(string SourcePath, string ExpectedSha256,
    RasterImage Expected, VisualComparisonOptions Options);

/// <summary>Pins screenshot expectations before launch and retains deterministic comparison evidence.</summary>
public static class ExecutionVisual
{
    public static void Validate(ExecutionSpec spec)
    {
        if (spec.ScreenshotAssertion is not { } assertion) return;
        if (string.IsNullOrWhiteSpace(assertion.ExpectedImage))
            throw new DiskException("execution.invalid_spec", "Screenshot assertion needs an expectedImage PNG path.", 2);
        try { VisualComparison.Validate(new(assertion.ChannelTolerance, assertion.MaxDifferentFraction, assertion.Crop)); }
        catch (DiskException exception) { throw new DiskException("execution.invalid_spec", exception.Message, 2); }
    }

    public static ExecutionVisualInput? Prepare(ExecutionSpec spec, string artifacts, CancellationToken cancellationToken = default)
    {
        Validate(spec);
        if (spec.ScreenshotAssertion is not { } assertion) return null;
        string source = Path.GetFullPath(assertion.ExpectedImage);
        string snapshot = Path.Combine(artifacts, "expected-screen.png");
        ImageTransactions.EnsureDistinctPaths(source, snapshot);
        byte[] bytes = ProgramFiles.ReadBytes(source, 32 * 1024 * 1024, cancellationToken);
        RasterImage expected = PngCodec.Decode(bytes);
        VisualComparisonOptions options = new(assertion.ChannelTolerance, assertion.MaxDifferentFraction, assertion.Crop);
        // This also verifies that a crop fits the expected image before starting the emulator.
        _ = VisualComparison.Compare(expected, expected, options);
        cancellationToken.ThrowIfCancellationRequested();
        using (FileStream output = new(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(bytes);
        return new(source, ProgramFiles.Hash(bytes), expected, options);
    }

    public static ExecutionScreenshotResult Compare(ExecutionVisualInput input, string artifacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        string actualPath = Path.Combine(artifacts, "screen.png");
        if (!File.Exists(actualPath))
            throw new DiskException("execution.screenshot_missing", "MAME did not create the screenshot required for visual comparison.", 4);
        byte[] actual = ProgramFiles.ReadBytes(actualPath, 32 * 1024 * 1024, cancellationToken);
        VisualComparisonResult result = VisualComparison.Compare(input.Expected, PngCodec.Decode(actual), input.Options);
        string difference = Path.Combine(artifacts, "screen-diff.png");
        byte[] diff = PngCodec.Encode(result.Diff);
        cancellationToken.ThrowIfCancellationRequested();
        using (FileStream output = new(difference, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(diff);
        if (ProgramFiles.Hash(ProgramFiles.ReadBytes(input.SourcePath, 32 * 1024 * 1024, cancellationToken)) != input.ExpectedSha256)
            throw new DiskException("execution.screenshot_changed", "The expected screenshot changed during execution; comparison evidence was retained.", 6);
        return new(input.ExpectedSha256, ProgramFiles.Hash(actual), result, difference);
    }
}
