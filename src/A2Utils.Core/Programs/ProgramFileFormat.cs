using System.Buffers.Binary;

namespace A2Utils.Core.Programs;

/// <summary>DOS host-file headers. These methods never add or remove filesystem sector padding.</summary>
public static class ProgramFileFormat
{
    public static byte[] EncodeDosBinary(ReadOnlySpan<byte> payload, ushort origin)
    {
        ValidateAddressRange(payload.Length, origin);
        ValidateDosLength(payload.Length);
        byte[] result = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(result, origin);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), (ushort)payload.Length);
        payload.CopyTo(result.AsSpan(4));
        return result;
    }

    public static (ushort Origin, byte[] Payload) DecodeDosBinary(ReadOnlySpan<byte> data)
    {
        ValidateHeader(data, 4, 2);
        ushort origin = BinaryPrimitives.ReadUInt16LittleEndian(data);
        ValidateAddressRange(data.Length - 4, origin);
        return (origin, data[4..].ToArray());
    }

    public static byte[] EncodeDosBasic(ReadOnlySpan<byte> payload)
    {
        ValidateDosLength(payload.Length);
        byte[] result = new byte[payload.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)payload.Length);
        payload.CopyTo(result.AsSpan(2));
        return result;
    }

    public static byte[] DecodeDosBasic(ReadOnlySpan<byte> data)
    {
        ValidateHeader(data, 2, 0);
        return data[2..].ToArray();
    }

    public static void ValidateAddressRange(int length, ushort origin)
    {
        if (length < 0 || length > 65536 - origin)
        {
            throw new DiskException("program.address_range", "The program extends beyond the 16-bit address space.", 2);
        }
    }

    private static void ValidateDosLength(int length)
    {
        if (length > ushort.MaxValue)
        {
            throw new DiskException("program.dos_length", "DOS headers can represent at most 65,535 payload bytes.", 2);
        }
    }

    private static void ValidateHeader(ReadOnlySpan<byte> data, int headerLength, int lengthOffset)
    {
        if (data.Length < headerLength)
        {
            throw new DiskException("program.truncated_header", "The DOS program header is truncated.", 4);
        }
        int declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(data[lengthOffset..]);
        if (declaredLength != data.Length - headerLength)
        {
            throw new DiskException("program.length_mismatch",
                "The DOS header length must exactly match the host payload, without sector padding. Read an image entry with --from-image to use its logical contents.", 4);
        }
    }
}
