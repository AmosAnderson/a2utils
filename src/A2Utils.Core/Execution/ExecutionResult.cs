using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

public sealed record ExecutionResult(
    int SchemaVersion, string Name, bool Passed, string StopReason, string? EmulatorVersion,
    double? EmulatedSeconds, IReadOnlyDictionary<string, long> Registers,
    IReadOnlyDictionary<int, string> Memory, string? ScreenText,
    string? InputSha256, string ArtifactDirectory, IReadOnlyList<string> Artifacts,
    IReadOnlyList<ProgramDiagnostic> Diagnostics)
{
    public IReadOnlyList<ExecutionDiskResult> Disks { get; init; } = [];
    public IReadOnlyList<ExecutionMemory> BankMemory { get; init; } = [];
    public IReadOnlyList<ExecutionStepResult> Steps { get; init; } = [];
    public IReadOnlyList<ExecutionCheckpoint> Checkpoints { get; init; } = [];
    public ExecutionAudioResult? Audio { get; init; }
    public ExecutionEnvironmentEvidence? Environment { get; init; }
    public ExecutionCycleResult? Cycles { get; init; }
    public ExecutionDebugResult? Debug { get; init; }
    public AppleIIeTextScreen? TextScreen { get; init; }
    public ExecutionScreenshotResult? ScreenshotComparison { get; init; }
    public IReadOnlyList<ExecutionGraphicsMemoryResult> GraphicsMemory { get; init; } = [];
    public IReadOnlyDictionary<string, int> Video { get; init; } = new Dictionary<string, int>();
}

public sealed record ExecutionDiskResult(string Device, string InputPath, string ArtifactPath,
    string InputSha256, string? OutputSha256);

public sealed record ExecutionSuiteResult(int SchemaVersion, bool Passed, IReadOnlyList<ExecutionResult> Tests)
{
    public string SuitePath { get; init; } = "";
    public string ArtifactDirectory { get; init; } = "";
    public ExecutionSuiteCounts Counts { get; init; } = new(0, 0, 0, 0, 0, 0);
    public IReadOnlyList<ExecutionSuiteCaseResult> Cases { get; init; } = [];
    public bool Cancelled { get; init; }
}
