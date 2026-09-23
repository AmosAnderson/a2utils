// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class AppleTextCodecTests
{
    [Fact]
    public void Decode_MixedHighBitsAndLineEndings_ReturnsAsciiUtf8WithLf()
    {
        byte[] appleText = [0xc1, 0x8d, 0x8a, 0x42, 0x0d, 0xc3, 0x0a, 0x89, 0x7e];

        Assert.Equal("A\nB\nC\n\t~"u8.ToArray(), AppleTextCodec.Decode(appleText));
    }

    [Theory]
    [InlineData("dos33")]
    [InlineData("prodos")]
    public void Encode_AsciiAndMixedLineEndings_UsesFilesystemTextConvention(string fileSystem)
    {
        byte[] expected = fileSystem == "dos33"
            ? [0xc1, 0x89, 0xda, 0x8d, 0xc2, 0x8d, 0xc3, 0x8d]
            : [0x41, 0x09, 0x5a, 0x0d, 0x42, 0x0d, 0x43, 0x0d];

        Assert.Equal(expected, AppleTextCodec.Encode("A\tZ\r\nB\rC\n"u8, fileSystem));
    }

    [Theory]
    [InlineData("dos33")]
    [InlineData("prodos")]
    public void Encode_Utf8Bom_StripsOnlyLeadingBom(string fileSystem)
    {
        byte[] hostText = [0xef, 0xbb, 0xbf, 0x41, 0x0a];

        Assert.Equal("A\n"u8.ToArray(), AppleTextCodec.Decode(AppleTextCodec.Encode(hostText, fileSystem)));
    }

    [Fact]
    public void Encode_NoFinalNewline_DoesNotAddTerminatorPaddingOrNewline()
    {
        Assert.Equal(new byte[] { 0xc1, 0xb2 }, AppleTextCodec.Encode("A2"u8, "dos33"));
        Assert.Equal("A2"u8.ToArray(), AppleTextCodec.Encode("A2"u8, "prodos"));
    }

    [Fact]
    public void Convert_EmptyText_ReturnsEmptyPayload()
    {
        Assert.Empty(AppleTextCodec.Decode([]));
        Assert.Empty(AppleTextCodec.Encode([], "dos33"));
        Assert.Empty(AppleTextCodec.Encode([], "prodos"));
        Assert.Empty(AppleTextCodec.Encode([0xef, 0xbb, 0xbf], "dos33"));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x08)]
    [InlineData(0x0b)]
    [InlineData(0x0c)]
    [InlineData(0x1b)]
    [InlineData(0x1f)]
    [InlineData(0x7f)]
    [InlineData(0x80)]
    [InlineData(0x8b)]
    [InlineData(0xff)]
    public void Decode_UnsupportedControl_ReportsOriginalByteAndOffset(int value)
    {
        DiskException error = Assert.Throws<DiskException>(() => AppleTextCodec.Decode([0xc1, (byte)value]));

        Assert.Equal("text.unsupported_character", error.Code);
        Assert.Contains($"0x{value:X2}", error.Message);
        Assert.Contains("offset 1", error.Message);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\b")]
    [InlineData("\v")]
    [InlineData("\f")]
    [InlineData("\u001b")]
    [InlineData("\u007f")]
    [InlineData("é")]
    [InlineData("©")]
    [InlineData("漢")]
    [InlineData("🚀")]
    [InlineData("A\ufeffB")]
    public void Encode_UnsupportedCharacter_RefusesLossyConversion(string value)
    {
        DiskException error = Assert.Throws<DiskException>(() =>
            AppleTextCodec.Encode(Encoding.UTF8.GetBytes(value), "dos33"));

        Assert.Equal("text.unsupported_character", error.Code);
    }

    [Theory]
    [InlineData(new byte[] { 0xe9 })]
    [InlineData(new byte[] { 0xc0, 0xaf })]
    [InlineData(new byte[] { 0xed, 0xa0, 0x80 })]
    [InlineData(new byte[] { 0xff, 0xfe, 0x41, 0x00 })]
    public void Encode_InvalidUtf8_RejectsInsteadOfUsingPlatformCodePage(byte[] bytes)
    {
        DiskException error = Assert.Throws<DiskException>(() => AppleTextCodec.Encode(bytes, "prodos"));

        Assert.Equal("text.invalid_utf8", error.Code);
    }

    [Fact]
    public void Encode_UnknownFilesystem_ReportsUnsupportedConvention()
    {
        DiskException error = Assert.Throws<DiskException>(() => AppleTextCodec.Encode("A"u8, "unknown"));

        Assert.Equal("text.unsupported_filesystem", error.Code);
    }
}
