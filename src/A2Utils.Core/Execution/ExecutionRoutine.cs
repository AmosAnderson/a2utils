using System.Text.Json;
using A2Utils.Core.Assembly;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

/// <summary>A subroutine invoked after power-on, using the real machine CPU and ROM.</summary>
public sealed record ExecutionRoutine
{
    public string Source { get; init; } = "";
    public string Kind { get; init; } = "asm";
    public int Origin { get; init; } = 0x6000;
    public int? EntryPoint { get; init; }
    public string? EntrySymbol { get; init; }
    public double StartAfterSeconds { get; init; } = 1;
    public long MaxCycles { get; init; } = 100000;
    public IReadOnlyList<RegisterAssertion> Registers { get; init; } = [];
    public IReadOnlyList<MemoryAssertion> Memory { get; init; } = [];
}

/// <summary>Measures CPU cycles from the first start hit to the first end hit, before either instruction executes.</summary>
public sealed record ExecutionCycleMeasurement(ExecutionBreakpoint Start, ExecutionBreakpoint End, long MaxCycles = 1000000);

public sealed record ExecutionCycleResult(int StartAddress, int EndAddress, long Cycles, bool Returned);

public sealed record PreparedRoutine(int Origin, int EntryPoint, byte[] Bytes,
    IReadOnlyDictionary<string, string> InputHashes, IReadOnlyDictionary<string, int> Symbols,
    IReadOnlyList<AssemblySourceMapEntry> SourceMap);

public static class RoutineHarness
{
    public const int ReturnAddress = 0x02ff;

    /// <summary>Validates and assembles a routine without creating execution artifacts.</summary>
    public static PreparedRoutine Assemble(ExecutionSpec spec, CancellationToken cancellationToken = default)
    {
        MameAdapter.Validate(spec);
        ExecutionRoutine routine = spec.Routine ?? throw new ArgumentException("A routine is required.", nameof(spec));
        AssemblyResult assembled;
        if (routine.Kind == "asm")
            assembled = Assembler.AssembleFile(routine.Source, (ushort)routine.Origin,
                spec.Machine is "apple2ee" or "apple2c" ? CpuKind.Apple65C02 : CpuKind.Mos6502, cancellationToken);
        else
        {
            byte[] bytes = ProgramFiles.ReadBytes(routine.Source, 0xc000, cancellationToken);
            assembled = new((ushort)routine.Origin, bytes)
            {
                DependencyHashes = new Dictionary<string, string> { [Path.GetFullPath(routine.Source)] = ProgramFiles.Hash(bytes) }
            };
        }
        int entry = routine.EntryPoint ?? assembled.Origin;
        if (routine.EntrySymbol is { } symbol)
        {
            if (!assembled.Symbols.TryGetValue(symbol, out entry))
                throw new DiskException("execution.routine_symbol", $"Routine entry symbol '{symbol}' is undefined.", 2);
        }
        if (assembled.Origin < 0x0800 || assembled.Bytes.Length == 0 || (long)assembled.Origin + assembled.Bytes.Length > 0xc000
            || entry < assembled.Origin || entry >= (long)assembled.Origin + assembled.Bytes.Length)
            throw new DiskException("execution.routine_range", "Routine code and entry must fit main RAM $0800..$BFFF.", 2);
        if (routine.Memory.Any(m => m.Address < assembled.Origin + assembled.Bytes.Length
            && m.Address + MameAdapter.ParseHex(m.Hex).Length > assembled.Origin))
            throw new DiskException("execution.routine_overlap", "Routine initial memory must not overwrite its code.", 2);
        return new(assembled.Origin, entry, assembled.Bytes, assembled.DependencyHashes, assembled.Symbols, assembled.SourceMap);
    }

    public static PreparedRoutine Prepare(ExecutionSpec spec, string artifacts, CancellationToken cancellationToken = default)
    {
        PreparedRoutine prepared = Assemble(spec, cancellationToken);
        using (FileStream output = new(Path.Combine(artifacts, "routine.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            output.Write(prepared.Bytes);
        string report = JsonSerializer.Serialize(new
        {
            prepared.Origin,
            prepared.EntryPoint,
            prepared.InputHashes,
            prepared.Symbols,
            prepared.SourceMap,
            Sha256 = ProgramFiles.Hash(prepared.Bytes),
            Length = prepared.Bytes.Length
        }, ExecutionSpec.JsonOptions);
        using (FileStream output = new(Path.Combine(artifacts, "routine.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (StreamWriter writer = new(output)) writer.Write(report);
        return prepared;
    }

    public static void ValidateInputs(PreparedRoutine routine, CancellationToken cancellationToken = default)
    {
        foreach (var input in routine.InputHashes)
            if (ProgramFiles.Hash(ProgramFiles.ReadBytes(input.Key, Assembler.MaximumSourceLength, cancellationToken)) != input.Value)
                throw new DiskException("execution.routine_changed", "Routine input changed during execution: " + input.Key, 6);
    }
}
