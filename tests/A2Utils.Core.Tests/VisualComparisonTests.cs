// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Graphics;

namespace A2Utils.Core.Tests;

public sealed class VisualComparisonTests
{
    [Fact]
    public void Compare_ChannelToleranceAndFraction_ApplyInclusiveThresholdsPerPixel()
    {
        RasterImage expected = new(2, 1, [10, 20, 30, 40, 50, 60]);
        RasterImage actual = new(2, 1, [12, 18, 30, 43, 50, 60]);
        VisualComparisonResult result = VisualComparison.Compare(expected, actual, new(2, 0.5));
        Assert.True(result.Passed);
        Assert.Equal(1, result.DifferentPixels);
        Assert.Equal(0.5, result.DifferentFraction);
        Assert.Equal(3, result.MaximumChannelDelta);
        Assert.Equal(new byte[] { 2, 2, 0, 3, 0, 0 }, result.Diff.Rgb);
        Assert.False(VisualComparison.Compare(expected, actual, new(2, 0.49)).Passed);
        Assert.Equal(result.Diff.Rgb, PngCodec.Decode(PngCodec.Encode(result.Diff)).Rgb);
        Assert.DoesNotContain("\"Diff\":", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Compare_Crop_UsesSameCoordinatesAndExcludesSurroundingChanges()
    {
        RasterImage expected = new(2, 2, new byte[12]);
        RasterImage actual = new(2, 2, [255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 0, 0]);
        VisualComparisonResult result = VisualComparison.Compare(expected, actual, new(Crop: new(1, 1, 1, 1)));
        Assert.True(result.Passed);
        Assert.Equal(1, result.ComparedPixels);
        Assert.Equal(0, result.MaximumChannelDelta);
    }

    [Fact]
    public void Compare_InvalidLayoutsAndTolerance_RejectsWithoutResampling()
    {
        RasterImage one = new(1, 1, new byte[3]);
        Assert.Throws<DiskException>(() => VisualComparison.Compare(one, new(2, 1, new byte[6])));
        Assert.Throws<DiskException>(() => VisualComparison.Compare(one, one, new(ChannelTolerance: 256)));
        Assert.Throws<DiskException>(() => VisualComparison.Compare(one, one, new(MaxDifferentFraction: double.NaN)));
        Assert.Throws<DiskException>(() => VisualComparison.Compare(one, one, new(Crop: new(1, 0, 1, 1))));
        Assert.Throws<DiskException>(() => VisualComparison.Compare(one, one, new(Crop: new(int.MaxValue, 0, 1, 1))));
    }
}
