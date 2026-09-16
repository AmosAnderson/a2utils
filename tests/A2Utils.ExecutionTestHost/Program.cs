using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using A2Utils.Core;
using A2Utils.Core.Backends;
using A2Utils.Core.Programs;
using A2Utils.Core.Setup;

// A separate process bounds malformed disk parsing independently of the test runner.
if (args is ["--probe-disk", string imagePath])
{
    try
    {
        using DiskSession disk = DiskSession.Open(imagePath);
        _ = disk.Verify();
        foreach (DiskEntry entry in disk.List(recursive: true).Where(entry => !entry.IsDirectory))
        {
            try { _ = disk.ReadFile(entry.Path, raw: true); }
            catch (DiskException) { }
        }
        return 0;
    }
    catch (DiskException)
    {
        return 0; // A bounded, classified refusal is a successful robustness result.
    }
}

// Process-contract test double. This program does not compile C, emulate an Apple II, or execute generated Lua.
if (args.Contains("--child"))
{
    await Task.Delay(Timeout.Infinite);
    return 0;
}
if (args.SequenceEqual(new[] { "--version" }))
{
    Console.Error.WriteLine("cl65 V0.0 - contract test double");
    return 0;
}
if (args.Contains("-t"))
{
    string main = args.Last(path => Path.GetExtension(path).ToLowerInvariant() is ".c" or ".s" or ".asm" or ".a65");
    string source = File.ReadAllText(main);
    if (Path.GetExtension(main).Equals(".c", StringComparison.OrdinalIgnoreCase))
        File.WriteAllText(Path.ChangeExtension(main, ".s"), "compiler intermediate assembly");
    File.WriteAllText(Path.ChangeExtension(main, ".o"), "compiler intermediate object");
    if (source.Contains("A2_FAKE_FAILURE"))
    {
        Console.Error.WriteLine(main + ":1: invalid C (controlled contract failure)");
        return 1;
    }
    if (args.Contains("-C"))
    {
        string config = args[Array.IndexOf(args, "-C") + 1];
        if (!File.Exists(config) || !Path.GetFullPath(config).StartsWith(Environment.CurrentDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Compiler config was not staged with project inputs.");
    }
    if (source.Contains("A2_FAKE_SLEEP"))
    {
        Process compilerChild = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--child" }
        })!;
        string marker = source.Split('\n').Single(line => line.StartsWith("// PID "))[7..].Trim();
        File.WriteAllText(marker, compilerChild.Id.ToString());
        await Task.Delay(Timeout.Infinite);
    }
    string binary = args[Array.IndexOf(args, "-o") + 1];
    byte[] appleSingle = new byte[60];
    BinaryPrimitives.WriteUInt32BigEndian(appleSingle, 0x00051600);
    BinaryPrimitives.WriteUInt32BigEndian(appleSingle.AsSpan(4), 0x00020000);
    BinaryPrimitives.WriteUInt16BigEndian(appleSingle.AsSpan(24), 2);
    BinaryPrimitives.WriteUInt32BigEndian(appleSingle.AsSpan(26), 1);
    BinaryPrimitives.WriteUInt32BigEndian(appleSingle.AsSpan(30), 58);
    BinaryPrimitives.WriteUInt32BigEndian(appleSingle.AsSpan(34), 2);
    BinaryPrimitives.WriteUInt32BigEndian(appleSingle.AsSpan(38), 11);
    BinaryPrimitives.WriteUInt32BigEndian(appleSingle.AsSpan(42), 50);
    BinaryPrimitives.WriteUInt32BigEndian(appleSingle.AsSpan(46), 8);
    BinaryPrimitives.WriteUInt16BigEndian(appleSingle.AsSpan(50), 0xe3);
    BinaryPrimitives.WriteUInt16BigEndian(appleSingle.AsSpan(52), 6);
    BinaryPrimitives.WriteUInt32BigEndian(appleSingle.AsSpan(54), 0x0803);
    appleSingle[58] = 0x60;
    if (source.Contains("A2_FAKE_INVALID")) appleSingle[0] = 0xff;
    File.WriteAllBytes(binary, appleSingle);
    string mapText = "Map fixture for " + main;
    string labelText = "al 000803 ._main\n";
    if (source.Contains("A2_FAKE_FEEDBACK"))
    {
        mapText = """
            Segment list:
            -------------
            Name                   Start     End    Size  Align
            ----------------------------------------------------
            ZEROPAGE              000080  000099  00001A  00001
            CODE                  000803  000804  000002  00001
            BSS                   000805  000904  000100  00001
            """;
        labelText += "al 009600 .__HIMEM__\nal 000800 .__STACKSIZE__\n";
        Console.Error.WriteLine(main + "(3): Warning: controlled source warning");
    }
    File.WriteAllText(args[Array.IndexOf(args, "-m") + 1], mapText);
    File.WriteAllText(args[Array.IndexOf(args, "-Ln") + 1], labelText);
    return 0;
}
string? firstDisk = Directory.GetFiles(Environment.CurrentDirectory, "disk*").Order(StringComparer.Ordinal).FirstOrDefault();
bool controlDisk = firstDisk is not null && new FileInfo(firstDisk).Length < 1024;
string mode = firstDisk is null ? "routine" : controlDisk ? File.ReadAllText(firstDisk) : "pass";
if (mode == "pass" && File.Exists("routine.bin")) mode = "routine";
if (args.SequenceEqual(new[] { "-version" }))
{
    if (mode == "hang-version") await Task.Delay(Timeout.Infinite);
    if (mode == "environment-change")
    {
        using JsonDocument specification = JsonDocument.Parse(File.ReadAllBytes("spec.json"));
        string lockPath = specification.RootElement.GetProperty("toolchainLock").GetString()!;
        DevelopmentEnvironmentLock locked = JsonSerializer.Deserialize<DevelopmentEnvironmentLock>(
            File.ReadAllBytes("toolchain-lock.json"), DevelopmentEnvironment.JsonOptions)!;
        byte[] changedProfile = File.ReadAllBytes(locked.ProfilePath).Append((byte)'\n').ToArray();
        File.WriteAllBytes(locked.ProfilePath, changedProfile);
        File.WriteAllText(lockPath, JsonSerializer.Serialize(locked with { ProfileSha256 = ProgramFiles.Hash(changedProfile) },
            DevelopmentEnvironment.JsonOptions));
    }
    Console.WriteLine(mode == "wrong-version" ? "0.288 (contract test double)" : "0.289 (contract test double)");
    return 0;
}
if (mode is "hang" or "cancel")
{
    Process child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--child" }
    })!;
    File.WriteAllText("child.pid", child.Id.ToString());
    File.WriteAllText("parent.pid", Environment.ProcessId.ToString());
    await Task.Delay(Timeout.Infinite);
}
if (mode == "failure")
{
    Console.Error.WriteLine("ROM file missing (simulated contract failure)");
    return 7;
}
if (mode == "missing") return 0;
if (mode == "wrong-rom") Console.Error.WriteLine("keyboard.rom WRONG CHECKSUMS:");
if (mode == "adapter-error")
{
    File.WriteAllText("adapter-error.txt", "Missing :maincpu (simulated API failure)");
    return 0;
}
if (mode == "malformed")
{
    File.WriteAllText("observations.tsv", "A2EXEC1\nSTOP\temulated_limit\n");
    File.WriteAllText("screen.txt", "");
    return 0;
}
if (mode == "save-file")
{
    // Change a known logical payload independently of the disk engine; no allocation or metadata changes are needed.
    string dataDisk = args[Array.IndexOf(args, "-flop2") + 1];
    byte[] bytes = File.ReadAllBytes(dataDisk);
    int offset = bytes.AsSpan().IndexOf("BEFORE"u8);
    if (offset < 0 || bytes.AsSpan(offset + 6).IndexOf("BEFORE"u8) >= 0) return 9;
    "AFTER!"u8.CopyTo(bytes.AsSpan(offset));
    File.WriteAllBytes(dataDisk, bytes);
}
if (mode == "corrupt-disk")
    File.WriteAllBytes(args[Array.IndexOf(args, "-flop2") + 1], [0]);
if (controlDisk) File.AppendAllText(firstDisk!, "\nmodified disposable copy");
if (mode == "visual")
    File.WriteAllBytes("screen.png", A2Utils.Core.Graphics.PngCodec.Encode(new(2, 1, [0, 0, 0, 255, 255, 255])));
if (mode == "routine")
{
    if (!File.Exists("routine.bin")) return 8;
    File.WriteAllText("observations.tsv", "A2EXEC1\nSTOP\troutine_return\nTIME\t1.01\nREG\tA\t42\nREG\tPC\t767\nBANK\tcpu\t768\t2A\nCYCLES\t24576\t767\t12\t1\nEND\n");
    File.WriteAllText("screen.txt", "");
    return 0;
}
if (mode is "sequence" or "sequence-timeout")
{
    string checkpoint = "A2EXEC1\nSTOP\temulated_limit\nTIME\t1\nREG\tA\t0\nBANK\tcpu\t768\t00\nEND\n";
    File.WriteAllText("checkpoint-001.tsv", checkpoint);
    File.WriteAllText("checkpoint-001.txt", mode == "sequence" ? "READY" : "WAITING");
    if (mode == "sequence")
    {
        File.WriteAllText("checkpoint-003.tsv", checkpoint.Replace("TIME\t1", "TIME\t2").Replace("768\t00", "768\t2A"));
        File.WriteAllText("checkpoint-003.txt", "DONE");
        File.WriteAllText("observations.tsv", "A2EXEC1\nSTOP\tsequence_complete\nTIME\t2\nBANK\tcpu\t768\t2A\nSTEP\t0\tpass\t1\nSTEP\t1\tpass\t1.1\nSTEP\t2\tpass\t2\nEND\n");
    }
    else
        File.WriteAllText("observations.tsv", "A2EXEC1\nSTOP\tstep_timeout\nTIME\t1\nSTEP\t0\ttimeout\t1\nEND\n");
    File.WriteAllText("screen.txt", mode == "sequence" ? "DONE" : "READY");
    return 0;
}
if (mode == "debug-iie")
{
    // Fixed evidence for serialization/assertion tests, not execution of the generated program or Lua.
    byte[] mainPage = Enumerable.Repeat((byte)0xa0, 1024).ToArray();
    byte[] auxPage = mainPage.ToArray();
    auxPage[0] = 0xc1;
    mainPage[0] = 0xb2;
    mainPage[1] = 0x40;
    File.WriteAllText("observations.tsv", "A2EXEC1\nSTOP\tbreakpoint\nTIME\t3.01\nREG\tA\t42\nREG\tPC\t8192\n" +
        "BANK\tcpu\t768\t2A\nBANK\taux\t768\t55\nBANK\tlc1\t53248\tAA\n" +
        "DEBUG\tbreakpoint\t0\t8192\t-1\t8192\t0\nHIST\t8190\tLDA #$2A\n" +
        "TEXTMEM\tmain\t" + Convert.ToHexString(mainPage) + "\nTEXTMEM\taux\t" + Convert.ToHexString(auxPage) +
        "\nVIDEO\taltCharset\t1\nEND\n");
    File.WriteAllText("screen.txt", "Contract fixture legacy text");
    return 0;
}
File.WriteAllText("observations.tsv", "A2EXEC1\nSTOP\temulated_limit\nTIME\t3.01\nREG\tA\t42\nREG\tPC\t8192\nMEM\t768\t2A\nEND\n");
File.WriteAllText("screen.txt", "HELLO APPLE II");
Console.WriteLine("Controlled process fixture completed; this is not an emulator run.");
return 0;
