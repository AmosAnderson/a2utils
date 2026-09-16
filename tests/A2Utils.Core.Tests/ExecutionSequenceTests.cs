using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class ExecutionSequenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-sequence-" + Guid.NewGuid().ToString("N"));
    public ExecutionSequenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Run_OrderedContract_RetainsIntermediateEvidenceAndOriginalDisk()
    {
        ExecutionSpec spec = Spec("sequence");
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, Path.Combine(_directory, "result"));
        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal(2, result.Checkpoints.Count);
        Assert.Equal("READY", result.Checkpoints[0].Text);
        Assert.Equal("DONE", result.Checkpoints[1].Text);
        Assert.Equal("sequence", File.ReadAllText(spec.DiskImage));
    }

    [Fact]
    public async Task Run_StepTimeout_FailsAndPreservesCheckpoint()
    {
        ExecutionSpec spec = Spec("sequence-timeout");
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, Path.Combine(_directory, "timeout"));
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, d => d.Code == "execution.step_timeout");
        Assert.Equal("timeout", Assert.Single(result.Checkpoints).Status);
        Assert.Equal("sequence-timeout", File.ReadAllText(spec.DiskImage));
    }

    [Fact]
    public void ConditionMatches_MemoryNotEqual_RequiresPresentEvidence()
    {
        ExecutionCondition condition = new() { MemoryNotEqual = [new(768, "FF")] };
        ExecutionObservation missing = new("emulated_limit", 1, new Dictionary<string, long>(), new Dictionary<int, string>(), "");
        Assert.False(ExecutionRunner.ConditionMatches(condition, missing));
        Assert.True(ExecutionRunner.ConditionMatches(condition, missing with { BankMemory = [new("cpu", 768, "00")] }));
        Assert.False(ExecutionRunner.ConditionMatches(condition, missing with { BankMemory = [new("cpu", 768, "FF")] }));
    }

    [Theory]
    [InlineData("STEP\t1\tpass\t1")]
    [InlineData("STEP\t0\tpass\t3")]
    [InlineData("STEP\t0\tfail\t1\nSTEP\t1\tpass\t2")]
    [InlineData("STEP\t0\tpass\t2\nSTEP\t1\tpass\t1")]
    public void ParseObservation_InvalidSequenceEvidence_Rejects(string evidence)
        => Assert.Throws<InvalidDataException>(() => ExecutionRunner.ParseObservation(
            "A2EXEC1\nSTOP\tsequence_complete\nTIME\t2\n" + evidence + "\nEND\n", ""));

    [Fact]
    public void Validate_InputRangesAndMixedScheduling_Refuses()
    {
        ExecutionSpec spec = Spec("pass");
        Assert.Throws<DiskException>(() => MameAdapter.Validate(spec with { Keys = [new(0, "X")] }));
        Assert.Throws<DiskException>(() => MameAdapter.Validate(spec with { Steps = [new() { Action = "input", Input = new("paddle0", 256) }], GamePort = "joystick" }));
        Assert.Throws<DiskException>(() => MameAdapter.Validate(spec with { Steps = [new() { Action = "input", Input = new("button0", 1) }] }));
        MameAdapter.Validate(spec with { Steps = [new() { Action = "input", Input = new("paddle0", 255) }], GamePort = "joystick" });
    }

    private ExecutionSpec Spec(string mode)
    {
        string disk = Path.Combine(_directory, "input.dsk");
        File.WriteAllText(disk, mode);
        return new()
        {
            EmulatorPath = TestPaths.ExecutionHost(),
            RomDirectory = _directory,
            Machine = "apple2ee",
            DiskImage = disk,
            Steps = [new() { Name = "ready", Condition = new() { TextContains = ["READY"] } },
                new() { Name = "respond", Action = "keys", Text = "X" },
                new() { Name = "done", Action = "assert", Condition = new() { Memory = [new(768, "2A")], TextContains = ["DONE"] } }],
            TextNotContains = ["ERROR"]
        };
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
