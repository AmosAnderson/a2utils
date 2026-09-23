// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Assembly;
using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class MameDebuggerSmokeTests
{
    [MameSmokeFact]
    public async Task Run_RealBreakpoint_CapturesBeforeInstructionExecutes()
    {
        (ExecutionResult result, AssemblyResult program) = await RunAsync(program => new()
        {
            Breakpoints = [new(program.Symbols["target"], AfterSeconds: 2)]
        });
        Assert.Equal("breakpoint", result.StopReason);
        Assert.Equal(program.Symbols["target"], result.Registers["PC"]);
        Assert.Equal(0, result.Debug!.Trigger.Index);
        Assert.Equal(program.Symbols["target"], result.Debug.Trigger.ProgramCounter);
        Assert.Equal("0000", result.Memory[0x300]);
        Assert.Equal(0, result.Debug.SteppedInstructions);
    }

    [MameSmokeFact]
    public async Task Run_RealBreakpointWithSteps_ExecutesExactlyTwoInstructions()
    {
        (ExecutionResult result, AssemblyResult program) = await RunAsync(program => new()
        {
            Breakpoints = [new(program.Symbols["target"], AfterSeconds: 2)],
            StepInstructions = 2,
            HistoryInstructions = 4
        });
        Assert.Equal("debug_steps", result.StopReason);
        Assert.Equal(program.Symbols["afterwrite"], result.Registers["PC"]);
        Assert.Equal("2A00", result.Memory[0x300]);
        Assert.Equal(42, result.Registers["A"]);
        Assert.Equal(2, result.Debug!.SteppedInstructions);
        Assert.Contains(result.Debug.History, instruction => instruction.Address == program.Symbols["target"]);
        Assert.Equal(program.Symbols["write"], result.Debug.History.Last().Address);
    }

    [MameSmokeFact]
    public async Task Run_RealWriteWatchpoint_ReportsAccessAndStopsBeforeFollowingInstruction()
    {
        (ExecutionResult result, AssemblyResult program) = await RunAsync(_ => new()
        {
            Watchpoints = [new(0x300, AfterSeconds: 2)]
        });
        Assert.Equal("watchpoint", result.StopReason);
        Assert.Equal(0x300, result.Debug!.Trigger.Address);
        Assert.Equal(42, result.Debug.Trigger.Value);
        Assert.Equal(program.Symbols["write"], result.Debug.Trigger.ProgramCounter);
        Assert.Equal(program.Symbols["afterwrite"], result.Registers["PC"]);
        Assert.Equal("2A00", result.Memory[0x300]);
    }

    private static async Task<(ExecutionResult Result, AssemblyResult Program)> RunAsync(Func<AssemblyResult, ExecutionDebug> configure)
    {
        string directory = Path.Combine(TestPaths.TemporaryRoot, "a2-debug-mame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Original self-booting sector. Keyboard input starts the test after all
            // debugger points are armed, without requiring DOS/ProDOS system files.
            AssemblyResult program = Assembler.Assemble("""
                .org $0800
                .byte 1
                lda #0
                sta $0300
                sta $0301
                bit $c010
                wait: lda $c000
                bpl wait
                bit $c010
                target: lda #$2a
                write: sta $0300
                afterwrite: inc $0301
                done: jmp done
                """);
            byte[] disk = new byte[143360];
            program.Bytes.CopyTo(disk, 0);
            string input = Path.Combine(directory, "boot.dsk");
            File.WriteAllBytes(input, disk);
            ExecutionSpec spec = new()
            {
                Name = "Real MAME instruction debugger smoke",
                EmulatorPath = Environment.GetEnvironmentVariable("A2_MAME_PATH")!,
                RomDirectory = Environment.GetEnvironmentVariable("A2_MAME_ROMS")!,
                Machine = "apple2ee",
                DiskImage = input,
                EmulatedSeconds = 6,
                HostTimeoutSeconds = 30,
                Keys = [new(2.5, "X")],
                ObserveMemory = [new(0x300, 2)],
                Debug = configure(program)
            };
            ExecutionResult result = await ExecutionRunner.RunAsync(spec, Path.Combine(directory, "run"));
            Assert.True(result.Passed, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.Equal(disk, File.ReadAllBytes(input));
            Assert.NotNull(result.Debug);
            return (result, program);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
