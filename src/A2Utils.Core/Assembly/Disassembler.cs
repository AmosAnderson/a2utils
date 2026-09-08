using System.Buffers.Binary;
using System.Text;

namespace A2Utils.Core.Assembly;

/// <summary>
/// Produces byte-preserving assembly with a linear sweep. Machine code cannot reveal
/// original names, comments, or which bytes were data; this does not infer control flow.
/// </summary>
public static class Disassembler
{
    public static string Disassemble(ReadOnlySpan<byte> data, ushort origin,
        CpuKind cpu = CpuKind.Mos6502, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > 0x10000 - origin)
        {
            throw new DiskException("assembly.address_overflow",
                "The binary extends beyond the 16-bit address space at the specified origin.", 2);
        }

        Dictionary<byte, Instruction> instructions = InstructionSet.GetInstructions(cpu)
            .ToDictionary(instruction => instruction.Opcode);
        StringBuilder source = new();
        source.Append(".org $").Append(origin.ToString("X4")).Append('\n');
        int offset = 0;
        while (offset < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int address = origin + offset;
            int length = 1;
            string statement;
            if (instructions.TryGetValue(data[offset], out Instruction? instruction) &&
                instruction.Length <= data.Length - offset)
            {
                length = instruction.Length;
                string operand = FormatOperand(instruction, data.Slice(offset, length), address);
                statement = instruction.Mnemonic + (operand.Length == 0 ? "" : " " + operand);
            }
            else
            {
                statement = $".byte ${data[offset]:X2}";
            }

            source.Append("    ").Append(statement.PadRight(24)).Append("; $")
                .Append(address.ToString("X4")).Append(':');
            for (int index = 0; index < length; index++)
            {
                source.Append(' ').Append(data[offset + index].ToString("X2"));
            }
            source.Append('\n');
            offset += length;
        }
        return source.ToString();
    }

    private static string FormatOperand(Instruction instruction, ReadOnlySpan<byte> data, int address)
    {
        int value = data.Length == 3
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[1..])
            : data.Length == 2 ? data[1] : 0;
        string absolute = value <= byte.MaxValue ? $"a:${value:X4}" : $"${value:X4}";
        // MOS MCS6500 Programming Manual, section 4.1 and Appendix H: signed branch
        // displacement is relative to the address immediately after the instruction.
        int destination = (address + instruction.Length + unchecked((sbyte)data[^1])) & 0xffff;
        return instruction.Mode switch
        {
            AddressingMode.Implied => "",
            AddressingMode.Accumulator => "A",
            AddressingMode.Immediate => $"#${value:X2}",
            AddressingMode.ZeroPage => $"${value:X2}",
            AddressingMode.ZeroPageX => $"${value:X2},X",
            AddressingMode.ZeroPageY => $"${value:X2},Y",
            AddressingMode.Absolute => absolute,
            AddressingMode.AbsoluteX => absolute + ",X",
            AddressingMode.AbsoluteY => absolute + ",Y",
            AddressingMode.Indirect => $"({absolute})",
            AddressingMode.IndexedIndirect => $"(${value:X2},X)",
            AddressingMode.IndirectIndexed => $"(${value:X2}),Y",
            AddressingMode.Relative => $"${destination:X4}",
            AddressingMode.ZeroPageIndirect => $"(${value:X2})",
            AddressingMode.AbsoluteIndexedIndirect => $"({absolute},X)",
            AddressingMode.ZeroPageRelative => $"${data[1]:X2},${destination:X4}",
            _ => throw new InvalidOperationException("Unsupported addressing mode.")
        };
    }
}
