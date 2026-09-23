// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Graphics;

namespace A2Utils.Core.Tests;

public sealed class DoubleHiresGraphicsTests
{
    [Fact]
    public void Encode_MonochromeDots_UsesPublishedAuxMainInterleaveAndScanlineOffsets()
    {
        byte[] rgb = new byte[560 * 192 * 3];
        foreach ((int x, int y) in new[] { (0, 0), (6, 0), (7, 0), (14, 0), (0, 1), (0, 8), (0, 64), (559, 191) })
            rgb.AsSpan((y * 560 + x) * 3, 3).Fill(255);

        byte[] result = DoubleHiresGraphics.Encode(new(560, 192, rgb));

        // TN #3 Figure 3: aux precedes main, seven LSB-first pixels per byte.
        Assert.Equal(0x41, result[0]);
        Assert.Equal(1, result[8192]);
        Assert.Equal(1, result[1]);
        Assert.Equal(1, result[0x400]);
        Assert.Equal(1, result[0x80]);
        Assert.Equal(1, result[0x28]);
        Assert.Equal(0x40, result[8192 + 0x1fd0 + 39]);
        Assert.Equal(rgb, DoubleHiresGraphics.Decode(result).Rgb);
        byte[] reverse = DoubleHiresGraphics.Encode(new(560, 192, rgb), bankOrder: "main-aux");
        Assert.Equal(result[..8192], reverse[8192..]);
        Assert.Equal(result[8192..], reverse[..8192]);
    }

    [Theory]
    [InlineData(0, "00000000")]
    [InlineData(1, "08112244")]
    [InlineData(8, "44081122")]
    [InlineData(9, "4C193366")]
    [InlineData(4, "22440811")]
    [InlineData(5, "2A552A55")]
    [InlineData(12, "664C1933")]
    [InlineData(13, "6E5D3B77")]
    [InlineData(2, "11224408")]
    [InlineData(3, "1933664C")]
    [InlineData(10, "552A552A")]
    [InlineData(11, "5D3B776E")]
    [InlineData(6, "33664C19")]
    [InlineData(7, "3B776E5D")]
    [InlineData(14, "776E5D3B")]
    [InlineData(15, "7F7F7F7F")]
    public void Encode_SolidColors_MatchesAllAppleTechnicalNoteThreeTableTwoPatterns(int paletteIndex, string expected)
    {
        int color = AppleGraphics.LoresPalette[paletteIndex];
        byte[] rgb = new byte[140 * 192 * 3];
        for (int index = 0; index < rgb.Length; index += 3)
        {
            rgb[index] = (byte)(color >> 16);
            rgb[index + 1] = (byte)(color >> 8);
            rgb[index + 2] = (byte)color;
        }

        byte[] result = DoubleHiresGraphics.Encode(new(140, 192, rgb), "color");

        Assert.Equal(Convert.FromHexString(expected), new byte[] { result[0], result[8192], result[1], result[8193] });
        Assert.Equal(rgb, DoubleHiresGraphics.Decode(result, "color").Rgb);
    }

    [Fact]
    public void Decode_WrongSizeOrOrder_RejectsAmbiguousBankData()
    {
        Assert.Throws<DiskException>(() => DoubleHiresGraphics.Decode(new byte[8192]));
        Assert.Throws<DiskException>(() => DoubleHiresGraphics.Decode(new byte[16384], bankOrder: "interleaved"));
    }
}
