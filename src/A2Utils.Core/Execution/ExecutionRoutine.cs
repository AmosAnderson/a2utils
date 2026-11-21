// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Text.Json;
using A2Utils.Core.Assembly;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

/// <summary>A subroutine invoked by the selected execution engine.</summary>
public sealed record ExecutionRoutine
{
    private readonly int? _origin;
    public string Source { get; init; } = "";
    public string Kind { get; init; } = "asm";
    public int Origin { get => _origin ?? (Kind == "packaged" ? 0 : 0x6000); init => _origin = value; }
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
        ExecutionRunner.Validate(spec);
        ExecutionRoutine routine = spec.Routine ?? throw new ArgumentException("A routine is required.", nameof(spec));
        AssemblyResult assembled;
        if (spec.PackagedRoutine is { } packaged && routine.Kind == "packaged")
            assembled = packaged.Read(cancellationToken);
        else if (routine.Kind == "asm")
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
        ValidateLayout(routine, assembled.Origin, assembled.Bytes.Length, entry);
        return new(assembled.Origin, entry, assembled.Bytes, assembled.DependencyHashes, assembled.Symbols, assembled.SourceMap);
    }

    internal static void ValidateLayout(ExecutionRoutine routine, int origin, int length, int entry)
    {
        long end = (long)origin + length;
        if (origin < 0x0800 || length <= 0 || end > 0xc000 || entry < origin || entry >= end)
            throw new DiskException("execution.routine_range", "Routine code and entry must fit main RAM $0800..$BFFF.", 2);
        if (routine.Memory.Any(m => m.Address < end
            && m.Address + MameAdapter.ParseHex(m.Hex).Length > origin))
            throw new DiskException("execution.routine_overlap", "Routine initial memory must not overwrite its code.", 2);
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
            Packaged = spec.PackagedRoutine is { } packaged
                ? new { Program = packaged.File.Path, packaged.ImagePath, packaged.ImageSha256 } : null,
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
