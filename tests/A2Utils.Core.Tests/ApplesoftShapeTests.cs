using System.Buffers.Binary;
using A2Utils.Core.Graphics;

namespace A2Utils.Core.Tests;

public sealed class ApplesoftShapeTests
{
    [Fact]
    public void Encode_AppleManualChapterNineVectors_MatchesPublishedTable()
    {
        // Applesoft II manual pp. 93-96: Figure 1's vectors and complete table at $1DFC.
        byte[] vectors = [2, 2, 7, 7, 0, 4, 4, 4, 1, 5, 5, 5, 2, 6, 6, 6, 3, 7];
        string[] directions = ["up", "right", "down", "left"];
        ShapeCommand[] commands = vectors.Select(vector => new ShapeCommand(directions[vector & 3], (vector & 4) != 0)).ToArray();

        ShapeTable table = ApplesoftShapes.Encode(new(1, [new("manual-example", commands)]));

        Assert.Equal(Convert.FromHexString("01000400123F20642D15361E0700"), table.Bytes);
        Assert.Equal(4, Assert.Single(table.Shapes).Offset);
    }

    [Fact]
    public void Encode_TwoShapes_UsesRelativeLittleEndianOffsetsAndTerminators()
    {
        ShapeTable table = ApplesoftShapes.Encode(new(1,
            [new("up", [new("up")]), new("square", [new("right"), new("down"), new("left"), new("up")])]));

        Assert.Equal(2, table.Bytes[0]);
        Assert.Equal(0, table.Bytes[1]);
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(table.Bytes.AsSpan(2)));
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(table.Bytes.AsSpan(4)));
        Assert.Equal(new byte[] { 4, 0, 0x35, 0x27, 0 }, table.Bytes[6..]);
        Assert.Equal(0, table.Shapes[1].EndX);
        Assert.Equal(0, table.Shapes[1].EndY);
    }

    [Fact]
    public void Encode_UpMovesNeedingLaterPackedVector_PreservesExactPath()
    {
        ShapeTable table = ApplesoftShapes.Encode(new(1,
            [new("move", [new("left"), new("up", false, 2), new("right", false)])]));
        Assert.Equal(new byte[] { 7, 0x40, 0 }, table.Bytes[4..]);
    }

    [Fact]
    public void Encode_TrailingNonplotUp_RejectsInsteadOfDroppingMove()
    {
        DiskException error = Assert.Throws<DiskException>(() => ApplesoftShapes.Encode(new(1,
            [new("bad", [new("right"), new("up", false)])])));
        Assert.Equal("graphics.shape_encoding", error.Code);
    }
}
