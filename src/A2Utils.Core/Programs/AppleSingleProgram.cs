// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;

namespace A2Utils.Core.Programs;

public sealed record AppleSinglePayload(byte[] Bytes, byte FileType, ushort AuxType, byte Access);

/// <summary>Decodes bounded AppleSingle v2 program data and ProDOS metadata, including cc65 output.</summary>
public static class AppleSingleProgram
{
    public static AppleSinglePayload Decode(ReadOnlySpan<byte> input)
    {
        if (input.Length < 26 || BinaryPrimitives.ReadUInt32BigEndian(input) != 0x00051600)
            throw Invalid("Expected an AppleSingle header.");
        if (BinaryPrimitives.ReadUInt32BigEndian(input[4..]) != 0x00020000)
            throw new DiskException("applesingle.version", "Program import supports AppleSingle version 2.", 3);
        int count = BinaryPrimitives.ReadUInt16BigEndian(input[24..]);
        int headerEnd = 26 + count * 12;
        if (count > 256 || headerEnd > input.Length) throw Invalid("Invalid entry table.");
        Dictionary<uint, (int Offset, int Length)> entries = [];
        List<(int Start, int End)> ranges = [];
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = input.Slice(26 + index * 12, 12);
            uint id = BinaryPrimitives.ReadUInt32BigEndian(entry);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset < headerEnd || offset > input.Length || length > input.Length - offset)
                throw Invalid("An entry extends outside the data area.");
            if (!entries.TryAdd(id, ((int)offset, (int)length))) throw Invalid("Duplicate entry ID.");
            if (length > 0) ranges.Add(((int)offset, (int)(offset + length)));
        }
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (int index = 1; index < ranges.Count; index++)
            if (ranges[index].Start < ranges[index - 1].End) throw Invalid("Overlapping entries.");
        if (entries.TryGetValue(2, out var resource) && resource.Length > 0)
            throw new DiskException("applesingle.resource_fork", "Program import cannot discard a resource fork.", 3);
        if (!entries.TryGetValue(1, out var data) || !entries.TryGetValue(11, out var info) || info.Length != 8)
            throw Invalid("Program import requires a data fork and an eight-byte ProDOS info entry.");
        ReadOnlySpan<byte> metadata = input.Slice(info.Offset, info.Length);
        ushort access = BinaryPrimitives.ReadUInt16BigEndian(metadata);
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(metadata[2..]);
        uint aux = BinaryPrimitives.ReadUInt32BigEndian(metadata[4..]);
        if (access > 255 || type > 255 || aux > 65535 || type == 0x0f)
            throw new DiskException("applesingle.metadata_range", "Program metadata exceeds supported file field widths.", 3);
        return new(input.Slice(data.Offset, data.Length).ToArray(), (byte)type, (ushort)aux, (byte)access);
    }

    private static DiskException Invalid(string message) => new("applesingle.invalid", message, 4);
}
