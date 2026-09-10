namespace A2Utils.Core.Graphics;

public sealed record BitmapAssetOptions(int CellWidth, int CellHeight,
    int BitsPerByte = 7, string BitOrder = "lsb", int Threshold = 128,
    bool Invert = false, string Kind = "sprite", int? FirstCodePoint = null);

public sealed record BitmapAssetCell(int Index, int Column, int Row, int Offset, int Length, int? CodePoint);

public sealed record BitmapAssetMetadata(int SchemaVersion, string Kind, int CellWidth, int CellHeight,
    int Columns, int Rows, int CellCount, int BitsPerByte, string BitOrder, int Threshold, bool Invert,
    int BytesPerRow, int BytesPerCell, int PayloadLength, IReadOnlyList<BitmapAssetCell> Cells);

public sealed record BitmapAsset(byte[] Bytes, BitmapAssetMetadata Metadata);

/// <summary>Packs fixed-cell monochrome PNG atlases into explicit, row-major software assets.</summary>
public static class GraphicsAssets
{
    public static BitmapAsset Pack(RasterImage image, BitmapAssetOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        Validate(options);
        if (image.Width is < 1 or > 2048 || image.Height is < 1 or > 2048 || image.Rgb is null ||
            image.Rgb.Length != image.Width * image.Height * 3 ||
            image.Width % options.CellWidth != 0 || image.Height % options.CellHeight != 0)
            throw Error("dimensions", "Atlas dimensions must be multiples of the cell dimensions and at most 2048 x 2048.");
        BitmapAssetMetadata metadata = Describe(options, image.Width / options.CellWidth, image.Height / options.CellHeight);
        byte[] output = new byte[metadata.PayloadLength];
        foreach (BitmapAssetCell cell in metadata.Cells)
        {
            for (int y = 0; y < options.CellHeight; y++)
                for (int x = 0; x < options.CellWidth; x++)
                {
                    int pixel = ((cell.Row * options.CellHeight + y) * image.Width + cell.Column * options.CellWidth + x) * 3;
                    bool set = image.Rgb[pixel] * 299 + image.Rgb[pixel + 1] * 587 + image.Rgb[pixel + 2] * 114 >= options.Threshold * 1000;
                    if (set != options.Invert)
                        output[cell.Offset + y * metadata.BytesPerRow + x / options.BitsPerByte] |=
                            (byte)(1 << Bit(options, x));
                }
        }
        return new(output, metadata);
    }

    public static RasterImage Unpack(ReadOnlySpan<byte> bytes, BitmapAssetOptions options, int columns = 1)
    {
        BitmapAssetMetadata metadata = Inspect(bytes, options, columns);
        int width = metadata.Columns * options.CellWidth, height = metadata.Rows * options.CellHeight;
        byte[] rgb = new byte[width * height * 3];
        foreach (BitmapAssetCell cell in metadata.Cells)
        {
            for (int y = 0; y < options.CellHeight; y++)
                for (int x = 0; x < options.CellWidth; x++)
                {
                    bool set = (bytes[cell.Offset + y * metadata.BytesPerRow + x / options.BitsPerByte] & (1 << Bit(options, x))) != 0;
                    byte value = set != options.Invert ? (byte)255 : (byte)0;
                    int pixel = ((cell.Row * options.CellHeight + y) * width + cell.Column * options.CellWidth + x) * 3;
                    rgb[pixel] = rgb[pixel + 1] = rgb[pixel + 2] = value;
                }
        }
        return new(width, height, rgb);
    }

    public static BitmapAssetMetadata Inspect(ReadOnlySpan<byte> bytes, BitmapAssetOptions options, int columns = 1)
    {
        Validate(options);
        int stride = (options.CellWidth + options.BitsPerByte - 1) / options.BitsPerByte;
        int length = stride * options.CellHeight;
        if (bytes.Length is < 1 or > 65536 || bytes.Length % length != 0 || columns < 1 ||
            bytes.Length / length % columns != 0)
            throw Error("length", "Input must contain complete cells in a rectangular atlas, with 1..65536 payload bytes.");
        BitmapAssetMetadata metadata = Describe(options, columns, bytes.Length / length / columns);
        // Reject bits that would be silently discarded by unpacking, including HGR bit 7.
        foreach (BitmapAssetCell cell in metadata.Cells)
            for (int y = 0; y < options.CellHeight; y++)
                for (int group = 0; group < stride; group++)
                {
                    int pixels = Math.Min(options.BitsPerByte, options.CellWidth - group * options.BitsPerByte);
                    int mask = (1 << pixels) - 1;
                    if (options.BitOrder == "msb") mask <<= options.BitsPerByte - pixels;
                    if ((bytes[cell.Offset + y * stride + group] & ~mask) != 0)
                        throw Error("padding", "Unused row-padding and high bits must be zero for a lossless bitmap preview.");
                }
        return metadata;
    }

    private static BitmapAssetMetadata Describe(BitmapAssetOptions options, int columns, int rows)
    {
        int stride = (options.CellWidth + options.BitsPerByte - 1) / options.BitsPerByte;
        int cellLength = stride * options.CellHeight;
        long count = (long)columns * rows;
        if (columns < 1 || rows < 1 || (long)columns * options.CellWidth > 2048 ||
            (long)rows * options.CellHeight > 2048 || count * cellLength > 65536)
            throw Error("size", "Asset must fit 65536 bytes and a 2048 x 2048 preview.");
        int? first = options.Kind == "font" ? options.FirstCodePoint ?? 32 : options.FirstCodePoint;
        if (first is not null && ((long)first + count - 1 > 0x10ffff ||
            first <= 0xdfff && (long)first + count > 0xd800))
            throw Error("codepoint", "Font labels must be Unicode scalar values and cannot cross the surrogate range.");
        BitmapAssetCell[] cells = Enumerable.Range(0, (int)count).Select(index =>
            new BitmapAssetCell(index, index % columns, index / columns, index * cellLength, cellLength,
                first is null ? null : first + index)).ToArray();
        return new(1, options.Kind, options.CellWidth, options.CellHeight, columns, rows, (int)count,
            options.BitsPerByte, options.BitOrder, options.Threshold, options.Invert, stride, cellLength,
            (int)count * cellLength, cells);
    }

    private static int Bit(BitmapAssetOptions options, int x) => options.BitOrder == "lsb"
        ? x % options.BitsPerByte : options.BitsPerByte - 1 - x % options.BitsPerByte;

    private static void Validate(BitmapAssetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CellWidth is < 1 or > 2048 || options.CellHeight is < 1 or > 2048 ||
            options.BitsPerByte is not (7 or 8) || options.BitOrder is not ("lsb" or "msb") ||
            options.Threshold is < 0 or > 255 || options.Kind is not ("sprite" or "tile" or "font") ||
            options.FirstCodePoint is < 0 or > 0x10ffff || options.Kind != "font" && options.FirstCodePoint is not null)
            throw Error("options", "Specify valid cell dimensions, 7/8 bits per byte, lsb/msb order, threshold 0..255, and sprite/tile/font kind; codepoints apply only to fonts.");
    }

    private static DiskException Error(string code, string message) => new("graphics.asset_" + code, message, 2);
}
