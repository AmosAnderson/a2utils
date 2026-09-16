namespace A2Utils.Core.Execution;

/// <summary>One ordered interaction. Conditions are conjunctions; waits poll once per emulated frame.</summary>
public sealed record ExecutionStep
{
    public string Name { get; init; } = "step";
    public string Action { get; init; } = "wait";
    public ExecutionCondition? Condition { get; init; }
    public double TimeoutSeconds { get; init; } = 5;
    public double Seconds { get; init; }
    public string? Text { get; init; }
    public GameInput? Input { get; init; }
}

public sealed record ExecutionCondition
{
    public IReadOnlyList<MemoryAssertion> Memory { get; init; } = [];
    public IReadOnlyList<MemoryAssertion> MemoryNotEqual { get; init; } = [];
    public IReadOnlyList<SymbolicMemoryAssertion> SymbolicMemory { get; init; } = [];
    public IReadOnlyList<RegisterAssertion> Registers { get; init; } = [];
    public IReadOnlyList<string> TextContains { get; init; } = [];
    public IReadOnlyList<string> TextNotContains { get; init; } = [];
}

public sealed record GameInput(string Control, int? Value);
public sealed record ExecutionStepResult(int Index, string Status, double EmulatedSeconds);
public sealed record ExecutionCheckpoint(int Index, string Name, string Action, string Status,
    double EmulatedSeconds, IReadOnlyDictionary<string, long> Registers,
    IReadOnlyList<ExecutionMemory> Memory, string Text, string ArtifactPath);
