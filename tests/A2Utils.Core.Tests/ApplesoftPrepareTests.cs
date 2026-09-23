// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Basic;

namespace A2Utils.Core.Tests;

public sealed class ApplesoftPrepareTests
{
    [Fact]
    public void Prepare_ForwardBackwardAndStandaloneLabels_EmitsNumberedSourceAndMapping()
    {
        const string source = "@start: HOME\nIF X=0 THEN @end\nGOSUB @sub\nGOTO @START\n\n@sub:\nPRINT \"HELLO\"\nRETURN\n@end: END\n";
        BasicPrepareResult result = ApplesoftTools.Prepare(source, 100, 5);
        Assert.Equal("100 HOME\n105 IF X=0 THEN 130\n110 GOSUB 120\n115 GOTO 100\n120 PRINT \"HELLO\"\n125 RETURN\n130 END\n", result.Source);
        Assert.Equal(7, result.Mapping.Count);
        Assert.Equal(7, result.Mapping[4].SourceLine);
        Assert.Equal(120, result.Mapping[4].BasicLine);
        Assert.Equal(new[] { "sub" }, result.Mapping[4].Labels);
        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(ApplesoftBasic.Compile(result.Source));
    }

    [Fact]
    public void Prepare_ProtectedStringsDataAndComments_LeaveSymbolLikeTextUntouched()
    {
        const string source = "@start: PRINT \"@missing: GOTO @else\"\nDATA @missing,\"@quoted: GOSUB @no\": GOTO @end\nREM @missing: GOTO @start\n@end: END\n";
        BasicPrepareResult result = ApplesoftTools.Prepare(source);
        Assert.Equal("10 PRINT \"@missing: GOTO @else\"\n20 DATA @missing,\"@quoted: GOSUB @no\": GOTO 40\n30 REM @missing: GOTO @start\n40 END\n", result.Source);
    }

    [Fact]
    public void Prepare_OnListsAndOnerr_RewritesEveryLiteralSymbolicTarget()
    {
        const string source = "ONERR GOTO @done\nON X GOSUB @first,@last\nON X GOTO @first, @done\n@first: @last: RETURN\n@done: END\n";
        BasicPrepareResult result = ApplesoftTools.Prepare(source);
        Assert.Equal("10 ONERR GOTO 50\n20 ON X GOSUB 40,40\n30 ON X GOTO 40, 50\n40 RETURN\n50 END\n", result.Source);
        Assert.Equal(new[] { "first", "last" }, result.Mapping[3].Labels);
    }

    [Fact]
    public void Prepare_IfGotoWithoutThen_AcceptsApplesoftShortcut()
    {
        BasicPrepareResult result = ApplesoftTools.Prepare("IF X=0 GOTO @done\nPRINT \"WORK\"\n@done: END\n");
        Assert.Equal("10 IF X=0 GOTO 30\n20 PRINT \"WORK\"\n30 END\n", result.Source);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("rem")]
    [InlineData("data_label")]
    [InlineData("remark")]
    [InlineData("DATA1")]
    [InlineData("Rem_End")]
    public void Prepare_KeywordLikeLabels_AreAtomicAndDoNotHideLaterReferences(string label)
    {
        BasicPrepareResult result = ApplesoftTools.Prepare($"ON X GOTO @{label},@done\n@{label}: PRINT 1\n@done: END\n");
        Assert.Equal("10 ON X GOTO 20,30\n20 PRINT 1\n30 END\n", result.Source);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("rem")]
    [InlineData("data_label")]
    [InlineData("remark")]
    public void Prepare_KeywordLikeLabelBeforeMissingReference_StillReportsUnresolvedTarget(string label)
    {
        DiskException error = Assert.Throws<DiskException>(() => ApplesoftTools.Prepare($"ON X GOTO @{label},@missing\n@{label}: END\n"));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "basic.undefined_label" && diagnostic.Symbol == "missing");
    }

    [Theory]
    [InlineData("GOTO @unknown\n", "basic.undefined_label")]
    [InlineData("@a: END\n@A: END\n", "basic.duplicate_label")]
    [InlineData("END\n@last:\n", "basic.label_without_statement")]
    [InlineData("@2bad: END\n", "basic.label_definition")]
    [InlineData("@a END\n", "basic.label_definition")]
    [InlineData("10 END\n", "basic.numbered_source")]
    [InlineData("PRINT @a\n@a: END\n", "basic.label_context")]
    [InlineData("GOTO @a+1\n@a: END\n", "basic.label_context")]
    [InlineData("LET X=@a\n@a: END\n", "basic.label_context")]
    [InlineData("GOTO @\n", "basic.label_reference")]
    [InlineData("RUN @a\n@a: END\n", "basic.label_context")]
    public void Prepare_InvalidLabelsOrContexts_RefuseWithSourceDiagnostics(string source, string expectedCode)
    {
        DiskException error = Assert.Throws<DiskException>(() => ApplesoftTools.Prepare(source, file: "labels.bas"));
        Assert.Equal(2, error.ExitCode);
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == expectedCode && diagnostic.File == "labels.bas" && diagnostic.Line > 0);
    }

    [Fact]
    public void Prepare_InvalidStatementAfterLabels_MapsDiagnosticToOriginalPhysicalLine()
    {
        DiskException error = Assert.Throws<DiskException>(() => ApplesoftTools.Prepare("@first:\n\n@alias:\nPRINT 1+\n", file: "labels.bas"));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "basic.expression" && diagnostic.Line == 4 && diagnostic.BasicLine == 10);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(64000, 10)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    [InlineData(63999, 1)]
    [InlineData(10, int.MaxValue)]
    public void Prepare_InvalidOrOverflowingNumbers_Refuses(int start, int step)
    {
        Assert.Equal("basic.prepare_range", Assert.Throws<DiskException>(() => ApplesoftTools.Prepare("HOME\nEND\n", start, step)).Code);
    }

    [Fact]
    public void Prepare_OverlongBodyOrInput_RefusesBeforeReturningSource()
    {
        Assert.Throws<DiskException>(() => ApplesoftTools.Prepare("REM " + new string('A', 251)));
        Assert.Equal("basic.source_line_too_long", Assert.Throws<DiskException>(() => ApplesoftTools.Prepare("REM " + new string('A', 16384))).Code);
        Assert.Equal("basic.source_too_large", Assert.Throws<DiskException>(() => ApplesoftTools.Prepare(new string(' ', ApplesoftBasic.MaximumSourceLength + 1))).Code);
    }

    [Fact]
    public void Prepare_Canceled_ThrowsBeforeReadingSource()
    {
        Assert.Throws<OperationCanceledException>(() => ApplesoftTools.Prepare("END", cancellationToken: new(true)));
    }
}
