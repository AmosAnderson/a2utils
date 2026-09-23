// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Assembly;

namespace A2Utils.Core.Tests;

public sealed class DisassemblerTests
{
    [Theory]
    [InlineData(CpuKind.Mos6502, 151)]
    [InlineData(CpuKind.Apple65C02, 178)]
    [InlineData(CpuKind.Wdc65C02, 212)]
    public void InstructionSet_DocumentedCpu_HasUniqueEncodingsAndCanonicalNop(CpuKind cpu, int count)
    {
        IReadOnlyList<Instruction> instructions = InstructionSet.GetInstructions(cpu);

        Assert.Equal(count, instructions.Count);
        Assert.Equal(count, instructions.Select(instruction => instruction.Opcode).Distinct().Count());
        Assert.Equal(count, instructions.Select(instruction => (instruction.Mnemonic, instruction.Mode)).Distinct().Count());
        Assert.Equal(0xea, Assert.Single(instructions, instruction => instruction.Mnemonic == "NOP").Opcode);
    }

    [Theory]
    [InlineData(CpuKind.Mos6502, "    .byte $64", "    .byte $CB", "    .byte $07")]
    [InlineData(CpuKind.Apple65C02, "    STZ $10", "    .byte $CB", "    .byte $07")]
    [InlineData(CpuKind.Wdc65C02, "    STZ $10", "    WAI", "    RMB0 $10")]
    public void Disassemble_CpuSpecificOpcodes_RespectsAppleAndWdcDifferences(
        CpuKind cpu, string stz, string wai, string rmb)
    {
        Assert.Contains(stz, Disassembler.Disassemble([0x64, 0x10], 0x2000, cpu));
        Assert.Contains(wai, Disassembler.Disassemble([0xcb], 0x2000, cpu));
        Assert.Contains(rmb, Disassembler.Disassemble([0x07, 0x10], 0x2000, cpu));
    }

    [Fact]
    public void Disassemble_IndependentMosExample_PrintsInstructionsAddressesAndBytes()
    {
        // Opcodes from MOS Technology's MCS6500 Programming Manual Appendix A.
        // https://www.bitsavers.org/components/mosTechnology/6500-50A_MCS6500pgmManJan76.pdf
        // This short program is authored here, with expected bytes computed separately.
        byte[] binary = [0xa2, 0x03, 0xca, 0xd0, 0xfd, 0xa9, 0xc1, 0x20, 0xed, 0xfd, 0x60];

        string result = Disassembler.Disassemble(binary, 0x2000);

        Assert.StartsWith(".org $2000\n", result);
        Assert.Contains("LDX #$03", result);
        Assert.Contains("DEX", result);
        Assert.Contains("BNE $2002", result);
        Assert.Contains("LDA #$C1", result);
        Assert.Contains("JSR $FDED", result);
        Assert.Contains("RTS", result);
        Assert.Contains("; $2007: 20 ED FD", result);
    }

    [Theory]
    [InlineData(new byte[] { 0xa5, 0x10 }, "LDA $10")]
    [InlineData(new byte[] { 0xb5, 0x10 }, "LDA $10,X")]
    [InlineData(new byte[] { 0xb6, 0x10 }, "LDX $10,Y")]
    [InlineData(new byte[] { 0xad, 0x10, 0x00 }, "LDA a:$0010")]
    [InlineData(new byte[] { 0xbd, 0x10, 0x00 }, "LDA a:$0010,X")]
    [InlineData(new byte[] { 0xb9, 0x10, 0x00 }, "LDA a:$0010,Y")]
    [InlineData(new byte[] { 0xa1, 0x10 }, "LDA ($10,X)")]
    [InlineData(new byte[] { 0xb1, 0x10 }, "LDA ($10),Y")]
    [InlineData(new byte[] { 0xb2, 0x10 }, "LDA ($10)")]
    [InlineData(new byte[] { 0x6c, 0x10, 0x00 }, "JMP (a:$0010)")]
    [InlineData(new byte[] { 0x7c, 0x10, 0x00 }, "JMP (a:$0010,X)")]
    [InlineData(new byte[] { 0x6c, 0xff, 0x20 }, "JMP ($20FF)")]
    [InlineData(new byte[] { 0x0a }, "ASL A")]
    [InlineData(new byte[] { 0x1a }, "INC A")]
    [InlineData(new byte[] { 0x3a }, "DEC A")]
    public void Disassemble_AddressingModes_PreservesOperandWidth(byte[] binary, string expected)
    {
        Assert.Contains(expected, Disassembler.Disassemble(binary, 0x2000, CpuKind.Apple65C02));
    }

    [Theory]
    [InlineData(0x2000, 0x7f, "$2081")]
    [InlineData(0x2000, 0x80, "$1F82")]
    [InlineData(0x2000, 0xfe, "$2000")]
    [InlineData(0xfffe, 0x00, "$0000")]
    [InlineData(0xfffe, 0x7f, "$007F")]
    [InlineData(0x0000, 0x80, "$FF82")]
    public void Disassemble_RelativeBranch_UsesSignedOffsetAndWrapsTarget(int origin, int displacement, string target)
    {
        Assert.Contains("BNE " + target, Disassembler.Disassemble([0xd0, (byte)displacement], (ushort)origin));
    }

    [Fact]
    public void Disassemble_WdcBitBranches_UsesThirdByteAsRelativeOffset()
    {
        // WDC datasheet Table 5-2: RMB7=$77, SMB7=$F7, BBR7=$7F, BBS7=$FF.
        // https://www.westerndesigncenter.com/wdc/documentation/w65c02s.pdf
        byte[] binary = [0x77, 0x10, 0xf7, 0x20, 0x7f, 0x30, 0xf9, 0xff, 0x40, 0x01, 0xdb];

        string result = Disassembler.Disassemble(binary, 0x2000, CpuKind.Wdc65C02);

        Assert.Contains("RMB7 $10", result);
        Assert.Contains("SMB7 $20", result);
        Assert.Contains("BBR7 $30,$2000", result);
        Assert.Contains("BBS7 $40,$200B", result);
        Assert.Contains("STP", result);
    }

    [Fact]
    public void Disassemble_UnknownAndTruncatedOpcodes_EmitsBytesWithoutPadding()
    {
        string result = Disassembler.Disassemble([0x02, 0xff, 0x4c, 0x10], 0x2000);

        Assert.Contains(".byte $02", result);
        Assert.Contains(".byte $FF", result);
        Assert.Contains(".byte $4C", result);
        Assert.Contains(".byte $10", result);
        Assert.Equal(5, result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Disassemble_BrkSignature_DoesNotDiscardFollowingByte()
    {
        string result = Disassembler.Disassemble([0x00, 0x02], 0xffff - 1);

        Assert.Contains("BRK", result);
        Assert.Contains(".byte $02", result);
        Assert.Contains("; $FFFF: 02", result);
    }

    [Fact]
    public void Disassemble_EmptyData_ProducesOnlyOrigin()
    {
        Assert.Equal(".org $0800\n", Disassembler.Disassemble([], 0x0800));
    }

    [Fact]
    public void Disassemble_DataCrossesAddressBoundary_RejectsInsteadOfWrappingStorage()
    {
        DiskException error = Assert.Throws<DiskException>(() => Disassembler.Disassemble([0xa9, 0x01], 0xffff));

        Assert.Equal("assembly.address_overflow", error.Code);
    }

    [Fact]
    public void Disassemble_UnknownCpu_RejectsUnsupportedEnum()
    {
        DiskException error = Assert.Throws<DiskException>(() => Disassembler.Disassemble([], 0, (CpuKind)123));

        Assert.Equal("assembly.unsupported_cpu", error.Code);
    }

    [Fact]
    public void Disassemble_Canceled_StopsBeforeProcessing()
    {
        Assert.Throws<OperationCanceledException>(() =>
            Disassembler.Disassemble([0xea], 0x2000, cancellationToken: new CancellationToken(true)));
    }

    [Theory]
    [InlineData(CpuKind.Mos6502)]
    [InlineData(CpuKind.Apple65C02)]
    [InlineData(CpuKind.Wdc65C02)]
    public void Disassemble_EveryOpcodeAndOperandBoundary_ReassemblesWithoutChangingBytes(CpuKind cpu)
    {
        // Include every opcode, including undefined bytes, and both short and full inputs.
        // Low absolute operands must not shrink to zero page during reassembly.
        foreach (ushort origin in new ushort[] { 0x0000, 0x2000, 0xfffd })
        {
            for (int opcode = 0; opcode <= byte.MaxValue; opcode++)
            {
                foreach (byte operand in new byte[] { 0x00, 0x7f, 0x80, 0xff })
                {
                    byte[] allBytes = [(byte)opcode, operand, 0x00];
                    for (int length = 1; length <= allBytes.Length; length++)
                    {
                        byte[] binary = allBytes[..length];
                        string source = Disassembler.Disassemble(binary, origin, cpu);
                        AssemblyResult rebuilt = Assembler.Assemble(source, cpu: cpu);

                        Assert.Equal(origin, rebuilt.Origin);
                        Assert.Equal(binary, rebuilt.Bytes);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(CpuKind.Mos6502)]
    [InlineData(CpuKind.Apple65C02)]
    [InlineData(CpuKind.Wdc65C02)]
    public void Disassemble_MixedInstructionAndDataStream_PreservesEveryByte(CpuKind cpu)
    {
        byte[] binary = new byte[8192];
        new Random(6502).NextBytes(binary);

        string source = Disassembler.Disassemble(binary, 0x0800, cpu);
        AssemblyResult rebuilt = Assembler.Assemble(source, cpu: cpu);

        Assert.Equal((ushort)0x0800, rebuilt.Origin);
        Assert.Equal(binary, rebuilt.Bytes);
    }
}
