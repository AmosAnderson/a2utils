// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

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
        => BindCore(spec, build, device, allowCheckOnly: false, imageSizeBytes: null);

    internal static ExecutionSpec BindForValidation(ExecutionSpec spec,
        ProjectBuildResult build, long imageSizeBytes, string device = "flop1")
        => BindCore(spec, build, device, allowCheckOnly: true,
            imageSizeBytes: imageSizeBytes);

    private static ExecutionSpec BindCore(ExecutionSpec spec, ProjectBuildResult build,
        string device, bool allowCheckOnly, long? imageSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(build);
        bool mountsBuild = spec.Engine == "mame";
        if (!mountsBuild && spec.Engine != "cpu")
            throw Error("invalid_spec", "engine must be mame or cpu.");
        if (mountsBuild)
        {
            if (!MameAdapter.IsStorageDevice(spec.StorageProfile, device))
                throw Error("device", "Build disk device must belong to the selected storage profile.");
            if (device is "hard1" or "hard2" && build.FileSystem != "prodos")
                throw Error("storage_format", "CFFA2 build media must contain ProDOS.");
            long? plannedImageSize = imageSizeBytes ?? build.Plan?.ImageSizeBytes;
            if (device is "flop1" or "flop2" && plannedImageSize > 143360)
                throw Error("storage_geometry", "Larger project images require cffa2 and a hard1/hard2 build device.");
        }
        if (!allowCheckOnly && (build.CheckOnly || build.Sha256.Length != 64))
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
        if (spec.Disks is null || spec.Disks.Any(disk => disk is null))
            throw Error("invalid_spec", "Execution disks cannot be null or contain null entries.");
        if (mountsBuild && (spec.Disks.Count > 2 ||
            spec.Disks.Count > 0 && !string.IsNullOrEmpty(spec.DiskImage) || !MameAdapter.IsStorageDevice(spec.StorageProfile, spec.DiskDevice)))
            throw Error("invalid_spec", "Use a valid legacy disk device or at most two explicit mounts; do not combine diskImage and disks.");

        // Keep user-specified OS mounts while replacing only the explicitly selected build device.
        IReadOnlyList<ExecutionDisk> disks = spec.Disks;
        if (mountsBuild)
        {
            IReadOnlyList<ExecutionDisk> mounts = spec.Disks.Count == 0 && string.IsNullOrEmpty(spec.DiskImage)
                ? [] : spec.GetDisks();
            if (mounts.Count(disk => disk.Device == device) > 1)
                throw Error("device", "Execution devices must be unique.");
            List<ExecutionDisk> buildMounts = mounts.Where(disk => disk.Device != device).ToList();
            ExecutionDisk? previous = mounts.FirstOrDefault(disk => disk.Device == device);
            buildMounts.Add(new(device, build.OutputPath,
                allowCheckOnly ? null : build.Sha256,
                InputFileSystem: build.FileSystem, Verify: previous?.Verify ?? true));
            disks = buildMounts;
        }
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
            DiskImage = mountsBuild ? "" : spec.DiskImage,
            Disks = disks.OrderBy(disk => disk.Device, StringComparer.Ordinal).ToArray(),
            Memory = memory,
            Until = until,
            SymbolicMemory = [],
            SymbolicUntil = null,
            Debug = BindDebug(spec.Debug, build),
            Cycles = BindCycles(spec.Cycles, build),
            Steps = BindSteps(spec.Steps, build)
        };
        ExecutionRunner.Validate(bound);
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
        => Locate(build, result, null);

    /// <summary>Locates build observations and, for CPU runs, exact routine instruction source evidence.</summary>
    public static IReadOnlyList<ExecutionSourceLocation> Locate(ProjectBuildResult build, ExecutionResult result,
        ExecutionSpec? source)
    {
        List<ExecutionSourceLocation> locations = [];
        bool cpu = result.EmulatorVersion == CpuExecutionEngine.Version;
        if (!cpu && result.Registers.TryGetValue("PC", out long pc) && pc is >= 0 and <= 65535)
            AddBuild((int)pc, "final PC");
        if (!cpu && result.Debug is { } debug)
        {
            AddBuild(debug.Trigger.ProgramCounter, "debug trigger PC");
            foreach (ExecutionInstruction instruction in debug.History)
                AddBuild(instruction.Address, "instruction history");
        }
        string trace = Path.Combine(result.ArtifactDirectory, "trace.tsv");
        if (File.Exists(trace))
        {
            string text = ProgramFiles.DecodeText(ProgramFiles.ReadBytes(trace, 16 * 1024 * 1024));
            string[] lines = text.Split('\n');
            string[] header = lines[0].TrimEnd('\r').Split('\t');
            int pcField = Array.FindIndex(header, field => field.Equals("PC", StringComparison.OrdinalIgnoreCase));
            bool cpuTrace = cpu || Array.Exists(header, field => field.Equals("cycleStart", StringComparison.OrdinalIgnoreCase));
            int fileField = Array.FindIndex(header, field => field.Equals("sourceFile", StringComparison.OrdinalIgnoreCase));
            int lineField = Array.FindIndex(header, field => field.Equals("sourceLine", StringComparison.OrdinalIgnoreCase));
            int sourceField = Array.FindIndex(header, field => field.Equals("source", StringComparison.OrdinalIgnoreCase));
            HashSet<int> sampled = [];
            foreach (string line in lines.Skip(1))
            {
                string[] fields = line.TrimEnd('\r').Split('\t');
                NumberStyles style = cpuTrace ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
                if (pcField < 0 || fields.Length <= pcField || !int.TryParse(fields[pcField], style,
                    CultureInfo.InvariantCulture, out int address) || address is < 0 or > 65535 || !sampled.Add(address))
                    continue;
                if (!cpuTrace)
                {
                    AddBuild(address, "sampled PC");
                    continue;
                }
                if (fileField < 0 || lineField < 0 || sourceField < 0 ||
                    fields.Length <= Math.Max(fileField, Math.Max(lineField, sourceField)) ||
                    string.IsNullOrWhiteSpace(fields[fileField]) ||
                    !int.TryParse(fields[lineField], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sourceLine) ||
                    sourceLine <= 0)
                    continue;
                string program = source?.Routine?.Source ?? fields[fileField];
                locations.Add(new(program, address, fields[fileField], sourceLine, fields[sourceField],
                    "executed instruction")
                { MemoryBank = "cpu" });
            }
        }
        return locations;

        void AddBuild(int address, string observation)
        {
            foreach (BuiltFile file in build.Files.Where(file => file.Resident))
            {
                foreach (AssemblySourceMapEntry entry in SourceEntries(file.SourceMap).Where(entry => entry.Length > 0 &&
                    address >= entry.Address && address < (long)entry.Address + entry.Length))
                    locations.Add(new(file.Path, address, entry.File, entry.Line, entry.Source, observation) { MemoryBank = file.MemoryBank });
            }
        }
    }

    /// <summary>Writes a bounded MAME PC trace with stable project source columns when mappings exist.</summary>
    public static string? WriteSourceTrace(ProjectBuildResult build, ExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(result);
        if (result.EmulatorVersion == CpuExecutionEngine.Version) return null;
        const int maximumBytes = 16 * 1024 * 1024;
        string inputPath = Path.Combine(result.ArtifactDirectory, "trace.tsv");
        if (!File.Exists(inputPath)) return null;
        string input = ProgramFiles.DecodeText(ProgramFiles.ReadBytes(inputPath, maximumBytes));
        string[] lines = input.Split('\n');
        if (lines.Length == 0) return null;
        string header = lines[0].TrimEnd('\r');
        string[] headerFields = header.Split('\t');
        int pcField = Array.FindIndex(headerFields, field => field.Equals("PC", StringComparison.OrdinalIgnoreCase));
        if (pcField < 0) return null;

        TraceSource?[] addresses = new TraceSource?[65536];
        foreach (BuiltFile file in build.Files.Where(file => file.Resident))
            foreach (AssemblySourceMapEntry entry in SourceEntries(file.SourceMap)
                .Where(entry => entry.Length > 0 && entry.Address is >= 0 and <= 65535)
                .OrderBy(entry => entry.Address).ThenBy(entry => entry.Length).ThenBy(entry => entry.File, PathComparer)
                .ThenBy(entry => entry.Line))
            {
                int end = (int)Math.Min(65536L, (long)entry.Address + entry.Length);
                for (int address = entry.Address; address < end; address++)
                    addresses[address] ??= new(file.Path, file.MemoryBank, entry.File, entry.Line, entry.Source);
            }
        if (!addresses.Any(source => source is not null)) return null;

        string outputPath = Path.Combine(result.ArtifactDirectory, "trace-source.tsv");
        const string truncated = "# trace-source truncated at the 16 MiB evidence limit\n";
        int truncatedBytes = System.Text.Encoding.UTF8.GetByteCount(truncated);
        bool mapped = false;
        int bytes = 0;
        using (FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (StreamWriter writer = new(output, new System.Text.UTF8Encoding(false)))
        {
            if (!Append(header + "\tprogram\tmemoryBank\tsourceFile\tsourceLine\tsource\n")) return null;
            foreach (string raw in lines.Skip(1))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                string[] fields = line.Split('\t');
                TraceSource? source = null;
                if (fields.Length > pcField && int.TryParse(fields[pcField], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int address) && address is >= 0 and <= 65535)
                    source = addresses[address];
                string suffix = source is null ? "\t\t\t\t"
                    : $"{Escape(source.Program)}\t{Escape(source.MemoryBank)}\t{Escape(source.File)}\t{source.Line}\t{Escape(source.Source)}";
                if (source is not null) mapped = true;
                string annotated = line + "\t" + suffix + "\n";
                if (bytes + System.Text.Encoding.UTF8.GetByteCount(annotated) + truncatedBytes > maximumBytes)
                {
                    Append(truncated);
                    break;
                }
                Append(annotated);
            }

            bool Append(string value)
            {
                int length = System.Text.Encoding.UTF8.GetByteCount(value);
                if (bytes + length > maximumBytes) return false;
                writer.Write(value);
                bytes += length;
                return true;
            }
        }
        if (mapped) return outputPath;
        File.Delete(outputPath);
        return null;

        static string Escape(string? value)
        {
            value ??= "";
            if (value.Length > 4096) value = value[..4096];
            return value.Replace("\t", "\\t", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
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
            AssemblySourceMapEntry? entry = SourceEntries(file.SourceMap)
                .FirstOrDefault(entry => entry.Length > 0 && address >= entry.Address && address < (long)entry.Address + entry.Length);
            return diagnostic with { Symbol = symbol.Program + ":" + symbol.Symbol, File = entry?.File, Line = entry?.Line };
        }
    }

    private static IEnumerable<AssemblySourceMapEntry> SourceEntries(object? sourceMap) => sourceMap switch
    {
        IEnumerable<AssemblySourceMapEntry> entries => entries,
        Cc65SourceMap compiler => compiler.Entries,
        _ => []
    };

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record TraceSource(string Program, string MemoryBank, string? File, int Line, string Source);

    private static DiskException Error(string code, string message) => new("execution." + code, message, 2);
}
