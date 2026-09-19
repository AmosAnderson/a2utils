using System.Globalization;
using System.Text;
using System.Text.Json;
using A2Utils.Core.Assembly;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

/// <summary>Deterministic, bounded 6502/Apple-compatible 65C02 routine execution without an emulator or ROM.</summary>
public static class CpuExecutionEngine
{
    public const string Version = "cpu-1";
    private const int MaximumTraceBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> RegisterNames = new(StringComparer.Ordinal)
    {
        "PC", "A", "X", "Y", "P", "S", "SP"
    };

    public static void Validate(ExecutionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
        {
            if (!condition) throw new DiskException("execution.invalid_spec", message, 2);
        }

        Require(spec.SchemaVersion == 1, "Only execution schemaVersion 1 is supported.");
        Require(spec.Engine == "cpu", "CpuExecutionEngine requires engine cpu.");
        Require(!string.IsNullOrWhiteSpace(spec.Name) && spec.Name.Length <= 128, "name must contain between 1 and 128 characters.");
        Require(spec.Machine is "apple2" or "apple2p" or "apple2e" or "apple2ee" or "apple2c",
            "CPU execution needs machine apple2, apple2p, apple2e, apple2ee, or apple2c to select the processor.");
        Require(spec.ToolchainLock is null, "CPU execution does not accept a MAME environment lock.");
        Require(spec.Routine is not null, "CPU execution requires a routine.");
        Require(spec.Disks is not null && spec.Disks.Count == 0 && string.IsNullOrEmpty(spec.DiskImage)
            && spec.DiskAssertions is not null && spec.DiskAssertions.Count == 0,
            "CPU execution is disk-free; diskImage, disks, and diskAssertions must be empty.");
        Require(spec.SymbolicMemory is not null && spec.SymbolicMemory.Count == 0 && spec.SymbolicUntil is null,
            "CPU execution accepts resolved numeric assertions only.");
        Require(spec.Debug is null && spec.Cycles is null && spec.Until is null,
            "A CPU routine supplies its own return and cycle-budget stop mechanism.");
        Require(spec.StorageProfile == "floppy", "CPU execution does not configure a storage controller.");
        Require(spec.Keys is not null && spec.Keys.Count == 0 && spec.Steps is not null && spec.Steps.Count == 0,
            "CPU execution does not provide keyboard or interactive machine steps.");
        Require(spec.GamePort == "none" && spec.Audio is null && !spec.Screenshot && spec.ScreenshotAssertion is null,
            "CPU execution does not provide game-port, audio, or screenshot devices.");
        Require(!spec.CheckBasicRuntime && spec.TextContains is not null && spec.TextContains.Count == 0
            && spec.TextNotContains is not null && spec.TextNotContains.Count == 0,
            "CPU execution does not provide an Applesoft runtime or text screen.");
        Require(double.IsFinite(spec.EmulatedSeconds) && spec.EmulatedSeconds is > 0 and <= 3600
            && double.IsFinite(spec.HostTimeoutSeconds) && spec.HostTimeoutSeconds is > 0 and <= 3600,
            "Execution deadlines must be finite values from greater than zero through 3600 seconds.");

        ExecutionRoutine routine = spec.Routine!;
        Require(!string.IsNullOrWhiteSpace(routine.Source) && routine.Kind is "asm" or "binary",
            "Routine requires source and kind asm or binary.");
        Require(routine.Origin is >= 0x0800 and < 0xc000 && routine.EntryPoint is null or >= 0x0800 and < 0xc000,
            "Routine origin and entry must be in main RAM $0800..$BFFF.");
        Require(routine.EntrySymbol is null || routine.EntryPoint is null && routine.Kind == "asm"
            && routine.EntrySymbol.Length is > 0 and <= 128, "Use entryPoint or an assembly entrySymbol.");
        Require(double.IsFinite(routine.StartAfterSeconds) && routine.StartAfterSeconds >= 0
            && routine.StartAfterSeconds < spec.EmulatedSeconds,
            "Routine startAfterSeconds must precede the execution deadline; the CPU engine otherwise ignores this machine-start delay.");
        Require(routine.MaxCycles is > 0 and <= 1_000_000_000, "Routine cycle budget must be 1..1000000000.");
        Require(routine.Registers is not null && routine.Registers.Count <= 4
            && routine.Registers.All(register => register is not null && register.Name is "A" or "X" or "Y" or "P"
                && register.Value is >= 0 and <= 255)
            && routine.Registers.Select(register => register.Name).Distinct().Count() == routine.Registers.Count,
            "Routine registers must be distinct A/X/Y/P bytes.");
        Require(routine.Memory is not null && routine.Memory.Count <= 128, "Routine memory must be a bounded array.");
        List<(int Start, int End)> initialRanges = [];
        foreach (MemoryAssertion range in routine.Memory)
        {
            Require(range is not null && range.Bank == "cpu" && range.Hex is not null,
                "Routine initial memory uses the flat CPU address space.");
            int length = MameAdapter.ParseHex(range.Hex).Length;
            long end = (long)range.Address + length;
            Require(length > 0 && range.Address >= 0 && end <= 65536 && (end <= 0x100 || range.Address >= 0x300),
                "CPU routine initial memory must fit the 16-bit address space and avoid the harness stack and return sentinel $0100..$02FF.");
            Require(initialRanges.All(previous => end <= previous.Start || range.Address >= previous.End),
                "Routine initial memory ranges must not overlap.");
            initialRanges.Add((range.Address, (int)end));
        }

        Require(spec.Memory is not null && spec.ObserveMemory is not null && spec.Registers is not null,
            "memory, observeMemory, and registers cannot be null.");
        Require(spec.Memory.Count <= 1024 && spec.ObserveMemory.Count <= 1024 && spec.Registers.Count <= 128,
            "CPU execution accepts at most 1024 memory ranges and 128 register assertions.");
        long observedBytes = 0;
        HashSet<int> starts = [];
        foreach (MemoryAssertion assertion in spec.Memory)
        {
            Require(assertion is not null && assertion.Bank == "cpu" && assertion.Hex is not null,
                "CPU memory assertions use bank cpu and require hex bytes.");
            int length = MameAdapter.ParseHex(assertion.Hex).Length;
            Require(length > 0 && assertion.Address >= 0 && (long)assertion.Address + length <= 65536,
                "CPU memory assertion ranges must fit the 16-bit address space.");
            Require(starts.Add(assertion.Address), "CPU memory assertion start addresses must be unique.");
            observedBytes += length;
        }
        foreach (MemoryCapture range in spec.ObserveMemory)
        {
            Require(range is not null && range.Bank == "cpu" && range.Length > 0 && range.Address >= 0
                && (long)range.Address + range.Length <= 65536,
                "CPU memory captures use bank cpu and must fit the 16-bit address space.");
            Require(starts.Add(range.Address), "CPU memory assertion and capture start addresses must be unique.");
            observedBytes += range.Length;
        }
        Require(observedBytes <= 65536, "CPU memory assertions and captures may observe at most 65536 bytes.");
        Require(spec.Registers.All(register => register is not null && RegisterNames.Contains(register.Name)
            && register.Value is >= 0 and <= 65535),
            "CPU register assertions use PC, A, X, Y, P, S, or SP and values 0..65535.");
        Require(spec.Registers.Select(register => register.Name).Distinct().Count() == spec.Registers.Count,
            "CPU register assertion names must be unique.");
    }

    internal static CpuKind ProcessorFor(string machine)
        => machine is "apple2ee" or "apple2c" ? CpuKind.Apple65C02 : CpuKind.Mos6502;

    internal static async Task<ExecutionResult> RunAsync(ExecutionSpec spec, string artifactDirectory,
        CancellationToken cancellationToken)
    {
        Validate(spec);
        cancellationToken.ThrowIfCancellationRequested();
        string artifacts = Path.GetFullPath(artifactDirectory);
        ImageTransactions.ValidatePath(artifacts);
        if (Directory.Exists(artifacts) || File.Exists(artifacts))
            throw new DiskException("execution.artifacts_exist", "Use a new artifact directory for each execution.", 2);
        Directory.CreateDirectory(artifacts);

        ExecutionObservation? observation = null;
        PreparedRoutine? prepared = null;
        List<ProgramDiagnostic> diagnostics = [];
        string reason = "cpu_fault";
        ExecutionEnvironmentEvidence? environment = null;
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(spec.HostTimeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdog.Token);
        try
        {
            environment = ExecutionRunner.CaptureEnvironment(spec, artifacts, linked.Token);
            prepared = RoutineHarness.Prepare(spec, artifacts, linked.Token);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "spec.json"),
                JsonSerializer.Serialize(spec, ExecutionSpec.JsonOptions), linked.Token);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "engine.json"), JsonSerializer.Serialize(new
            {
                engine = spec.Engine,
                version = Version,
                cpu = ProcessorFor(spec.Machine) == CpuKind.Apple65C02 ? "65c02" : "6502",
                flatMemory = true,
                devices = Array.Empty<string>()
            }, ExecutionSpec.JsonOptions), linked.Token);

            string? tracePath = spec.Trace ? Path.Combine(artifacts, "trace.tsv") : null;
            CpuRunOutcome outcome = await Task.Run(() => Cpu6502.Run(spec, prepared, tracePath, linked.Token), linked.Token);
            ExecutionRunner.ValidateExecutionEnvironment(spec, environment, linked.Token);
            RoutineHarness.ValidateInputs(prepared, linked.Token);
            reason = outcome.StopReason;
            observation = CreateObservation(spec, prepared, outcome);
            if (outcome.Fault is { } fault)
                diagnostics.Add(new("execution.cpu_fault", "error", fault.Message,
                    Symbol: "$" + fault.ProgramCounter.ToString("X4", CultureInfo.InvariantCulture),
                    Actual: "$" + fault.Opcode.ToString("X2", CultureInfo.InvariantCulture)));
            diagnostics.AddRange(ExecutionRunner.Evaluate(spec, observation));
            await File.WriteAllTextAsync(Path.Combine(artifacts, "observations.tsv"),
                ObservationText(observation), linked.Token);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "screen.txt"), "", linked.Token);
            ExecutionRunner.ValidateExecutionEnvironment(spec, environment, linked.Token);
        }
        catch (OperationCanceledException)
        {
            reason = cancellationToken.IsCancellationRequested ? "cancelled" : "host_timeout";
            diagnostics.Add(new("execution." + reason, "error", reason == "cancelled"
                ? "Execution was cancelled."
                : "CPU execution exceeded hostTimeoutSeconds."));
        }
        catch (DiskException exception)
        {
            reason = "cpu_fault";
            diagnostics.AddRange(exception.Diagnostics.Count > 0
                ? exception.Diagnostics : [new(exception.Code, "error", exception.Message)]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            reason = "cpu_fault";
            diagnostics.Add(new("execution.cpu_fault", "error", exception.Message));
        }

        bool passed = observation is not null && diagnostics.All(diagnostic => diagnostic.Severity != "error");
        ExecutionResult result = new(1, spec.Name, passed, reason, Version, observation?.EmulatedSeconds,
            observation?.Registers ?? new Dictionary<string, long>(), observation?.Memory ?? new Dictionary<int, string>(),
            observation?.ScreenText, null, artifacts,
            Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray(), diagnostics)
        {
            BankMemory = observation?.BankMemory ?? [],
            Cycles = observation?.Cycles,
            Environment = environment
        };
        string resultPath = Path.Combine(artifacts, "result.json");
        result = result with { Artifacts = result.Artifacts.Append(resultPath).ToArray() };
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, ExecutionSpec.JsonOptions), CancellationToken.None);
        return result;
    }

    private static ExecutionObservation CreateObservation(ExecutionSpec spec, PreparedRoutine prepared, CpuRunOutcome outcome)
    {
        Dictionary<int, string> memory = [];
        foreach (MemoryCapture range in spec.Memory
            .Select(assertion => new MemoryCapture(assertion.Address, MameAdapter.ParseHex(assertion.Hex).Length))
            .Concat(spec.ObserveMemory))
            memory[range.Address] = Convert.ToHexString(outcome.Memory.AsSpan(range.Address, range.Length));
        return new(outcome.StopReason, 0, outcome.Registers, memory, "")
        {
            Cycles = new(prepared.EntryPoint, RoutineHarness.ReturnAddress, outcome.Cycles, outcome.Returned),
            BankMemory = memory.Select(pair => new ExecutionMemory("cpu", pair.Key, pair.Value)).ToArray()
        };
    }

    private static string ObservationText(ExecutionObservation observation)
    {
        StringBuilder text = new("A2EXEC1\n");
        text.Append("STOP\t").Append(observation.StopReason).Append('\n');
        text.Append("TIME\t0\n");
        foreach (var register in observation.Registers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            text.Append("REG\t").Append(register.Key).Append('\t').Append(register.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var memory in observation.Memory.OrderBy(pair => pair.Key))
            text.Append("MEM\t").Append(memory.Key.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(memory.Value).Append('\n');
        if (observation.Cycles is { } cycles)
            text.Append("CYCLES\t").Append(cycles.StartAddress).Append('\t').Append(cycles.EndAddress).Append('\t')
                .Append(cycles.Cycles).Append('\t').Append(cycles.Returned ? '1' : '0').Append('\n');
        return text.Append("END\n").ToString();
    }

    internal sealed record CpuFault(int ProgramCounter, byte Opcode, string Message);
    internal sealed record CpuRunOutcome(string StopReason, long Cycles, bool Returned, byte[] Memory,
        IReadOnlyDictionary<string, long> Registers, CpuFault? Fault);

    private sealed class Cpu6502 : IDisposable
    {
        private const byte Carry = 0x01;
        private const byte Zero = 0x02;
        private const byte InterruptDisable = 0x04;
        private const byte Decimal = 0x08;
        private const byte Break = 0x10;
        private const byte Unused = 0x20;
        private const byte Overflow = 0x40;
        private const byte Negative = 0x80;
        private static readonly HashSet<string> ImplementedMnemonics = new(StringComparer.Ordinal)
        {
            "ADC", "AND", "ASL", "BCC", "BCS", "BEQ", "BIT", "BMI", "BNE", "BPL", "BRA", "BRK", "BVC", "BVS",
            "CLC", "CLD", "CLI", "CLV", "CMP", "CPX", "CPY", "DEC", "DEX", "DEY", "EOR", "INC", "INX", "INY",
            "JMP", "JSR", "LDA", "LDX", "LDY", "LSR", "NOP", "ORA", "PHA", "PHP", "PHX", "PHY", "PLA", "PLP",
            "PLX", "PLY", "ROL", "ROR", "RTI", "RTS", "SBC", "SEC", "SED", "SEI", "STA", "STX", "STY", "STZ",
            "TAX", "TAY", "TRB", "TSB", "TSX", "TXA", "TXS", "TYA"
        };

        private readonly CpuKind _cpu;
        private readonly byte[] _memory = new byte[65536];
        private readonly IReadOnlyDictionary<byte, Instruction> _instructions;
        private readonly CpuTraceWriter? _trace;
        private readonly CancellationToken _cancellationToken;
        private readonly List<string> _accesses = [];
        private readonly List<byte> _instructionBytes = [];
        private byte _a;
        private byte _x;
        private byte _y;
        private byte _p = 0x24;
        private byte _sp = 0xfd;
        private ushort _pc;
        private long _cycles;

        private Cpu6502(ExecutionSpec spec, PreparedRoutine routine, string? tracePath, CancellationToken cancellationToken)
        {
            _cpu = ProcessorFor(spec.Machine);
            _instructions = InstructionSet.GetInstructions(_cpu).ToDictionary(instruction => instruction.Opcode);
            if (_instructions.Values.Any(instruction => !ImplementedMnemonics.Contains(instruction.Mnemonic)))
                throw new InvalidOperationException("The CPU engine is missing a documented instruction implementation.");
            routine.Bytes.CopyTo(_memory, routine.Origin);
            foreach (MemoryAssertion range in spec.Routine!.Memory)
                MameAdapter.ParseHex(range.Hex).CopyTo(_memory, range.Address);
            foreach (RegisterAssertion register in spec.Routine.Registers)
            {
                byte value = (byte)register.Value;
                if (register.Name == "A") _a = value;
                else if (register.Name == "X") _x = value;
                else if (register.Name == "Y") _y = value;
                else if (register.Name == "P") _p = (byte)((value & ~Break) | Unused);
            }
            _memory[0x01fe] = 0xfe;
            _memory[0x01ff] = 0x02;
            _pc = (ushort)routine.EntryPoint;
            _cancellationToken = cancellationToken;
            _trace = tracePath is null ? null : new CpuTraceWriter(tracePath, MaximumTraceBytes, routine.SourceMap);
        }

        public static CpuRunOutcome Run(ExecutionSpec spec, PreparedRoutine routine, string? tracePath,
            CancellationToken cancellationToken)
        {
            using Cpu6502 cpu = new(spec, routine, tracePath, cancellationToken);
            return cpu.Execute(spec.Routine!.MaxCycles);
        }

        private CpuRunOutcome Execute(long maximumCycles)
        {
            CpuFault? fault = null;
            bool returned = false;
            string reason;
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (_pc == RoutineHarness.ReturnAddress)
                {
                    returned = true;
                    reason = "routine_return";
                    break;
                }
                ushort instructionPc = _pc;
                byte beforeA = _a, beforeX = _x, beforeY = _y, beforeP = _p, beforeSp = _sp;
                long beforeCycles = _cycles;
                _accesses.Clear();
                _instructionBytes.Clear();
                byte opcode = Fetch();
                if (!_instructions.TryGetValue(opcode, out Instruction? instruction))
                {
                    fault = new(instructionPc, opcode,
                        $"Opcode ${opcode:X2} at ${instructionPc:X4} is not documented for {_cpu}.");
                    _trace?.Write(beforeCycles, _cycles, instructionPc, _instructionBytes, "ILLEGAL",
                        beforeA, beforeX, beforeY, beforeP, beforeSp, _a, _x, _y, _p, _sp, _accesses);
                    reason = "cpu_fault";
                    break;
                }
                int instructionCycles;
                try { instructionCycles = ExecuteInstruction(instruction); }
                catch (CpuExecutionFault exception)
                {
                    fault = new(instructionPc, opcode, exception.Message);
                    reason = "cpu_fault";
                    break;
                }
                _cycles += instructionCycles;
                _trace?.Write(beforeCycles, _cycles, instructionPc, _instructionBytes, instruction.Mnemonic,
                    beforeA, beforeX, beforeY, beforeP, beforeSp, _a, _x, _y, _p, _sp, _accesses);
                if (_cycles > maximumCycles)
                {
                    reason = "cycle_limit";
                    break;
                }
            }
            return new(reason, _cycles, returned, _memory,
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["PC"] = _pc,
                    ["A"] = _a,
                    ["X"] = _x,
                    ["Y"] = _y,
                    ["P"] = _p,
                    ["S"] = _sp,
                    ["SP"] = 0x100 | _sp
                }, fault);
        }

        private int ExecuteInstruction(Instruction instruction)
        {
            bool crossed = false;
            switch (instruction.Mnemonic)
            {
                case "LDA": _a = ReadOperand(instruction.Mode, out crossed); SetNz(_a); break;
                case "LDX": _x = ReadOperand(instruction.Mode, out crossed); SetNz(_x); break;
                case "LDY": _y = ReadOperand(instruction.Mode, out crossed); SetNz(_y); break;
                case "STA": Write(Address(instruction.Mode, out _), _a); break;
                case "STX": Write(Address(instruction.Mode, out _), _x); break;
                case "STY": Write(Address(instruction.Mode, out _), _y); break;
                case "STZ": Write(Address(instruction.Mode, out _), 0); break;
                case "ORA": _a |= ReadOperand(instruction.Mode, out crossed); SetNz(_a); break;
                case "AND": _a &= ReadOperand(instruction.Mode, out crossed); SetNz(_a); break;
                case "EOR": _a ^= ReadOperand(instruction.Mode, out crossed); SetNz(_a); break;
                case "ADC": Adc(ReadOperand(instruction.Mode, out crossed)); break;
                case "SBC": Sbc(ReadOperand(instruction.Mode, out crossed)); break;
                case "CMP": Compare(_a, ReadOperand(instruction.Mode, out crossed)); break;
                case "CPX": Compare(_x, ReadOperand(instruction.Mode, out crossed)); break;
                case "CPY": Compare(_y, ReadOperand(instruction.Mode, out crossed)); break;
                case "BIT": Bit(ReadOperand(instruction.Mode, out crossed), instruction.Mode == AddressingMode.Immediate); break;
                case "ASL": Modify(instruction.Mode, value => { Set(Carry, (value & 0x80) != 0); return (byte)(value << 1); }, out crossed); break;
                case "LSR": Modify(instruction.Mode, value => { Set(Carry, (value & 1) != 0); return (byte)(value >> 1); }, out crossed); break;
                case "ROL":
                    Modify(instruction.Mode, value =>
                    {
                        bool oldCarry = IsSet(Carry); Set(Carry, (value & 0x80) != 0); return (byte)((value << 1) | (oldCarry ? 1 : 0));
                    }, out crossed);
                    break;
                case "ROR":
                    Modify(instruction.Mode, value =>
                    {
                        bool oldCarry = IsSet(Carry); Set(Carry, (value & 1) != 0); return (byte)((value >> 1) | (oldCarry ? 0x80 : 0));
                    }, out crossed);
                    break;
                case "INC": Modify(instruction.Mode, value => (byte)(value + 1), out crossed); break;
                case "DEC": Modify(instruction.Mode, value => (byte)(value - 1), out crossed); break;
                case "TSB": TestAndModify(instruction.Mode, set: true); break;
                case "TRB": TestAndModify(instruction.Mode, set: false); break;
                case "TAX": _x = _a; SetNz(_x); break;
                case "TAY": _y = _a; SetNz(_y); break;
                case "TXA": _a = _x; SetNz(_a); break;
                case "TYA": _a = _y; SetNz(_a); break;
                case "TSX": _x = _sp; SetNz(_x); break;
                case "TXS": _sp = _x; break;
                case "INX": _x++; SetNz(_x); break;
                case "INY": _y++; SetNz(_y); break;
                case "DEX": _x--; SetNz(_x); break;
                case "DEY": _y--; SetNz(_y); break;
                case "PHA": Push(_a); break;
                case "PHP": Push((byte)(_p | Break | Unused)); break;
                case "PHX": Push(_x); break;
                case "PHY": Push(_y); break;
                case "PLA": _a = Pop(); SetNz(_a); break;
                case "PLP": _p = (byte)((Pop() & ~Break) | Unused); break;
                case "PLX": _x = Pop(); SetNz(_x); break;
                case "PLY": _y = Pop(); SetNz(_y); break;
                case "CLC": Set(Carry, false); break;
                case "SEC": Set(Carry, true); break;
                case "CLI": Set(InterruptDisable, false); break;
                case "SEI": Set(InterruptDisable, true); break;
                case "CLV": Set(Overflow, false); break;
                case "CLD": Set(Decimal, false); break;
                case "SED": Set(Decimal, true); break;
                case "JMP": _pc = Address(instruction.Mode, out _); break;
                case "JSR":
                    {
                        ushort target = FetchWord();
                        ushort returnAddress = (ushort)(_pc - 1);
                        Push((byte)(returnAddress >> 8)); Push((byte)returnAddress); _pc = target;
                        break;
                    }
                case "RTS": _pc = (ushort)((Pop() | Pop() << 8) + 1); break;
                case "RTI": _p = (byte)((Pop() & ~Break) | Unused); _pc = (ushort)(Pop() | Pop() << 8); break;
                case "BRK":
                    {
                        _ = Read(_pc); _pc++;
                        Push((byte)(_pc >> 8)); Push((byte)_pc); Push((byte)(_p | Break | Unused));
                        Set(InterruptDisable, true); if (_cpu == CpuKind.Apple65C02) Set(Decimal, false);
                        _pc = (ushort)(Read(0xfffe) | Read(0xffff) << 8);
                        break;
                    }
                case "BPL": return Branch(!IsSet(Negative));
                case "BMI": return Branch(IsSet(Negative));
                case "BVC": return Branch(!IsSet(Overflow));
                case "BVS": return Branch(IsSet(Overflow));
                case "BCC": return Branch(!IsSet(Carry));
                case "BCS": return Branch(IsSet(Carry));
                case "BNE": return Branch(!IsSet(Zero));
                case "BEQ": return Branch(IsSet(Zero));
                case "BRA": return Branch(true);
                case "NOP": break;
                default: throw new CpuExecutionFault("Instruction semantics are unavailable: " + instruction.Mnemonic);
            }
            return Cycles(instruction, crossed);
        }

        private byte ReadOperand(AddressingMode mode, out bool crossed)
        {
            crossed = false;
            if (mode == AddressingMode.Immediate) return Fetch();
            return Read(Address(mode, out crossed));
        }

        private ushort Address(AddressingMode mode, out bool crossed)
        {
            crossed = false;
            switch (mode)
            {
                case AddressingMode.ZeroPage: return Fetch();
                case AddressingMode.ZeroPageX: return (byte)(Fetch() + _x);
                case AddressingMode.ZeroPageY: return (byte)(Fetch() + _y);
                case AddressingMode.Absolute: return FetchWord();
                case AddressingMode.AbsoluteX:
                    {
                        ushort basis = FetchWord(), result = (ushort)(basis + _x); crossed = PageCrossed(basis, result); return result;
                    }
                case AddressingMode.AbsoluteY:
                    {
                        ushort basis = FetchWord(), result = (ushort)(basis + _y); crossed = PageCrossed(basis, result); return result;
                    }
                case AddressingMode.IndexedIndirect:
                    {
                        byte pointer = (byte)(Fetch() + _x); return ReadZeroPageWord(pointer);
                    }
                case AddressingMode.IndirectIndexed:
                    {
                        ushort basis = ReadZeroPageWord(Fetch()), result = (ushort)(basis + _y); crossed = PageCrossed(basis, result); return result;
                    }
                case AddressingMode.ZeroPageIndirect: return ReadZeroPageWord(Fetch());
                case AddressingMode.Indirect:
                    {
                        ushort pointer = FetchWord();
                        byte low = Read(pointer);
                        ushort highAddress = _cpu == CpuKind.Mos6502
                            ? (ushort)((pointer & 0xff00) | (byte)(pointer + 1)) : (ushort)(pointer + 1);
                        return (ushort)(low | Read(highAddress) << 8);
                    }
                case AddressingMode.AbsoluteIndexedIndirect:
                    {
                        ushort pointer = (ushort)(FetchWord() + _x);
                        return (ushort)(Read(pointer) | Read((ushort)(pointer + 1)) << 8);
                    }
                default: throw new CpuExecutionFault("Addressing mode is unavailable: " + mode);
            }
        }

        private void Modify(AddressingMode mode, Func<byte, byte> operation, out bool crossed)
        {
            crossed = false;
            if (mode == AddressingMode.Accumulator)
            {
                _a = operation(_a); SetNz(_a); return;
            }
            ushort address = Address(mode, out crossed);
            byte result = operation(Read(address));
            Write(address, result); SetNz(result);
        }

        private void TestAndModify(AddressingMode mode, bool set)
        {
            ushort address = Address(mode, out _);
            byte value = Read(address);
            Set(Zero, (_a & value) == 0);
            Write(address, set ? (byte)(value | _a) : (byte)(value & ~_a));
        }

        private void Bit(byte value, bool immediate)
        {
            Set(Zero, (_a & value) == 0);
            if (!immediate)
            {
                Set(Negative, (value & Negative) != 0);
                Set(Overflow, (value & Overflow) != 0);
            }
        }

        private void Compare(byte register, byte value)
        {
            int result = register - value;
            Set(Carry, result >= 0); SetNz((byte)result);
        }

        private void Adc(byte value)
        {
            int carry = IsSet(Carry) ? 1 : 0;
            int binary = _a + value + carry;
            Set(Overflow, (~(_a ^ value) & (_a ^ binary) & 0x80) != 0);
            if (!IsSet(Decimal))
            {
                Set(Carry, binary > 0xff); _a = (byte)binary; SetNz(_a); return;
            }
            int low = (_a & 0x0f) + (value & 0x0f) + carry;
            if (low > 9) low = ((low + 6) & 0x0f) + 0x10;
            int adjusted = (_a & 0xf0) + (value & 0xf0) + low;
            // Decimal carry from the low digit affects N and V before the high digit is corrected.
            Set(Overflow, (~(_a ^ value) & (_a ^ adjusted) & 0x80) != 0);
            if (_cpu == CpuKind.Mos6502)
            {
                Set(Zero, (byte)binary == 0);
                Set(Negative, (adjusted & 0x80) != 0);
            }
            if (adjusted >= 0xa0) adjusted += 0x60;
            Set(Carry, adjusted > 0xff);
            _a = (byte)adjusted;
            if (_cpu == CpuKind.Apple65C02) SetNz(_a);
        }

        private void Sbc(byte value)
        {
            int borrow = IsSet(Carry) ? 0 : 1;
            int binary = _a - value - borrow;
            Set(Overflow, ((_a ^ binary) & (_a ^ value) & 0x80) != 0);
            Set(Carry, binary >= 0);
            if (!IsSet(Decimal))
            {
                _a = (byte)binary; SetNz(_a); return;
            }
            int low = (_a & 0x0f) - (value & 0x0f) - borrow;
            if (_cpu == CpuKind.Apple65C02)
            {
                // CMOS decimal correction can borrow between digits, including for non-BCD operands.
                int adjusted = binary - (low < 0 ? 6 : 0) - (binary < 0 ? 0x60 : 0);
                _a = (byte)adjusted;
                SetNz(_a);
                return;
            }
            int high = (_a >> 4) - (value >> 4);
            if (low < 0) { low -= 6; high--; }
            if (high < 0) high -= 6;
            _a = (byte)(((high << 4) & 0xf0) | (low & 0x0f));
            SetNz(_cpu == CpuKind.Mos6502 ? (byte)binary : _a);
        }

        private int Branch(bool condition)
        {
            sbyte displacement = unchecked((sbyte)Fetch());
            if (!condition) return 2;
            ushort before = _pc;
            _pc = (ushort)(_pc + displacement);
            return 3 + (PageCrossed(before, _pc) ? 1 : 0);
        }

        private int Cycles(Instruction instruction, bool crossed)
        {
            string mnemonic = instruction.Mnemonic;
            AddressingMode mode = instruction.Mode;
            if (mnemonic == "BRK") return 7;
            if (mnemonic == "JSR") return 6;
            if (mnemonic == "RTS" || mnemonic == "RTI") return 6;
            if (mnemonic == "JMP") return mode switch
            {
                AddressingMode.Absolute => 3,
                AddressingMode.Indirect => _cpu == CpuKind.Mos6502 ? 5 : 6,
                AddressingMode.AbsoluteIndexedIndirect => 6,
                _ => throw new CpuExecutionFault("Invalid JMP mode.")
            };
            if (mnemonic is "PHA" or "PHP" or "PHX" or "PHY") return 3;
            if (mnemonic is "PLA" or "PLP" or "PLX" or "PLY") return 4;
            if (mnemonic is "CLC" or "CLD" or "CLI" or "CLV" or "SEC" or "SED" or "SEI"
                or "DEX" or "DEY" or "INX" or "INY" or "TAX" or "TAY" or "TSX" or "TXA" or "TXS" or "TYA" or "NOP"
                || mode == AddressingMode.Accumulator) return 2;
            if (mnemonic is "TSB" or "TRB") return mode == AddressingMode.ZeroPage ? 5 : 6;
            if (mnemonic == "BIT") return mode switch
            {
                AddressingMode.Immediate => 2,
                AddressingMode.ZeroPage => 3,
                AddressingMode.ZeroPageX => 4,
                AddressingMode.Absolute => 4,
                AddressingMode.AbsoluteX => 4 + (crossed ? 1 : 0),
                _ => throw new CpuExecutionFault("Invalid BIT mode.")
            };
            bool store = mnemonic is "STA" or "STX" or "STY" or "STZ";
            bool modify = mnemonic is "ASL" or "LSR" or "ROL" or "ROR" or "INC" or "DEC";
            if (modify) return mode switch
            {
                AddressingMode.ZeroPage => 5,
                AddressingMode.ZeroPageX => 6,
                AddressingMode.Absolute => 6,
                AddressingMode.AbsoluteX => _cpu == CpuKind.Mos6502 || crossed || mnemonic is "INC" or "DEC" ? 7 : 6,
                _ => throw new CpuExecutionFault("Invalid read-modify-write mode.")
            };
            int cycles = mode switch
            {
                AddressingMode.Immediate => 2,
                AddressingMode.ZeroPage => 3,
                AddressingMode.ZeroPageX or AddressingMode.ZeroPageY => 4,
                AddressingMode.Absolute => 4,
                AddressingMode.AbsoluteX or AddressingMode.AbsoluteY => store ? 5 : 4 + (crossed ? 1 : 0),
                AddressingMode.IndexedIndirect => 6,
                AddressingMode.IndirectIndexed => store ? 6 : 5 + (crossed ? 1 : 0),
                AddressingMode.ZeroPageIndirect => 5,
                _ => throw new CpuExecutionFault("Invalid operand mode for " + mnemonic + ".")
            };
            if (_cpu == CpuKind.Apple65C02 && IsSet(Decimal) && mnemonic is "ADC" or "SBC") cycles++;
            return cycles;
        }

        private byte Fetch()
        {
            byte value = Read(_pc++); _instructionBytes.Add(value); return value;
        }

        private ushort FetchWord()
        {
            byte low = Fetch(), high = Fetch(); return (ushort)(low | high << 8);
        }

        private ushort ReadZeroPageWord(byte address)
            => (ushort)(Read(address) | Read((byte)(address + 1)) << 8);

        private byte Read(ushort address)
        {
            byte value = _memory[address]; _accesses.Add($"R:${address:X4}=${value:X2}"); return value;
        }

        private void Write(ushort address, byte value)
        {
            _memory[address] = value; _accesses.Add($"W:${address:X4}=${value:X2}");
        }

        private void Push(byte value) { Write((ushort)(0x100 | _sp), value); _sp--; }
        private byte Pop() { _sp++; return Read((ushort)(0x100 | _sp)); }
        private bool IsSet(byte flag) => (_p & flag) != 0;
        private void Set(byte flag, bool value) => _p = value ? (byte)(_p | flag | Unused) : (byte)((_p & ~flag) | Unused);
        private void SetNz(byte value) { Set(Zero, value == 0); Set(Negative, (value & 0x80) != 0); }
        private static bool PageCrossed(ushort first, ushort second) => (first & 0xff00) != (second & 0xff00);
        public void Dispose() => _trace?.Dispose();
    }

    private sealed class CpuTraceWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly int _maximumBytes;
        private readonly AssemblySourceMapEntry?[] _sourceMap = new AssemblySourceMapEntry?[65536];
        private int _bytes;
        private bool _truncated;

        public CpuTraceWriter(string path, int maximumBytes, IReadOnlyList<AssemblySourceMapEntry> sourceMap)
        {
            _writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _maximumBytes = maximumBytes;
            foreach (AssemblySourceMapEntry entry in sourceMap.Where(entry => entry.Length > 0 && entry.Address is >= 0 and <= 65535)
                .OrderBy(entry => entry.Address).ThenBy(entry => entry.Length).ThenBy(entry => entry.File, StringComparer.Ordinal)
                .ThenBy(entry => entry.Line))
            {
                int end = (int)Math.Min(65536L, (long)entry.Address + entry.Length);
                for (int address = entry.Address; address < end; address++) _sourceMap[address] ??= entry;
            }
            Append("cycleStart\tcycleEnd\tpc\topcode\tmnemonic\taBefore\txBefore\tyBefore\tpBefore\tspBefore\taAfter\txAfter\tyAfter\tpAfter\tspAfter\taccesses\tsourceFile\tsourceLine\tsource\n");
        }

        public void Write(long start, long end, ushort pc, IReadOnlyList<byte> bytes, string mnemonic,
            byte beforeA, byte beforeX, byte beforeY, byte beforeP, byte beforeSp,
            byte afterA, byte afterX, byte afterY, byte afterP, byte afterSp, IReadOnlyList<string> accesses)
        {
            if (_truncated) return;
            AssemblySourceMapEntry? source = _sourceMap[pc];
            string line = $"{start}\t{end}\t{pc:X4}\t{Convert.ToHexString(bytes.ToArray())}\t{mnemonic}\t" +
                $"{beforeA:X2}\t{beforeX:X2}\t{beforeY:X2}\t{beforeP:X2}\t{beforeSp:X2}\t" +
                $"{afterA:X2}\t{afterX:X2}\t{afterY:X2}\t{afterP:X2}\t{afterSp:X2}\t{string.Join(',', accesses)}\t" +
                $"{Escape(source?.File)}\t{source?.Line.ToString(CultureInfo.InvariantCulture) ?? ""}\t{Escape(source?.Source)}\n";
            const string marker = "# trace truncated at the 16 MiB evidence limit\n";
            int markerBytes = Encoding.UTF8.GetByteCount(marker);
            if (_bytes + Encoding.UTF8.GetByteCount(line) + markerBytes > _maximumBytes)
            {
                _truncated = true;
                Append(marker);
                return;
            }
            Append(line);
        }

        private static string Escape(string? value)
        {
            value ??= "";
            if (value.Length > 4096) value = value[..4096];
            return value.Replace("\t", "\\t", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        }

        private void Append(string text) { _writer.Write(text); _bytes += Encoding.UTF8.GetByteCount(text); }
        public void Dispose() => _writer.Dispose();
    }

    private sealed class CpuExecutionFault(string message) : Exception(message);
}
