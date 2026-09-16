using System.Globalization;

namespace A2Utils.Core.Execution;

public static partial class ExecutionRunner
{
    public static ExecutionObservation ParseObservation(string text, string screenText)
    {
        if (text.Length > 1024 * 1024) throw new InvalidDataException("The emulator observation exceeds 1 MiB.");
        string[] lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 4 || lines[0] != "A2EXEC1" || lines[^1] != "END")
            throw new InvalidDataException("The emulator observation file is incomplete or has an unsupported version.");
        Dictionary<string, long> registers = new(StringComparer.Ordinal);
        Dictionary<int, string> memory = [];
        Dictionary<(string, int), ExecutionMemory> banks = [];
        Dictionary<string, string> textPages = [];
        Dictionary<string, int> video = [];
        List<ExecutionInstruction> history = [];
        List<ExecutionStepResult> steps = [];
        ExecutionDebugResult? debug = null;
        ExecutionCycleResult? cycles = null;
        string? reason = null;
        double? seconds = null;
        foreach (string line in lines.Skip(1).SkipLast(1))
        {
            string[] fields = line.Split('\t');
            if (fields is ["STOP", var stop] && reason is null && stop is "completion_condition" or "emulated_limit" or "breakpoint" or "watchpoint" or "debug_steps" or "sequence_complete" or "step_timeout" or "step_failed" or "cycle_limit" or "routine_return" or "cycle_complete") reason = stop;
            else if (fields is ["TIME", var time] && seconds is null
                && double.TryParse(time, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) && parsed >= 0) seconds = parsed;
            else if (fields is ["REG", var register, var value] && long.TryParse(value, CultureInfo.InvariantCulture, out long number)
                && registers.TryAdd(register, number)) { }
            else if (fields is ["MEM", var address, var hex] && AddMemory("cpu", address, hex)) { }
            else if (fields is ["BANK", var bank, var bankAddress, var bankHex] && AddMemory(bank, bankAddress, bankHex)) { }
            else if (fields is ["TEXTMEM", var textBank, var page] && textBank is "main" or "aux" && page.Length == 2048
                && HexBytes().IsMatch(page) && textPages.TryAdd(textBank, page)) { }
            else if (fields is ["VIDEO", var flag, var flagValue] && flag is "altCharset" or "columns80" or "page2" or "store80" or "graphics" or "mixed" or "hires" or "doubleHires" or "flash"
                && int.TryParse(flagValue, out int boolean) && boolean is 0 or 1 && video.TryAdd(flag, boolean)) { }
            else if (fields is ["DEBUG", var kind, var debugIndex, var triggerAddress, var triggerValue, var triggerPc, var debugSteps]
                && debug is null && kind is "breakpoint" or "watchpoint"
                && Integer(debugIndex, 0, 63, out int pointIndex) && Integer(triggerAddress, 0, 65535, out int at)
                && Integer(triggerValue, -1, 255, out int data) && Integer(triggerPc, 0, 65535, out int pc)
                && Integer(debugSteps, 0, 1024, out int stepped))
                debug = new(new(kind, pointIndex, at, data < 0 ? null : data, pc), stepped, []);
            else if (fields is ["CYCLES", var cycleStart, var cycleEnd, var cycleCount, var returned] && cycles is null
                && Integer(cycleStart, 0, 65535, out int startAddress) && Integer(cycleEnd, 0, 65535, out int endAddress)
                && long.TryParse(cycleCount, NumberStyles.None, CultureInfo.InvariantCulture, out long count) && count is >= 0 and <= 1000000100 && returned is "0" or "1")
                cycles = new(startAddress, endAddress, count, returned == "1");
            else if (fields is ["HIST", var instructionAddress, var disassembly] && history.Count < 256
                && Integer(instructionAddress, 0, 65535, out int instructionPc) && disassembly.Length is > 0 and <= 512)
                history.Add(new(instructionPc, disassembly));
            else if (fields is ["STEP", var stepIndex, var status, var stepTime] && Integer(stepIndex, 0, 127, out int index)
                && index == steps.Count && status is "pass" or "fail" or "timeout"
                && double.TryParse(stepTime, CultureInfo.InvariantCulture, out double atTime) && double.IsFinite(atTime) && atTime >= 0
                && (steps.Count == 0 || steps[^1].Status == "pass" && atTime >= steps[^1].EmulatedSeconds))
                steps.Add(new(index, status, atTime));
            else throw new InvalidDataException("The emulator observation file contains an invalid or duplicate field.");
        }
        if (reason is null || seconds is null) throw new InvalidDataException("The emulator observation is missing its stop reason or time.");
        if ((reason is "cycle_complete" or "cycle_limit" or "routine_return") != (cycles is not null)
            || cycles is not null && cycles.Returned != (reason is "cycle_complete" or "routine_return")
            || steps.Count > 0 && steps[^1].EmulatedSeconds > seconds.Value
            || (reason is "step_failed" or "step_timeout") != (steps.Count > 0 && steps[^1].Status != "pass"))
            throw new InvalidDataException("The emulator stop reason and sequence/cycle evidence disagree.");
        bool stoppedInDebugger = reason is "breakpoint" or "watchpoint" or "debug_steps";
        if (stoppedInDebugger != (debug is not null) || history.Count > 0 && debug is null ||
            debug is not null && (reason != (debug.SteppedInstructions > 0 ? "debug_steps" : debug.Trigger.Kind)
                || debug.Trigger.Kind == "breakpoint" && (debug.Trigger.Value is not null || debug.Trigger.ProgramCounter != debug.Trigger.Address)
                || debug.Trigger.Kind == "watchpoint" && debug.Trigger.Value is null))
            throw new InvalidDataException("The emulator debug stop and evidence disagree.");
        return new(reason, seconds.Value, registers, memory, screenText)
        {
            BankMemory = banks.Values.ToArray(),
            Debug = debug is null ? null : debug with { History = history },
            TextPages = textPages,
            Video = video,
            Steps = steps,
            Cycles = cycles
        };

        bool AddMemory(string bank, string address, string hex)
        {
            if (!Integer(address, 0, 65535, out int offset) || hex.Length % 2 != 0 || !HexBytes().IsMatch(hex)
                || !AppleIIeMemory.IsValidRange(bank, offset, hex.Length / 2)
                || banks.Values.Sum(m => m.Hex.Length) + hex.Length > 131072
                || !banks.TryAdd((bank, offset), new(bank, offset, hex))) return false;
            return bank != "cpu" || memory.TryAdd(offset, hex);
        }
    }

    private static bool Integer(string value, int minimum, int maximum, out int number)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= minimum && number <= maximum;

    private static ExecutionObservation DecodeText(ExecutionSpec spec, ExecutionObservation observation)
    {
        if (!observation.TextPages.TryGetValue("main", out string? main)
            || !observation.TextPages.TryGetValue("aux", out string? aux)
            || !observation.Video.TryGetValue("altCharset", out int alternate))
            throw new InvalidDataException("The emulator omitted the requested IIe text memory or character-set state.");
        AppleIIeTextScreen screen = AppleIIeTextDecoder.Decode(Convert.FromHexString(main), Convert.FromHexString(aux),
            spec.TextColumns, alternate != 0, mouseTextSupported: spec.Machine != "apple2e");
        return observation with { ScreenText = screen.Text, TextScreen = screen };
    }
}
