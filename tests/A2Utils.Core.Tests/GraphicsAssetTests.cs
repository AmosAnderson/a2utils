// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Graphics;

namespace A2Utils.Core.Tests;

public sealed class GraphicsAssetTests
{
    [Theory]
    [InlineData(7, "lsb", "01010200")]
    [InlineData(7, "msb", "40402000")]
    [InlineData(8, "lsb", "8102")]
    [InlineData(8, "msb", "8140")]
    public void Pack_IndependentTwoRowPattern_UsesExplicitPixelBitOrder(int bits, string order, string expected)
    {
        RasterImage image = Pixels(8, 2, (0, 0), (7, 0), (1, 1));
        BitmapAssetOptions options = new(8, 2, bits, order);

        BitmapAsset asset = GraphicsAssets.Pack(image, options);

        Assert.Equal(Convert.FromHexString(expected), asset.Bytes);
        Assert.Equal(image.Rgb, GraphicsAssets.Unpack(asset.Bytes, options).Rgb);
        Assert.Equal(asset.Bytes.Length, asset.Metadata.BytesPerCell);
    }

    [Fact]
    public void Pack_FontAtlas_OrdersCellsBeforeRowsAndLabelsGlyphs()
    {
        RasterImage image = Pixels(4, 4, (0, 0), (1, 1), (3, 0), (2, 1), (0, 2), (2, 3));
        BitmapAssetOptions options = new(2, 2, 8, Kind: "font", FirstCodePoint: 65);

        BitmapAsset asset = GraphicsAssets.Pack(image, options);

        Assert.Equal(new byte[] { 1, 2, 2, 1, 1, 0, 0, 1 }, asset.Bytes);
        Assert.Equal(4, asset.Metadata.CellCount);
        Assert.Equal(68, asset.Metadata.Cells[3].CodePoint);
        Assert.Equal(6, asset.Metadata.Cells[3].Offset);
        Assert.Equal(image.Rgb, GraphicsAssets.Unpack(asset.Bytes, options, 2).Rgb);
    }

    [Fact]
    public void Pack_ThresholdAndInversion_LeavesUnusedPaddingClear()
    {
        RasterImage image = new(3, 1, [127, 127, 127, 128, 128, 128, 255, 255, 255]);
        BitmapAssetOptions options = new(3, 1, Invert: true);

        BitmapAsset asset = GraphicsAssets.Pack(image, options);

        Assert.Equal(new byte[] { 1 }, asset.Bytes);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 255 }, GraphicsAssets.Unpack(asset.Bytes, options).Rgb);
    }

    [Theory]
    [InlineData(7, "lsb", 128)]
    [InlineData(7, "lsb", 8)]
    [InlineData(7, "msb", 1)]
    [InlineData(8, "msb", 1)]
    public void Unpack_UnusedBitsSet_RefusesLossyPreview(int bits, string order, byte value)
    {
        DiskException error = Assert.Throws<DiskException>(() => GraphicsAssets.Unpack([value], new(3, 1, bits, order)));
        Assert.Equal("graphics.asset_padding", error.Code);
    }

    [Fact]
    public void Pack_AssetExceedsMemoryBudget_RejectsBeforeOutputAllocation()
    {
        RasterImage image = new(1024, 1024, new byte[1024 * 1024 * 3]);
        Assert.Equal("graphics.asset_size", Assert.Throws<DiskException>(() => GraphicsAssets.Pack(image, new(8, 8, 8))).Code);
    }

    [Fact]
    public void Unpack_IncompleteCellAndAtlas_RejectsAmbiguousLayout()
    {
        Assert.Throws<DiskException>(() => GraphicsAssets.Unpack([1], new(8, 2, 8)));
        Assert.Throws<DiskException>(() => GraphicsAssets.Unpack([1, 2, 3], new(8, 1, 8), 2));
    }

    [Fact]
    public void Pack_FontSurrogateRange_RejectsInvalidGlyphMetadata()
    {
        Assert.Throws<DiskException>(() => GraphicsAssets.Pack(Pixels(2, 1), new(1, 1, Kind: "font", FirstCodePoint: 0xd7ff)));
    }

    private static RasterImage Pixels(int width, int height, params (int X, int Y)[] white)
    {
        byte[] rgb = new byte[width * height * 3];
        foreach ((int x, int y) in white)
            rgb.AsSpan((y * width + x) * 3, 3).Fill(255);
        return new(width, height, rgb);
    }
}
