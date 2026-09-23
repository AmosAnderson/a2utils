// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using A2Utils.Core.Graphics;

namespace A2Utils.Core.Tests;

public sealed class GraphicsTests
{
    // Independently encoded by Windows GDI+ (September 10, 2026): red, half-alpha green, blue, transparent.
    private const string IndependentPng = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAYSURBVBhXY/jPwPCf4T9DAwMDw38QYAAAQ9MIeepCscIAAAAASUVORK5CYII=";

    [Fact]
    public void Png_IndependentRgbaFixture_CompositesAlphaOnBlack()
    {
        RasterImage image = PngCodec.Decode(Convert.FromBase64String(IndependentPng));
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 128, 0, 0, 0, 255, 0, 0, 0 }, image.Rgb);
    }

    [Fact]
    public void Png_EncodeDecode_PreservesEveryRgbChannel()
    {
        RasterImage image = new(40, 48, Enumerable.Range(0, 40 * 48 * 3).Select(index => (byte)(index * 97)).ToArray());
        RasterImage decoded = PngCodec.Decode(PngCodec.Encode(image));
        Assert.Equal(image.Width, decoded.Width);
        Assert.Equal(image.Rgb, decoded.Rgb);
    }

    [Fact]
    public void Png_CorruptChecksum_RefusesInsteadOfDecoding()
    {
        byte[] png = Convert.FromBase64String(IndependentPng);
        png[100] ^= 1;
        Assert.Equal("png.invalid", Assert.Throws<DiskException>(() => PngCodec.Decode(png)).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(25)]
    [InlineData(70)]
    [InlineData(127)]
    public void Png_TruncatedInput_RejectsWithBoundedError(int length)
    {
        byte[] png = Convert.FromBase64String(IndependentPng);
        Assert.Throws<DiskException>(() => PngCodec.Decode(png.AsSpan(0, length)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Png_TruncatedZlibWithValidChunkChecksum_Rejects(int removed)
    {
        byte[] png = RewriteImageData(Convert.FromBase64String(IndependentPng), data => data[..^removed]);
        Assert.Equal("png.invalid", Assert.Throws<DiskException>(() => PngCodec.Decode(png)).Code);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("dictionary")]
    [InlineData("checksum")]
    [InlineData("missing-final-block")]
    [InlineData("trailing")]
    [InlineData("duplicate-checksum")]
    [InlineData("concatenated")]
    public void Png_InvalidZlibWithValidChunkChecksum_Rejects(string mutation)
    {
        byte[] png = RewriteImageData(Convert.FromBase64String(IndependentPng), data =>
        {
            switch (mutation)
            {
                case "header": data[0] ^= 1; return data;
                case "dictionary":
                    data[1] = 0x20;
                    data[1] += (byte)((31 - (data[0] * 256 + data[1]) % 31) % 31);
                    return data;
                case "checksum": data[^1] ^= 1; return data;
                case "missing-final-block": data[2] &= 0xfe; return data;
                case "trailing": return [.. data, 0];
                case "duplicate-checksum": return [.. data, .. data[^4..]];
                case "concatenated": return [.. data, .. data];
                default: throw new ArgumentException("Unknown test mutation.");
            }
        });
        Assert.Equal("png.invalid", Assert.Throws<DiskException>(() => PngCodec.Decode(png)).Code);
    }

    [Fact]
    public void Png_ZlibSplitAcrossSingleByteChunks_DecodesIndependentFixture()
    {
        byte[] png = Convert.FromBase64String(IndependentPng);
        byte[] split = RewriteImageData(png, data => data, split: true);
        RasterImage image = PngCodec.Decode(split);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(PngCodec.Decode(png).Rgb, image.Rgb);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0x80)]
    [InlineData(8, 0x28)]
    [InlineData(23, 0x3d0)]
    public void Lores_RowAddress_MatchesIndependentAppleMemoryVectors(int row, int expected)
        => Assert.Equal(expected, AppleGraphics.TextOffset(row));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0x400)]
    [InlineData(8, 0x80)]
    [InlineData(64, 0x28)]
    [InlineData(191, 0x1fd0)]
    public void Hires_RowAddress_MatchesIndependentAppleMemoryVectors(int row, int expected)
        => Assert.Equal(expected, AppleGraphics.HiresOffset(row));

    [Fact]
    public void Lores_PixelPairs_PackLowThenHighNibbleWithZeroHoles()
    {
        byte[] rgb = new byte[40 * 48 * 3];
        Set(rgb, 0, AppleGraphics.LoresPalette[1]);
        Set(rgb, 40, AppleGraphics.LoresPalette[14]);
        Set(rgb, 47 * 40 + 39, AppleGraphics.LoresPalette[15]);
        byte[] raw = AppleGraphics.Encode(new(40, 48, rgb), "lores");
        Assert.Equal(0xe1, raw[0]);
        Assert.Equal(0xf0, raw[0x3f7]);
        Assert.Equal(0, raw[0x3f8]);
        Assert.Equal(rgb, AppleGraphics.Decode(raw, "lores").Rgb);
    }

    [Fact]
    public void Hires_Monochrome_PacksSevenLeastSignificantDotsPerByte()
    {
        byte[] rgb = new byte[280 * 192 * 3];
        foreach (int pixel in new[] { 0, 6, 7, 280, 191 * 280 + 279 }) Set(rgb, pixel, 0xffffff);
        byte[] raw = AppleGraphics.Encode(new(280, 192, rgb), "hires");
        Assert.Equal(0x41, raw[0]);
        Assert.Equal(0x01, raw[1]);
        Assert.Equal(0x01, raw[0x400]);
        Assert.Equal(0x40, raw[0x1ff7]);
        Assert.Equal(rgb, AppleGraphics.Decode(raw, "hires").Rgb);
    }

    [Fact]
    public void Hires_ArtifactPreview_UsesPhaseParityAndAdjacentWhite()
    {
        byte[] raw = new byte[8192];
        raw[0] = 1; raw[1] = 0x81; raw[2] = 3;
        byte[] rgb = AppleGraphics.Decode(raw, "hires-color").Rgb;
        Assert.Equal(new byte[] { 0xdd, 0x22, 0xdd }, rgb[..3]);
        Assert.Equal(new byte[] { 0xff, 0x66, 0 }, rgb[21..24]);
        Assert.Equal(new byte[] { 255, 255, 255 }, rgb[42..45]);
    }

    [Theory]
    [InlineData("lores", 1023)]
    [InlineData("hires", 8193)]
    [InlineData("woz", 0)]
    public void Decode_InvalidModeOrPageSize_Rejects(string mode, int length)
        => Assert.Throws<DiskException>(() => AppleGraphics.Decode(new byte[length], mode));

    private static void Set(byte[] rgb, int pixel, int color)
    {
        rgb[pixel * 3] = (byte)(color >> 16);
        rgb[pixel * 3 + 1] = (byte)(color >> 8);
        rgb[pixel * 3 + 2] = (byte)color;
    }

    private static byte[] RewriteImageData(byte[] png, Func<byte[], byte[]> rewrite, bool split = false)
    {
        using MemoryStream output = new();
        output.Write(png.AsSpan(0, 8));
        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            if (png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8))
            {
                byte[] data = rewrite(png.AsSpan(offset + 8, length).ToArray());
                if (split)
                    foreach (byte value in data) WriteDataChunk(output, [value]);
                else WriteDataChunk(output, data);
            }
            else output.Write(png.AsSpan(offset, length + 12));
            offset += length + 12;
        }
        return output.ToArray();
    }

    private static void WriteDataChunk(Stream output, byte[] data)
    {
        byte[] chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        "IDAT"u8.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        uint checksum = uint.MaxValue;
        foreach (byte value in chunk.AsSpan(4, data.Length + 4))
        {
            checksum ^= value;
            for (int bit = 0; bit < 8; bit++) checksum = (checksum >> 1) ^ ((checksum & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~checksum);
        output.Write(chunk);
    }

}
