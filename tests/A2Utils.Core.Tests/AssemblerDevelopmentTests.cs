using System.Security.Cryptography;
using A2Utils.Core.Assembly;

namespace A2Utils.Core.Tests;

public sealed class AssemblerDevelopmentTests
{
    [Fact]
    public void Assemble_LocalLabelsAndAlignment_ExportsAddressesAndSourceLocations()
    {
        AssemblyResult result = Assembler.Assemble("""
            .org $2000
            first: ldx #2
            @loop: dex
                bne @loop
                .align 8,$ee
            second: ldx #1
            @loop: dex
                bne @loop
            end: .assert end-first == 13,"unexpected size"
            """);

        Assert.Equal(new byte[] { 0xa2, 2, 0xca, 0xd0, 0xfd, 0xee, 0xee, 0xee, 0xa2, 1, 0xca, 0xd0, 0xfd }, result.Bytes);
        Assert.Equal(0x2002, result.Symbols["FIRST@LOOP"]);
        Assert.Equal(0x200a, result.Symbols["second@loop"]);
        AssemblySourceMapEntry padding = Assert.Single(result.SourceMap, entry => entry.Line == 5);
        Assert.Equal(0x2005, padding.Address);
        Assert.Equal(3, padding.Length);
        Assert.Contains("2005  EEEEEE", Assembler.CreateListing(result));
        Assert.Contains("<source>:3", Assembler.CreateListing(result));
    }

    [Theory]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("32/3", 10)]
    [InlineData("32%3", 2)]
    [InlineData("1<<2+1", 8)]
    [InlineData("128>>2", 32)]
    [InlineData("$a0|$0f&3", 163)]
    [InlineData("$aa^$ff", 85)]
    [InlineData("<~$aa", 85)]
    [InlineData("<$1234+1", 53)]
    [InlineData("2<=2", 1)]
    [InlineData("2!=2", 0)]
    [InlineData("3>2", 1)]
    public void Assemble_ExpressionOperators_UsesDocumentedPrecedence(string expression, byte expected)
    {
        Assert.Equal(new byte[] { expected }, Assembler.Assemble(".byte " + expression, 0).Bytes);
    }

    [Theory]
    [InlineData(".byte 1/0", "assembly.division_by_zero")]
    [InlineData(".word 1<<64", "assembly.shift_range")]
    [InlineData(".word 1<<-1", "assembly.shift_range")]
    [InlineData(".word 1<<63", "assembly.invalid_source")]
    [InlineData(".align 3", "assembly.invalid_alignment")]
    [InlineData(".align 0", "assembly.invalid_alignment")]
    [InlineData(".assert 0,\"too large\"", "assembly.assertion_failed")]
    [InlineData("bne missing", "assembly.undefined_symbol")]
    [InlineData("bne $3000", "assembly.branch_range")]
    [InlineData("@loop: nop", "assembly.local_scope")]
    public void Assemble_InvalidDevelopmentSource_ReturnsStructuredDiagnostic(string source, string code)
    {
        DiskException exception = Assert.Throws<DiskException>(() => Assembler.Assemble(".org $2000\n" + source));
        Assert.Equal("assembly.invalid_source", exception.Code);
        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(2, diagnostic.Line);
    }

    [Fact]
    public void AssembleFile_NestedSourceAndBinaryIncludes_PreservesBytesAndOriginalLocations()
    {
        using FixtureWorkspace workspace = new();
        Directory.CreateDirectory(workspace.NewPath("lib"));
        string main = workspace.NewPath("main.asm");
        string child = Path.GetFullPath(workspace.NewPath("lib/code.asm"));
        string binary = workspace.NewPath("data.bin");
        File.WriteAllText(main, ".org $2000\n.include \"lib/code.asm\"\n.incbin \"data.bin\"\n");
        File.WriteAllText(child, "start: lda #$41\n@done: rts\n");
        File.WriteAllBytes(binary, [0, 0xff, 0x80]);

        AssemblyResult result = Assembler.AssembleFile(main);

        Assert.Equal(new byte[] { 0xa9, 0x41, 0x60, 0, 0xff, 0x80 }, result.Bytes);
        Assert.Equal(0x2000, result.Symbols["start"]);
        Assert.Equal(0x2002, result.Symbols["start@done"]);
        Assert.Contains(result.SourceMap, entry => entry.File == child && entry.Line == 2 && entry.Address == 0x2002);
        Assert.Equal(3, result.Dependencies.Count);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(binary))), result.DependencyHashes[binary]);
    }

    [Fact]
    public void AssembleFile_ErrorInInclude_ReportsIncludedFileAndPhysicalLine()
    {
        using FixtureWorkspace workspace = new();
        string main = workspace.NewPath("main.asm");
        string child = workspace.NewPath("code.asm");
        File.WriteAllText(main, ".org $2000\n.include \"code.asm\"\n");
        File.WriteAllText(child, "nop\nlda missing\n");

        DiskException exception = Assert.Throws<DiskException>(() => Assembler.AssembleFile(main));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(child, diagnostic.File);
        Assert.Equal(2, diagnostic.Line);
        Assert.Equal("missing", diagnostic.Symbol);
        Assert.Contains("Line 2:", exception.Message);
    }

    [Theory]
    [InlineData(".include \"main.asm\"", "assembly.include_cycle")]
    [InlineData(".include \"../outside.asm\"", "assembly.include_path")]
    [InlineData(".incbin \"../outside.bin\"", "assembly.include_path")]
    public void AssembleFile_UnsafeInclude_RejectsBeforeReadingOutsideRoot(string directive, string code)
    {
        using FixtureWorkspace workspace = new();
        string main = workspace.NewPath("main.asm");
        File.WriteAllText(main, ".org 0\n" + directive);

        DiskException exception = Assert.Throws<DiskException>(() => Assembler.AssembleFile(main));

        Assert.Equal(code, Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public void Assemble_LargeConstant_KeepsArithmeticWithoutTruncatingSymbolMap()
    {
        AssemblyResult result = Assembler.Assemble("wide = 4294967296\n.org $2000\n.byte <wide\n");
        Assert.Equal(new byte[] { 0 }, result.Bytes);
        Assert.DoesNotContain("wide", result.Symbols.Keys);
    }

    [Fact]
    public void AssembleFile_IncludeDepthLimit_ReportsBoundedInputError()
    {
        using FixtureWorkspace workspace = new();
        for (int index = 0; index < 33; index++)
            File.WriteAllText(workspace.NewPath($"file{index}.asm"), $".include \"file{index + 1}.asm\"\n");

        DiskException exception = Assert.Throws<DiskException>(() => Assembler.AssembleFile(workspace.NewPath("file0.asm"), 0));

        Assert.Equal("assembly.include_limit", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public void AssembleFile_OversizedBinaryInclude_RejectsBeforeAllocation()
    {
        using FixtureWorkspace workspace = new();
        string main = workspace.NewPath("main.asm");
        File.WriteAllText(main, ".org 0\n.incbin \"data.bin\"");
        File.WriteAllBytes(workspace.NewPath("data.bin"), new byte[65537]);

        DiskException exception = Assert.Throws<DiskException>(() => Assembler.AssembleFile(main));

        Assert.Equal("assembly.include_limit", Assert.Single(exception.Diagnostics).Code);
    }
}
