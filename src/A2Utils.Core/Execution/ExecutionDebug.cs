namespace A2Utils.Core.Execution;

/// <summary>A bounded debugging run. Stops at the first trigger, optionally executes instructions, then captures evidence.</summary>
public sealed record ExecutionDebug
{
    public IReadOnlyList<ExecutionBreakpoint> Breakpoints { get; init; } = [];
    public IReadOnlyList<ExecutionWatchpoint> Watchpoints { get; init; } = [];
    public int StepInstructions { get; init; }
    public int HistoryInstructions { get; init; } = 64;
}

/// <summary>Choose a CPU address or a project program/symbol pair. Symbols are resolved by build --test.</summary>
public sealed record ExecutionBreakpoint(int? Address = null, string? Program = null, string? Symbol = null,
    int Offset = 0, double AfterSeconds = 0);

/// <summary>Observes CPU bus accesses, including I/O, without performing additional reads.</summary>
public sealed record ExecutionWatchpoint(int? Address = null, int Length = 1, string Access = "write",
    string? Program = null, string? Symbol = null, int Offset = 0, double AfterSeconds = 0);

public sealed record ExecutionMemory(string Bank, int Address, string Hex);
public sealed record ExecutionDebugStop(string Kind, int Index, int Address, int? Value, int ProgramCounter);
public sealed record ExecutionInstruction(int Address, string Disassembly);
public sealed record ExecutionDebugResult(ExecutionDebugStop Trigger, int SteppedInstructions,
    IReadOnlyList<ExecutionInstruction> History);
