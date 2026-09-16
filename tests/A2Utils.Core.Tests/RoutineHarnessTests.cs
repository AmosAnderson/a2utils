using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class RoutineHarnessTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-routine-" + Guid.NewGuid().ToString("N"));
    public RoutineHarnessTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Run_RoutineProcessContract_CapturesCyclesAndPinsSource()
    {
        ExecutionSpec spec = Spec();
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, Path.Combine(_directory, "run"));
        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(d => d.Message)));
        Assert.Empty(result.Disks);
        Assert.Equal(12, result.Cycles!.Cycles);
        Assert.Equal("2A", result.Memory[768]);
        Assert.Equal(new byte[] { 0xa9, 0x2a, 0x8d, 0x00, 0x03, 0x60 }, File.ReadAllBytes(Path.Combine(_directory, "run/routine.bin")));
        Assert.True(File.Exists(Path.Combine(_directory, "run/routine.json")));
    }

    [Fact]
    public void Prepare_ChangedInclude_RejectsRevalidation()
    {
        ExecutionSpec spec = Spec();
        File.WriteAllText(spec.Routine!.Source, ".include \"part.inc\"");
        string include = Path.Combine(_directory, "part.inc");
        File.WriteAllText(include, "rts");
        PreparedRoutine prepared = RoutineHarness.Prepare(spec, _directory);
        File.WriteAllText(include, "nop\nrts");
        Assert.Equal("execution.routine_changed", Assert.Throws<DiskException>(() => RoutineHarness.ValidateInputs(prepared)).Code);
    }

    [Fact]
    public void Prepare_InitializationOverwritesCode_Refuses()
    {
        ExecutionSpec spec = Spec();
        spec = spec with { Routine = spec.Routine! with { Memory = [new(24576, "00")] } };
        Assert.Equal("execution.routine_overlap", Assert.Throws<DiskException>(() => RoutineHarness.Prepare(spec, _directory)).Code);
    }

    [Fact]
    public void Evaluate_OverBudgetOrWrongReturn_Refuses()
    {
        ExecutionSpec spec = Spec();
        ExecutionObservation observation = ExecutionRunner.ParseObservation("A2EXEC1\nSTOP\troutine_return\nTIME\t1.1\nCYCLES\t24576\t767\t12\t1\nEND\n", "");
        Assert.Empty(ExecutionRunner.Evaluate(spec with { Memory = [] }, observation));
        Assert.Contains(ExecutionRunner.Evaluate(spec with { Routine = spec.Routine! with { MaxCycles = 11 } }, observation), d => d.Code == "execution.cycle_budget");
        Assert.Contains(ExecutionRunner.Evaluate(spec, observation with { Cycles = new(24576, 768, 12, true) }), d => d.Code == "execution.cycles_evidence");
    }

    [Theory]
    [InlineData(0x100)]
    [InlineData(0x1fe)]
    [InlineData(0x2ff)]
    [InlineData(0xc000)]
    public void Validate_UnsafeInitializationAddress_Refuses(int address)
    {
        ExecutionSpec spec = Spec();
        Assert.Throws<DiskException>(() => MameAdapter.Validate(spec with { Routine = spec.Routine! with { Memory = [new(address, "00")] } }));
    }

    [Fact]
    public void Script_Routine_UsesInstructionCycleCounterAndNativeBreakpoint()
    {
        ExecutionSpec spec = Spec();
        PreparedRoutine prepared = RoutineHarness.Prepare(spec, _directory);
        string script = MameAdapter.CreateScript(spec, _directory, prepared);
        Assert.Contains("temp0=totalcycles", script);
        Assert.Contains("totalcycles-temp0>", script);
        Assert.Contains("cpu.state[\"SP\"]", script);
        Assert.Contains("counter_cpu:bpset(measurement.finish", script);
        Assert.Contains("-debug", MameAdapter.CreateArguments(spec, new Dictionary<string, string>(), "run.lua", _directory));
    }

    private ExecutionSpec Spec()
    {
        string source = Path.Combine(_directory, "routine.asm");
        File.WriteAllText(source, "lda #$2a\nsta $0300\nrts\n");
        return new()
        {
            EmulatorPath = TestPaths.ExecutionHost(),
            RomDirectory = _directory,
            Machine = "apple2ee",
            Routine = new() { Source = source },
            Memory = [new(768, "2A")]
        };
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
