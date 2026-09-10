namespace A2Utils.Core.Graphics;

/// <summary>Screen-page conversion using Apple II interleaved display memory.</summary>
public static class AppleGraphics
{
    // An explicit approximate RGB palette; composite displays vary by monitor and phase.
    public static IReadOnlyList<int> LoresPalette { get; } = Array.AsReadOnly(new[]
    {
        0x000000, 0xdd0033, 0x000099, 0xdd22dd, 0x007722, 0x555555, 0x2222ff, 0x66aaff,
        0x885500, 0xff6600, 0xaaaaaa, 0xff9988, 0x11dd00, 0xffff00, 0x44ff99, 0xffffff
    });
    private static readonly int[] HiresPalette = [0x000000, 0xdd22dd, 0x11dd00, 0xffffff, 0x2222ff, 0xff6600];

    public static byte[] EncodePng(byte[] png, string mode) => Encode(PngCodec.Decode(png), mode);
    public static byte[] DecodePng(byte[] bytes, string mode) => PngCodec.Encode(Decode(bytes, mode));

    public static byte[] Encode(RasterImage image, string mode)
    {
        bool lores = mode == "lores";
        ValidateMode(mode);
        int width = lores ? 40 : 280, height = lores ? 48 : 192;
        if (image.Width != width || image.Height != height || image.Rgb.Length != width * height * 3)
            throw new DiskException("graphics.dimensions", $"{mode} requires a {width} x {height} PNG.", 2);
        byte[] output = new byte[lores ? 1024 : 8192];
        if (lores)
        {
            for (int y = 0; y < 48; y++)
                for (int x = 0; x < 40; x++)
                {
                    int palette = Nearest(image.Rgb.AsSpan((y * 40 + x) * 3, 3), LoresPalette);
                    output[TextOffset(y / 2) + x] |= (byte)(palette << (y % 2 * 4));
                }
        }
        else if (mode == "hires")
        {
            for (int y = 0; y < 192; y++)
                for (int x = 0; x < 280; x++)
                {
                    int pixel = (y * 280 + x) * 3;
                    if (image.Rgb[pixel] * 299 + image.Rgb[pixel + 1] * 587 + image.Rgb[pixel + 2] * 114 >= 128000)
                        output[HiresOffset(y) + x / 7] |= (byte)(1 << (x % 7));
                }
        }
        else
        {
            // Minimize RGB error per seven-dot group, then refine with neighboring groups present.
            for (int pass = 0; pass < 2; pass++)
                for (int y = 0; y < 192; y++)
                    for (int group = 0; group < 40; group++)
                    {
                        long bestError = long.MaxValue;
                        byte best = 0;
                        int index = HiresOffset(y) + group;
                        for (int candidate = 0; candidate < 256; candidate++)
                        {
                            output[index] = (byte)candidate;
                            long error = 0;
                            for (int bit = 0; bit < 7; bit++)
                            {
                                int x = group * 7 + bit;
                                error += Distance(image.Rgb.AsSpan((y * 280 + x) * 3, 3), HiresColor(output, x, y));
                            }
                            if (error < bestError) { bestError = error; best = (byte)candidate; }
                        }
                        output[index] = best;
                    }
        }
        return output;
    }

    public static RasterImage Decode(ReadOnlySpan<byte> bytes, string mode)
    {
        ValidateMode(mode);
        bool lores = mode == "lores";
        if (bytes.Length != (lores ? 1024 : 8192))
            throw new DiskException("graphics.length", $"{mode} requires exactly {(lores ? 1024 : 8192)} raw screen-page bytes.", 2);
        int width = lores ? 40 : 280, height = lores ? 48 : 192;
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int color = lores ? LoresPalette[(bytes[TextOffset(y / 2) + x] >> (y % 2 * 4)) & 15]
                    : mode == "hires" ? (Bit(bytes, x, y) ? 0xffffff : 0) : HiresColor(bytes, x, y);
                int index = (y * width + x) * 3;
                rgb[index] = (byte)(color >> 16); rgb[index + 1] = (byte)(color >> 8); rgb[index + 2] = (byte)color;
            }
        return new(width, height, rgb);
    }

    public static int TextOffset(int row) => row is >= 0 and < 24 ? (row & 7) * 128 + (row >> 3) * 40 : throw new ArgumentOutOfRangeException(nameof(row));
    public static int HiresOffset(int y) => y is >= 0 and < 192 ? (y & 7) * 1024 + ((y >> 3) & 7) * 128 + (y >> 6) * 40 : throw new ArgumentOutOfRangeException(nameof(y));

    private static bool Bit(ReadOnlySpan<byte> bytes, int x, int y) => x is >= 0 and < 280 && (bytes[HiresOffset(y) + x / 7] & (1 << (x % 7))) != 0;
    private static int HiresColor(ReadOnlySpan<byte> bytes, int x, int y)
    {
        if (!Bit(bytes, x, y)) return 0;
        if (Bit(bytes, x - 1, y) || Bit(bytes, x + 1, y)) return 0xffffff;
        int phase = bytes[HiresOffset(y) + x / 7] >> 7;
        return HiresPalette[phase == 0 ? 1 + x % 2 : 4 + x % 2];
    }

    private static int Nearest(ReadOnlySpan<byte> rgb, IReadOnlyList<int> palette)
    {
        int best = 0, bestDistance = int.MaxValue;
        for (int index = 0; index < palette.Count; index++)
        {
            int distance = Distance(rgb, palette[index]);
            if (distance < bestDistance) { best = index; bestDistance = distance; }
        }
        return best;
    }

    private static int Distance(ReadOnlySpan<byte> rgb, int color)
    {
        int r = rgb[0] - (color >> 16), g = rgb[1] - ((color >> 8) & 255), b = rgb[2] - (color & 255);
        return r * r + g * g + b * b;
    }

    private static void ValidateMode(string mode)
    {
        if (mode is not ("lores" or "hires" or "hires-color"))
            throw new DiskException("graphics.mode", "Mode must be lores, hires (monochrome), or hires-color (approximate artifact color).", 2);
    }
}
