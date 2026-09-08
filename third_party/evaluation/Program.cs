using System.Security.Cryptography;
using A2Utils.Core.Backends;

string fixture = Path.GetFullPath(args.Length > 0 ? args[0] : "tests/TestData/independent-dos33.do");
string output = Path.Combine(Path.GetTempPath(), "a2utils-engine-eval-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)));
using (DiskSession original = DiskSession.Open(fixture))
{
    Console.WriteLine($"Fixture: {original.Info.FileSystem} {original.Info.Order}; entries={original.List().Count}");
    byte[] raw = original.ReadFile("HELLO.BIN", raw: true);
    Console.WriteLine($"HELLO.BIN: raw={raw.Length}, payload={original.ReadFile("HELLO.BIN").Length}, extents={original.GetSparseExtents("HELLO.BIN").Count}");
    if (raw.Length != 256 || raw[9] != 0xcc) throw new InvalidOperationException("Trailing bytes were not preserved.");
    Console.WriteLine($"Diagnostics: {string.Join("; ", original.Verify().Select(d => d.Severity + ":" + d.Message))}");
}
string modified = Path.Combine(output, "modified.do");
File.Copy(fixture, modified);
using (DiskSession edit = DiskSession.Open(modified, writable: true))
{
    edit.SetAttributes("HELLO.BIN", locked: false);
    edit.Replace("HELLO.BIN", [1, 2, 3, 4]);
    edit.Add("NEWFILE", [0x80, 0xff, 0], "B", 0x3000);
}
using (DiskSession reopened = DiskSession.Open(modified))
{
    if (!reopened.ReadFile("HELLO.BIN").SequenceEqual(new byte[] { 1, 2, 3, 4 }))
        throw new InvalidOperationException("Replacement did not persist.");
    if (reopened.List("HELLO.BIN")[0].AuxType != 0x2000)
        throw new InvalidOperationException("Replacement lost the load address.");
    if (reopened.Verify().Any(d => d.Severity == "error")) throw new InvalidOperationException("Modified image has errors.");
    Console.WriteLine("DOS temporary-copy modify/reopen: PASS");
}
if (before != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))))
    throw new InvalidOperationException("Source fixture changed.");
foreach (string fs in new[] { "dos33", "prodos" })
{
    string created = Path.Combine(output, fs + ".2mg");
    DiskSession.Create(created, fs, container: "2mg", order: "prodos");
    using (FileStream imageStream = File.Open(created, FileMode.Open, FileAccess.ReadWrite))
    using (DiskArc.Disk.TwoIMG container = DiskArc.Disk.TwoIMG.OpenDisk(imageStream,
        new CommonUtil.AppHook(new CommonUtil.SimpleMessageLog())))
    {
        container.Comment = "DiskArc reuse probe metadata";
        container.CreatorData = [0x41, 0x32, 0xff, 0x00];
    }
    byte[] containerBefore = File.ReadAllBytes(created);
    using (DiskSession edit = DiskSession.Open(created, writable: true))
    {
        if (fs == "prodos") edit.Mkdir("DIR");
        edit.Add(fs == "prodos" ? "DIR/TEST" : "TEST", [0, 0xff, 0x80], "BIN", 0x4000);
    }
    using DiskSession read = DiskSession.Open(created);
    if (read.List(recursive: true).Count != (fs == "prodos" ? 2 : 1))
        throw new InvalidOperationException("Creation/catalog failed.");
    byte[] containerAfter = File.ReadAllBytes(created);
    if (!containerBefore.AsSpan(0, 64).SequenceEqual(containerAfter.AsSpan(0, 64)) ||
        !containerBefore.AsSpan(64 + 143360).SequenceEqual(containerAfter.AsSpan(64 + 143360)))
        throw new InvalidOperationException("Container metadata changed during a filesystem mutation.");
    Console.WriteLine($"{fs} 2IMG creation/add/reopen, header/comment/creator preservation: PASS");
}
Console.WriteLine($"Source hash unchanged: {before}");
Console.WriteLine($"Experiment outputs: {output}");
