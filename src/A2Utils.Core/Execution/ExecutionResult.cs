using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

public sealed record ExecutionResult(
    int SchemaVersion, string Name, bool Passed, string StopReason, string? EmulatorVersion,
    double? EmulatedSeconds, IReadOnlyDictionary<string, long> Registers,
    IReadOnlyDictionary<int, string> Memory, string? ScreenText,
    string? InputSha256, string ArtifactDirectory, IReadOnlyList<string> Artifacts,
    IReadOnlyList<ProgramDiagnostic> Diagnostics);

public sealed record ExecutionSuiteResult(int SchemaVersion, bool Passed, IReadOnlyList<ExecutionResult> Tests);
