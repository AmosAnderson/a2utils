// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text;

namespace A2Utils.Core.Execution;

public static partial class MameAdapter
{
    private static void AppendDebugConfiguration(StringBuilder script, ExecutionSpec spec)
    {
        if (spec.Debug is not { } debug) return;
        script.AppendLine($"local debug_steps = {debug.StepInstructions}");
        script.AppendLine($"local debug_history_limit = {debug.HistoryInstructions}");
        script.AppendLine("local debug_breakpoints = {");
        for (int index = 0; index < debug.Breakpoints.Count; index++)
        {
            ExecutionBreakpoint breakpoint = debug.Breakpoints[index];
            script.AppendLine($"  {{ index = {index}, address = {breakpoint.Address}, after = {Number(breakpoint.AfterSeconds)} }},");
        }
        script.AppendLine("}");
        script.AppendLine("local debug_watchpoints = {");
        for (int index = 0; index < debug.Watchpoints.Count; index++)
        {
            ExecutionWatchpoint watchpoint = debug.Watchpoints[index];
            string access = watchpoint.Access switch { "read" => "r", "write" => "w", _ => "rw" };
            script.AppendLine($"  {{ index = {index}, address = {watchpoint.Address}, length = {watchpoint.Length}, access = {LuaString(access)}, after = {Number(watchpoint.AfterSeconds)} }},");
        }
        script.AppendLine("}");
    }

    private static string CreateDebugScript(ExecutionSpec spec)
    {
        if (spec.Debug is null) return "local function debug_frame(now) end\n";
        // In MAME 0.289 wait_for_debugger invokes the Lua periodic callback synchronously,
        // before the headless 'none' debugger resumes execution. That provider overwrites
        // single_step, so a persistent registerpoint supplies bounded instruction stepping.
        return """
            local debugger = assert(machine.debugger, "The MAME debugger is required")
            local cpu_debug = assert(cpu.debug, "CPU debugging is unavailable")
            debugger.visible_cpu = cpu
            local debug_trigger = nil
            local debug_stepped = 0
            local debug_complete = false
            local debug_log_cursor = #debugger.consolelog
            local function debug_frame(now)
                if debug_trigger or finished or not sequence_ready() then return end
                for _, point in ipairs(debug_breakpoints) do
                    if not point.armed and now >= point.after then
                        local action = string.format('printf "A2DEBUG:breakpoint:%d:%%d:-1:%%d",curpc,curpc', point.index)
                        cpu_debug:bpset(point.address, "1", action)
                        point.armed = true
                    end
                end
                for _, point in ipairs(debug_watchpoints) do
                    if not point.armed and now >= point.after then
                        local action = string.format('printf "A2DEBUG:watchpoint:%d:%%d:%%d:%%d",wpaddr,wpdata,curpc', point.index)
                        cpu_debug:wpset(memory, point.access, point.address, point.length, "1", action)
                        point.armed = true
                    end
                end
            end
            debug_observation_lines = function()
                if not debug_complete then return {} end
                local lines = {
                    "DEBUG\t" .. debug_trigger.kind .. "\t" .. tostring(debug_trigger.index)
                        .. "\t" .. tostring(debug_trigger.address) .. "\t" .. tostring(debug_trigger.value)
                        .. "\t" .. tostring(debug_trigger.pc) .. "\t" .. tostring(debug_stepped)
                }
                if debug_history_limit > 0 then
                    -- Native history records instruction addresses, including the current
                    -- instruction that has not executed yet. Drop that last entry. MAME
                    -- disassembles history using the memory/bank state at capture time.
                    local start = #debugger.consolelog
                    debugger:command(string.format("history :maincpu,%X", math.min(debug_history_limit + 1, 256)))
                    local history = {}
                    for index = start + 1, #debugger.consolelog do
                        local line = debugger.consolelog[index] or ""
                        local address, instruction = line:match("^%s*(%x+):%s+(.+)$")
                        if address then
                            history[#history + 1] = "HIST\t" .. tostring(tonumber(address, 16)) .. "\t"
                                .. instruction:gsub("[\t\r\n]", " ")
                        end
                    end
                    for index = 1, #history - 1 do lines[#lines + 1] = history[index] end
                end
                return lines
            end
            local function debug_periodic()
                if finished or debugger.execution_state ~= "stop" then return end
                if not debug_trigger then
                    for index = debug_log_cursor + 1, #debugger.consolelog do
                        local line = debugger.consolelog[index] or ""
                        local kind, point, address, value, pc = line:match("^A2DEBUG:(%a+):(%d+):(%d+):(%-?%d+):(%d+)$")
                        if kind then
                            debug_trigger = { kind = kind, index = tonumber(point), address = tonumber(address),
                                value = tonumber(value), pc = tonumber(pc) }
                            break
                        end
                    end
                    debug_log_cursor = #debugger.consolelog
                    if not debug_trigger then return end
                    cpu_debug:bpclear()
                    cpu_debug:wpclear()
                    if debug_steps > 0 then
                        debugger:command("rpset 1")
                        return
                    end
                else
                    debug_stepped = debug_stepped + 1
                end
                if debug_stepped >= debug_steps then
                    debugger:command("rpclear")
                    debug_complete = true
                    local reason = debug_steps > 0 and "debug_steps" or debug_trigger.kind
                    capture(reason, machine.time:as_double())
                end
            end
            emu.register_periodic(function()
                local ok, message = pcall(debug_periodic)
                if not ok then fail(message) end
            end)
            debug_frame(machine.time:as_double())
            """;
    }
}
