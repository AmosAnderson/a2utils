// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Assembly;
using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class RuntimeLibraryMameTests
{
    [MameSmokeFact]
    public async Task Run_ShortRoutine_CountsTwelveCyclesThroughRts()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("routine.asm");
        File.WriteAllText(source, "LDA #42\nSTA $0300\nRTS\n");
        ExecutionResult result = await ExecutionRunner.RunAsync(Spec(new() { Source = source, MaxCycles = 12 }) with
        {
            Memory = [new(0x0300, "2A")]
        }, workspace.NewPath("run"));

        Assert.True(result.Passed, Details(result));
        Assert.NotNull(result.Cycles);
        Assert.Equal(12, result.Cycles.Cycles);
        Assert.True(result.Cycles.Returned);
    }

    [MameSmokeFact]
    public async Task Run_EndlessRoutine_StopsAtInstructionCycleBudget()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("routine.asm");
        File.WriteAllText(source, "loop: JMP loop\n");
        ExecutionResult result = await ExecutionRunner.RunAsync(Spec(new() { Source = source, MaxCycles = 10 }), workspace.NewPath("run"));

        Assert.False(result.Passed);
        Assert.Equal("cycle_limit", result.StopReason);
        Assert.NotNull(result.Cycles);
        Assert.False(result.Cycles.Returned);
        Assert.InRange(result.Cycles.Cycles, 11, 13);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "execution.cycle_budget");
    }

    [MameSmokeFact]
    public async Task Run_JoystickButtonBeforeRoutine_ExposesPressedSwitchToCpu()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("routine.asm");
        File.WriteAllText(source, "LDA $C061\nAND #$80\nSTA $0300\nRTS\n");
        ExecutionResult result = await ExecutionRunner.RunAsync(Spec(new() { Source = source }) with
        {
            GamePort = "joystick",
            Steps = [new() { Name = "press button", Action = "input", Input = new("button0", 1) }],
            Memory = [new(0x0300, "80")]
        }, workspace.NewPath("run"));

        Assert.True(result.Passed, Details(result));
        Assert.Equal("routine_return", result.StopReason);
    }

    [MameSmokeFact]
    public async Task Run_HgrSpriteRoutine_ClipsRightAndBottomWhilePreservingAdjacentBytes()
    {
        using FixtureWorkspace workspace = new();
        ExecutionSpec spec = Spec(new()
        {
            Source = RuntimeLibraryTests.Example("hgr-sprite.inc"),
            EntrySymbol = "a2_hgr_sprite",
            Registers = [new("X", 0x5a)],
            Memory = [new(0x06, "0050"), new(0x0a, "27BE0204"), new(0x5000, "1122334455667788"),
                new(0x3bf6, "AAAAAA"), new(0x3ff6, "BBBBBB")]
        }) with
        {
            Memory = [new(0x3bf6, "AA11AA"), new(0x3ff6, "BB33BB"), new(0x06, "0450"),
                new(0x0b, "C0"), new(0x5000, "1122334455667788")],
            Registers = [new("X", 0x5a)]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, workspace.NewPath("run"));

        Assert.True(result.Passed, Details(result));
        Assert.Equal("routine_return", result.StopReason);
    }

    [MameSmokeFact]
    public async Task Run_HgrSpriteOutsideScreen_ReturnsWithoutWritingOrAdvancingSource()
    {
        using FixtureWorkspace workspace = new();
        ExecutionSpec spec = Spec(new()
        {
            Source = RuntimeLibraryTests.Example("hgr-sprite.inc"),
            EntrySymbol = "a2_hgr_sprite",
            Memory = [new(0x06, "0050"), new(0x0a, "28000204"), new(0x2000, "AABBCC")]
        }) with
        { Memory = [new(0x06, "0050"), new(0x2000, "AABBCC")] };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, workspace.NewPath("run"));

        Assert.True(result.Passed, Details(result));
    }

    [MameSmokeFact]
    public async Task Run_BasicCallRoutine_AddsAcrossByteCarryAndPreservesIndexRegisters()
    {
        using FixtureWorkspace workspace = new();
        AssemblyResult program = Assembler.AssembleFile(RuntimeLibraryTests.Example("basic-call.asm"));
        string source = workspace.NewPath("adder.bin");
        File.WriteAllBytes(source, program.Bytes);
        ExecutionSpec spec = Spec(new()
        {
            Source = source,
            Kind = "binary",
            Registers = [new("X", 0x5a), new("Y", 0xa5), new("P", 0x2d)],
            Memory = [new(0x6040, "FA140000")]
        }) with
        { Memory = [new(0x6040, "FA140E01")], Registers = [new("X", 0x5a), new("Y", 0xa5)] };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, workspace.NewPath("run"));

        Assert.True(result.Passed, Details(result));
        Assert.Equal(program.Bytes, File.ReadAllBytes(source));
        Assert.Equal(0x08, result.Registers["P"] & 0x08); // Decimal mode restored for the BASIC caller.
    }

    private static ExecutionSpec Spec(ExecutionRoutine routine) => new()
    {
        Name = "Original runtime library routine smoke",
        EmulatorPath = Environment.GetEnvironmentVariable("A2_MAME_PATH")!,
        RomDirectory = Environment.GetEnvironmentVariable("A2_MAME_ROMS")!,
        Machine = "apple2ee",
        EmulatedSeconds = 4,
        HostTimeoutSeconds = 30,
        Routine = routine
    };

    private static string Details(ExecutionResult result) => string.Join('\n', result.Diagnostics.Select(diagnostic =>
        $"{diagnostic.Message} Expected: {diagnostic.Expected}; actual: {diagnostic.Actual}"));
}
