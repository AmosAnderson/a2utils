// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using static A2Utils.Core.Assembly.AddressingMode;

namespace A2Utils.Core.Assembly;

public enum CpuKind
{
    Mos6502,
    Apple65C02,
    Wdc65C02
}

public enum AddressingMode
{
    Implied,
    Accumulator,
    Immediate,
    ZeroPage,
    ZeroPageX,
    ZeroPageY,
    Absolute,
    AbsoluteX,
    AbsoluteY,
    Indirect,
    IndexedIndirect,
    IndirectIndexed,
    Relative,
    ZeroPageIndirect,
    AbsoluteIndexedIndirect,
    ZeroPageRelative
}

public sealed record Instruction(byte Opcode, string Mnemonic, AddressingMode Mode)
{
    // BRK emits its opcode only, following conventional assembler syntax. At runtime
    // the processor also skips a signature byte; source must supply that byte separately.
    public int Length => Mode switch
    {
        Implied or Accumulator => 1,
        Absolute or AbsoluteX or AbsoluteY or Indirect or AbsoluteIndexedIndirect or ZeroPageRelative => 3,
        _ => 2
    };
}

/// <summary>Documented instruction encodings; undocumented opcodes are intentionally absent.</summary>
public static class InstructionSet
{
    // MOS MCS6500 Programming Manual, Appendix A (January 1976):
    // https://www.bitsavers.org/components/mosTechnology/6500-50A_MCS6500pgmManJan76.pdf
    // Apple IIe Technical Reference, Appendix A (Apple-compatible CMOS additions):
    // https://www.applelogic.org/files/AIIETECHREF3.pdf
    // WDC W65C02S datasheet, Table 5-2 (additional WDC/Rockwell instructions):
    // https://www.westerndesigncenter.com/wdc/documentation/w65c02s.pdf
    private static readonly IReadOnlyList<Instruction> MosInstructions = BuildMos();
    private static readonly IReadOnlyList<Instruction> AppleInstructions = BuildApple();
    private static readonly IReadOnlyList<Instruction> WdcInstructions = BuildWdc();

    public static IReadOnlyList<Instruction> GetInstructions(CpuKind cpu) => cpu switch
    {
        CpuKind.Mos6502 => MosInstructions,
        CpuKind.Apple65C02 => AppleInstructions,
        CpuKind.Wdc65C02 => WdcInstructions,
        _ => throw new DiskException("assembly.unsupported_cpu", "The requested processor is not supported.", 2)
    };

    private static IReadOnlyList<Instruction> BuildMos() => Freeze([
        new(0x00, "BRK", Implied),
        new(0x01, "ORA", IndexedIndirect),
        new(0x05, "ORA", ZeroPage),
        new(0x06, "ASL", ZeroPage),
        new(0x08, "PHP", Implied),
        new(0x09, "ORA", Immediate),
        new(0x0a, "ASL", Accumulator),
        new(0x0d, "ORA", Absolute),
        new(0x0e, "ASL", Absolute),
        new(0x10, "BPL", Relative),
        new(0x11, "ORA", IndirectIndexed),
        new(0x15, "ORA", ZeroPageX),
        new(0x16, "ASL", ZeroPageX),
        new(0x18, "CLC", Implied),
        new(0x19, "ORA", AbsoluteY),
        new(0x1d, "ORA", AbsoluteX),
        new(0x1e, "ASL", AbsoluteX),
        new(0x20, "JSR", Absolute),
        new(0x21, "AND", IndexedIndirect),
        new(0x24, "BIT", ZeroPage),
        new(0x25, "AND", ZeroPage),
        new(0x26, "ROL", ZeroPage),
        new(0x28, "PLP", Implied),
        new(0x29, "AND", Immediate),
        new(0x2a, "ROL", Accumulator),
        new(0x2c, "BIT", Absolute),
        new(0x2d, "AND", Absolute),
        new(0x2e, "ROL", Absolute),
        new(0x30, "BMI", Relative),
        new(0x31, "AND", IndirectIndexed),
        new(0x35, "AND", ZeroPageX),
        new(0x36, "ROL", ZeroPageX),
        new(0x38, "SEC", Implied),
        new(0x39, "AND", AbsoluteY),
        new(0x3d, "AND", AbsoluteX),
        new(0x3e, "ROL", AbsoluteX),
        new(0x40, "RTI", Implied),
        new(0x41, "EOR", IndexedIndirect),
        new(0x45, "EOR", ZeroPage),
        new(0x46, "LSR", ZeroPage),
        new(0x48, "PHA", Implied),
        new(0x49, "EOR", Immediate),
        new(0x4a, "LSR", Accumulator),
        new(0x4c, "JMP", Absolute),
        new(0x4d, "EOR", Absolute),
        new(0x4e, "LSR", Absolute),
        new(0x50, "BVC", Relative),
        new(0x51, "EOR", IndirectIndexed),
        new(0x55, "EOR", ZeroPageX),
        new(0x56, "LSR", ZeroPageX),
        new(0x58, "CLI", Implied),
        new(0x59, "EOR", AbsoluteY),
        new(0x5d, "EOR", AbsoluteX),
        new(0x5e, "LSR", AbsoluteX),
        new(0x60, "RTS", Implied),
        new(0x61, "ADC", IndexedIndirect),
        new(0x65, "ADC", ZeroPage),
        new(0x66, "ROR", ZeroPage),
        new(0x68, "PLA", Implied),
        new(0x69, "ADC", Immediate),
        new(0x6a, "ROR", Accumulator),
        new(0x6c, "JMP", Indirect),
        new(0x6d, "ADC", Absolute),
        new(0x6e, "ROR", Absolute),
        new(0x70, "BVS", Relative),
        new(0x71, "ADC", IndirectIndexed),
        new(0x75, "ADC", ZeroPageX),
        new(0x76, "ROR", ZeroPageX),
        new(0x78, "SEI", Implied),
        new(0x79, "ADC", AbsoluteY),
        new(0x7d, "ADC", AbsoluteX),
        new(0x7e, "ROR", AbsoluteX),
        new(0x81, "STA", IndexedIndirect),
        new(0x84, "STY", ZeroPage),
        new(0x85, "STA", ZeroPage),
        new(0x86, "STX", ZeroPage),
        new(0x88, "DEY", Implied),
        new(0x8a, "TXA", Implied),
        new(0x8c, "STY", Absolute),
        new(0x8d, "STA", Absolute),
        new(0x8e, "STX", Absolute),
        new(0x90, "BCC", Relative),
        new(0x91, "STA", IndirectIndexed),
        new(0x94, "STY", ZeroPageX),
        new(0x95, "STA", ZeroPageX),
        new(0x96, "STX", ZeroPageY),
        new(0x98, "TYA", Implied),
        new(0x99, "STA", AbsoluteY),
        new(0x9a, "TXS", Implied),
        new(0x9d, "STA", AbsoluteX),
        new(0xa0, "LDY", Immediate),
        new(0xa1, "LDA", IndexedIndirect),
        new(0xa2, "LDX", Immediate),
        new(0xa4, "LDY", ZeroPage),
        new(0xa5, "LDA", ZeroPage),
        new(0xa6, "LDX", ZeroPage),
        new(0xa8, "TAY", Implied),
        new(0xa9, "LDA", Immediate),
        new(0xaa, "TAX", Implied),
        new(0xac, "LDY", Absolute),
        new(0xad, "LDA", Absolute),
        new(0xae, "LDX", Absolute),
        new(0xb0, "BCS", Relative),
        new(0xb1, "LDA", IndirectIndexed),
        new(0xb4, "LDY", ZeroPageX),
        new(0xb5, "LDA", ZeroPageX),
        new(0xb6, "LDX", ZeroPageY),
        new(0xb8, "CLV", Implied),
        new(0xb9, "LDA", AbsoluteY),
        new(0xba, "TSX", Implied),
        new(0xbc, "LDY", AbsoluteX),
        new(0xbd, "LDA", AbsoluteX),
        new(0xbe, "LDX", AbsoluteY),
        new(0xc0, "CPY", Immediate),
        new(0xc1, "CMP", IndexedIndirect),
        new(0xc4, "CPY", ZeroPage),
        new(0xc5, "CMP", ZeroPage),
        new(0xc6, "DEC", ZeroPage),
        new(0xc8, "INY", Implied),
        new(0xc9, "CMP", Immediate),
        new(0xca, "DEX", Implied),
        new(0xcc, "CPY", Absolute),
        new(0xcd, "CMP", Absolute),
        new(0xce, "DEC", Absolute),
        new(0xd0, "BNE", Relative),
        new(0xd1, "CMP", IndirectIndexed),
        new(0xd5, "CMP", ZeroPageX),
        new(0xd6, "DEC", ZeroPageX),
        new(0xd8, "CLD", Implied),
        new(0xd9, "CMP", AbsoluteY),
        new(0xdd, "CMP", AbsoluteX),
        new(0xde, "DEC", AbsoluteX),
        new(0xe0, "CPX", Immediate),
        new(0xe1, "SBC", IndexedIndirect),
        new(0xe4, "CPX", ZeroPage),
        new(0xe5, "SBC", ZeroPage),
        new(0xe6, "INC", ZeroPage),
        new(0xe8, "INX", Implied),
        new(0xe9, "SBC", Immediate),
        new(0xea, "NOP", Implied),
        new(0xec, "CPX", Absolute),
        new(0xed, "SBC", Absolute),
        new(0xee, "INC", Absolute),
        new(0xf0, "BEQ", Relative),
        new(0xf1, "SBC", IndirectIndexed),
        new(0xf5, "SBC", ZeroPageX),
        new(0xf6, "INC", ZeroPageX),
        new(0xf8, "SED", Implied),
        new(0xf9, "SBC", AbsoluteY),
        new(0xfd, "SBC", AbsoluteX),
        new(0xfe, "INC", AbsoluteX)
    ]);

    private static IReadOnlyList<Instruction> BuildApple() => Freeze([
        .. MosInstructions,
        new(0x04, "TSB", ZeroPage),
        new(0x0c, "TSB", Absolute),
        new(0x12, "ORA", ZeroPageIndirect),
        new(0x14, "TRB", ZeroPage),
        new(0x1a, "INC", Accumulator),
        new(0x1c, "TRB", Absolute),
        new(0x32, "AND", ZeroPageIndirect),
        new(0x34, "BIT", ZeroPageX),
        new(0x3a, "DEC", Accumulator),
        new(0x3c, "BIT", AbsoluteX),
        new(0x52, "EOR", ZeroPageIndirect),
        new(0x5a, "PHY", Implied),
        new(0x64, "STZ", ZeroPage),
        new(0x72, "ADC", ZeroPageIndirect),
        new(0x74, "STZ", ZeroPageX),
        new(0x7a, "PLY", Implied),
        new(0x7c, "JMP", AbsoluteIndexedIndirect),
        new(0x80, "BRA", Relative),
        new(0x89, "BIT", Immediate),
        new(0x92, "STA", ZeroPageIndirect),
        new(0x9c, "STZ", Absolute),
        new(0x9e, "STZ", AbsoluteX),
        new(0xb2, "LDA", ZeroPageIndirect),
        new(0xd2, "CMP", ZeroPageIndirect),
        new(0xda, "PHX", Implied),
        new(0xf2, "SBC", ZeroPageIndirect),
        new(0xfa, "PLX", Implied)
    ]);

    private static IReadOnlyList<Instruction> BuildWdc()
    {
        List<Instruction> instructions = [.. AppleInstructions,
            new(0xcb, "WAI", Implied), new(0xdb, "STP", Implied)];
        for (int bit = 0; bit < 8; bit++)
        {
            instructions.Add(new((byte)(0x07 + bit * 0x10), $"RMB{bit}", ZeroPage));
            instructions.Add(new((byte)(0x87 + bit * 0x10), $"SMB{bit}", ZeroPage));
            instructions.Add(new((byte)(0x0f + bit * 0x10), $"BBR{bit}", ZeroPageRelative));
            instructions.Add(new((byte)(0x8f + bit * 0x10), $"BBS{bit}", ZeroPageRelative));
        }
        return Freeze(instructions);
    }

    private static IReadOnlyList<Instruction> Freeze(IEnumerable<Instruction> instructions) =>
        Array.AsReadOnly(instructions.OrderBy(instruction => instruction.Opcode).ToArray());
}
