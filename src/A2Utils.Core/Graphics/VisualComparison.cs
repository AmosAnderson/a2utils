using System.Text.Json.Serialization;

namespace A2Utils.Core.Graphics;

public sealed record ImageCrop(int X, int Y, int Width, int Height);
public sealed record VisualComparisonOptions(int ChannelTolerance = 0, double MaxDifferentFraction = 0, ImageCrop? Crop = null);
public sealed record VisualComparisonResult(bool Passed, int ComparedPixels, int DifferentPixels,
    double DifferentFraction, int MaximumChannelDelta, int Width, int Height)
{
    [JsonIgnore]
    public RasterImage Diff { get; init; } = new(1, 1, new byte[3]);
}

/// <summary>Compares RGB screenshot pixels with explicit tolerances; no scaling or implicit alignment.</summary>
public static class VisualComparison
{
    public static void Validate(VisualComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ChannelTolerance is < 0 or > 255 || !double.IsFinite(options.MaxDifferentFraction)
            || options.MaxDifferentFraction is < 0 or > 1)
            throw Error("tolerance", "Channel tolerance must be 0..255 and maximum different fraction must be finite and 0..1.");
        if (options.Crop is { } crop && (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0
            || (long)crop.X + crop.Width > 2048 || (long)crop.Y + crop.Height > 2048))
            throw Error("crop", "Crop must be a positive rectangle within the supported 2048 x 2048 image bounds.");
    }

    public static VisualComparisonResult Compare(RasterImage expected, RasterImage actual, VisualComparisonOptions? options = null)
    {
        options ??= new();
        Validate(options);
        ValidateImage(expected);
        ValidateImage(actual);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            throw Error("dimensions", $"Expected screenshot is {expected.Width} x {expected.Height}, actual is {actual.Width} x {actual.Height}; dimensions must match.");
        ImageCrop crop = options.Crop ?? new(0, 0, expected.Width, expected.Height);
        if ((long)crop.X + crop.Width > expected.Width || (long)crop.Y + crop.Height > expected.Height)
            throw Error("crop", "The comparison crop extends outside the screenshots.");

        byte[] diff = new byte[crop.Width * crop.Height * 3];
        int different = 0, maximum = 0;
        for (int y = 0; y < crop.Height; y++)
            for (int x = 0; x < crop.Width; x++)
            {
                int source = ((y + crop.Y) * expected.Width + x + crop.X) * 3;
                int target = (y * crop.Width + x) * 3;
                bool differs = false;
                for (int channel = 0; channel < 3; channel++)
                {
                    int delta = Math.Abs(expected.Rgb[source + channel] - actual.Rgb[source + channel]);
                    maximum = Math.Max(maximum, delta);
                    differs |= delta > options.ChannelTolerance;
                    diff[target + channel] = (byte)delta;
                }
                if (differs) different++;
            }
        int count = crop.Width * crop.Height;
        double fraction = (double)different / count;
        return new(fraction <= options.MaxDifferentFraction, count, different, fraction, maximum, crop.Width, crop.Height)
        {
            Diff = new(crop.Width, crop.Height, diff)
        };
    }

    private static void ValidateImage(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width is < 1 or > 2048 || image.Height is < 1 or > 2048
            || image.Rgb is null || image.Rgb.Length != image.Width * image.Height * 3)
            throw Error("image", "Comparison requires complete RGB images with dimensions 1..2048.");
    }

    private static DiskException Error(string code, string message) => new("graphics.compare_" + code, message, 2);
}
