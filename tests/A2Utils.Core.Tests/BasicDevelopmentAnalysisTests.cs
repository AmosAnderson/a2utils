// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Basic;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class BasicDevelopmentAnalysisTests
{
    [Theory]
    [InlineData("10 PRINT SQR(1,2)")]
    [InlineData("10 PRINT LEFT$(\"A\")")]
    [InlineData("10 PRINT MID$(\"A\",1,1,1)")]
    [InlineData("10 PRINT FN A(1,2)")]
    public void Check_InvalidFunctionArity_RejectsSource(string source)
        => Assert.False(ApplesoftTools.Check(source).Valid);

    [Theory]
    [InlineData("10 PRINT MID$(\"APPLE\",2)")]
    [InlineData("10 PRINT MID$(\"APPLE\",2,3)")]
    [InlineData("10 PRINT SCRN(2,3)")]
    [InlineData("10 A(1,2,3)=4")]
    public void Check_ValidArgumentLists_AcceptsSource(string source)
        => Assert.True(ApplesoftTools.Check(source).Valid);

    [Fact]
    public void Check_LoopAndReturnContext_ReportsAdvisoriesWithoutRejectingBranches()
    {
        BasicCheckResult result = ApplesoftTools.Check("10 NEXT I\n20 RETURN\n30 FOR J=1 TO 5\n40 END");
        Assert.True(result.Valid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "basic.next_without_for" && diagnostic.Severity == "warning");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "basic.for_without_next");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "basic.return_without_gosub");
    }

    [Fact]
    public void Check_NestedLoopsAndNextList_MatchesSignificantVariableNames()
    {
        BasicCheckResult result = ApplesoftTools.Check("10 FOR IX=1 TO 2:FOR JY=1 TO 3\n20 NEXT JY,IX\n30 END");
        Assert.True(result.Valid);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code.Contains("without_", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_LiteralArrayBounds_AreInclusiveAndComputedSubscriptsRemainUnproven()
    {
        BasicCheckResult result = ApplesoftTools.Check("10 DIM A(5,3)\n20 A(5,3)=1\n30 A(6,3)=1\n40 A(I,3)=1");
        Assert.True(result.Valid);
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "basic.array_bounds");
        Assert.Equal(30, result.Diagnostics.Single(diagnostic => diagnostic.Code == "basic.array_bounds").BasicLine);
    }

    [Theory]
    [InlineData("10 PRINT SQR(-1)", "basic.numeric_argument")]
    [InlineData("10 PRINT LOG(0)", "basic.numeric_argument")]
    [InlineData("10 PRINT CHR$(256)", "basic.numeric_argument")]
    [InlineData("10 A(-1)=2", "basic.negative_subscript")]
    public void Check_ProvablyInvalidLiteral_ReportsError(string source, string code)
    {
        BasicCheckResult result = ApplesoftTools.Check(source);
        Assert.False(result.Valid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Severity == "error");
    }

    [Fact]
    public void RuntimeDiagnostics_LabeledSource_UsesOriginalFileAndPhysicalLine()
    {
        BasicPrepareResult prepared = ApplesoftTools.Prepare("@start:\n\nPRINT 1/0", file: "/project/main.bas");
        BuiltFile file = new("MAIN", "basic-labels", "BAS", 0x801, 0x801, null, 20, "", true,
            new Dictionary<string, int>(), prepared.Mapping);
        ProjectBuildResult build = new("", "", "apple2enh", "65c02", "prodos", "", "", new(2000, 1, 1), [], [file], [], []);
        var diagnostic = Assert.Single(ApplesoftTools.RuntimeDiagnostics("\n?DIVISION BY ZERO ERROR IN 10\n]", build));
        Assert.Equal("/project/main.bas", diagnostic.File);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal(10, diagnostic.BasicLine);
    }

    [Fact]
    public void RuntimeDiagnostics_AmbiguousProgram_DoesNotInventSourceLocation()
    {
        BuiltFile file = new("FIRST", "basic", "BAS", 0x801, 0x801, null, 20, "", true,
            new Dictionary<string, int>(), new[] { new BasicPreparedLine(1, 10, []) { File = "first.bas" } });
        ProjectBuildResult build = new("", "", "apple2enh", "65c02", "prodos", "", "", new(2000, 1, 1), [],
            [file, file with { Path = "SECOND" }], [], []);
        var diagnostic = Assert.Single(ApplesoftTools.RuntimeDiagnostics("?SYNTAX ERROR IN 10", build));
        Assert.Null(diagnostic.File);
        Assert.Contains("multiple", diagnostic.Message);
        Assert.Empty(ApplesoftTools.RuntimeDiagnostics("PRINT \"?SYNTAX ERROR IN 10\"", build));
    }

    [Fact]
    public void RuntimeDiagnostics_StandaloneScreen_ReportsErrorWithoutInventingSource()
    {
        var diagnostic = Assert.Single(ApplesoftTools.RuntimeDiagnostics("\n?BAD SUBSCRIPT ERROR IN 120\n]"));
        Assert.Equal("basic.runtime_error", diagnostic.Code);
        Assert.Equal(120, diagnostic.BasicLine);
        Assert.Null(diagnostic.File);
        Assert.Null(diagnostic.Line);
    }
}
