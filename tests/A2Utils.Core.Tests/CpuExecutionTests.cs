using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class CpuExecutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-cpu-" + Guid.NewGuid().ToString("N"));

    public CpuExecutionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Run_6502RoutineWithoutMame_ReturnsTraceCyclesRegistersAndMemory()
    {
        string source = At("routine.asm");
        File.WriteAllText(source, "lda #$2a\nsta $0300\nrts\n");
        ExecutionSpec spec = CpuSpec(source) with
        {
            Trace = true,
            Memory = [new(0x0300, "2A")],
            Registers = [new("A", 0x2a), new("PC", RoutineHarness.ReturnAddress), new("SP", 0x01ff)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal("routine_return", result.StopReason);
        Assert.Equal(CpuExecutionEngine.Version, result.EmulatorVersion);
        Assert.Equal(12, result.Cycles!.Cycles);
        Assert.Equal("2A", result.Memory[0x0300]);
        Assert.Null(result.InputSha256);
        Assert.Empty(result.Disks);
        string trace = File.ReadAllText(At("run/trace.tsv"));
        Assert.Contains("cycleStart\tcycleEnd\tpc\topcode", trace);
        Assert.Contains("accesses\tsourceFile\tsourceLine\tsource", trace);
        Assert.Contains("A9", trace);
        Assert.Contains("W:$0300=$2A", trace);
        Assert.Contains(source + "\t1\tlda #$2a", trace);
        using JsonDocument resultJson = JsonDocument.Parse(File.ReadAllText(At("run/result.json")));
        Assert.Equal("cpu-1", resultJson.RootElement.GetProperty("emulatorVersion").GetString());
    }

    [Fact]
    public async Task Run_Apple65C02Instructions_ExecuteWithDocumentedState()
    {
        string source = At("cmos.asm");
        File.WriteAllText(source, """
            lda #$ff
            sta $0300
            stz $0300
            ldx #$2a
            phx
            ldx #0
            plx
            stx $0301
            bra done
            .byte $00
            done: rts
            """);
        ExecutionSpec spec = CpuSpec(source) with
        {
            Machine = "apple2ee",
            Memory = [new(0x0300, "002A")],
            Registers = [new("X", 0x2a)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("cmos-run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(34, result.Cycles!.Cycles);
    }

    [Fact]
    public async Task Run_Apple65C02BitAbsoluteX_PageCrossingAddsCycle()
    {
        string source = At("bit-indexed.asm");
        File.WriteAllText(source, "ldx #1\nlda #$ff\nbit $30fe,x\nbit $30ff,x\nrts\n");
        ExecutionSpec baseSpec = CpuSpec(source);
        ExecutionSpec spec = baseSpec with
        {
            Machine = "apple2ee",
            Routine = baseSpec.Routine! with { Memory = [new(0x30ff, "FFFF")] }
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("bit-indexed-run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(19, result.Cycles!.Cycles);
    }

    [Fact]
    public async Task Run_DecimalArithmeticAndNestedSubroutine_PreservesDeterministicFlagsAndStack()
    {
        string source = At("decimal.asm");
        File.WriteAllText(source, """
            sed
            sec
            lda #$50
            sbc #$01
            jsr add_one
            sta $0300
            rts
            add_one:
            clc
            adc #$01
            rts
            """);
        ExecutionSpec spec = CpuSpec(source) with { Memory = [new(0x0300, "50")] };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("decimal-run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(0x50, result.Registers["A"]);
        Assert.Equal(0x01ff, result.Registers["SP"]);
    }

    [Theory]
    [InlineData("apple2e", 0x79, 0x00, 1, 0x80, 0xec)]
    [InlineData("apple2ee", 0x79, 0x00, 1, 0x80, 0xec)]
    [InlineData("apple2e", 0x99, 0x00, 1, 0x00, 0xad)]
    [InlineData("apple2ee", 0x99, 0x00, 1, 0x00, 0x2f)]
    [InlineData("apple2e", 0x50, 0x50, 0, 0x00, 0xed)]
    [InlineData("apple2ee", 0x50, 0x50, 0, 0x00, 0x6f)]
    [InlineData("apple2e", 0x0f, 0x0f, 0, 0x14, 0x2c)]
    [InlineData("apple2ee", 0x0f, 0x0f, 0, 0x14, 0x2c)]
    public async Task Run_DecimalAddition_UsesProcessorDigitCarryAndFlags(string machine,
        int accumulator, int operand, int carry, int expectedAccumulator, int expectedStatus)
    {
        string source = At("decimal-add.asm");
        File.WriteAllText(source, $"adc #${operand:X2}\nrts\n");
        ExecutionSpec spec = CpuSpec(source) with
        {
            Machine = machine,
            Routine = new() { Source = source, Registers = [new("A", accumulator), new("P", 0x2c | carry)] },
            Registers = [new("A", expectedAccumulator), new("P", expectedStatus)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("decimal-add-run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(machine == "apple2ee" ? 9 : 8, result.Cycles!.Cycles);
    }

    [Theory]
    [InlineData("apple2e", 0x9b)]
    [InlineData("apple2ee", 0x8b)]
    public async Task Run_DecimalSubtractionWithNonBcdOperand_UsesProcessorBorrowSemantics(
        string machine, int expectedAccumulator)
    {
        string source = At("decimal-subtract.asm");
        File.WriteAllText(source, "sbc #$0f\nrts\n");
        ExecutionSpec spec = CpuSpec(source) with
        {
            Machine = machine,
            Routine = new() { Source = source, Registers = [new("A", 0), new("P", 0x2d)] },
            Registers = [new("A", expectedAccumulator), new("P", 0xac)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("decimal-subtract-run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Theory]
    [InlineData("asl", 21)]
    [InlineData("lsr", 21)]
    [InlineData("rol", 21)]
    [InlineData("ror", 21)]
    [InlineData("inc", 22)]
    [InlineData("dec", 22)]
    public async Task Run_Apple65C02IndexedMemoryModification_UsesInstructionAndPageCrossingTiming(
        string mnemonic, int expectedCycles)
    {
        string source = At("modify-indexed.asm");
        File.WriteAllText(source, $"ldx #1\n{mnemonic} $30fe,x\n{mnemonic} $30ff,x\nrts\n");

        ExecutionResult result = await ExecutionRunner.RunAsync(CpuSpec(source) with
        {
            Machine = "apple2ee"
        }, At("modify-indexed-run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(expectedCycles, result.Cycles!.Cycles);
    }

    [Fact]
    public async Task Run_IndirectJump_UsesSelectedProcessorSemanticsAndTiming()
    {
        string source = At("indirect.asm");
        File.WriteAllText(source, """
            jmp ($30ff)
            .org $6100
            lda #$11
            rts
            .org $6200
            lda #$22
            rts
            """);
        ExecutionRoutine routine = CpuSpec(source).Routine! with
        {
            Memory = [new(0x30ff, "00"), new(0x3000, "61"), new(0x3100, "62")]
        };

        ExecutionResult mos = await ExecutionRunner.RunAsync(CpuSpec(source) with
        {
            Routine = routine,
            Registers = [new("A", 0x11)]
        }, At("indirect-mos"));
        ExecutionResult cmos = await ExecutionRunner.RunAsync(CpuSpec(source) with
        {
            Machine = "apple2ee",
            Routine = routine,
            Registers = [new("A", 0x22)]
        }, At("indirect-cmos"));

        Assert.True(mos.Passed, string.Join('\n', mos.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.True(cmos.Passed, string.Join('\n', cmos.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(13, mos.Cycles!.Cycles);
        Assert.Equal(14, cmos.Cycles!.Cycles);
    }

    [Fact]
    public async Task Run_BrkAndRti_UseVectorAndRestoreProcessorState()
    {
        string source = At("interrupt.asm");
        File.WriteAllText(source, """
            cld
            brk
            .byte $ea
            lda #$2a
            rts
            handler: sed
            rti
            """);
        ExecutionSpec baseSpec = CpuSpec(source);
        ExecutionSpec spec = baseSpec with
        {
            Trace = true,
            Routine = baseSpec.Routine! with { Memory = [new(0xfffe, "0660")] },
            Registers = [new("A", 0x2a), new("P", 0x24)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("interrupt-run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(25, result.Cycles!.Cycles);
        string trace = File.ReadAllText(At("interrupt-run/trace.tsv"));
        Assert.Contains("R:$FFFE=$06", trace);
        Assert.Contains("R:$FFFF=$60", trace);
    }

    [Fact]
    public async Task Run_IllegalOpcode_ReturnsCpuFaultWithProgramCounterEvidence()
    {
        string source = At("illegal.bin");
        File.WriteAllBytes(source, [0x02]);
        ExecutionSpec spec = CpuSpec(source) with
        {
            Routine = new() { Source = source, Kind = "binary" }
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("illegal-run"));

        Assert.False(result.Passed);
        Assert.Equal("cpu_fault", result.StopReason);
        ProgramDiagnostic diagnostic = Assert.Single(result.Diagnostics,
            diagnostic => diagnostic.Code == "execution.cpu_fault");
        Assert.Equal("$6000", diagnostic.Symbol);
        Assert.Equal("$02", diagnostic.Actual);
    }

    [Fact]
    public async Task Run_EndlessRoutine_StopsAfterInstructionThatExceedsCycleBudget()
    {
        string source = At("loop.asm");
        File.WriteAllText(source, "loop: jmp loop\n");
        ExecutionSpec spec = CpuSpec(source) with { Routine = CpuSpec(source).Routine! with { MaxCycles = 10 } };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("limit-run"));

        Assert.False(result.Passed);
        Assert.Equal("cycle_limit", result.StopReason);
        Assert.Equal(12, result.Cycles!.Cycles);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "execution.cycle_budget");
    }

    [Fact]
    public void Validate_CpuEngineWithMachineMediaOrInteractiveFeatures_Refuses()
    {
        string source = At("routine.asm");
        File.WriteAllText(source, "rts\n");
        ExecutionSpec spec = CpuSpec(source);

        Assert.Equal("execution.invalid_spec", Assert.Throws<DiskException>(() =>
            ExecutionRunner.Validate(spec with { DiskImage = "disk.po" })).Code);
        Assert.Equal("execution.invalid_spec", Assert.Throws<DiskException>(() =>
            ExecutionRunner.Validate(spec with { Keys = [new(0, "X")] })).Code);
        Assert.Equal("execution.invalid_spec", Assert.Throws<DiskException>(() =>
            ExecutionRunner.Validate(spec with { Machine = "" })).Code);
    }

    private static ExecutionSpec CpuSpec(string source) => new()
    {
        Engine = "cpu",
        Machine = "apple2e",
        Routine = new() { Source = source }
    };

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
