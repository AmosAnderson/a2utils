using System.Text;
using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Programs;

namespace A2Utils.Cli.Tests;

public sealed class ProgramWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"a2-program-tests-{Guid.NewGuid():N}");

    public ProgramWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Help_ProgramGroups_DescribesCompilersAndCpuChoices()
    {
        Assert.Contains("asm", Success("--help").Output);
        Assert.Contains("basic", Success("--help").Output);
        Assert.Contains("compile", Success("asm", "--help").Output);
        Assert.Contains("w65c02", Success("asm", "compile", "--help").Output);
        Assert.Contains("decompile", Success("basic", "--help").Output);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("dos")]
    public void Assembly_CompileDecompileCompile_PreservesExactKnownProgram(string format)
    {
        File.WriteAllText(At("source.asm"), ".org $2000\nLDA #$41\nJSR $FDED\nRTS\n");
        byte[] expected = [0xa9, 0x41, 0x20, 0xed, 0xfd, 0x60];
        if (format == "dos") expected = ProgramFileFormat.EncodeDosBinary(expected, 0x2000);
        var result = Success("asm", "compile", At("source.asm"), "--to", At("program.bin"), "--format", format, "--json");
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("asm.compile", json.RootElement.GetProperty("command").GetString());
        Assert.Equal(0x2000, json.RootElement.GetProperty("data").GetProperty("origin").GetInt32());
        Assert.Equal(expected, File.ReadAllBytes(At("program.bin")));
        Success("asm", "decompile", At("program.bin"), "--to", At("listing.asm"), "--format", format, "--origin", "0x2000");
        Success("asm", "assemble", At("listing.asm"), "--to", At("rebuilt.bin"), "--format", format);
        Assert.Equal(expected, File.ReadAllBytes(At("rebuilt.bin")));
    }

    [Fact]
    public void Assembly_DosInput_InfersHeaderLoadAddress()
    {
        File.WriteAllBytes(At("input.bin"), [0x00, 0x30, 0x01, 0x00, 0x60]);
        Success("asm", "disassemble", At("input.bin"), "--to", At("listing.asm"), "--format", "dos");
        Assert.Contains("$3000", File.ReadAllText(At("listing.asm")));
    }

    [Fact]
    public void Assembly_RawWithoutOrigin_RefusesGuessingAddress()
    {
        File.WriteAllBytes(At("input.bin"), [0x60]);
        var result = Run("asm", "decompile", At("input.bin"), "--to", At("listing.asm"), "--json");
        Assert.Equal(2, result.Code);
        Assert.Contains("program.origin_required", result.Error);
        Assert.Empty(result.Output);
        Assert.False(File.Exists(At("listing.asm")));
    }

    [Theory]
    [InlineData("65c02", "STZ $10", new byte[] { 0x64, 0x10 })]
    [InlineData("w65c02", "RMB0 $10", new byte[] { 0x07, 0x10 })]
    public void Assembly_CpuChoice_EnablesSelectedExtensions(string cpu, string instruction, byte[] expected)
    {
        File.WriteAllText(At("source.asm"), instruction);
        Success("asm", "compile", At("source.asm"), "--to", At("program.bin"), "--origin", "0x2000", "--cpu", cpu);
        Assert.Equal(expected, File.ReadAllBytes(At("program.bin")));
        var invalid = Run("asm", "compile", At("source.asm"), "--to", At("bad.bin"), "--origin", "0x2000", "--cpu", "6502");
        Assert.NotEqual(0, invalid.Code);
        Assert.False(File.Exists(At("bad.bin")));
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("dos")]
    public void Basic_CompileDecompileCompile_PreservesCanonicalTokens(string format)
    {
        File.WriteAllText(At("source.bas"), "10 PRINT \"HELLO\"\r\n20 GOTO 10\r\n", new UTF8Encoding(true));
        Success("basic", "tokenize", At("source.bas"), "--to", At("program.basbin"), "--format", format);
        Success("basic", "detokenize", At("program.basbin"), "--to", At("listing.bas"), "--format", format);
        string listing = File.ReadAllText(At("listing.bas"));
        Assert.Contains("PRINT", listing);
        Assert.Contains("\"HELLO\"", listing);
        Success("basic", "compile", At("listing.bas"), "--to", At("rebuilt.basbin"), "--format", format);
        Assert.Equal(File.ReadAllBytes(At("program.basbin")), File.ReadAllBytes(At("rebuilt.basbin")));
    }

    [Fact]
    public void Basic_KnownOneLineProgram_ProducesRomCompatibleTokensAndPointers()
    {
        File.WriteAllText(At("source.bas"), "10 PRINT \"HI\"\n");
        Success("basic", "compile", At("source.bas"), "--to", At("program.basbin"));
        Assert.Equal(new byte[] { 0x0b, 0x08, 0x0a, 0x00, 0xba, 0x22, 0x48, 0x49, 0x22, 0x00, 0x00, 0x00 },
            File.ReadAllBytes(At("program.basbin")));
    }

    [Theory]
    [InlineData("dos33", false)]
    [InlineData("prodos", false)]
    [InlineData("dos33", true)]
    [InlineData("prodos", true)]
    public void Programs_CompileAddDecompileFromImage_PreserveContents(string fs, bool basic)
    {
        string group = basic ? "basic" : "asm";
        File.WriteAllText(At("source.txt"), basic ? "10 PRINT \"DISK\"\n20 END\n" : ".org $3000\nLDA #$41\nRTS\n");
        Success(group, "compile", At("source.txt"), "--to", At("payload.bin"));
        string disk = At("disk.po");
        Success("disk", "create", disk, "--fs", fs);
        List<string> add = ["disk", "add", disk, At("payload.bin"), "--name", "PROGRAM", "--type", basic ? "BAS" : "BIN", "--in-place"];
        if (fs == "prodos") add.AddRange(["--aux-type", basic ? "0x0801" : "0x3000"]);
        else if (!basic) add.AddRange(["--load-address", "0x3000"]);
        Success(add.ToArray());
        byte[] before = File.ReadAllBytes(disk);
        Success(group, "decompile", "PROGRAM", "--from-image", disk, "--to", At("listing.txt"));
        Success(group, "compile", At("listing.txt"), "--to", At("rebuilt.bin"));
        Assert.Equal(File.ReadAllBytes(At("payload.bin")), File.ReadAllBytes(At("rebuilt.bin")));
        Assert.Equal(before, File.ReadAllBytes(disk));
        using DiskSession session = DiskSession.Open(disk);
        Assert.Equal(basic ? 0xfc : 6, session.GetEntry("PROGRAM").FileType);
    }

    [Fact]
    public void Decompile_ImageWrongFileType_RefusesAndPreservesDestination()
    {
        string disk = At("disk.do");
        Success("disk", "create", disk, "--fs", "dos33");
        File.WriteAllText(At("text.txt"), "HELLO");
        Success("disk", "add", disk, At("text.txt"), "--name", "TEXT", "--format", "text", "--in-place");
        var result = Run("asm", "decompile", "TEXT", "--from-image", disk, "--to", At("bad.asm"));
        Assert.Equal(3, result.Code);
        Assert.Contains("program.file_type", result.Error);
        Assert.False(File.Exists(At("bad.asm")));
    }

    [Theory]
    [InlineData("asm", ".org $2000\nRTS")]
    [InlineData("basic", "10 END")]
    public void Compile_ExistingOutputAndSourceAlias_RequireSafeDestination(string group, string source)
    {
        File.WriteAllText(At("source.txt"), source);
        File.WriteAllBytes(At("output.bin"), [1, 2, 3]);
        Assert.Equal(6, Run(group, "compile", At("source.txt"), "--to", At("output.bin")).Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(At("output.bin")));
        Assert.Equal(6, Run(group, "compile", At("source.txt"), "--to", At("source.txt"), "--overwrite").Code);
        Assert.Equal(source, File.ReadAllText(At("source.txt")));
        Success(group, "compile", At("source.txt"), "--to", At("output.bin"), "--overwrite");
    }

    [Theory]
    [InlineData("asm", ".org $2000\nBOGUS")]
    [InlineData("basic", "NOT A NUMBERED LINE")]
    public void Compile_InvalidSource_LeavesExistingOutputUnchanged(string group, string source)
    {
        File.WriteAllText(At("source.txt"), source);
        File.WriteAllBytes(At("output.bin"), [1, 2, 3]);
        var result = Run(group, "compile", At("source.txt"), "--to", At("output.bin"), "--overwrite", "--json");
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("error", result.Error);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(At("output.bin")));
        Assert.Empty(Directory.GetFiles(_directory, ".*.a2-*"));
    }

    [Fact]
    public void Basic_CorruptProgram_ReportsErrorWithoutOutput()
    {
        File.WriteAllBytes(At("bad.bin"), [0x08, 0x08, 0x0a, 0, 0xba]);
        var result = Run("basic", "decompile", At("bad.bin"), "--to", At("bad.bas"));
        Assert.NotEqual(0, result.Code);
        Assert.Empty(result.Output);
        Assert.False(File.Exists(At("bad.bas")));
    }

    [Fact]
    public void Compile_InvalidUtf8_RejectsBeforeWriting()
    {
        File.WriteAllBytes(At("bad.txt"), [0xff, 0xfe]);
        var result = Run("asm", "compile", At("bad.txt"), "--to", At("bad.bin"), "--origin", "0x2000");
        Assert.Equal(2, result.Code);
        Assert.Contains("program.invalid_utf8", result.Error);
        Assert.False(File.Exists(At("bad.bin")));
    }

    [Fact]
    public void Basic_CustomProDosOrigin_UsesFileMetadataForDecompilation()
    {
        File.WriteAllText(At("source.bas"), "10 END\n");
        Success("basic", "compile", At("source.bas"), "--to", At("payload.bin"), "--origin", "0x2001");
        Success("disk", "create", At("disk.po"), "--fs", "prodos");
        Success("disk", "add", At("disk.po"), At("payload.bin"), "--name", "PROGRAM", "--type", "BAS",
            "--aux-type", "0x2001", "--in-place");
        var result = Success("basic", "decompile", "PROGRAM", "--from-image", At("disk.po"), "--to", At("listing.bas"), "--json");
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal(0x2001, json.RootElement.GetProperty("data").GetProperty("origin").GetInt32());
        Assert.Contains("10 END", File.ReadAllText(At("listing.bas")));
    }

    [Fact]
    public void Decompile_OutputAliasesImage_RefusesEvenWithOverwrite()
    {
        Success("disk", "create", At("disk.do"), "--fs", "dos33");
        byte[] before = File.ReadAllBytes(At("disk.do"));
        var result = Run("asm", "decompile", "PROGRAM", "--from-image", At("disk.do"), "--to", At("disk.do"), "--overwrite");
        Assert.Equal(6, result.Code);
        Assert.Contains("write.source_alias", result.Error);
        Assert.Equal(before, File.ReadAllBytes(At("disk.do")));
    }

    [Fact]
    public void ProgramOptions_ConflictingImageAndHostFlags_RejectsUnusedOptions()
    {
        var imageFormat = Run("asm", "decompile", "PROGRAM", "--from-image", "disk.do", "--format", "raw", "--to", At("bad.asm"));
        Assert.Equal(2, imageFormat.Code);
        Assert.Contains("--format", imageFormat.Error);
        var sourceOrder = Run("asm", "compile", "source.asm", "--input-order", "dos", "--to", At("bad.bin"));
        Assert.Equal(2, sourceOrder.Code);
        Assert.Contains("--from-image", sourceOrder.Error);
        var unknownCpu = Run("asm", "compile", "source.asm", "--cpu", "65816", "--to", At("bad.bin"));
        Assert.Equal(2, unknownCpu.Code);
        Assert.Contains("--cpu", unknownCpu.Error);
    }

    [Fact]
    public void Compile_QuietMode_WritesFileWithoutNormalOutput()
    {
        File.WriteAllText(At("source.asm"), ".org $2000\nRTS");
        var result = Success("asm", "compile", At("source.asm"), "--to", At("output.bin"), "--quiet");
        Assert.Empty(result.Output);
        Assert.Empty(result.Error);
        Assert.Equal(new byte[] { 0x60 }, File.ReadAllBytes(At("output.bin")));
    }

    [Fact]
    public void Compile_Canceled_DoesNotCreateOutput()
    {
        File.WriteAllText(At("source.bas"), "10 END");
        using StringWriter output = new();
        using StringWriter error = new();
        int result = CliApplication.Run(["basic", "compile", At("source.bas"), "--to", At("output.bin")],
            output, error, new CancellationToken(canceled: true));
        Assert.Equal(6, result);
        Assert.False(File.Exists(At("output.bin")));
        Assert.Empty(output.ToString());
    }

    private (int Code, string Output, string Error) Success(params string[] args)
    {
        var result = Run(args);
        Assert.True(result.Code == 0, $"Exit {result.Code}: {result.Error}");
        return result;
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string At(string name) => Path.Combine(_directory, name);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
