// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class DiskInspectionTests
{
    [Theory]
    [InlineData("do", 14)]
    [InlineData("po", 1)]
    public void Read_DosBinarySector_MapsBlockAndFileAddresses(string extension, int slot)
    {
        using var workspace = new FixtureWorkspace();
        string path = workspace.CopyDisk(extension);
        byte[] original = File.ReadAllBytes(path);
        int offset = (18 * 16 + slot) * 256;
        var bySector = Assert.Single(DiskInspection.Read(path, track: 18, sector: 14).Ranges);
        Assert.Equal(Convert.ToHexString(original, offset, 256), bySector.Hex);
        Assert.StartsWith("00200500007F80FF0D", bySector.Hex);
        Assert.Equal(offset, bySector.FileOffset);
        Assert.Equal(144, bySector.Block);
        Assert.Equal(256, bySector.BlockOffset);
        var byBlock = DiskInspection.Read(path, block: 144);
        Assert.Equal([0, 256], byBlock.Ranges.Select(r => r.BlockOffset));
        Assert.Equal(bySector, byBlock.Ranges[1]);
        var byOffset = DiskInspection.Read(path, fileOffset: offset + 255);
        Assert.Equal(offset + 255, byOffset.RequestedFileOffset);
        Assert.Equal(bySector, Assert.Single(byOffset.Ranges));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Read_ProDosFixtureSeedBlock_MatchesBothHalvesOfPayload()
    {
        string path = FixtureWorkspace.FindFixture("independent-prodos.po");
        byte[] bytes = File.ReadAllBytes(path);
        var result = DiskInspection.Read(path, block: 32);
        Assert.Equal("prodos", result.Order);
        Assert.Equal(Convert.ToHexString(bytes, 32 * 512, 512), string.Concat(result.Ranges.Select(r => r.Hex)));
        Assert.StartsWith("007F80FF0D", result.Ranges[0].Hex);
    }

    [Fact]
    public void Read_WrappedDisk_UsesDeclaredPayloadStartAndRejectsMetadata()
    {
        using var workspace = new FixtureWorkspace();
        string path = workspace.NewPath("disguised.bin");
        byte[] payload = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-dos33.po"));
        const int start = 128;
        byte[] image = new byte[start + payload.Length + 16];
        "2IMG"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(8), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), (uint)payload.Length / 512);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), start);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), (uint)payload.Length);
        payload.CopyTo(image, start);
        File.WriteAllBytes(path, image);
        int sectorStart = (18 * 16 + 1) * 256;
        var result = DiskInspection.Read(path, fileOffset: start + sectorStart + 20);
        Assert.Equal("image.order_conflict", Assert.Throws<DiskException>(() =>
            DiskInspection.Read(path, block: 0, inputOrder: "dos")).Code);
        Assert.Equal("2mg", result.Container);
        Assert.Equal(start, result.PayloadStart);
        var range = Assert.Single(result.Ranges);
        Assert.Equal(start + sectorStart, range.FileOffset);
        Assert.Equal(sectorStart, range.PayloadOffset);
        Assert.Equal(Convert.ToHexString(payload, sectorStart, 256), range.Hex);
        foreach (long outside in new long[] { 0, start - 1, start + payload.Length })
            Assert.Equal("inspect.range", Assert.Throws<DiskException>(() => DiskInspection.Read(path, fileOffset: outside)).Code);
        Assert.Equal(image, File.ReadAllBytes(path));
    }

    [Fact]
    public void Read_LargerRawImage_ReturnsWholeBlocksAndRejectsTrackNumbers()
    {
        using var workspace = new FixtureWorkspace();
        string path = workspace.NewPath("unformatted.raw");
        byte[] bytes = new byte[800 * 1024];
        bytes[^1] = 126;
        File.WriteAllBytes(path, bytes);
        var result = DiskInspection.Read(path, fileOffset: bytes.Length - 1);
        var range = Assert.Single(result.Ranges);
        Assert.Equal("prodos", result.Order);
        Assert.Equal(1599, range.Block);
        Assert.Equal(1599 * 512, range.FileOffset);
        Assert.Null(range.Track);
        Assert.Null(range.Sector);
        Assert.Equal(512, range.Ascii.Length);
        Assert.EndsWith("~", range.Ascii);
        Assert.Equal("inspect.range", Assert.Throws<DiskException>(() => DiskInspection.Read(path, track: 0, sector: 0)).Code);
        Assert.Equal("inspect.range", Assert.Throws<DiskException>(() => DiskInspection.Read(path, block: 1600)).Code);
    }

    [Theory]
    [InlineData(null, null, null, null, "inspect.address")]
    [InlineData(0, 1, 2, null, "inspect.address")]
    [InlineData(0, null, null, 10L, "inspect.address")]
    [InlineData(null, null, 0, null, "inspect.address")]
    [InlineData(null, 35, 0, null, "inspect.range")]
    [InlineData(null, 1, -1, null, "inspect.range")]
    [InlineData(-1, null, null, null, "inspect.range")]
    [InlineData(null, null, null, -1L, "inspect.range")]
    public void Read_InvalidAddresses_ReportSpecificError(int? block, int? track, int? sector, long? offset, string code)
    {
        string path = FixtureWorkspace.FindFixture("independent-dos33.do");
        Assert.Equal(code, Assert.Throws<DiskException>(() => DiskInspection.Read(path, block, track, sector, offset)).Code);
    }

    [Fact]
    public void Read_UnknownRawFloppy_RequiresOrderOverride()
    {
        using var workspace = new FixtureWorkspace();
        string path = workspace.NewPath("blank.do");
        File.WriteAllBytes(path, new byte[143360]);
        Assert.Equal("inspect.order_required", Assert.Throws<DiskException>(() => DiskInspection.Read(path, block: 0)).Code);
        Assert.Equal("dos", DiskInspection.Read(path, block: 0, inputOrder: "dos").Order);
        Assert.Throws<DiskException>(() => DiskInspection.Read(path, block: 0, inputOrder: "dos", inputFileSystem: "dos33"));
    }
}
