using System.Text;

namespace A2Utils.Core.Execution;

public static partial class MameAdapter
{
    private static void ValidateSequence(ExecutionSpec spec)
    {
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
        {
            if (!condition) throw new DiskException("execution.invalid_spec", message, 2);
        }
        Require(spec.Steps is not null && spec.Steps.Count <= 128, "steps must be an array of at most 128 interactions.");
        Require(spec.TextNotContains is not null && spec.TextNotContains.Count <= 128 && spec.TextNotContains.All(s => !string.IsNullOrEmpty(s)),
            "textNotContains must contain at most 128 nonempty strings.");
        Require(spec.GamePort is "none" or "joystick" or "paddles", "gamePort must be none, joystick, or paddles.");
        Require(spec.Steps!.Count == 0 || spec.Keys.Count == 0, "Choose ordered steps or time-scheduled keys, not both.");
        Require(spec.Environment is not null || spec.ToolchainLock is null, "toolchainLock requires an environment profile.");
        long expectedBytes = 0;
        foreach (ExecutionStep step in spec.Steps)
        {
            Require(step is not null && !string.IsNullOrWhiteSpace(step.Name) && step.Name.Length <= 128 && !step.Name.Any(char.IsControl),
                "Steps need a name of 1..128 printable characters.");
            Require(step!.Action is "wait" or "assert" or "keys" or "delay" or "input" or "capture", "Unknown step action.");
            Require(double.IsFinite(step.TimeoutSeconds) && step.TimeoutSeconds > 0 && step.TimeoutSeconds <= 3600,
                "Step timeoutSeconds must be greater than zero and at most 3600.");
            Require(double.IsFinite(step.Seconds) && step.Seconds >= 0 && step.Seconds < spec.EmulatedSeconds,
                "Step seconds must be nonnegative and shorter than the run deadline.");
            Require((step.Action is "wait" or "assert") == (step.Condition is not null), "Only wait/assert steps require a condition.");
            Require((step.Action == "keys") == (step.Text is not null) && (step.Text is null || step.Text.Length is > 0 and <= 16384),
                "Only keys steps require nonempty text of at most 16384 characters.");
            Require(step.Action == "delay" || step.Seconds == 0, "Only delay steps accept seconds.");
            Require((step.Action == "input") == (step.Input is not null), "Only input steps require an input object.");
            if (step.Input is { } input)
            {
                bool axis = input.Control is "paddle0" or "paddle1" or "paddle2" or "paddle3";
                bool button = input.Control is "button0" or "button1" or "button2" or "button3";
                Require(spec.GamePort != "none" && (axis || button) && (input.Value is null || input.Value >= 0 && input.Value <= (axis ? 255 : 1)),
                    "Inputs need a configured gamePort, paddle0..3 (0..255), or button0..3 (0/1); null releases an override.");
            }
            if (step.Condition is not { } condition) continue;
            Require(condition.Memory is not null && condition.MemoryNotEqual is not null && condition.SymbolicMemory is not null &&
                condition.Registers is not null && condition.TextContains is not null && condition.TextNotContains is not null,
                "Condition arrays cannot be null.");
            Require(condition.SymbolicMemory!.Count == 0, "Symbolic step conditions require build --test.");
            Require(condition.Memory!.Count + condition.MemoryNotEqual!.Count <= 1024 && condition.Registers!.Count <= 128 &&
                condition.TextContains!.Count + condition.TextNotContains!.Count <= 128 &&
                condition.Memory.Count + condition.MemoryNotEqual.Count + condition.Registers.Count + condition.TextContains.Count + condition.TextNotContains.Count > 0,
                "Conditions must contain bounded, nonempty expectations.");
            Require(condition.TextContains.Concat(condition.TextNotContains).All(s => !string.IsNullOrEmpty(s) && s.Length <= 4096),
                "Condition text must contain 1..4096 characters.");
            foreach (MemoryAssertion memory in condition.Memory.Concat(condition.MemoryNotEqual))
            {
                Require(memory is not null && memory.Hex is not null, "Condition memory needs a bank, address and hex bytes.");
                int length = ParseHex(memory!.Hex).Length;
                Require(AppleIIeMemory.IsValidRange(memory.Bank, memory.Address, length) && (memory.Bank == "cpu" || AppleIIeMemory.IsIIeMachine(spec.Machine)),
                    "Condition memory is outside the selected bank/machine.");
                expectedBytes += length;
            }
            Require(condition.Memory.Concat(condition.MemoryNotEqual).Select(m => (m.Bank, m.Address)).Distinct().Count() == condition.Memory.Count + condition.MemoryNotEqual.Count,
                "Condition memory start addresses must be distinct within each bank.");
            Require(condition.Registers.All(r => r is not null && r.Name is not null && RegisterName().IsMatch(r.Name) && r.Value is >= 0 and <= 65535)
                && condition.Registers.Select(r => r.Name).Distinct().Count() == condition.Registers.Count,
                "Condition registers need distinct uppercase names and values 0..65535.");
        }
        Require(expectedBytes <= 65536, "All step conditions together may expect at most 65536 memory bytes.");
    }

    private static void AppendSequenceConfiguration(StringBuilder script, ExecutionSpec spec, string artifacts)
    {
        script.AppendLine($"local checkpoint_directory = {LuaString(artifacts)}");
        script.AppendLine($"local text_columns = {spec.TextColumns}");
        script.AppendLine($"local decode_iie_text = {(spec.DecodeIIeText || spec.TextColumns == 80 ? "true" : "false")}");
        script.AppendLine($"local mouse_text_supported = {(spec.Machine == "apple2e" ? "false" : "true")}");
        script.AppendLine($"local game_port = {LuaString(spec.GamePort)}");
        script.AppendLine("local steps = {");
        foreach (ExecutionStep step in spec.Steps)
        {
            script.AppendLine($"{{ action = {LuaString(step.Action)}, timeout = {Number(step.TimeoutSeconds)}, seconds = {Number(step.Seconds)},");
            if (step.Text is not null) script.AppendLine($"text = {LuaString(step.Text)},");
            if (step.Input is not null)
                script.AppendLine($"input = {{ control = {LuaString(step.Input.Control)}, value = {(step.Input.Value is null ? "nil" : step.Input.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))} }},");
            if (step.Condition is { } condition)
            {
                script.AppendLine("condition = { memory = {");
                foreach (MemoryAssertion memory in condition.Memory)
                    script.AppendLine($"{{ bank={LuaString(memory.Bank)}, address={memory.Address}, hex={LuaString(Convert.ToHexString(ParseHex(memory.Hex)))}, equal=true }},");
                foreach (MemoryAssertion memory in condition.MemoryNotEqual)
                    script.AppendLine($"{{ bank={LuaString(memory.Bank)}, address={memory.Address}, hex={LuaString(Convert.ToHexString(ParseHex(memory.Hex)))}, equal=false }},");
                script.AppendLine("}, registers = {");
                foreach (RegisterAssertion register in condition.Registers)
                    script.AppendLine($"{{ name={LuaString(register.Name)}, value={register.Value} }},");
                script.AppendLine("}, contains = {" + string.Join(",", condition.TextContains.Select(LuaString)) + "},");
                script.AppendLine("absent = {" + string.Join(",", condition.TextNotContains.Select(LuaString)) + "} },");
            }
            script.AppendLine("},");
        }
        script.AppendLine("}");
    }

    private static string CreateSequenceHelpers() => """
        local function current_text()
            local rows = {}
            local alternate = decode_iie_text and read_video_flag("altCharset") ~= 0
            for row = 0, 23 do
                local chars = {}
                local address = page + (row % 8) * 128 + math.floor(row / 8) * 40
                for column = 0, text_columns - 1 do
                    local bank = "cpu"
                    local offset = column
                    if decode_iie_text then
                        bank = "main"
                        if text_columns == 80 then
                            bank = column % 2 == 0 and "aux" or "main"
                            offset = math.floor(column / 2)
                        end
                    end
                    local value = read_bank(bank, address + offset)
                    if decode_iie_text then
                        chars[#chars + 1] = decode_text_byte(value, alternate, mouse_text_supported)
                    else
                        value = value & 127
                        if value < 32 then value = value + 64 end
                        chars[#chars + 1] = string.char(value)
                    end
                end
                rows[#rows + 1] = table.concat(chars)
            end
            return table.concat(rows, "\n")
        end
        local function condition_matches(condition)
            for _, range in ipairs(condition.memory) do
                local actual = read_hex(range.address, #range.hex / 2, range.bank)
                if (actual == range.hex) ~= range.equal then return false end
            end
            for _, register in ipairs(condition.registers) do
                local entry = assert(cpu.state[register.name], "Missing condition register " .. register.name)
                if entry.value ~= register.value then return false end
            end
            if #condition.contains > 0 or #condition.absent > 0 then
                local text = current_text()
                for _, expected in ipairs(condition.contains) do
                    if not text:find(expected, 1, true) then return false end
                end
                for _, unexpected in ipairs(condition.absent) do
                    if text:find(unexpected, 1, true) then return false end
                end
            end
            return true
        end
        local function set_game_input(input)
            local axis = input.control:match("^paddle(%d)$")
            local button = input.control:match("^button(%d)$")
            local tag, mask
            if game_port == "joystick" then
                tag = ":gameio:joy:"
                if axis then
                    local index = tonumber(axis)
                    tag = tag .. "joystick_" .. tostring(math.floor(index / 2) + 1) .. (index % 2 == 0 and "_x" or "_y")
                    mask = 255
                else tag = tag .. "joystick_buttons"; mask = 1 << (tonumber(button) + 4) end
            else
                tag = ":gameio:paddles:"
                if axis then tag = tag .. "paddle_" .. tostring(tonumber(axis) + 1); mask = 255
                else tag = tag .. "paddle_buttons"; mask = 1 << (tonumber(button) + 4) end
            end
            local port = assert(machine.ioport.ports[tag], "Missing game input port " .. tag)
            local field = assert(port:field(mask), "Missing game input field " .. tag)
            if input.value == nil then field:clear_value() else field:set_value(input.value) end
        end
        local step_index, step_started = 1, nil
        local step_results = {}
        local function sequence_ready() return step_index > #steps end
        local function sequence_lines() return step_results end
        local function checkpoint(index, step, now)
            local lines = { "A2EXEC1", "STOP\temulated_limit", "TIME\t" .. tostring(now) }
            local names = {}
            for _, name in ipairs(register_names) do names[name] = true end
            if step.condition then
                for _, register in ipairs(step.condition.registers) do names[register.name] = true end
            end
            for name, _ in pairs(names) do
                local entry = cpu.state[name]
                if entry then lines[#lines + 1] = "REG\t" .. name .. "\t" .. tostring(entry.value) end
            end
            local memory_ranges = step.condition and step.condition.memory or ranges
            for _, range in ipairs(memory_ranges) do
                lines[#lines + 1] = "BANK\t" .. range.bank .. "\t" .. tostring(range.address) .. "\t"
                    .. read_hex(range.address, range.length or #range.hex / 2, range.bank)
            end
            local prefix = checkpoint_directory .. "/checkpoint-" .. string.format("%03d", index)
            local file = assert(io.open(prefix .. ".tsv", "wb"))
            file:write(table.concat(lines, "\n") .. "\nEND\n"); file:close()
            file = assert(io.open(prefix .. ".txt", "wb"))
            file:write(current_text()); file:close()
        end
        """;

    private static string CreateSequenceFrame(ExecutionSpec spec) => """
        local function sequence_frame(now)
            if sequence_ready() then return end
            local step = steps[step_index]
            step_started = step_started or now
            local status = "pass"
            if step.action == "wait" then
                if not condition_matches(step.condition) then
                    if now - step_started < step.timeout then return end
                    status = "timeout"
                end
            elseif step.action == "assert" then
                if not condition_matches(step.condition) then status = "fail" end
            elseif step.action == "delay" then
                if now - step_started < step.seconds then return end
            elseif step.action == "keys" then
                assert(machine.natkeyboard.can_post, "Natural keyboard is unavailable")
                machine.natkeyboard:post(step.text)
            elseif step.action == "input" then set_game_input(step.input)
            end
            if step.action == "wait" or step.action == "assert" or step.action == "capture" then checkpoint(step_index, step, now) end
            step_results[#step_results + 1] = "STEP\t" .. tostring(step_index - 1) .. "\t" .. status .. "\t" .. tostring(now)
            step_index = step_index + 1
            step_started = nil
            if status ~= "pass" then capture(status == "timeout" and "step_timeout" or "step_failed", now) end
        end
        """;
}
