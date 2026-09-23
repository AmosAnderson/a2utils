// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using System.Text;
using A2Utils.Core.Basic;

namespace A2Utils.Core.Tests;

public sealed class ApplesoftBasicTests
{
    [Fact]
    public void Compile_IndependentHelloListing_ProducesLinkedProgramWithoutDosHeader()
    {
        // Hand-encoded from the Apple ROM table: PRINT=$BA, END=$80;
        // first record occupies $0801..$080D and second $080E..$0813.
        byte[] expected = Convert.FromHexString("0E080A00BA2248454C4C4F22001408140080000000");

        Assert.Equal(expected, ApplesoftBasic.Compile("10 PRINT \"HELLO\"\n20 END\n"));
        Assert.Equal("10 PRINT \"HELLO\"\n20 END\n", ApplesoftBasic.Decompile(expected));
    }

    [Theory]
    [InlineData("10 H G R 2:HGR :ON ERR:GO TO 10:F O R M", "903A913AA53AAB31303A814D")]
    [InlineData("10 A TO:AT N:ATN(1):AT O", "41C13AC54E3AE12831293AC54F")]
    [InlineData("10 score=1:format=2", "5343CE45D0313A814DC5D032")]
    [InlineData("10 ?\"Hello PRINT\";chr$(13)", "BA2248656C6C6F205052494E54223BE728313329")]
    [InlineData("10 REM Print:DATA ? \"tail  ", "B2205072696E743A44415441203F20227461696C2020")]
    [InlineData("10 DATA 1,\"a:b\",PRINT:PRINT \"done\"", "8320312C22613A62222C5052494E543ABA22646F6E6522")]
    [InlineData("10 DATA ? REM\"x:PRINT", "83203F2052454D22783A5052494E54")]
    [InlineData("10 PRINT \"unclosed:REM ", "BA22756E636C6F7365643A52454D20")]
    [InlineData("10\tpr\tint\t\"a\tb\"", "BA2261096222")]
    public void Compile_AppleLexicalRules_UsesIndependentExpectedTokens(string source, string bodyHex)
    {
        byte[] expected = Program(Convert.FromHexString(bodyHex));

        Assert.Equal(expected, ApplesoftBasic.Compile(source));
        Assert.Equal(expected, ApplesoftBasic.Compile(ApplesoftBasic.Decompile(expected)));
    }

    [Fact]
    public void Compile_AllRomKeywords_UsesEveryDocumentedTokenValue()
    {
        // Token values are explicitly grouped by the ROM's sixteen-byte ranges, not
        // obtained by reflecting the implementation's table.
        string[] rows =
        [
            "80 END|81 FOR|82 NEXT|83 DATA|84 INPUT|85 DEL|86 DIM|87 READ|88 GR|89 TEXT|8A PR#|8B IN#|8C CALL|8D PLOT|8E HLIN|8F VLIN",
            "90 HGR2|91 HGR|92 HCOLOR=|93 HPLOT|94 DRAW|95 XDRAW|96 HTAB|97 HOME|98 ROT=|99 SCALE=|9A SHLOAD|9B TRACE|9C NOTRACE|9D NORMAL|9E INVERSE|9F FLASH",
            "A0 COLOR=|A1 POP|A2 VTAB|A3 HIMEM:|A4 LOMEM:|A5 ONERR|A6 RESUME|A7 RECALL|A8 STORE|A9 SPEED=|AA LET|AB GOTO|AC RUN|AD IF|AE RESTORE|AF &",
            "B0 GOSUB|B1 RETURN|B2 REM|B3 STOP|B4 ON|B5 WAIT|B6 LOAD|B7 SAVE|B8 DEF|B9 POKE|BA PRINT|BB CONT|BC LIST|BD CLEAR|BE GET|BF NEW",
            "C0 TAB(|C1 TO|C2 FN|C3 SPC(|C4 THEN|C5 AT|C6 NOT|C7 STEP|C8 +|C9 -|CA *|CB /|CC ^|CD AND|CE OR|CF >",
            "D0 =|D1 <|D2 SGN|D3 INT|D4 ABS|D5 USR|D6 FRE|D7 SCRN(|D8 PDL|D9 POS|DA SQR|DB RND|DC LOG|DD EXP|DE COS|DF SIN",
            "E0 TAN|E1 ATN|E2 PEEK|E3 LEN|E4 STR$|E5 VAL|E6 ASC|E7 CHR$|E8 LEFT$|E9 RIGHT$|EA MID$"
        ];

        int count = 0;
        foreach (string entry in rows.SelectMany(row => row.Split('|')))
        {
            byte token = Convert.ToByte(entry[..2], 16);
            string keyword = entry[3..];
            byte[] expected = Program([token]);
            Assert.Equal(expected, ApplesoftBasic.Compile($"10 {keyword}"));
            Assert.Equal($"10 {keyword}\n", ApplesoftBasic.Decompile(expected));
            count++;
        }
        Assert.Equal(107, count);
    }

    [Fact]
    public void Compile_MixedNewlinesBomAndBlankLines_AcceptsPortableSource()
    {
        Assert.Equal(ApplesoftBasic.Compile("0 PRINT 1\n10 END\n63999 REM"),
            ApplesoftBasic.Compile("\ufeff\r\n0 ? 1\r\t\r10 end\n63999 rem\r\n"));
    }

    [Fact]
    public void Compile_RemAndDataTrailingWhitespace_PreservesLiteralBytes()
    {
        string source = "10 REM Lowercase \t  \n20 DATA A,  b, \t \n30 PRINT \"text \t  ";
        byte[] program = ApplesoftBasic.Compile(source);

        Assert.Equal(source + "\n", ApplesoftBasic.Decompile(program));
    }

    [Theory]
    [InlineData(0x100)]
    [InlineData(0x801)]
    [InlineData(0x2001)]
    [InlineData(0xfff8)]
    public void Compile_CustomOrigin_UsesAbsoluteLittleEndianLinks(int origin)
    {
        byte[] expected = Program([0x80], origin: (ushort)origin);

        Assert.Equal(expected, ApplesoftBasic.Compile("10 END", (ushort)origin));
        Assert.Equal("10 END\n", ApplesoftBasic.Decompile(expected, (ushort)origin));
    }

    [Fact]
    public void Compile_EmptySource_ProducesOnlyTerminator()
    {
        Assert.Equal(new byte[] { 0, 0 }, ApplesoftBasic.Compile(" \t\n\r\n"));
        Assert.Equal(string.Empty, ApplesoftBasic.Decompile([0, 0]));
        Assert.Equal(new byte[] { 0, 0 }, ApplesoftBasic.Compile("", 0xfffe));
    }

    [Theory]
    [InlineData("PRINT 1", "line_number")]
    [InlineData("-1 END", "line_number")]
    [InlineData("64000 END", "line_number")]
    [InlineData("999999999999999999999999999 END", "line_number")]
    [InlineData("10 END\n10 PRINT 1", "line_order")]
    [InlineData("20 END\n10 PRINT 1", "line_order")]
    [InlineData("10 \t", "empty_line")]
    [InlineData("10 PRINT \"caf\u00e9\"", "unsupported_character")]
    [InlineData("10 REM \0", "unsupported_character")]
    [InlineData("10 PRINT\u007f", "unsupported_character")]
    [InlineData("10 \ufeffPRINT 1", "unsupported_character")]
    public void Compile_InvalidSource_ReportsSpecificDiagnosticAndSourceLine(string source, string code)
    {
        DiskException exception = Assert.Throws<DiskException>(() => ApplesoftBasic.Compile(source));

        Assert.Equal($"basic.{code}", exception.Code);
        Assert.Contains("Source line", exception.Message);
        Assert.Equal(2, exception.ExitCode);
    }

    [Fact]
    public void Compile_TokenizedLineBoundary_Accepts250AndRejects251Bytes()
    {
        byte[] program = ApplesoftBasic.Compile("10 REM" + new string('x', 249));
        Assert.Equal(257, program.Length);
        Assert.Equal(program, ApplesoftBasic.Compile(ApplesoftBasic.Decompile(program)));

        DiskException exception = Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Compile("10 REM" + new string('x', 250)));
        Assert.Equal("basic.line_too_long", exception.Code);
    }

    [Fact]
    public void Compile_ExpandedSourceLine_DoesNotApplyInteractiveKeyboardLimit()
    {
        string source = "10 " + string.Join(':', Enumerable.Repeat("PRINT 1", 50));

        Assert.True(source.Length > 239);
        byte[] program = ApplesoftBasic.Compile(source);
        Assert.Equal(program, ApplesoftBasic.Compile(ApplesoftBasic.Decompile(program)));
    }

    [Fact]
    public void Compile_WholeSourceLimit_RejectsExcessiveWhitespaceBeforeTokenization()
    {
        DiskException exception = Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Compile(new string(' ', ApplesoftBasic.MaximumSourceLength + 1)));

        Assert.Equal("basic.source_too_large", exception.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xff)]
    [InlineData(0xffff)]
    public void Convert_InvalidOrigin_RejectsUnusableLineLinks(int origin)
    {
        Assert.Equal("basic.origin", Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Compile("", (ushort)origin)).Code);
        Assert.Equal("basic.origin", Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Decompile([0, 0], (ushort)origin)).Code);
    }

    [Fact]
    public void Compile_OriginWouldWrap_RejectsBeforeTruncatingAddress()
    {
        DiskException exception = Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Compile("10 END", 0xfff9));

        Assert.Equal("basic.program_too_large", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("000000")]
    [InlineData("0108")]
    [InlineData("01080A0080000000")]
    [InlineData("06080A0080000000")]
    [InlineData("08080A0080000000")]
    [InlineData("07080A0080010000")]
    [InlineData("07080A0000000000")]
    [InlineData("070800FA80000000")]
    [InlineData("07080A008000")]
    [InlineData("07080A0080000100")]
    [InlineData("07080A0080000D080A0080000000")]
    [InlineData("0708140080000D080A0080000000")]
    [InlineData("07080A0080000108140080000000")]
    public void Decompile_MalformedLinksOrStructure_RejectsWithoutPartialListing(string hex)
    {
        DiskException exception = Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Decompile(Convert.FromHexString(hex)));

        Assert.Equal("basic.invalid_program", exception.Code);
        Assert.Equal(4, exception.ExitCode);
    }

    [Fact]
    public void Decompile_WrongOrigin_RejectsInconsistentLinks()
    {
        Assert.Throws<DiskException>(() => ApplesoftBasic.Decompile(Program([0x80]), 0x2001));
    }

    [Theory]
    [InlineData("EB")]
    [InlineData("FF")]
    [InlineData("BA228022")]
    [InlineData("B2FF")]
    [InlineData("83BA")]
    [InlineData("BA01")]
    [InlineData("BA7F")]
    public void Decompile_UnsupportedRawOrTokenByte_ReportsBasicLineAndByte(string bodyHex)
    {
        DiskException exception = Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Decompile(Program(Convert.FromHexString(bodyHex))));

        Assert.Equal("basic.unsupported_byte", exception.Code);
        Assert.Contains("BASIC line 10", exception.Message);
        Assert.Contains("body offset", exception.Message);
    }

    [Theory]
    [InlineData("5052494E54")]
    [InlineData("BA2061")]
    [InlineData("BA3F")]
    [InlineData("91" + "32")]
    public void Decompile_UnrepresentableNoncanonicalBytes_RejectsRatherThanChangingProgram(string bodyHex)
    {
        DiskException exception = Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Decompile(Program(Convert.FromHexString(bodyHex))));

        Assert.Equal("basic.noncanonical_program", exception.Code);
    }

    [Fact]
    public void Decompile_OversizedLine_RejectsUnsafeEightBitLineTraversal()
    {
        byte[] body = Encoding.ASCII.GetBytes("X" + new string('x', 250));
        body[0] = 0xb2;

        Assert.Equal("basic.invalid_program", Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Decompile(Program(body))).Code);
    }

    [Fact]
    public void Decompile_ProgramExtendsPastAddressSpace_Rejects()
    {
        Assert.Equal("basic.invalid_program", Assert.Throws<DiskException>(() =>
            ApplesoftBasic.Decompile(new byte[0x10000])).Code);
    }

    [Fact]
    public void Convert_CanceledToken_StopsBeforeProcessing()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ApplesoftBasic.Compile("10 END", cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            ApplesoftBasic.Decompile(Program([0x80]), cancellationToken: cancellation.Token));
    }

    private static byte[] Program(byte[] body, ushort number = 10, ushort origin = 0x0801)
    {
        byte[] program = new byte[body.Length + 7];
        BinaryPrimitives.WriteUInt16LittleEndian(program, checked((ushort)(origin + body.Length + 5)));
        BinaryPrimitives.WriteUInt16LittleEndian(program.AsSpan(2), number);
        body.CopyTo(program.AsSpan(4));
        return program;
    }
}
