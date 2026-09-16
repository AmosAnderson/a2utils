using System.Globalization;
using A2Utils.Core.Assembly;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Execution;

public sealed record SymbolicMemoryAssertion(string Program, string Symbol, string Hex, int Offset = 0, string? Bank = null);
public sealed record SymbolicCompletionCondition(string Program, string Symbol, int Value,
    int Offset = 0, double AfterSeconds = 0, string? Bank = null);
public sealed record ExecutionSourceLocation(string Program, int Address, string? File, int Line,
    string Source, string Observation)
{
    public string MemoryBank { get; init; } = "main";
}

/// <summary>Binds machine tests to a specific build and resolves native assembly symbols.</summary>
public static class BuildExecution
{
    public static ExecutionSpec Bind(ExecutionSpec spec, ProjectBuildResult build, string device = "flop1")
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(build);
        if (!MameAdapter.IsStorageDevice(spec.StorageProfile, device)) throw Error("device", "Build disk device must belong to the selected storage profile.");
        if (device is "hard1" or "hard2" && build.FileSystem != "prodos")
            throw Error("storage_format", "CFFA2 build media must contain ProDOS.");
        if (device is "flop1" or "flop2" && build.Plan is { ImageSizeBytes: > 143360 })
            throw Error("storage_geometry", "Larger project images require cffa2 and a hard1/hard2 build device.");
        if (build.CheckOnly || build.Sha256.Length != 64)
            throw Error("build_required", "Execution requires a full build or preflight report with an image hash.");
        string machine = build.Target switch
        {
            "apple2plus" => "apple2p",
            "apple2e" => "apple2e",
            "apple2enh" => "apple2ee",
            "apple2c" => "apple2c",
            _ => throw Error("target", "The build target has no execution profile.")
        };
        if (spec.Machine != machine)
            throw Error("machine", $"Build target '{build.Target}' requires execution machine '{machine}'.");
        if (spec.SymbolicMemory is null || spec.Memory is null || spec.SymbolicMemory.Count > 1024)
            throw Error("symbols", "Symbolic and numeric memory assertions must be bounded arrays.");
        if (spec.SymbolicUntil is not null && spec.Until is not null)
            throw Error("symbols", "Specify either until or symbolicUntil.");
        if (spec.Disks is null || spec.Disks.Count > 2 || spec.Disks.Any(disk => disk is null) ||
            spec.Disks.Count > 0 && !string.IsNullOrEmpty(spec.DiskImage) || !MameAdapter.IsStorageDevice(spec.StorageProfile, spec.DiskDevice))
            throw Error("invalid_spec", "Use a valid legacy disk device or at most two explicit mounts; do not combine diskImage and disks.");

        // Keep user-specified OS mounts while replacing only the explicitly selected build device.
        IReadOnlyList<ExecutionDisk> mounts = spec.Disks.Count == 0 && string.IsNullOrEmpty(spec.DiskImage)
            ? [] : spec.GetDisks();
        if (mounts.Count(disk => disk.Device == device) > 1)
            throw Error("device", "Execution devices must be unique.");
        List<ExecutionDisk> disks = mounts.Where(disk => disk.Device != device).ToList();
        ExecutionDisk? previous = mounts.FirstOrDefault(disk => disk.Device == device);
        disks.Add(new(device, build.OutputPath, build.Sha256,
            InputFileSystem: build.FileSystem, Verify: previous?.Verify ?? true));
        List<MemoryAssertion> memory = spec.Memory.ToList();
        foreach (SymbolicMemoryAssertion item in spec.SymbolicMemory)
        {
            if (item is null) throw Error("symbols", "Symbolic memory assertions cannot contain null.");
            memory.Add(new(ResolveAddress(build, item.Program, item.Symbol, item.Offset), item.Hex, ResolveBank(build, item.Program, item.Bank)));
        }
        CompletionCondition? until = spec.SymbolicUntil is { } completion
            ? new(ResolveAddress(build, completion.Program, completion.Symbol, completion.Offset),
                completion.Value, completion.AfterSeconds, ResolveBank(build, completion.Program, completion.Bank))
            : spec.Until;
        ExecutionSpec bound = spec with
        {
            DiskImage = "",
            Disks = disks.OrderBy(disk => disk.Device, StringComparer.Ordinal).ToArray(),
            Memory = memory,
            Until = until,
            SymbolicMemory = [],
            SymbolicUntil = null,
            Debug = BindDebug(spec.Debug, build),
            Cycles = BindCycles(spec.Cycles, build),
            Steps = BindSteps(spec.Steps, build)
        };
        MameAdapter.Validate(bound);
        return bound;
    }

    private static ExecutionCycleMeasurement? BindCycles(ExecutionCycleMeasurement? cycles, ProjectBuildResult build)
    {
        if (cycles is null) return null;
        if (cycles.Start is null || cycles.End is null) throw Error("cycles", "Cycle start and end are required.");
        ExecutionDebug bound = BindDebug(new() { Breakpoints = [cycles.Start, cycles.End] }, build)!;
        return cycles with { Start = bound.Breakpoints[0], End = bound.Breakpoints[1] };
    }

    private static IReadOnlyList<ExecutionStep> BindSteps(IReadOnlyList<ExecutionStep> steps, ProjectBuildResult build)
    {
        if (steps is null || steps.Count > 128) throw Error("steps", "Steps must be a bounded array.");
        return steps.Select(step =>
        {
            if (step is null) throw Error("steps", "Null step.");
            if (step.Condition is not { } condition) return step;
            if (condition.SymbolicMemory is null || condition.Memory is null || condition.SymbolicMemory.Count > 1024)
                throw Error("steps", "Step symbolic memory must be a bounded array.");
            return step with
            {
                Condition = condition with
                {
                    Memory = condition.Memory.Concat(condition.SymbolicMemory.Select(item => item is null ? throw Error("steps", "Null step symbol.")
                        : new MemoryAssertion(ResolveAddress(build, item.Program, item.Symbol, item.Offset), item.Hex, ResolveBank(build, item.Program, item.Bank)))).ToArray(),
                    SymbolicMemory = []
                }
            };
        }).ToArray();
    }

    private static string ResolveBank(ProjectBuildResult build, string program, string? bank)
    {
        if (bank is not null) return bank;
        string declared = build.Files.Single(file => file.Path.Equals(program, StringComparison.OrdinalIgnoreCase)).MemoryBank;
        return declared == "main" ? "cpu" : declared;
    }

    private static ExecutionDebug? BindDebug(ExecutionDebug? debug, ProjectBuildResult build)
    {
        if (debug is null) return null;
        if (debug.Breakpoints is null || debug.Watchpoints is null || debug.Breakpoints.Count + debug.Watchpoints.Count > 64)
            throw Error("debug", "Debug points must be bounded arrays.");
        return debug with
        {
            Breakpoints = debug.Breakpoints.Select(point => point is null ? throw Error("debug", "Null breakpoint.") : point with
            {
                Address = Resolve(point.Address, point.Program, point.Symbol, point.Offset),
                Program = null,
                Symbol = null,
                Offset = 0
            }).ToArray(),
            Watchpoints = debug.Watchpoints.Select(point => point is null ? throw Error("debug", "Null watchpoint.") : point with
            {
                Address = Resolve(point.Address, point.Program, point.Symbol, point.Offset),
                Program = null,
                Symbol = null,
                Offset = 0
            }).ToArray()
        };

        int Resolve(int? address, string? program, string? symbol, int offset)
        {
            if (address.HasValue)
            {
                if (program is not null || symbol is not null || offset != 0)
                    throw Error("debug", "Choose a numeric debug address or program/symbol/offset.");
                return address.Value;
            }
            return ResolveAddress(build, program!, symbol!, offset);
        }
    }

    public static int ResolveAddress(ProjectBuildResult build, string program, string symbol, int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(program) || string.IsNullOrWhiteSpace(symbol))
            throw Error("symbol", "A symbolic address requires a program image path and symbol name.");
        BuiltFile file = build.Files.SingleOrDefault(file => file.Path.Equals(program, StringComparison.OrdinalIgnoreCase))
            ?? throw Error("symbol_program", $"The build contains no program '{program}'.");
        KeyValuePair<string, int>[] matches = file.Symbols.Where(pair => pair.Key.Equals(symbol,
            file.Kind == "asm" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw Error("symbol_missing", $"Program '{program}' has no unique exported symbol '{symbol}'.");
        long address = (long)matches[0].Value + offset;
        if (address is < 0 or > 65535) throw Error("symbol_range", $"Symbol '{symbol}' plus offset is outside the 16-bit address space.");
        return (int)address;
    }

    public static IReadOnlyList<ExecutionSourceLocation> Locate(ProjectBuildResult build, ExecutionResult result)
    {
        List<ExecutionSourceLocation> locations = [];
        if (result.Registers.TryGetValue("PC", out long pc) && pc is >= 0 and <= 65535)
            Add((int)pc, "final PC");
        if (result.Debug is { } debug)
        {
            Add(debug.Trigger.ProgramCounter, "debug trigger PC");
            foreach (ExecutionInstruction instruction in debug.History)
                Add(instruction.Address, "instruction history");
        }
        string trace = Path.Combine(result.ArtifactDirectory, "trace.tsv");
        if (File.Exists(trace))
        {
            string text = ProgramFiles.DecodeText(ProgramFiles.ReadBytes(trace, 16 * 1024 * 1024));
            HashSet<int> sampled = [];
            foreach (string line in text.Split('\n').Skip(1))
            {
                string[] fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length == 2 && int.TryParse(fields[1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int address) && address is >= 0 and <= 65535 && sampled.Add(address))
                    Add(address, "sampled PC");
            }
        }
        return locations;

        void Add(int address, string observation)
        {
            foreach (BuiltFile file in build.Files.Where(file => file.Resident))
            {
                if (file.SourceMap is not IEnumerable<AssemblySourceMapEntry> map) continue;
                foreach (AssemblySourceMapEntry entry in map.Where(entry => entry.Length > 0 &&
                    address >= entry.Address && address < (long)entry.Address + entry.Length))
                    locations.Add(new(file.Path, address, entry.File, entry.Line, entry.Source, observation) { MemoryBank = file.MemoryBank });
            }
        }
    }

    public static ExecutionResult Annotate(ExecutionSpec source, ProjectBuildResult build, ExecutionResult result)
    {
        IEnumerable<ProgramDiagnostic> diagnostics = result.Diagnostics.Select(AnnotateDiagnostic);
        if (source.CheckBasicRuntime && result.ScreenText is not null)
            diagnostics = diagnostics.Where(d => d.Code != "basic.runtime_error").Concat(Basic.ApplesoftTools.RuntimeDiagnostics(result.ScreenText, build));
        ProgramDiagnostic[] annotated = diagnostics.ToArray();
        return result with { Diagnostics = annotated, Passed = result.Passed && annotated.All(d => d.Severity != "error") };

        ProgramDiagnostic AnnotateDiagnostic(ProgramDiagnostic diagnostic)
        {
            SymbolicMemoryAssertion? symbol = source.SymbolicMemory.FirstOrDefault(item =>
                diagnostic.Code == "execution.memory_assertion" && diagnostic.Symbol ==
                    (ResolveBank(build, item.Program, item.Bank) == "cpu" ? "" : ResolveBank(build, item.Program, item.Bank) + ":") +
                    "$" + ResolveAddress(build, item.Program, item.Symbol, item.Offset).ToString("X4"));
            if (symbol is null) return diagnostic;
            int address = ResolveAddress(build, symbol.Program, symbol.Symbol, symbol.Offset);
            BuiltFile file = build.Files.Single(file => file.Path.Equals(symbol.Program, StringComparison.OrdinalIgnoreCase));
            AssemblySourceMapEntry? entry = (file.SourceMap as IEnumerable<AssemblySourceMapEntry>)?
                .FirstOrDefault(entry => entry.Length > 0 && address >= entry.Address && address < (long)entry.Address + entry.Length);
            return diagnostic with { Symbol = symbol.Program + ":" + symbol.Symbol, File = entry?.File, Line = entry?.Line };
        }
    }

    private static DiskException Error(string code, string message) => new("execution." + code, message, 2);
}
