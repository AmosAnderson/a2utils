namespace A2Utils.Core.Graphics;

/// <summary>Converts explicit auxiliary/main 8 KiB pages using Apple IIe Technical Note #3.</summary>
public static class DoubleHiresGraphics
{
    // TN #3, Figure 3 and Table 2: aux/main alternate seven LSB-first dots.
    // Table 2 color-pattern order differs from the low-resolution palette order.
    // https://mirrors.apple2.org.za/ftp.apple.asimov.net/documentation/hardware/misc/Apple%20IIe%20Technical%20Notes.pdf
    private static readonly int[] PatternToPalette = [0, 1, 8, 9, 4, 5, 12, 13, 2, 3, 10, 11, 6, 7, 14, 15];

    public static byte[] Encode(RasterImage image, string mode = "mono", string bankOrder = "aux-main")
    {
        ArgumentNullException.ThrowIfNull(image);
        Validate(mode, bankOrder);
        int width = mode == "mono" ? 560 : 140;
        if (image.Width != width || image.Height != 192 || image.Rgb.Length != width * 192 * 3)
            throw new DiskException("graphics.dhires_dimensions", $"Double-hires {mode} requires {width} x 192 pixels.", 2);
        byte[] output = new byte[16384];
        for (int y = 0; y < 192; y++)
            for (int x = 0; x < width; x++)
            {
                int pixel = (y * width + x) * 3;
                if (mode == "mono")
                {
                    if (image.Rgb[pixel] * 299 + image.Rgb[pixel + 1] * 587 + image.Rgb[pixel + 2] * 114 >= 128000)
                        SetDot(output, x, y, bankOrder);
                }
                else
                {
                    int pattern = NearestPattern(image.Rgb.AsSpan(pixel, 3));
                    for (int bit = 0; bit < 4; bit++)
                        if ((pattern & (1 << (3 - bit))) != 0) SetDot(output, x * 4 + bit, y, bankOrder);
                }
            }
        return output;
    }

    public static RasterImage Decode(ReadOnlySpan<byte> bytes, string mode = "mono", string bankOrder = "aux-main")
    {
        Validate(mode, bankOrder);
        if (bytes.Length != 16384)
            throw new DiskException("graphics.dhires_length", "Double-hires requires exactly 16384 bytes: two complete screen pages.", 2);
        int width = mode == "mono" ? 560 : 140;
        byte[] rgb = new byte[width * 192 * 3];
        for (int y = 0; y < 192; y++)
            for (int x = 0; x < width; x++)
            {
                int color;
                if (mode == "mono") color = Dot(bytes, x, y, bankOrder) ? 0xffffff : 0;
                else
                {
                    int pattern = 0;
                    for (int bit = 0; bit < 4; bit++) pattern = (pattern << 1) | (Dot(bytes, x * 4 + bit, y, bankOrder) ? 1 : 0);
                    color = AppleGraphics.LoresPalette[PatternToPalette[pattern]];
                }
                int pixel = (y * width + x) * 3;
                rgb[pixel] = (byte)(color >> 16);
                rgb[pixel + 1] = (byte)(color >> 8);
                rgb[pixel + 2] = (byte)color;
            }
        return new(width, 192, rgb);
    }

    private static int Offset(int x, int y, string bankOrder)
    {
        int group = x / 7;
        bool auxiliary = group % 2 == 0;
        int bank = auxiliary == (bankOrder == "aux-main") ? 0 : 8192;
        return bank + AppleGraphics.HiresOffset(y) + group / 2;
    }

    private static void SetDot(byte[] bytes, int x, int y, string bankOrder) =>
        bytes[Offset(x, y, bankOrder)] |= (byte)(1 << (x % 7));

    private static bool Dot(ReadOnlySpan<byte> bytes, int x, int y, string bankOrder) =>
        (bytes[Offset(x, y, bankOrder)] & (1 << (x % 7))) != 0;

    private static int NearestPattern(ReadOnlySpan<byte> rgb)
    {
        int best = 0, error = int.MaxValue;
        for (int pattern = 0; pattern < PatternToPalette.Length; pattern++)
        {
            int color = AppleGraphics.LoresPalette[PatternToPalette[pattern]];
            int r = rgb[0] - (color >> 16), g = rgb[1] - ((color >> 8) & 255), b = rgb[2] - (color & 255);
            int distance = r * r + g * g + b * b;
            if (distance < error) { best = pattern; error = distance; }
        }
        return best;
    }

    private static void Validate(string mode, string bankOrder)
    {
        if (mode is not ("mono" or "color") || bankOrder is not ("aux-main" or "main-aux"))
            throw new DiskException("graphics.dhires_options", "Specify mono/color mode and aux-main/main-aux bank order.", 2);
    }
}
