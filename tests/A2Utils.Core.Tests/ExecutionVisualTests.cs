using A2Utils.Core.Execution;
using A2Utils.Core.Graphics;

namespace A2Utils.Core.Tests;

public sealed class ExecutionVisualTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_VisualContract_ReturnsComparisonAndPreservesSource(bool matches)
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("input.dsk");
        File.WriteAllText(input, "visual");
        string expected = workspace.NewPath("reference.png");
        byte[] reference = PngCodec.Encode(new(2, 1, matches ? [0, 0, 0, 255, 255, 255] : new byte[6]));
        File.WriteAllBytes(expected, reference);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FixtureWorkspace.FindFixture("independent-dos33.do"))!, "..", ".."));
        string host = Path.Combine(root, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));

        ExecutionResult result = await ExecutionRunner.RunAsync(new()
        {
            EmulatorPath = host,
            RomDirectory = workspace.DirectoryPath,
            Machine = "apple2ee",
            DiskImage = input,
            ScreenshotAssertion = new(expected)
        }, workspace.NewPath("run"));

        Assert.Equal(matches, result.Passed);
        Assert.NotNull(result.ScreenshotComparison);
        Assert.Equal(matches, result.ScreenshotComparison.Comparison.Passed);
        Assert.Equal(matches ? 0 : 1, result.ScreenshotComparison.Comparison.DifferentPixels);
        Assert.Contains(result.Artifacts, path => Path.GetFileName(path) == "screen-diff.png");
        Assert.Contains(result.Artifacts, path => Path.GetFileName(path) == "expected-screen.png");
        Assert.Equal("visual", File.ReadAllText(input));
        Assert.Equal(reference, File.ReadAllBytes(expected));
        Assert.Equal(!matches, result.Diagnostics.Any(diagnostic => diagnostic.Code == "execution.screenshot_assertion"));
    }

    [Fact]
    public void PrepareAndCompare_PixelMismatch_RetainsPinnedExpectationAndDiffWithoutChangingInput()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("expected.png");
        byte[] expected = PngCodec.Encode(new(2, 1, [0, 0, 0, 255, 255, 255]));
        File.WriteAllBytes(source, expected);
        string artifacts = workspace.NewPath("run");
        Directory.CreateDirectory(artifacts);
        ExecutionVisualInput input = ExecutionVisual.Prepare(new() { ScreenshotAssertion = new(source) }, artifacts)!;
        File.WriteAllBytes(Path.Combine(artifacts, "screen.png"), PngCodec.Encode(new(2, 1, new byte[6])));

        ExecutionScreenshotResult result = ExecutionVisual.Compare(input, artifacts);

        Assert.False(result.Comparison.Passed);
        Assert.Equal(0.5, result.Comparison.DifferentFraction);
        Assert.Equal(64, result.ExpectedSha256.Length);
        Assert.Equal(expected, File.ReadAllBytes(source));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(artifacts, "expected-screen.png")));
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255 }, PngCodec.Decode(File.ReadAllBytes(result.DifferenceImage)).Rgb);
    }

    [Fact]
    public void Compare_ExpectedSourceChanges_RejectsAndKeepsOriginalSnapshot()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("expected.png");
        byte[] expected = PngCodec.Encode(new(1, 1, new byte[3]));
        File.WriteAllBytes(source, expected);
        string artifacts = workspace.NewPath("run");
        Directory.CreateDirectory(artifacts);
        ExecutionVisualInput input = ExecutionVisual.Prepare(new() { ScreenshotAssertion = new(source) }, artifacts)!;
        File.WriteAllBytes(Path.Combine(artifacts, "screen.png"), expected);
        File.WriteAllBytes(source, PngCodec.Encode(new(1, 1, [255, 0, 0])));

        DiskException exception = Assert.Throws<DiskException>(() => ExecutionVisual.Compare(input, artifacts));

        Assert.Equal("execution.screenshot_changed", exception.Code);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(artifacts, "expected-screen.png")));
        Assert.True(File.Exists(Path.Combine(artifacts, "screen-diff.png")));
    }

    [Fact]
    public void Prepare_InvalidCrop_RejectsBeforeCreatingSnapshot()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("expected.png");
        File.WriteAllBytes(source, PngCodec.Encode(new(1, 1, new byte[3])));
        string artifacts = workspace.NewPath("run");
        Directory.CreateDirectory(artifacts);
        Assert.Throws<DiskException>(() => ExecutionVisual.Prepare(new()
        {
            ScreenshotAssertion = new(source, Crop: new(0, 0, 2, 2))
        }, artifacts));
        Assert.False(File.Exists(Path.Combine(artifacts, "expected-screen.png")));
    }

    [Fact]
    public void CreateScript_VisualAssertionWithoutScreenshotFlag_ForcesCaptureAndResolvesExpectedPath()
    {
        using FixtureWorkspace workspace = new();
        ExecutionSpec spec = new()
        {
            EmulatorPath = "mame",
            Machine = "apple2ee",
            RomDirectory = "roms",
            DiskImage = "disk.dsk",
            ScreenshotAssertion = new("reference.png")
        };
        ExecutionSpec resolved = spec.ResolvePaths(workspace.DirectoryPath);
        Assert.Equal(workspace.NewPath("reference.png"), resolved.ScreenshotAssertion!.ExpectedImage);
        Assert.Contains("local screenshot = true", MameAdapter.CreateScript(resolved, workspace.NewPath("run")));
    }
}
