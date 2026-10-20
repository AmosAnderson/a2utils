// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Globalization;
using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class CpuDebugExecutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-cpu-debug-" + Guid.NewGuid().ToString("N"));

    public CpuDebugExecutionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Run_Breakpoint_CapturesBeforeInstructionWithoutRequiringReturn()
    {
        ExecutionSpec spec = Spec("lda #$2a\nsta $0300\nrts\n") with
        {
            Debug = new() { Breakpoints = [new(0x6002)] },
            Memory = [new(0x300, "00")],
            Registers = [new("A", 0x2a), new("PC", 0x6002)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, Messages(result));
        Assert.Equal("breakpoint", result.StopReason);
        Assert.Equal(new("breakpoint", 0, 0x6002, null, 0x6002), result.Debug!.Trigger);
        Assert.Equal(0, result.Debug.SteppedInstructions);
        Assert.Equal(0x6000, Assert.Single(result.Debug.History).Address);
        Assert.False(result.Cycles!.Returned);
        Assert.Equal(2, result.Cycles.Cycles);
        ExecutionObservation parsed = ExecutionRunner.ParseObservation(File.ReadAllText(At("run/observations.tsv")), "");
        Assert.Equal(result.Debug.Trigger, parsed.Debug!.Trigger);
        Assert.Equal(result.Cycles, parsed.Cycles);
    }

    [Fact]
    public async Task Run_BreakpointWithSteps_DisablesOtherPointsAndStopsAtExactBoundary()
    {
        ExecutionSpec spec = Spec("lda #$2a\nsta $0300\ninx\nrts\n") with
        {
            Debug = new()
            {
                Breakpoints = [new(0x6000), new(0x6002)],
                Watchpoints = [new(0x300)],
                StepInstructions = 2
            },
            Memory = [new(0x300, "2A")],
            Registers = [new("PC", 0x6005), new("X", 0)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, Messages(result));
        Assert.Equal("debug_steps", result.StopReason);
        Assert.Equal(0x6000, result.Debug!.Trigger.ProgramCounter);
        Assert.Equal(2, result.Debug.SteppedInstructions);
        Assert.Equal([0x6000, 0x6002], result.Debug.History.Select(instruction => instruction.Address));
    }

    [Theory]
    [InlineData("read", 0x11)]
    [InlineData("write", 0x12)]
    [InlineData("readWrite", 0x11)]
    public async Task Run_ReadModifyWriteWatchpoint_RecordsFirstMatchingAccessAndTriggerPc(string access, int value)
    {
        ExecutionSpec spec = Spec("inc $0300\ninx\nrts\n") with
        {
            Debug = new() { Watchpoints = [new(0x300, Access: access)] },
            Memory = [new(0x300, "12")],
            Registers = [new("PC", 0x6003), new("X", 0)]
        };
        spec = spec with { Routine = spec.Routine! with { Memory = [new(0x300, "11")] } };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, Messages(result));
        Assert.Equal(new("watchpoint", 0, 0x300, value, 0x6000), result.Debug!.Trigger);
        Assert.Equal(0, result.Debug.SteppedInstructions);
        Assert.Equal(0x6000, Assert.Single(result.Debug.History).Address);
    }

    [Fact]
    public async Task Run_WatchpointWithSteps_CountsOnlyInstructionsAfterTriggeringInstruction()
    {
        ExecutionSpec spec = Spec("lda #$2a\nsta $0300\ninx\niny\nrts\n") with
        {
            Debug = new() { Watchpoints = [new(0x300)], StepInstructions = 1, HistoryInstructions = 2 },
            Registers = [new("PC", 0x6006), new("X", 1), new("Y", 0)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, Messages(result));
        Assert.Equal(0x6002, result.Debug!.Trigger.ProgramCounter);
        Assert.Equal(1, result.Debug.SteppedInstructions);
        Assert.Equal([0x6002, 0x6005], result.Debug.History.Select(instruction => instruction.Address));
    }

    [Fact]
    public async Task Run_InstructionFetchWatchpoint_UsesInstructionStartAndMatchingPointIndex()
    {
        ExecutionSpec spec = Spec("lda #$2a\nrts\n") with
        {
            Debug = new() { Watchpoints = [new(0x300), new(0x6000, Access: "read")] },
            Registers = [new("PC", 0x6002), new("A", 0x2a)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, Messages(result));
        Assert.Equal(new("watchpoint", 1, 0x6000, 0xa9, 0x6000), result.Debug!.Trigger);
    }

    [Fact]
    public async Task Run_SelfModifyingCode_HistoryUsesExecutedBytes()
    {
        ExecutionSpec spec = Spec("lda #$ea\nsta $6000\nrts\n") with
        {
            Debug = new() { Breakpoints = [new(0x6005)], HistoryInstructions = 2 },
            Memory = [new(0x6000, "EA")]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, Messages(result));
        Assert.Contains("LDA #$EA", result.Debug!.History[0].Disassembly);
        Assert.Contains("A9 EA", result.Debug.History[0].Disassembly);
    }

    [Fact]
    public async Task Run_UnreachedBreakpointOrIncompleteSteps_FailsWithinCycleBudget()
    {
        ExecutionSpec original = Spec("loop: jmp loop\n");
        foreach (int address in new[] { 0x6000, 0x6100 })
        {
            ExecutionSpec spec = original with
            {
                Debug = new() { Breakpoints = [new(address)], StepInstructions = 10 },
                Routine = original.Routine! with { MaxCycles = 10 }
            };
            ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run-" + address));

            Assert.False(result.Passed);
            Assert.Equal("cycle_limit", result.StopReason);
            Assert.Equal(12, result.Cycles!.Cycles);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "execution.debug_timeout");
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "execution.debug_evidence");
            if (address == 0x6000) Assert.Equal(4, result.Debug!.SteppedInstructions);
            else Assert.Null(result.Debug);
        }
    }

    [Fact]
    public async Task Run_ZeroHistory_ReturnsNoHistory()
    {
        ExecutionSpec spec = Spec("nop\nrts\n") with
        {
            Debug = new() { Breakpoints = [new(0x6001)], HistoryInstructions = 0 }
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, Messages(result));
        Assert.Empty(result.Debug!.History);
    }

    [Fact]
    public async Task Run_RoutineReturnsBeforeAllSteps_PreservesParseablePartialDebugEvidence()
    {
        ExecutionSpec spec = Spec("rts\n") with
        {
            Debug = new() { Breakpoints = [new(0x6000)], StepInstructions = 2 }
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.False(result.Passed);
        Assert.Equal("routine_return", result.StopReason);
        Assert.Equal("execution.debug_timeout", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, result.Debug!.SteppedInstructions);
        Assert.True(result.Cycles!.Returned);
        ExecutionObservation parsed = ExecutionRunner.ParseObservation(File.ReadAllText(At("run/observations.tsv")), "");
        Assert.Equal(result.Debug.Trigger, parsed.Debug!.Trigger);
        Assert.Equal(1, parsed.Debug.SteppedInstructions);
        Assert.Equal(result.Cycles, parsed.Cycles);
        Assert.Equal("execution.debug_timeout", Assert.Single(ExecutionRunner.Evaluate(spec, parsed)).Code);
    }

    [Theory]
    [InlineData("""{"breakpoints":[]}""")]
    [InlineData("""{"breakpoints":[{"address":24576,"afterSeconds":1}]}""")]
    [InlineData("""{"breakpoints":[{"address":24576}],"stepInstructions":1025}""")]
    [InlineData("""{"breakpoints":[{"address":24576}],"historyInstructions":257}""")]
    [InlineData("""{"breakpoints":[{"address":65536}]}""")]
    [InlineData("""{"watchpoints":[{"address":65535,"length":2}]}""")]
    [InlineData("""{"watchpoints":[{"address":768,"access":"execute"}]}""")]
    public void Validate_InvalidDebug_Refuses(string json)
    {
        ExecutionSpec spec = Spec("rts\n") with
        {
            Debug = JsonSerializer.Deserialize<ExecutionDebug>(json, ExecutionSpec.JsonOptions)
        };

        Assert.Equal("execution.invalid_spec", Assert.Throws<DiskException>(() => ExecutionRunner.Validate(spec)).Code);
    }

    [Fact]
    public async Task Run_ProjectSymbolBreakpoint_UsesExistingBuildBinding()
    {
        ExecutionSpec spec = Spec("lda #$2a\nrts\n") with
        {
            Debug = new() { Breakpoints = [new(Program: "MAIN", Symbol: "target")] }
        };
        ProjectBuildResult build = new(At("build.do"), new string('0', 64), "apple2e", "6502", "dos33", "data", "test",
            new(2000, 1, 1), [], [new BuiltFile("MAIN", "asm", "BIN", 0x6000, 0x6000, 0x6000, 3, "", true,
                new Dictionary<string, int> { ["TARGET"] = 0x6002 }, null)], [], []);

        ExecutionResult result = await ExecutionRunner.RunAsync(BuildExecution.Bind(spec, build), At("run"));

        Assert.True(result.Passed, Messages(result));
        Assert.Equal(0x6002, result.Debug!.Trigger.Address);
        Assert.Equal(0x2a, result.Registers["A"]);
    }

    [Fact]
    public async Task Run_CancelledAfterInstruction_RetainsFinalStateAndTraceTail()
    {
        ExecutionSpec spec = Spec("lda #$2a\nsta $0300\nloop: jmp loop\n") with
        {
            Trace = true,
            ObserveMemory = [new(0x300, 1)]
        };
        using CancellationTokenSource cancellation = new();

        ExecutionResult result = await CpuExecutionEngine.RunAsync(spec, At("run"), cancellation.Token,
            (cycles, _) => { if (cycles == 6) cancellation.Cancel(); });

        Assert.False(result.Passed);
        Assert.Equal("cancelled", result.StopReason);
        Assert.Equal(0x6005, result.Registers["PC"]);
        Assert.Equal(6, result.Cycles!.Cycles);
        Assert.Equal("2A", result.Memory[0x300]);
        Assert.Contains("STOP\tcancelled", File.ReadAllText(At("run/observations.tsv")));
        Assert.Equal("cancelled", ExecutionRunner.ParseObservation(File.ReadAllText(At("run/observations.tsv")), "").StopReason);
        string[] tail = File.ReadAllLines(At("run/trace-tail.tsv"));
        Assert.Equal(3, tail.Length);
        Assert.Contains("W:$0300=$2A", tail[^1]);
    }

    [Fact]
    public async Task Run_HostTimeoutAfterInstruction_RetainsFinalStateAndTimeoutReason()
    {
        ExecutionSpec spec = Spec("inx\nloop: jmp loop\n") with { HostTimeoutSeconds = 2 };

        ExecutionResult result = await CpuExecutionEngine.RunAsync(spec, At("run"), CancellationToken.None,
            (_, token) => Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))));

        Assert.False(result.Passed);
        Assert.Equal("host_timeout", result.StopReason);
        Assert.Equal(1, result.Registers["X"]);
        Assert.Equal(0x6001, result.Registers["PC"]);
        Assert.Equal(2, result.Cycles!.Cycles);
        Assert.Contains("STOP\thost_timeout", File.ReadAllText(At("run/observations.tsv")));
    }

    [Fact]
    public async Task Run_TraceExceedsLimit_RetainsLast256InstructionsAfterTruncation()
    {
        ExecutionSpec original = Spec("loop: jmp loop ; " + new string('x', 4000) + "\n");
        ExecutionSpec spec = original with
        {
            Trace = true,
            Routine = original.Routine! with { MaxCycles = 30000 }
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.Equal("cycle_limit", result.StopReason);
        Assert.True(new FileInfo(At("run/trace.tsv")).Length <= 16 * 1024 * 1024);
        Assert.Contains("# trace truncated", File.ReadAllText(At("run/trace.tsv")));
        string[] tail = File.ReadAllLines(At("run/trace-tail.tsv"));
        Assert.Equal(257, tail.Length);
        Assert.Equal(30003, long.Parse(tail[^1].Split('\t')[1], CultureInfo.InvariantCulture));
        Assert.Contains(At("run/trace-tail.tsv"), result.Artifacts);
    }

    private ExecutionSpec Spec(string source)
    {
        File.WriteAllText(At("routine.asm"), source);
        return new() { Engine = "cpu", Machine = "apple2e", Routine = new() { Source = At("routine.asm") } };
    }

    private static string Messages(ExecutionResult result) => string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message));
    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
