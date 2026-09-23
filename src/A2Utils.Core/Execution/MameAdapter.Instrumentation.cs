// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text;

namespace A2Utils.Core.Execution;

public static partial class MameAdapter
{
    private static void ValidateInstrumentation(ExecutionSpec spec)
    {
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
        {
            if (!condition) throw new DiskException("execution.invalid_spec", message, 2);
        }
        if (spec.Routine is null && spec.Cycles is null) return;
        Require(spec.Debug is null && spec.Until is null && !(spec.Routine is not null && spec.Cycles is not null),
            "Choose routine, cycles, debug, or until as the execution stop mechanism.");
        if (spec.Cycles is { } cycles)
        {
            Require(cycles.Start is not null && cycles.End is not null, "Cycle measurement requires start and end points.");
            foreach (ExecutionBreakpoint point in new[] { cycles.Start, cycles.End })
                Require(point.Address is >= 0 and <= 65535 && point.Program is null && point.Symbol is null && point.Offset == 0,
                    "Cycle points require CPU addresses; use build --test to resolve symbols.");
            Require(cycles.Start.Address != cycles.End.Address && cycles.End.AfterSeconds == 0,
                "Cycle start/end addresses must differ; only the start accepts afterSeconds.");
            Require(double.IsFinite(cycles.Start.AfterSeconds) && cycles.Start.AfterSeconds >= 0 && cycles.Start.AfterSeconds < spec.EmulatedSeconds,
                "Cycle start afterSeconds must precede the deadline.");
            Require(cycles.MaxCycles is > 0 and <= 1000000000, "Cycle budget must be 1..1000000000.");
        }
        if (spec.Routine is not { } routine) return;
        Require(!string.IsNullOrWhiteSpace(routine.Source) && routine.Kind is "asm" or "binary", "Routine requires source and kind asm or binary.");
        Require(routine.Origin is >= 0x0800 and < 0xc000 && routine.EntryPoint is null or >= 0x0800 and < 0xc000,
            "Routine origin and entry must be in main RAM $0800..$BFFF.");
        Require(routine.EntrySymbol is null || routine.EntryPoint is null && routine.Kind == "asm" && routine.EntrySymbol.Length is > 0 and <= 128,
            "Use entryPoint or an assembly entrySymbol.");
        Require(double.IsFinite(routine.StartAfterSeconds) && routine.StartAfterSeconds >= 0 && routine.StartAfterSeconds < spec.EmulatedSeconds,
            "Routine startAfterSeconds must precede the emulated deadline.");
        Require(routine.MaxCycles is > 0 and <= 1000000000, "Routine cycle budget must be 1..1000000000.");
        Require(routine.Registers is not null && routine.Registers.Count <= 4 && routine.Registers.All(r => r is not null && r.Name is "A" or "X" or "Y" or "P" && r.Value is >= 0 and <= 255)
            && routine.Registers.Select(r => r.Name).Distinct().Count() == routine.Registers.Count, "Routine registers must be distinct A/X/Y/P bytes.");
        Require(routine.Memory is not null && routine.Memory.Count <= 128, "Routine memory must be a bounded array.");
        List<(int Start, int End)> ranges = [];
        foreach (MemoryAssertion range in routine.Memory)
        {
            Require(range is not null && range.Bank == "cpu" && range.Hex is not null, "Routine writes use CPU main RAM.");
            int length = ParseHex(range.Hex).Length;
            long end = (long)range.Address + length;
            Require(length > 0 && range.Address >= 0 && end <= 0xc000 && (end <= 0x100 || range.Address >= 0x300),
                "Routine initial memory must fit RAM and avoid the harness stack and return sentinel $0100..$02FF.");
            Require(ranges.All(r => end <= r.Start || range.Address >= r.End), "Routine initial memory ranges must not overlap.");
            ranges.Add((range.Address, (int)end));
        }
    }

    private static void AppendInstrumentationConfiguration(StringBuilder script, ExecutionSpec spec, PreparedRoutine? prepared)
    {
        script.AppendLine($"local instrumentation_enabled = {(spec.Routine is not null || spec.Cycles is not null ? "true" : "false")}");
        if (spec.Routine is { } routine)
        {
            if (prepared is null) throw new DiskException("execution.routine_unprepared", "Prepare the routine before generating its script.", 2);
            script.AppendLine($"local measurement = {{ start = {prepared.EntryPoint}, finish = {RoutineHarness.ReturnAddress}, after = {Number(routine.StartAfterSeconds)}, budget = {routine.MaxCycles}, routine = true }}");
            script.AppendLine($"local routine_origin = {prepared.Origin}");
            script.AppendLine($"local routine_bytes = {LuaString(Convert.ToHexString(prepared.Bytes))}");
            script.AppendLine("local routine_registers = { A = 0, X = 0, Y = 0, P = 36 }");
            foreach (RegisterAssertion register in routine.Registers)
                script.AppendLine($"routine_registers[{LuaString(register.Name)}] = {register.Value}");
            script.AppendLine("local routine_memory = {");
            foreach (MemoryAssertion range in routine.Memory)
                script.AppendLine($"{{ address = {range.Address}, hex = {LuaString(Convert.ToHexString(ParseHex(range.Hex)))} }},");
            script.AppendLine("}");
        }
        else if (spec.Cycles is { } cycles)
            script.AppendLine($"local measurement = {{ start = {cycles.Start.Address}, finish = {cycles.End.Address}, after = {Number(cycles.Start.AfterSeconds)}, budget = {cycles.MaxCycles}, routine = false }}");
    }

    private static string CreateInstrumentationScript(ExecutionSpec spec)
    {
        if (spec.Routine is null && spec.Cycles is null) return "local function instrumentation_frame(now) end\n";
        return """
            local counter_debugger = assert(machine.debugger, "Cycle measurement requires the debugger")
            local counter_cpu = assert(cpu.debug, "CPU debug support is unavailable")
            counter_debugger.visible_cpu = cpu
            local counter_cursor = #counter_debugger.consolelog
            local counter_armed = false
            local counter_started = false
            local counter_cycles = nil
            local counter_returned = false
            local function write_hex(address, hex)
                for index = 1, #hex, 2 do memory:write_u8(address + (index - 1) / 2, tonumber(hex:sub(index, index + 1), 16)) end
            end
            local function instrumentation_frame(now)
                if counter_armed or not sequence_ready() or now < measurement.after then return end
                counter_armed = true
                counter_cpu:bpset(measurement.start, "1", 'temp0=totalcycles;printf "A2CYCLE:START:0"')
                if measurement.routine then
                    -- Restore ordinary main-RAM mapping on IIe/IIc before injecting code.
                    if machine.devices[":a2video"] then
                        memory:write_u8(0xc000, 0); memory:write_u8(0xc002, 0); memory:write_u8(0xc004, 0); memory:write_u8(0xc008, 0)
                        memory:read_u8(0xc082)
                    end
                    write_hex(routine_origin, routine_bytes)
                    for _, range in ipairs(routine_memory) do write_hex(range.address, range.hex) end
                    for name, value in pairs(routine_registers) do assert(cpu.state[name], "Missing initial CPU register").value = value end
                    -- RTS pulls $02FE and increments it to the harness return breakpoint $02FF.
                    memory:write_u8(0x01fe, 0xfe); memory:write_u8(0x01ff, 0x02)
                    assert(cpu.state["SP"], "Missing CPU stack pointer").value = 0x01fd
                    assert(cpu.state["PC"], "Missing CPU program counter").value = measurement.start
                end
            end
            instrumentation_lines = function()
                if counter_cycles == nil then return {} end
                return { "CYCLES\t" .. tostring(measurement.start) .. "\t" .. tostring(measurement.finish)
                    .. "\t" .. tostring(counter_cycles) .. "\t" .. (counter_returned and "1" or "0") }
            end
            local function counter_periodic()
                if finished or counter_debugger.execution_state ~= "stop" then return end
                local event, elapsed = nil, nil
                for index = counter_cursor + 1, #counter_debugger.consolelog do
                    local line = counter_debugger.consolelog[index] or ""
                    local kind, count = line:match("^A2CYCLE:(%u+):(%d+)$")
                    if kind then event = kind; elapsed = tonumber(count) end
                end
                counter_cursor = #counter_debugger.consolelog
                if event == "START" and not counter_started then
                    counter_started = true
                    counter_cpu:bpclear()
                    counter_cpu:bpset(measurement.finish, "1", 'printf "A2CYCLE:END:%d",totalcycles-temp0')
                    counter_debugger:command(string.format('rpset totalcycles-temp0>%X,{printf "A2CYCLE:LIMIT:%%d",totalcycles-temp0}', measurement.budget))
                elseif counter_started and (event == "END" or event == "LIMIT") then
                    counter_cpu:bpclear(); counter_debugger:command("rpclear")
                    counter_cycles = elapsed
                    counter_returned = event == "END"
                    capture(event == "LIMIT" and "cycle_limit" or measurement.routine and "routine_return" or "cycle_complete", machine.time:as_double())
                end
            end
            emu.register_periodic(function()
                local ok, message = pcall(counter_periodic)
                if not ok then fail(message) end
            end)
            """;
    }
}
