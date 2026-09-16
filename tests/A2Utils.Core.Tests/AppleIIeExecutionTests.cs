using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class AppleIIeExecutionTests
{
    [Theory]
    [InlineData("main", 0x0000, 0xc000, true)]
    [InlineData("aux", 0xbfff, 1, true)]
    [InlineData("main", 0xbfff, 2, false)]
    [InlineData("aux", 0xd000, 1, false)]
    [InlineData("lc1", 0xd000, 0x3000, true)]
    [InlineData("lc2", 0xd000, 0x3000, true)]
    [InlineData("aux-lc1", 0xffff, 1, true)]
    [InlineData("aux-lc2", 0xffff, 2, false)]
    [InlineData("lc1", 0xcfff, 2, false)]
    [InlineData("cpu", 0x0000, 0xc000, true)]
    [InlineData("cpu", 0xd000, 0x3000, true)]
    [InlineData("cpu", 0xbfff, 0x1002, false)]
    [InlineData("cpu", 0xc000, 1, false)]
    [InlineData("cpu", 0xcfff, 1, false)]
    [InlineData("cpu", -1, 1, false)]
    [InlineData("cpu", 0, 0, false)]
    [InlineData("cpu", 0xffff, int.MaxValue, false)]
    [InlineData("aux", int.MaxValue, int.MaxValue, false)]
    [InlineData("MAIN", 0x1000, 1, false)]
    [InlineData("unknown", 0x1000, 1, false)]
    [InlineData(null, 0x1000, 1, false)]
    public void IsValidRange_BankBoundaries_ExcludeIoAndOverflow(string? bank, int address, int length, bool expected)
    {
        Assert.Equal(expected, AppleIIeMemory.IsValidRange(bank, address, length));
    }

    [Fact]
    public void Decode_FortyColumns_UsesAppleTextRowInterleaveAndIgnoresPageHoles()
    {
        byte[] main = Page();
        main[0] = 0xc1;
        main[0x80] = 0xc2;
        main[0x28] = 0xc3;
        main[0x50] = 0xc4;
        main[0x3f7] = 0xda;
        main[0x78] = 0xd8; // First unused eight-byte screen hole.

        AppleIIeTextScreen result = AppleIIeTextDecoder.Decode(main, [], 40, false);
        string[] lines = result.Text.Split('\n');

        Assert.Equal(24, lines.Length);
        Assert.All(lines, line => Assert.Equal(40, line.Length));
        Assert.Equal('A', lines[0][0]);
        Assert.Equal('B', lines[1][0]);
        Assert.Equal('C', lines[8][0]);
        Assert.Equal('D', lines[16][0]);
        Assert.Equal('Z', lines[23][39]);
        Assert.DoesNotContain('X', result.Text);
        Assert.Equal(960, result.Cells.Count);
        Assert.Equal(new AppleIIeTextCell(23, 39, "main", 0x3f7, 0xda, "Z", "normal", null), result.Cells[^1]);
    }

    [Fact]
    public void Decode_EightyColumns_InterleavesAuxiliaryBeforeMainAtEveryRow()
    {
        byte[] main = Page();
        byte[] aux = Page();
        aux[0] = 0xc1;
        main[0] = 0xc2;
        aux[1] = 0xc3;
        main[1] = 0xc4;
        aux[0x3f7] = 0xd9;
        main[0x3f7] = 0xda;

        AppleIIeTextScreen result = AppleIIeTextDecoder.Decode(main, aux, 80, false);
        string[] lines = result.Text.Split('\n');

        Assert.All(lines, line => Assert.Equal(80, line.Length));
        Assert.StartsWith("ABCD", lines[0]);
        Assert.EndsWith("YZ", lines[23]);
        Assert.Equal(1920, result.Cells.Count);
        Assert.Equal("aux", result.Cells[0].Bank);
        Assert.Equal("main", result.Cells[1].Bank);
        Assert.Equal(0x3f7, result.Cells[^2].Offset);
    }

    [Theory]
    [InlineData(0x01, false, "A", "inverse")]
    [InlineData(0x20, false, " ", "inverse")]
    [InlineData(0x41, false, "A", "flash")]
    [InlineData(0x61, false, "!", "flash")]
    [InlineData(0x81, false, "A", "normal")]
    [InlineData(0xc1, false, "A", "normal")]
    [InlineData(0xe1, false, "a", "normal")]
    [InlineData(0x01, true, "A", "inverse")]
    [InlineData(0x61, true, "a", "inverse")]
    [InlineData(0xe1, true, "a", "normal")]
    public void Decode_TextAttributes_PreservesNormalInverseFlashAndLowercase(byte value, bool alternate, string text, string mode)
    {
        byte[] main = Page();
        main[0] = value;

        AppleIIeTextCell cell = AppleIIeTextDecoder.Decode(main, [], 40, alternate).Cells[0];

        Assert.Equal(text, cell.Text);
        Assert.Equal(mode, cell.DisplayMode);
        Assert.Equal(value, cell.Value);
        Assert.Null(cell.MouseTextIndex);
    }

    [Fact]
    public void Decode_AlternateMouseText_RetainsAllThirtyTwoGlyphsAsCellsAndStableTokens()
    {
        byte[] main = Page();
        for (int index = 0; index < 32; index++) main[index] = (byte)(0x40 + index);

        AppleIIeTextScreen result = AppleIIeTextDecoder.Decode(main, [], 40, true);

        Assert.StartsWith("{MT:00}{MT:01}", result.Text);
        Assert.Contains("{MT:1E}{MT:1F}", result.Text);
        Assert.Equal(960, result.Cells.Count);
        for (int index = 0; index < 32; index++)
        {
            Assert.Equal(index, result.Cells[index].MouseTextIndex);
            Assert.Equal("mousetext", result.Cells[index].DisplayMode);
            Assert.Equal(index, result.Cells[index].Column);
        }
    }

    [Fact]
    public void Decode_UnenhancedIIeAlternateSet_UsesInverseUppercaseInsteadOfMouseText()
    {
        byte[] main = Page();
        main[0] = 0x41;

        AppleIIeTextCell cell = AppleIIeTextDecoder.Decode(main, [], 40, true, mouseTextSupported: false).Cells[0];

        Assert.Equal("A", cell.Text);
        Assert.Equal("inverse", cell.DisplayMode);
        Assert.Null(cell.MouseTextIndex);
    }

    [Theory]
    [InlineData(1023, 0, 40)]
    [InlineData(1025, 0, 40)]
    [InlineData(1024, 1, 40)]
    [InlineData(1024, 0, 80)]
    [InlineData(1024, 1023, 80)]
    [InlineData(1024, 1024, 81)]
    public void Decode_MalformedPages_RejectsIncompleteOrUnsupportedLayouts(int mainLength, int auxLength, int columns)
    {
        Assert.ThrowsAny<ArgumentException>(() => AppleIIeTextDecoder.Decode(new byte[mainLength], new byte[auxLength], columns, false));
    }

    private static byte[] Page() => Enumerable.Repeat((byte)0xa0, 1024).ToArray();
}
