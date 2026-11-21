// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Graphics;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class ExecutionReplayTests
{
    [Fact]
    public void Check_RelocatedCpuRun_ReusesArchivedAssertionsAndChecksNewGraphicsWithoutWrites()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("routine.asm");
        File.WriteAllText(source, "lda #$42\nsta $0301\nrts\n");
        string expected = workspace.NewPath("black.png");
        byte[] png = AppleGraphics.DecodePng(new byte[1024], "lores");
        File.WriteAllBytes(expected, png);
        string run = workspace.NewPath("run");
        ExecutionResult original = ExecutionRunner.Run(new ExecutionSpec()
        {
            Engine = "cpu",
            Machine = "apple2e",
            Routine = new() { Source = source },
            GraphicsMemory = [new(expected, "lores")],
            ObserveMemory = [new(0x300, 4)],
            Registers = [new("A", 0x42)]
        }, run);
        Assert.True(original.Passed);
        File.Delete(source);
        File.Delete(expected);
        string archived = workspace.NewPath("relocated");
        Directory.Move(run, archived);
        string[] Snapshot() => Directory.EnumerateFiles(archived, "*", SearchOption.AllDirectories)
            .Order().Select(path => path + "=" + ProgramFiles.Hash(File.ReadAllBytes(path))).ToArray();
        string[] before = Snapshot();

        ExecutionReplayResult replay = ExecutionReplay.Check(archived);
        Assert.True(replay.Passed);
        Assert.Equal(2, replay.Assertions);
        Assert.Equal("routine_return", replay.RecordedStopReason);

        File.WriteAllBytes(workspace.NewPath("replacement.png"), png);
        string alternative = workspace.NewPath("checks.json");
        WriteJson(alternative, new ReplayExpectations
        {
            Memory = [new(0x301, "42 00")],
            Registers = [new("A", 0x42)],
            GraphicsMemory = [new("replacement.png", "lores")]
        });
        ExecutionReplayResult withPng = ExecutionReplay.Check(archived, alternative);
        Assert.True(withPng.Passed);
        Assert.Equal(ProgramFiles.Hash(png), Assert.Single(withPng.GraphicsInputs).Sha256);
        WriteJson(alternative, new ReplayExpectations { TextNotContains = ["ERROR"] });
        var missing = Assert.Single(ExecutionReplay.Check(archived, alternative).Diagnostics);
        Assert.Equal("replay.missing_evidence", missing.Code);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Check_Checkpoint_UsesBankedRangesAndMatchingScreen()
    {
        using FixtureWorkspace workspace = new();
        string root = workspace.NewPath("mame");
        Directory.CreateDirectory(root);
        WriteJson(Path.Combine(root, "spec.json"), new ExecutionSpec
        {
            Engine = "mame",
            EmulatorPath = "/missing/emulator",
            Environment = "/missing/profile",
            Steps = [new() { Action = "keys", Text = "RUN" }, new()
            {
                Action = "assert", Condition = new()
                {
                    Memory = [new(769, "02 03", "aux")],
                    MemoryNotEqual = [new(768, "01 00", "main")],
                    Registers = [new("PC", 4096)], TextContains = ["READY"], TextNotContains = ["ERROR"]
                }
            }]
        });
        File.WriteAllText(Path.Combine(root, "checkpoint-002.tsv"),
            "A2EXEC1\nSTOP\temulated_limit\nTIME\t2\nREG\tPC\t4096\nBANK\taux\t768\t0102\nBANK\taux\t770\t0304\nBANK\tmain\t768\tABCD\nEND\n");
        string screen = Path.Combine(root, "checkpoint-002.txt");
        File.WriteAllText(screen, "READY\n");
        ExecutionReplayResult result = ExecutionReplay.Check(root, checkpoint: 2);
        Assert.True(result.Passed);
        Assert.Equal(5, result.Assertions);
        Assert.Equal(2, result.Checkpoint);
        Assert.Equal(ProgramFiles.Hash(File.ReadAllBytes(screen)), result.ScreenSha256);

        File.Delete(screen);
        string checks = workspace.NewPath("expect.json");
        WriteJson(checks, new ReplayExpectations
        {
            Memory = [new(768, "AB00", "main")],
            MemoryNotEqual = [new(769, "FF00", "main")],
            Registers = [new("A", 1)],
            TextNotContains = ["ERROR"]
        });
        result = ExecutionReplay.Check(root, checks, 2);
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, d => d.Code == "replay.mismatch" && d.Symbol == "main:$0301" && d.Actual == "CD");
        Assert.Contains(result.Diagnostics, d => d.Code == "replay.missing_evidence" && d.Symbol == "main:$0302");
        Assert.Contains(result.Diagnostics, d => d.Code == "replay.missing_evidence" && d.Symbol == "A");
        Assert.Contains(result.Diagnostics, d => d.Code == "replay.missing_evidence" && d.Symbol == "screen");
        Assert.Equal("replay.missing_evidence", Assert.Throws<DiskException>(() => ExecutionReplay.Check(root, checks, 1)).Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"diskAssertions\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"registers\":[{\"name\":\"A\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"memory\":[null]}")]
    [InlineData("{\"schemaVersion\":1,\"memory\":[{\"address\":65535,\"hex\":\"FFEE\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"textNotContains\":null}")]
    public void Check_InvalidExpectations_Refuses(string json)
    {
        using FixtureWorkspace workspace = new();
        string run = workspace.NewPath("run");
        Directory.CreateDirectory(run);
        WriteJson(Path.Combine(run, "spec.json"), new ExecutionSpec());
        File.WriteAllText(Path.Combine(run, "observations.tsv"), "A2EXEC1\nSTOP\temulated_limit\nTIME\t0\nEND\n");
        string expect = workspace.NewPath("invalid.json");
        File.WriteAllText(expect, json);
        Assert.Equal("replay.invalid_expectations", Assert.Throws<DiskException>(() => ExecutionReplay.Check(run, expect)).Code);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("A2EXEC1\nSTOP\temulated_limit\nTIME\t0\nMEM\t768\t0000\nMEM\t769\tFF\nEND\n")]
    public void Check_MalformedOrConflictingCapture_ReportsInvalidEvidence(string content)
    {
        using FixtureWorkspace workspace = new();
        string run = workspace.NewPath("run");
        Directory.CreateDirectory(run);
        WriteJson(Path.Combine(run, "spec.json"), new ExecutionSpec { Memory = [new(768, "00")] });
        File.WriteAllText(Path.Combine(run, "observations.tsv"), content);
        Assert.Equal("replay.invalid_evidence", Assert.Throws<DiskException>(() => ExecutionReplay.Check(run)).Code);
    }

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, ExecutionSpec.JsonOptions));
}
