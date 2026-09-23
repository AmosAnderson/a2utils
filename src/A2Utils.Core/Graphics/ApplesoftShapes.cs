// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;

namespace A2Utils.Core.Graphics;

public sealed record ShapeCommand(string Direction, bool Plot = true, int Count = 1);
public sealed record ShapeDefinition(string Name, IReadOnlyList<ShapeCommand> Commands);
public sealed record ShapeDocument(int SchemaVersion, IReadOnlyList<ShapeDefinition> Shapes);
public sealed record ShapeMetadata(int Number, string Name, int Offset, int Length, int VectorCount, int EndX, int EndY);
public sealed record ShapeTable(byte[] Bytes, IReadOnlyList<ShapeMetadata> Shapes);

/// <summary>Encodes the raw table format documented in Applesoft II chapter 9, pages 92-96.</summary>
public static class ApplesoftShapes
{
    // Apple manual: https://mirrors.apple2.org.za/ftp.apple.asimov.net/documentation/programming/basic/Applesoft%20II%20GREENBOOK%202019.pdf
    // DRAW consumes bits 0..2, 3..5, then 6..7. Empty high groups are skipped;
    // a wholly zero byte ends the shape. Bit 2 plots BEFORE the directional move.
    public static ShapeTable Encode(ShapeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1 || document.Shapes is null || document.Shapes.Count is < 1 or > 255)
            throw Error("schema", "Shape schemaVersion must be 1 with 1..255 shapes.");
        if (document.Shapes.Any(shape => shape is null || string.IsNullOrWhiteSpace(shape.Name) || shape.Commands is null) ||
            document.Shapes.Select(shape => shape.Name).Distinct(StringComparer.Ordinal).Count() != document.Shapes.Count)
            throw Error("schema", "Each shape needs a unique name and a commands array.");
        List<byte[]> definitions = [];
        List<ShapeMetadata> metadata = [];
        int offset = 2 + document.Shapes.Count * 2;
        int totalVectors = 0;
        foreach (ShapeDefinition shape in document.Shapes)
        {
            List<byte> vectors = [];
            int x = 0, y = 0;
            foreach (ShapeCommand command in shape.Commands)
            {
                if (command is null || command.Count is < 1 or > 65535 || totalVectors > 65535 - command.Count)
                    throw Error("limit", "A shape table accepts at most 65535 vectors; repeat counts must be positive.");
                int direction = command.Direction switch
                {
                    "up" => 0,
                    "right" => 1,
                    "down" => 2,
                    "left" => 3,
                    _ => throw Error("direction", "Shape directions are up, right, down, and left.")
                };
                totalVectors += command.Count;
                for (int repeat = 0; repeat < command.Count; repeat++) vectors.Add((byte)(direction | (command.Plot ? 4 : 0)));
                x += direction == 1 ? command.Count : direction == 3 ? -command.Count : 0;
                y += direction == 2 ? command.Count : direction == 0 ? -command.Count : 0;
            }
            if (vectors.Count == 0) throw Error("empty", "Shapes require at least one vector.");
            byte[] bytes = Pack(vectors);
            if (offset > 65535 - bytes.Length) throw Error("limit", "The shape table must fit 65535 bytes.");
            metadata.Add(new(metadata.Count + 1, shape.Name, offset, bytes.Length, vectors.Count, x, y));
            definitions.Add(bytes);
            offset += bytes.Length;
        }
        byte[] output = new byte[offset];
        output[0] = (byte)definitions.Count;
        // Byte 1 is reserved and zero; shape numbers are one-based.
        for (int index = 0; index < definitions.Count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2 + index * 2), (ushort)metadata[index].Offset);
            definitions[index].CopyTo(output, metadata[index].Offset);
        }
        return new(output, metadata);
    }

    private static byte[] Pack(List<byte> vectors)
    {
        int[] sizes = new int[vectors.Count + 1];
        byte[] takes = new byte[vectors.Count];
        Array.Fill(sizes, int.MaxValue);
        sizes[^1] = 0;
        for (int index = vectors.Count - 1; index >= 0; index--)
        {
            for (int count = Math.Min(3, vectors.Count - index); count >= 1; count--)
            {
                if (vectors[index + count - 1] == 0 || count == 3 && vectors[index + 2] > 3 || sizes[index + count] == int.MaxValue)
                    continue;
                int length = sizes[index + count] + 1;
                if (length < sizes[index]) { sizes[index] = length; takes[index] = (byte)count; }
            }
        }
        if (sizes[0] == int.MaxValue)
            throw Error("encoding", "These vectors cannot be represented without changing the path. A nonplot up vector must be followed by encodable nonzero vectors within the same byte; trailing nonplot up is not representable.");
        byte[] output = new byte[sizes[0] + 1];
        int position = 0;
        for (int index = 0; index < vectors.Count;)
        {
            int count = takes[index];
            for (int part = 0; part < count; part++) output[position] |= (byte)(vectors[index + part] << (part * 3));
            index += count;
            position++;
        }
        return output;
    }

    private static DiskException Error(string code, string message) => new("graphics.shape_" + code, message, 2);
}
