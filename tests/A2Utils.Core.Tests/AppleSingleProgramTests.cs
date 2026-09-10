using System.Buffers.Binary;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class AppleSingleProgramTests
{
    // AppleSingle v2, entry 11 at $32 (8 bytes), data fork at $3A (3 bytes).
    private static byte[] Sample() => Convert.FromHexString(
        "0005160000020000000000000000000000000000000000000002" +
        "0000000B0000003200000008000000010000003A00000003" +
        "00C3000600002000A92A60");

    [Fact]
    public void Decode_IndependentBigEndianHeader_ExtractsPayloadAndLoadMetadata()
    {
        AppleSinglePayload result = AppleSingleProgram.Decode(Sample());
        Assert.Equal(new byte[] { 0xa9, 0x2a, 0x60 }, result.Bytes);
        Assert.Equal(6, result.FileType);
        Assert.Equal(0x2000, result.AuxType);
        Assert.Equal(0xc3, result.Access);
    }

    [Theory]
    [InlineData(30, 0)]
    [InlineData(30, 0xfffffff0)]
    [InlineData(34, 0xfffffff0)]
    [InlineData(42, 50)]
    public void Decode_InvalidEntryRangeOrOverlap_Refuses(int offset, uint value)
    {
        byte[] input = Sample();
        BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(offset), value);
        Assert.Throws<DiskException>(() => AppleSingleProgram.Decode(input));
    }

    [Fact]
    public void Decode_WideAuxiliaryMetadata_RefusesTruncation()
    {
        byte[] input = Sample();
        BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(54), 0x10000);
        Assert.Equal("applesingle.metadata_range", Assert.Throws<DiskException>(() => AppleSingleProgram.Decode(input)).Code);
    }

    [Fact]
    public void Decode_NonemptyResourceFork_RefusesLoss()
    {
        byte[] input = Sample();
        BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(38), 2);
        Assert.Equal("applesingle.resource_fork", Assert.Throws<DiskException>(() => AppleSingleProgram.Decode(input)).Code);
    }
}
