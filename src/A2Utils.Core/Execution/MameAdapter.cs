using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

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
        Require(spec.Engine == "mame", "MameAdapter requires engine mame.");
        Require(!string.IsNullOrWhiteSpace(spec.Name) && spec.Name.Length <= 128, "name must contain between 1 and 128 characters.");
        Require(spec.ExpectedVersion == ApiVersion, $"This adapter targets MAME {ApiVersion}; expectedVersion must match.");
        Require(spec.Machine is "apple2" or "apple2p" or "apple2e" or "apple2ee" or "apple2c",
            "machine must explicitly select apple2, apple2p, apple2e, apple2ee, or apple2c.");
        Require(!string.IsNullOrWhiteSpace(spec.EmulatorPath), "emulatorPath must identify the MAME executable.");
        Require(!string.IsNullOrWhiteSpace(spec.RomDirectory), "romDirectory is required.");
        Require(spec.Disks is not null && spec.DiskAssertions is not null && spec.SymbolicMemory is not null,
            "disks, diskAssertions and symbolicMemory cannot be null.");
        Require(spec.SymbolicMemory!.Count == 0 && spec.SymbolicUntil is null,
            "Symbolic assertions require a project build; use a2 build with an execution specification or suite.");
        ValidateDevelopment(spec);
        ExecutionVisual.Validate(spec);
        Require(spec.Disks!.Count <= 2, "At most two disks may be mounted.");
        Require(spec.Disks.Count == 0 || string.IsNullOrEmpty(spec.DiskImage), "Use either diskImage or disks, not both.");
        ValidateStorage(spec);
        Require(IsStorageDevice(spec.StorageProfile, spec.DiskDevice), "diskDevice must belong to the selected storageProfile.");
        IReadOnlyList<ExecutionDisk> disks = spec.GetDisks();
        HashSet<string> devices = new(StringComparer.Ordinal);
        List<string> images = [];
        foreach (ExecutionDisk disk in disks)
        {
            Require(disk is not null && IsStorageDevice(spec.StorageProfile, disk.Device) && !string.IsNullOrWhiteSpace(disk.Image),
                "Each disk needs an image path and a device supported by storageProfile.");
            Require(devices.Add(disk!.Device), "Disk devices must be unique.");
            Require(disk.ExpectedSha256 is null || IsSha256(disk.ExpectedSha256), "Disk expectedSha256 must contain 64 hexadecimal digits.");
            Require(disk.InputOrder is null or "dos" or "prodos" && disk.InputFileSystem is null or "dos33" or "prodos",
                "Disk inputOrder must be dos or prodos; inputFileSystem must be dos33 or prodos.");
            ImageTransactions.ValidatePath(disk.Image);
            foreach (string image in images) ImageTransactions.EnsureDistinctPaths(image, disk.Image);
            images.Add(disk.Image);
        }
        Require(spec.DiskAssertions!.Count <= 1024, "At most 1024 disk assertions are allowed.");
        HashSet<string> assertionPaths = new(StringComparer.Ordinal);
        long assertionBytes = 0;
        foreach (DiskFileAssertion assertion in spec.DiskAssertions)
        {
            Require(assertion is not null && assertion.Device is not null && devices.Contains(assertion.Device)
                && !string.IsNullOrWhiteSpace(assertion.Path) && assertion.Path.Length <= 4096
                && !assertion.Path.Any(char.IsControl),
                "Disk assertions need a mounted device and an image name or path of 1..4096 characters without control characters.");
            Require(assertionPaths.Add(assertion!.Device + "/" + assertion.Path), "Disk assertion paths must be unique within a device.");
            Require(assertion.Sha256 is null || IsSha256(assertion.Sha256), "Disk assertion sha256 must contain 64 hexadecimal digits.");
            Require(assertion.Sha256 is null || assertion.Hex is null, "A disk assertion may specify sha256 or hex, not both.");
            Require(assertion.AuxType is null or >= 0 and <= 65535 && assertion.Length is null or >= 0 and <= 32 * 1024 * 1024,
                "Disk assertion auxType must be 0..65535 and length must be 0..33554432.");
            Require(assertion.Exists || assertion.Sha256 is null && assertion.Hex is null && assertion.Type is null
                && assertion.AuxType is null && assertion.Length is null, "An absent-file assertion cannot include file metadata or payload expectations.");
            if (assertion.Hex is { } hex)
            {
                Require(hex.Length <= 2 * 1024 * 1024, "Disk assertion hex is too large.");
                int length = ParseHex(hex).Length;
                assertionBytes += length;
                Require(assertion.Length is null || assertion.Length == length, "Disk assertion hex and length disagree.");
            }
            if (assertion.Type is { } type)
            {
                try { _ = DiskSession.ParseFileType(type); }
                catch (DiskException) { Require(false, "Disk assertion type must be a known Apple file type or 0x00..0xff."); }
            }
        }
        Require(assertionBytes <= 1024 * 1024, "Disk assertions may contain at most 1 MiB of expected payload bytes.");
        Require(double.IsFinite(spec.EmulatedSeconds) && spec.EmulatedSeconds > 0 && spec.EmulatedSeconds <= 3600,
            "emulatedSeconds must be greater than zero and at most 3600.");
        Require(double.IsFinite(spec.HostTimeoutSeconds) && spec.HostTimeoutSeconds > 0 && spec.HostTimeoutSeconds <= 3600,
            "hostTimeoutSeconds must be greater than zero and at most 3600.");
        Require(spec.TextPage is 1 or 2, "textPage must be 1 or 2.");
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
            Require(bytes.Length > 0 && bytes.Length <= 65536 && AppleIIeMemory.IsValidRange(assertion.Bank, assertion.Address, bytes.Length),
                "Memory assertion range is outside its bank; CPU I/O reads are excluded.");
        }
        foreach (RegisterAssertion register in spec.Registers!)
        {
            Require(register is not null && register.Name is not null && RegisterName().IsMatch(register.Name)
                && register.Value >= 0 && register.Value <= 65535,
                "Register assertions need an uppercase CPU register name and a value between 0 and 65535.");
        }
        Require(spec.Registers!.Select(r => r.Name).Distinct().Count() == spec.Registers.Count,
            "Register assertion names must be unique.");
        Require(spec.Memory!.Select(r => (r.Bank, r.Address)).Distinct().Count() == spec.Memory.Count,
            "Memory assertion start addresses must be unique within each bank.");
        Require(spec.Memory.Sum(r => (long)ParseHex(r.Hex).Length) + spec.ObserveMemory.Sum(r => (long)r.Length) <= 65536,
            "Memory assertions and captures may observe at most 65536 bytes in total.");
        Require(spec.TextContains!.All(s => !string.IsNullOrEmpty(s)), "Text assertions must be nonempty strings.");
        if (spec.Until is { } until)
        {
            Require(AppleIIeMemory.IsValidRange(until.Bank, until.Address, 1) && until.Value is >= 0 and <= 255
                && double.IsFinite(until.AfterSeconds) && until.AfterSeconds >= 0 && until.AfterSeconds < spec.EmulatedSeconds,
                "until needs an observable byte address, value 0-255, and afterSeconds before the deadline.");
        }
        ValidateSequence(spec);
        ValidateInstrumentation(spec);
        if (spec.Audio is { } audio) ExecutionAudio.Validate(audio, spec.EmulatedSeconds);
    }

    public static IReadOnlyList<string> CreateArguments(ExecutionSpec spec, string copiedDisk, string script, string artifacts)
    {
        Validate(spec);
        if (spec.GetDisks().Count != 1)
            throw new DiskException("execution.invalid_spec", "Multiple mounts require a copied-disk mapping.", 2);
        return CreateArguments(spec, new Dictionary<string, string> { [spec.GetDisks()[0].Device] = copiedDisk }, script, artifacts);
    }

    public static IReadOnlyList<string> CreateArguments(ExecutionSpec spec, IReadOnlyDictionary<string, string> copiedDisks,
        string script, string artifacts)
    {
        Validate(spec);
        List<string> arguments = [spec.Machine, "-noreadconfig", "-nowriteconfig", "-rompath", Path.GetFullPath(spec.RomDirectory),
            "-autoboot_script", Path.GetFullPath(script),
            "-autoboot_delay", "0", "-video", "none", "-sound", "none", "-nothrottle", "-skip_gameinfo",
            "-seconds_to_run", Math.Ceiling(spec.EmulatedSeconds + 2).ToString(CultureInfo.InvariantCulture),
            "-cfg_directory", Path.Combine(artifacts, "cfg"), "-nvram_directory", Path.Combine(artifacts, "nvram"),
            "-diff_directory", Path.Combine(artifacts, "diff"), "-state_directory", Path.Combine(artifacts, "state"),
            "-snapshot_directory", artifacts, "-snapname", "screen", "-noplugins"];
        if (spec.Debug is not null || spec.Routine is not null || spec.Cycles is not null) arguments.AddRange(["-debug", "-debugger", "none"]);
        if (spec.Audio is not null) arguments.AddRange(["-wavwrite", Path.GetFullPath(Path.Combine(artifacts, "audio.wav")), "-samplerate", "44100"]);
        if (spec.StorageProfile == "cffa2") arguments.AddRange(["-sl7", spec.Machine == "apple2ee" ? "cffa2" : "cffa202"]);
        if (spec.GamePort != "none") arguments.AddRange(["-gameio", spec.GamePort == "joystick" ? "joy" : "paddles"]);
        if (copiedDisks.Count != spec.GetDisks().Count)
            throw new DiskException("execution.invalid_spec", "Every mounted disk needs exactly one isolated copy.", 2);
        foreach (ExecutionDisk disk in spec.GetDisks())
        {
            if (!copiedDisks.TryGetValue(disk.Device, out string? copy) || string.IsNullOrWhiteSpace(copy))
                throw new DiskException("execution.invalid_spec", "Every mounted disk needs an isolated copy.", 2);
            arguments.AddRange(["-" + disk.Device, Path.GetFullPath(copy)]);
        }
        if (spec.Machine != "apple2c")
        {
            // Disable default serial and speech cards: baseline tests need only the machine and disk controller ROMs.
            arguments.AddRange(["-sl2", "", "-sl4", ""]);
        }
        return arguments;
    }

    public static string CreateScript(ExecutionSpec spec, string artifacts, PreparedRoutine? routine = null)
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
        script.AppendLine($"local screenshot = {(spec.Screenshot || spec.ScreenshotAssertion is not null ? "true" : "false")}");
        script.AppendLine($"local trace_enabled = {(spec.Trace ? "true" : "false")}");
        AppendDebugConfiguration(script, spec);
        AppendInstrumentationConfiguration(script, spec, routine);
        AppendSequenceConfiguration(script, spec, artifacts);
        script.AppendLine($"local debug_enabled = {(spec.Debug is null ? "false" : "true")}");
        script.AppendLine("local keys = {");
        foreach (ExecutionKeys key in spec.Keys.OrderBy(k => k.AtSeconds))
            script.AppendLine($"  {{ at = {Number(key.AtSeconds)}, text = {LuaString(key.Text)} }},");
        script.AppendLine("}");
        script.AppendLine("local ranges = {");
        foreach (MemoryCapture range in spec.Memory.Select(a => new MemoryCapture(a.Address, ParseHex(a.Hex).Length, a.Bank)).Concat(spec.ObserveMemory))
            script.AppendLine($"  {{ address = {range.Address}, length = {range.Length}, bank = {LuaString(range.Bank)} }},");
        script.AppendLine("}");
        script.AppendLine("local register_names = { " + string.Join(", ", new[] { "PC", "A", "X", "Y", "P", "S", "SP" }
            .Concat(spec.Registers.Select(r => r.Name)).Distinct().Select(LuaString)) + " }");
        script.AppendLine(spec.Until is { } until
            ? $"local until_condition = {{ address = {until.Address}, value = {until.Value}, after = {Number(until.AfterSeconds)}, bank = {LuaString(until.Bank)} }}"
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
            """);
        script.AppendLine(AppleIIeMemory.CreateLuaHelpers());
        script.AppendLine(AppleIIeTextDecoder.CreateLuaDecoder());
        script.AppendLine("""
                local debug_observation_lines = function() return {} end
                local instrumentation_lines = function() return {} end
                if #keys > 0 then assert(machine.natkeyboard.can_post, "Natural keyboard is unavailable") end
                if trace_enabled then
                    trace = assert(io.open(trace_path, "wb"))
                    trace:write("seconds\tPC\n")
                end
                local function read_hex(address, length, bank)
                    local bytes = {}
                    for offset = 0, length - 1 do
                        bytes[#bytes + 1] = string.format("%02X", read_bank(bank or "cpu", address + offset))
                    end
                    return table.concat(bytes)
                end
            """);
        script.AppendLine(CreateSequenceHelpers());
        script.AppendLine("""
                local function capture(reason, now)
                    local lines = { "A2EXEC1", "STOP\t" .. reason, "TIME\t" .. tostring(now) }
                    for _, name in ipairs(register_names) do
                        local entry = cpu.state[name]
                        if entry then lines[#lines + 1] = "REG\t" .. name .. "\t" .. tostring(entry.value) end
                    end
                    for _, range in ipairs(ranges) do
                        lines[#lines + 1] = "BANK\t" .. range.bank .. "\t" .. tostring(range.address) .. "\t" .. read_hex(range.address, range.length, range.bank)
                    end
                    for _, line in ipairs(debug_observation_lines()) do lines[#lines + 1] = line end
                    for _, line in ipairs(sequence_lines()) do lines[#lines + 1] = line end
                    for _, line in ipairs(instrumentation_lines()) do lines[#lines + 1] = line end
            """);
        if (spec.DecodeIIeText || spec.TextColumns == 80)
        {
            script.AppendLine("""
                    lines[#lines + 1] = "TEXTMEM\tmain\t" .. read_hex(page, 1024, "main")
                    lines[#lines + 1] = "TEXTMEM\taux\t" .. read_hex(page, 1024, "aux")
                    for _, name in ipairs({"altCharset", "columns80", "page2", "store80", "graphics", "mixed", "hires", "doubleHires", "flash"}) do
                        lines[#lines + 1] = "VIDEO\t" .. name .. "\t" .. tostring(read_video_flag(name))
                    end
                """);
        }
        script.AppendLine("""
                    local screen = assert(io.open(screen_path, "wb"))
                    screen:write(current_text()); screen:close()
                    if screenshot then machine.video:snapshot() end
                    if trace then trace:close(); trace = nil end
                    local file = assert(io.open(result_path, "wb"))
                    file:write(table.concat(lines, "\n") .. "\nEND\n"); file:close()
                    finished = true
                    machine:exit()
                end
            """);
        script.AppendLine(CreateDebugScript(spec));
        script.AppendLine(CreateInstrumentationScript(spec));
        script.AppendLine(CreateSequenceFrame(spec));
        script.AppendLine("""
                local function frame()
                    if finished then return end
                    local now = machine.time:as_double()
                    sequence_frame(now)
                    if finished then return end
                    if sequence_ready() then debug_frame(now); instrumentation_frame(now) end
                    while key_index <= #keys and now >= keys[key_index].at do
                        machine.natkeyboard:post(keys[key_index].text)
                        key_index = key_index + 1
                    end
                    if trace then
                        local pc = cpu.state["PC"]
                        trace:write(tostring(now) .. "\t" .. tostring(pc and pc.value or -1) .. "\n")
                    end
                    if #steps > 0 and sequence_ready() and not until_condition and not debug_enabled and not instrumentation_enabled then
                        capture("sequence_complete", now)
                    elseif sequence_ready() and until_condition and now >= until_condition.after
                        and read_bank(until_condition.bank, until_condition.address) == until_condition.value then
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

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

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
