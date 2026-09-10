using System.Buffers.Binary;
using System.Diagnostics;

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
    File.WriteAllText(args[Array.IndexOf(args, "-m") + 1], "Map fixture for " + main);
    File.WriteAllText(args[Array.IndexOf(args, "-Ln") + 1], "al 000803 ._main\n");
    return 0;
}
string mode = File.ReadAllText("disk.dsk");
if (args.SequenceEqual(new[] { "-version" }))
{
    if (mode == "hang-version") await Task.Delay(Timeout.Infinite);
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
File.AppendAllText("disk.dsk", "\nmodified disposable copy");
File.WriteAllText("observations.tsv", "A2EXEC1\nSTOP\temulated_limit\nTIME\t3.01\nREG\tA\t42\nMEM\t768\t2A\nEND\n");
File.WriteAllText("screen.txt", "HELLO APPLE II");
Console.WriteLine("Controlled process fixture completed; this is not an emulator run.");
return 0;
