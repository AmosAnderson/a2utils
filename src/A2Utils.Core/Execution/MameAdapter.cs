using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace A2Utils.Core.Execution;

/// <summary>Generates a data-only Lua observation script for the documented MAME 0.289 API.</summary>
public static partial class MameAdapter
{
    public const string ApiVersion = "0.289";

    public static void Validate(ExecutionSpec spec)
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new DiskException("execution.invalid_spec", message, 2);
        }
        Require(spec.SchemaVersion == 1, "Only execution schemaVersion 1 is supported.");
        Require(!string.IsNullOrWhiteSpace(spec.Name) && spec.Name.Length <= 128, "name must contain between 1 and 128 characters.");
        Require(spec.ExpectedVersion == ApiVersion, $"This adapter targets MAME {ApiVersion}; expectedVersion must match.");
        Require(spec.Machine is "apple2" or "apple2p" or "apple2e" or "apple2ee" or "apple2c",
            "machine must explicitly select apple2, apple2p, apple2e, apple2ee, or apple2c.");
        Require(!string.IsNullOrWhiteSpace(spec.EmulatorPath), "emulatorPath must identify the MAME executable.");
        Require(!string.IsNullOrWhiteSpace(spec.RomDirectory), "romDirectory is required.");
        Require(!string.IsNullOrWhiteSpace(spec.DiskImage), "diskImage is required.");
        Require(spec.DiskDevice is "flop1" or "flop2", "diskDevice must be flop1 or flop2.");
        Require(double.IsFinite(spec.EmulatedSeconds) && spec.EmulatedSeconds > 0 && spec.EmulatedSeconds <= 3600,
            "emulatedSeconds must be greater than zero and at most 3600.");
        Require(double.IsFinite(spec.HostTimeoutSeconds) && spec.HostTimeoutSeconds > 0 && spec.HostTimeoutSeconds <= 3600,
            "hostTimeoutSeconds must be greater than zero and at most 3600.");
        Require(spec.TextPage is 1 or 2, "textPage must be 1 or 2 (40-column text memory).");
        Require(spec.Keys is not null && spec.Memory is not null && spec.Registers is not null && spec.TextContains is not null,
            "keys, memory, registers and textContains cannot be null.");
        Require(spec.Keys!.Count <= 1024 && spec.Memory!.Count <= 1024 && spec.Registers!.Count <= 128 && spec.TextContains!.Count <= 128,
            "At most 1024 key inputs or memory assertions, and 128 register or text assertions are allowed.");
        foreach (ExecutionKeys key in spec.Keys!)
        {
            Require(key is not null && double.IsFinite(key.AtSeconds) && key.AtSeconds >= 0
                && key.AtSeconds < spec.EmulatedSeconds && key.Text is not null && key.Text.Length <= 16384,
                "Each key input needs text and an atSeconds before the emulated deadline.");
        }
        foreach (MemoryAssertion assertion in spec.Memory!)
        {
            Require(assertion is not null && assertion.Hex is not null, "Memory assertions need address and hex.");
            byte[] bytes = ParseHex(assertion!.Hex!);
            Require(bytes.Length > 0 && bytes.Length <= 65536 && IsObservableRange(assertion.Address, bytes.Length),
                "Memory assertions must stay in $0000-$BFFF or $D000-$FFFF; I/O reads are excluded.");
        }
        foreach (RegisterAssertion register in spec.Registers!)
        {
            Require(register is not null && register.Name is not null && RegisterName().IsMatch(register.Name)
                && register.Value >= 0 && register.Value <= 65535,
                "Register assertions need an uppercase CPU register name and a value between 0 and 65535.");
        }
        Require(spec.Registers!.Select(r => r.Name).Distinct().Count() == spec.Registers.Count,
            "Register assertion names must be unique.");
        Require(spec.Memory!.Select(r => r.Address).Distinct().Count() == spec.Memory.Count,
            "Memory assertion start addresses must be unique.");
        Require(spec.Memory.Sum(r => (long)ParseHex(r.Hex).Length) <= 65536, "Memory assertions may observe at most 65536 bytes in total.");
        Require(spec.TextContains!.All(s => !string.IsNullOrEmpty(s)), "Text assertions must be nonempty strings.");
        if (spec.Until is { } until)
        {
            Require(IsObservableRange(until.Address, 1) && until.Value is >= 0 and <= 255
                && double.IsFinite(until.AfterSeconds) && until.AfterSeconds >= 0 && until.AfterSeconds < spec.EmulatedSeconds,
                "until needs an observable byte address, value 0-255, and afterSeconds before the deadline.");
        }
    }

    public static IReadOnlyList<string> CreateArguments(ExecutionSpec spec, string copiedDisk, string script, string artifacts)
    {
        Validate(spec);
        List<string> arguments = [spec.Machine, "-noreadconfig", "-nowriteconfig", "-rompath", Path.GetFullPath(spec.RomDirectory),
            "-" + spec.DiskDevice, Path.GetFullPath(copiedDisk), "-autoboot_script", Path.GetFullPath(script),
            "-autoboot_delay", "0", "-video", "none", "-sound", "none", "-nothrottle", "-skip_gameinfo",
            "-seconds_to_run", Math.Ceiling(spec.EmulatedSeconds + 2).ToString(CultureInfo.InvariantCulture),
            "-cfg_directory", Path.Combine(artifacts, "cfg"), "-nvram_directory", Path.Combine(artifacts, "nvram"),
            "-diff_directory", Path.Combine(artifacts, "diff"), "-state_directory", Path.Combine(artifacts, "state"),
            "-snapshot_directory", artifacts, "-snapname", "screen", "-noplugins"];
        if (spec.Machine != "apple2c")
        {
            // Disable default serial and speech cards: baseline tests need only the machine and disk controller ROMs.
            arguments.AddRange(["-sl2", "", "-sl4", ""]);
        }
        return arguments;
    }

    public static string CreateScript(ExecutionSpec spec, string artifacts)
    {
        Validate(spec);
        StringBuilder script = new();
        script.AppendLine("-- Generated by a2; no user-supplied Lua is evaluated.");
        script.AppendLine($"local result_path = {LuaString(Path.Combine(artifacts, "observations.tsv"))}");
        script.AppendLine($"local screen_path = {LuaString(Path.Combine(artifacts, "screen.txt"))}");
        script.AppendLine($"local error_path = {LuaString(Path.Combine(artifacts, "adapter-error.txt"))}");
        script.AppendLine($"local trace_path = {LuaString(Path.Combine(artifacts, "trace.tsv"))}");
        script.AppendLine($"local deadline = {Number(spec.EmulatedSeconds)}");
        script.AppendLine($"local page = {spec.TextPage * 1024}");
        script.AppendLine($"local screenshot = {(spec.Screenshot ? "true" : "false")}");
        script.AppendLine($"local trace_enabled = {(spec.Trace ? "true" : "false")}");
        script.AppendLine("local keys = {");
        foreach (ExecutionKeys key in spec.Keys.OrderBy(k => k.AtSeconds))
            script.AppendLine($"  {{ at = {Number(key.AtSeconds)}, text = {LuaString(key.Text)} }},");
        script.AppendLine("}");
        script.AppendLine("local ranges = {");
        foreach (MemoryAssertion assertion in spec.Memory)
            script.AppendLine($"  {{ address = {assertion.Address}, length = {ParseHex(assertion.Hex).Length} }},");
        script.AppendLine("}");
        script.AppendLine("local register_names = { " + string.Join(", ", new[] { "PC", "A", "X", "Y", "P", "S", "SP" }
            .Concat(spec.Registers.Select(r => r.Name)).Distinct().Select(LuaString)) + " }");
        script.AppendLine(spec.Until is { } until
            ? $"local until_condition = {{ address = {until.Address}, value = {until.Value}, after = {Number(until.AfterSeconds)} }}"
            : "local until_condition = nil");
        script.AppendLine("""
            local machine = manager.machine
            local finished = false
            local key_index = 1
            local trace = nil
            local function fail(message)
                if finished then return end
                finished = true
                local file = io.open(error_path, "wb")
                if file then file:write(tostring(message)); file:close() end
                if trace then trace:close(); trace = nil end
                machine:exit()
            end
            local function setup()
                local cpu = assert(machine.devices[":maincpu"], "Missing :maincpu")
                local memory = assert(cpu.spaces["program"], "Missing program address space")
                if #keys > 0 then assert(machine.natkeyboard.can_post, "Natural keyboard is unavailable") end
                if trace_enabled then
                    trace = assert(io.open(trace_path, "wb"))
                    trace:write("seconds\tPC\n")
                end
                local function read_hex(address, length)
                    local bytes = {}
                    for offset = 0, length - 1 do
                        bytes[#bytes + 1] = string.format("%02X", memory:read_u8(address + offset))
                    end
                    return table.concat(bytes)
                end
                local function capture(reason, now)
                    local lines = { "A2EXEC1", "STOP\t" .. reason, "TIME\t" .. tostring(now) }
                    for _, name in ipairs(register_names) do
                        local entry = cpu.state[name]
                        if entry then lines[#lines + 1] = "REG\t" .. name .. "\t" .. tostring(entry.value) end
                    end
                    for _, range in ipairs(ranges) do
                        lines[#lines + 1] = "MEM\t" .. tostring(range.address) .. "\t" .. read_hex(range.address, range.length)
                    end
                    local rows = {}
                    for row = 0, 23 do
                        local chars = {}
                        local address = page + (row % 8) * 128 + math.floor(row / 8) * 40
                        for column = 0, 39 do
                            local value = memory:read_u8(address + column) & 127
                            if value < 32 then value = value + 64 end
                            chars[#chars + 1] = string.char(value)
                        end
                        rows[#rows + 1] = table.concat(chars)
                    end
                    local screen = assert(io.open(screen_path, "wb"))
                    screen:write(table.concat(rows, "\n")); screen:close()
                    if screenshot then machine.video:snapshot() end
                    if trace then trace:close(); trace = nil end
                    local file = assert(io.open(result_path, "wb"))
                    file:write(table.concat(lines, "\n") .. "\nEND\n"); file:close()
                    finished = true
                    machine:exit()
                end
                local function frame()
                    if finished then return end
                    local now = machine.time:as_double()
                    while key_index <= #keys and now >= keys[key_index].at do
                        machine.natkeyboard:post(keys[key_index].text)
                        key_index = key_index + 1
                    end
                    if trace then
                        local pc = cpu.state["PC"]
                        trace:write(tostring(now) .. "\t" .. tostring(pc and pc.value or -1) .. "\n")
                    end
                    if until_condition and now >= until_condition.after
                        and memory:read_u8(until_condition.address) == until_condition.value then
                        capture("completion_condition", now)
                    elseif now >= deadline then
                        capture("emulated_limit", now)
                    end
                end
                -- Keep the subscription alive for the lifetime of this script.
                a2_frame_subscription = emu.add_machine_frame_notifier(function()
                    local ok, message = pcall(frame)
                    if not ok then fail(message) end
                end)
            end
            local ok, message = pcall(setup)
            if not ok then fail(message) end
            """);
        return script.ToString();
    }

    public static byte[] ParseHex(string hex)
    {
        try { return Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal)); }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            throw new DiskException("execution.invalid_spec", "Memory hex must contain complete hexadecimal bytes.", 2);
        }
    }

    private static bool IsObservableRange(int address, int length)
        => address >= 0 && length > 0 && (long)address + length <= 65536
            && (address >= 0xd000 || (long)address + length <= 0xc000);

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // Decimal escapes avoid Lua/JSON differences, newline injection, and delimiter collisions.
    private static string LuaString(string value) => "\"" + string.Concat(Encoding.UTF8.GetBytes(value)
        .Select(b => "\\" + b.ToString("D3", CultureInfo.InvariantCulture))) + "\"";

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegisterName();
}
