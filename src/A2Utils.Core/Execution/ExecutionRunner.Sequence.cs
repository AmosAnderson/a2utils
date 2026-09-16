using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

public static partial class ExecutionRunner
{
    public static bool ConditionMatches(ExecutionCondition condition, ExecutionObservation observation)
    {
        bool MemoryEquals(MemoryAssertion expected)
        {
            string? actual = expected.Bank == "cpu" ? observation.Memory.GetValueOrDefault(expected.Address)
                ?? observation.BankMemory.FirstOrDefault(range => range.Bank == "cpu" && range.Address == expected.Address)?.Hex
                : observation.BankMemory.FirstOrDefault(range => range.Bank == expected.Bank && range.Address == expected.Address)?.Hex;
            return string.Equals(actual, Convert.ToHexString(MameAdapter.ParseHex(expected.Hex)), StringComparison.OrdinalIgnoreCase);
        }
        return condition.Memory.All(MemoryEquals) && condition.MemoryNotEqual.All(expected =>
            observation.BankMemory.Any(range => range.Bank == expected.Bank && range.Address == expected.Address && range.Hex.Length == MameAdapter.ParseHex(expected.Hex).Length * 2)
            && !MemoryEquals(expected))
            && condition.Registers.All(expected => observation.Registers.TryGetValue(expected.Name, out long actual) && actual == expected.Value)
            && condition.TextContains.All(text => observation.ScreenText.Contains(text, StringComparison.Ordinal))
            && condition.TextNotContains.All(text => !observation.ScreenText.Contains(text, StringComparison.Ordinal));
    }

    private static void ReadCheckpoints(ExecutionSpec spec, ExecutionObservation observation, string artifacts,
        List<ExecutionCheckpoint> checkpoints)
    {
        foreach (ExecutionStepResult result in observation.Steps)
        {
            if (result.Index >= spec.Steps.Count) throw new InvalidDataException("Unexpected checkpoint index.");
            ExecutionStep step = spec.Steps[result.Index];
            if (step.Action is not ("wait" or "assert" or "capture")) continue;
            string prefix = Path.Combine(artifacts, $"checkpoint-{result.Index + 1:D3}");
            ExecutionObservation checkpoint = ParseObservation(ReadBounded(prefix + ".tsv", 1024 * 1024), ReadBounded(prefix + ".txt", 32768));
            if (checkpoint.EmulatedSeconds != result.EmulatedSeconds || checkpoint.Steps.Count != 0 || checkpoint.Debug is not null || checkpoint.Cycles is not null)
                throw new InvalidDataException("Checkpoint timing or content disagrees with its step.");
            if (step.Condition is not null && (result.Status == "pass") != ConditionMatches(step.Condition, checkpoint))
                throw new InvalidDataException("Checkpoint observations disagree with the reported condition status.");
            checkpoints.Add(new(result.Index, step.Name, step.Action, result.Status, result.EmulatedSeconds,
                checkpoint.Registers, checkpoint.BankMemory, checkpoint.ScreenText, prefix + ".tsv"));
        }
    }
}
