using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class Cc65FeedbackTests
{
    [Fact]
    public void ParseLabels_ViceExports_RetainsCNamesAndIgnoresWideConstants()
    {
        var symbols = Cc65Feedback.ParseLabels("al 002000 ._main\nal 000080 .sp\nal 100000 .wide\nal FFFFFFFF .negative\n");
        Assert.Equal(0x2000, symbols["_main"]);
        Assert.Equal(0x80, symbols["sp"]);
        Assert.Equal(2, symbols.Count);
    }

    [Fact]
    public void ParseLabels_AmbiguousExport_RefusesSilentWrongAddress()
    {
        Assert.Equal("cc65.labels", Assert.Throws<DiskException>(() =>
            Cc65Feedback.ParseLabels("al 002000 ._main\nal 003000 ._main\n")).Code);
    }

    [Fact]
    public void ParseSegments_LinkedMap_SeparatesBssZeroPageAndHeader()
    {
        var segments = Cc65Feedback.ParseSegments("""
            Modules list:
            main.o:
                CODE Offs=000000 Size=000010
            Segment list:
            -------------
            Name                   Start     End    Size  Align
            ----------------------------------------------------
            ZEROPAGE              000080  000099  00001A  00001
            EXEHDR                0007C9  000802  00003A  00001
            CODE                  002000  00200F  000010  00001
            BSS                   002008  002027  000020  00001
            Exports list by name:
            _main 002000
            """);
        Assert.Equal(4, segments.Count);
        Assert.Equal("zero-page", segments[0].Kind);
        Assert.Equal("header", segments[1].Kind);
        Assert.Equal("bss", segments[3].Kind);
        Assert.Equal(32, segments[3].Length);
    }

    [Theory]
    [InlineData("CODE 00FFF0 01000F 000020 00001")]
    [InlineData("CODE 002000 002100 000010 00001")]
    [InlineData("CODE invalid map")]
    public void ParseSegments_InvalidBounds_RejectsIncompleteAccounting(string row)
    {
        Assert.Equal("cc65.map", Assert.Throws<DiskException>(() => Cc65Feedback.ParseSegments("Segment list:\n" + row)).Code);
    }

    [Theory]
    [InlineData("src/main.c(12): Warning: conversion loses precision", 12, null, "warning")]
    [InlineData("src/main.c:15:8: Error: unknown symbol", 15, 8, "error")]
    [InlineData("src/main.c(7,3): Note: previous declaration", 7, 3, "info")]
    public void ParseDiagnostics_CompilerLocations_MapStagedPathsToOriginalSources(string output, int line, int? column, string severity)
    {
        string stage = Path.Combine(TestPaths.TemporaryRoot, "stage");
        string root = Path.Combine(TestPaths.TemporaryRoot, "project");
        var diagnostic = Assert.Single(Cc65Feedback.ParseDiagnostics(output, stage, root));
        Assert.Equal(Path.Combine(root, "src", "main.c"), diagnostic.File);
        Assert.Equal(line, diagnostic.Line);
        Assert.Equal(column, diagnostic.Column);
        Assert.Equal(severity, diagnostic.Severity);
    }

    [Fact]
    public void ValidateConfig_SingleOutputAndTypedSegments_ReportsCustomRuntimeKinds()
    {
        var kinds = Cc65LinkerConfiguration.Validate("""
            # Paths in comments are not output directives: file = "../escape"
            SYMBOLS { __STACKSIZE__: type = weak, value = $0800; }
            MEMORY { RAM: file = %O, start = %S, size = $1000; ZP: file = "", start = $80, size = $1A; }
            SEGMENTS { CODE: load = RAM, type = ro; SCRATCH: load = RAM, type = bss; MYZP: load = ZP, type = zp; }
            FILES { %O: format = bin; }
            """);
        Assert.Equal("bss", kinds["SCRATCH"]);
        Assert.Equal("zero-page", kinds["MYZP"]);
    }

    [Theory]
    [InlineData("MEMORY { RAM: file = \"../escape.bin\", start=$2000,size=$100; }")]
    [InlineData("MEMORY { RAM: file \"%O/../../escape\", start=$2000,size=$100; }")]
    [InlineData("MEMORY { RAM: file = %O/../../escape, start=$2000,size=$100; }")]
    [InlineData("FILES { \"output.bin\": format = bin; }")]
    [InlineData("INCLUDE \"other.cfg\"")]
    [InlineData("MEMORY { RAM: file = \"%O\\\"; start=$2000,size=$100; }")]
    [InlineData("MEMORY { RAM: file = %O, start=$2000,size=$100; } unexpected")]
    public void ValidateConfig_AlternateOutputOrUnsupportedSyntax_RefusesBeforeCompiler(string source)
    {
        Assert.Equal("cc65.linker_config", Assert.Throws<DiskException>(() => Cc65LinkerConfiguration.Validate(source)).Code);
    }
}
