// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Basic;

namespace A2Utils.Core.Tests;

public sealed class ApplesoftToolsTests
{
    [Theory]
    [InlineData("10 GOTO 999\n", 999)]
    [InlineData("10 GOSUB 999\n20 END\n", 999)]
    [InlineData("10 IF A=1 THEN 999\n20 END\n", 999)]
    [InlineData("10 IF A=1 THEN GOTO 999\n20 END\n", 999)]
    [InlineData("10 ON X GOTO 20,999\n20 END\n", 999)]
    [InlineData("10 ON X GOSUB 999,20\n20 RETURN\n", 999)]
    [InlineData("10 ONERR GOTO 999\n20 END\n", 999)]
    [InlineData("10 RUN 999\n20 END\n", 999)]
    public void Check_MissingLiteralTarget_ReturnsLocatedError(string source, int target)
    {
        BasicCheckResult result = ApplesoftTools.Check(source, "example.bas");
        Assert.False(result.Valid);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "basic.missing_target");
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("example.bas", diagnostic.File);
        Assert.Equal(1, diagnostic.Line);
        Assert.Equal(10, diagnostic.BasicLine);
        Assert.True(diagnostic.Column > 0);
        Assert.Equal(target.ToString(), diagnostic.Actual);
    }

    [Fact]
    public void Check_StringsDataCommentsAndLoops_DoNotTreatProtectedTextAsCode()
    {
        string source = "10 PRINT \"GOTO 999: SCORE=1\"\n20 DATA GOSUB 999,\"THEN 800: A=(\",SCORE=2\n30 REM GOTO 999: SCORE=(\n40 FOR I=1 TO 5 STEP 1\n50 PRINT I;CHR$(65);LEFT$(\"ABC\",2)\n60 NEXT I\n70 IF I>2 THEN GOTO 90\n80 ON I GOSUB 100,100\n90 END\n100 RETURN\n";
        BasicCheckResult result = ApplesoftTools.Check(source);
        Assert.True(result.Valid, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("10 A=\n")]
    [InlineData("10 A=(1+2\n")]
    [InlineData("10 A=1+*2\n")]
    [InlineData("10 PRINT 1+\n")]
    [InlineData("10 IF THEN 20\n20 END\n")]
    [InlineData("10 IF A=2\n")]
    [InlineData("10 FOR I=1 TO\n")]
    [InlineData("10 ON GOTO 20\n20 END\n")]
    [InlineData("10 ON X GOTO 20,\n20 END\n")]
    [InlineData("10 GOTO\n")]
    [InlineData("10 A=1.2.3\n")]
    public void Check_IncompleteCommonSyntax_ReportsError(string source)
    {
        Assert.False(ApplesoftTools.Check(source).Valid);
        // This opt-in checker does not alter the ROM-compatible tokenizer contract.
        Assert.NotEmpty(ApplesoftBasic.Compile(source));
    }

    [Fact]
    public void Check_VariableNameCollisions_AreAdvisoryAndRespectTypeAndArrays()
    {
        BasicCheckResult result = ApplesoftTools.Check("10 COUNT=1:COLOUR=2\n20 COUNT$=\"X\":COLOUR%(1)=2\n30 END\n");
        Assert.True(result.Valid);
        var collision = Assert.Single(result.Diagnostics);
        Assert.Equal("basic.variable_collision", collision.Code);
        Assert.Equal("warning", collision.Severity);
        Assert.Equal("COLOUR", collision.Symbol);
    }

    [Fact]
    public void Check_KeywordInsideAssignmentName_ExplainsTokenization()
    {
        BasicCheckResult result = ApplesoftTools.Check("10 SCORE=1\n");
        Assert.False(result.Valid);
        Assert.Contains(result.Diagnostics, d => d.Code == "basic.keyword_collision" && d.Symbol == "SCORE");
    }

    [Fact]
    public void Check_OpenQuoteAtLineEnd_IsAdvisoryBecauseRomAcceptsIt()
    {
        BasicCheckResult result = ApplesoftTools.Check("10 PRINT \"HELLO\n20 END\n");
        Assert.True(result.Valid);
        Assert.Equal("basic.unterminated_string", Assert.Single(result.Diagnostics).Code);
        Assert.NotEmpty(ApplesoftBasic.Compile("10 PRINT \"HELLO\n20 END\n"));
    }

    [Fact]
    public void Check_WhitespaceInsideKeywordsAndNumbers_UsesRomLexing()
    {
        BasicCheckResult result = ApplesoftTools.Check("10 G O T O 2 0\n20 END\n");
        Assert.True(result.Valid);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Check_PrintAdjacentStringsAndTabExpressions_AcceptsRomSyntax()
    {
        Assert.True(ApplesoftTools.Check("10 PRINT TAB(4)\"HELLO\"CHR$(13)\n20 PRINT 1 EXP(2)\n30 END\n").Valid);
    }

    [Fact]
    public void Renumber_ReferencesAndProtectedText_RewritesOnlyLineNumbersAndTargets()
    {
        string source = "\ufeff  10  IF A=1 THEN 40  : PRINT \"GOTO 20\"\r\n\r\n20 DATA 10,20,\"GOTO 40: THEN 10\": GOSUB 40\r\n30 ON X GOTO 10, 40:REM GOTO 20\r\n40 RETURN\r\n";
        string expected = "\ufeff  100  IF A=1 THEN 130  : PRINT \"GOTO 20\"\r\n\r\n110 DATA 10,20,\"GOTO 40: THEN 10\": GOSUB 130\r\n120 ON X GOTO 100, 130:REM GOTO 20\r\n130 RETURN\r\n";
        BasicRenumberResult result = ApplesoftTools.Renumber(source, 100, 10);
        Assert.Equal(expected, result.Source);
        Assert.Equal(new BasicLineMapping(3, 20, 110), result.Mapping[1]);
        Assert.True(ApplesoftTools.Check(result.Source).Valid);
    }

    [Fact]
    public void Renumber_RunOnerrAndThenGoto_PreservesAllSupportedTargets()
    {
        BasicRenumberResult result = ApplesoftTools.Renumber("1 ONERR GOTO 4\n2 IF X THEN GOTO 4\n3 RUN 1\n4 END\n");
        Assert.Equal("10 ONERR GOTO 40\n20 IF X THEN GOTO 40\n30 RUN 10\n40 END\n", result.Source);
    }

    [Theory]
    [InlineData("10 GOTO X\n20 END\n", "basic.renumber_computed_target")]
    [InlineData("10 GOTO 10+10\n20 END\n", "basic.renumber_computed_target")]
    [InlineData("10 LIST 10,20\n20 END\n", "basic.renumber_range_reference")]
    [InlineData("10 DEL 10,20\n20 END\n", "basic.renumber_range_reference")]
    [InlineData("10 GOTO 999\n", "basic.missing_target")]
    public void Renumber_UnprovableReferences_RefusesWithDiagnostics(string source, string code)
    {
        DiskException exception = Assert.Throws<DiskException>(() => ApplesoftTools.Renumber(source));
        Assert.Equal(2, exception.ExitCode);
        Assert.Contains(exception.Diagnostics, d => d.Code == code);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(64000, 10)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    [InlineData(63999, 1)]
    [InlineData(1, int.MaxValue)]
    public void Renumber_InvalidOrOverflowingRange_Refuses(int start, int step)
    {
        Assert.Equal("basic.renumber_range", Assert.Throws<DiskException>(() =>
            ApplesoftTools.Renumber("10 END\n20 END\n", start, step)).Code);
    }

    [Theory]
    [InlineData("20 END\n10 END\n")]
    [InlineData("10 END\n10 END\n")]
    [InlineData("64000 END\n")]
    [InlineData("HELLO\n")]
    public void Check_MalformedNumberedSource_ReturnsCompilerDiagnostic(string source)
    {
        BasicCheckResult result = ApplesoftTools.Check(source, "bad.bas");
        Assert.False(result.Valid);
        Assert.Equal("bad.bas", Assert.Single(result.Diagnostics).File);
        Assert.Throws<DiskException>(() => ApplesoftTools.Renumber(source));
    }

    [Fact]
    public void Tools_Canceled_ThrowBeforeProcessing()
    {
        CancellationToken cancellation = new(true);
        Assert.Throws<OperationCanceledException>(() => ApplesoftTools.Check("10 END", cancellationToken: cancellation));
        Assert.Throws<OperationCanceledException>(() => ApplesoftTools.Renumber("10 END", cancellationToken: cancellation));
    }
}
