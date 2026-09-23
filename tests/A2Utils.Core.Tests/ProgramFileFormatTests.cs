// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class ProgramFileFormatTests
{
    [Fact]
    public void DosBinary_KnownHeader_StoresAddressAndLengthLittleEndian()
    {
        byte[] encoded = ProgramFileFormat.EncodeDosBinary([0xa9, 0x01, 0x60], 0x2000);
        Assert.Equal(new byte[] { 0x00, 0x20, 0x03, 0x00, 0xa9, 0x01, 0x60 }, encoded);
        var decoded = ProgramFileFormat.DecodeDosBinary(encoded);
        Assert.Equal(0x2000, decoded.Origin);
        Assert.Equal(new byte[] { 0xa9, 0x01, 0x60 }, decoded.Payload);
    }

    [Fact]
    public void DosBasic_KnownHeader_StoresOnlyLength()
    {
        byte[] encoded = ProgramFileFormat.EncodeDosBasic([0x00, 0x00]);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00 }, encoded);
        Assert.Equal(new byte[] { 0, 0 }, ProgramFileFormat.DecodeDosBasic(encoded));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 32, 1 })]
    [InlineData(new byte[] { 0, 32, 1, 0 })]
    [InlineData(new byte[] { 0, 32, 1, 0, 96, 0 })]
    public void DosBinary_InvalidLength_RejectsTruncationAndTrailingBytes(byte[] data)
    {
        Assert.Equal(4, Assert.Throws<DiskException>(() => ProgramFileFormat.DecodeDosBinary(data)).ExitCode);
    }

    [Theory]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 1, 0 })]
    [InlineData(new byte[] { 0, 0, 0 })]
    public void DosBasic_InvalidLength_RejectsTruncationAndTrailingBytes(byte[] data)
    {
        Assert.Equal(4, Assert.Throws<DiskException>(() => ProgramFileFormat.DecodeDosBasic(data)).ExitCode);
    }

    [Fact]
    public void DosBinary_AddressWrap_RefusesUnrepresentableLoad()
    {
        Assert.Throws<DiskException>(() => ProgramFileFormat.EncodeDosBinary([1, 2], 0xffff));
        Assert.Throws<DiskException>(() => ProgramFileFormat.DecodeDosBinary([0xff, 0xff, 2, 0, 1, 2]));
        Assert.Equal(5, ProgramFileFormat.EncodeDosBinary([0x60], 0xffff).Length);
    }

    [Fact]
    public void DosBasic_64KiBPayload_RefusesLengthTruncation()
    {
        Assert.Throws<DiskException>(() => ProgramFileFormat.EncodeDosBasic(new byte[65536]));
    }
}
