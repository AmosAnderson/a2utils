// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Assembly;

namespace A2Utils.Core.Tests;

public sealed class AssemblerTests
{
    [Fact]
    public void Assemble_MonitorCharacterRoutine_ProducesKnown6502Bytes()
    {
        AssemblyResult result = Assembler.Assemble("""
            ; Print high-bit ASCII through the Apple II monitor COUT entry.
            .org $0800
            start: ldx #0
            loop: lda message,x
                  beq done
                  jsr $fded
                  inx
                  bne loop
            done: rts
            message: .byte $c1,$b2,0
            """);

        Assert.Equal(0x0800, result.Origin);
        Assert.Equal(new byte[]
        {
            0xa2, 0x00, 0xbd, 0x0e, 0x08, 0xf0, 0x06,
            0x20, 0xed, 0xfd, 0xe8, 0xd0, 0xf5, 0x60, 0xc1, 0xb2, 0x00
        }, result.Bytes);
    }

    [Fact]
    public void Assemble_Documented6502AddressingModes_UsesKnownOpcodes()
    {
        AssemblyResult result = Assembler.Assemble("""
            * = $0800
            lda #$12
            lda $12
            lda $12,x
            ldx $12,y
            lda $1234
            lda $1234,x
            lda $1234,y
            jmp ($1234)
            lda ($12,x)
            lda ($12),y
            asl a
            nop
            bne *
            """);

        Assert.Equal(new byte[]
        {
            0xa9, 0x12, 0xa5, 0x12, 0xb5, 0x12, 0xb6, 0x12,
            0xad, 0x34, 0x12, 0xbd, 0x34, 0x12, 0xb9, 0x34, 0x12,
            0x6c, 0x34, 0x12, 0xa1, 0x12, 0xb1, 0x12, 0x0a, 0xea, 0xd0, 0xfe
        }, result.Bytes);
    }

    [Fact]
    public void Assemble_Apple65C02Extensions_UsesKnownOpcodes()
    {
        AssemblyResult result = Assembler.Assemble("""
            .org $1000
            stz $10
            stz $10,x
            stz $1234
            stz $1234,x
            lda ($10)
            jmp ($1234,x)
            tsb $10
            trb $1234
            bra *
            inc a
            dec a
            phx
            ply
            """, cpu: CpuKind.Apple65C02);

        Assert.Equal(new byte[]
        {
            0x64, 0x10, 0x74, 0x10, 0x9c, 0x34, 0x12, 0x9e, 0x34, 0x12,
            0xb2, 0x10, 0x7c, 0x34, 0x12, 0x04, 0x10, 0x1c, 0x34, 0x12,
            0x80, 0xfe, 0x1a, 0x3a, 0xda, 0x7a
        }, result.Bytes);
    }

    [Fact]
    public void Assemble_Wdc65C02Extensions_UsesKnownOpcodesAndThreeByteBranches()
    {
        AssemblyResult result = Assembler.Assemble("""
            .org $1000
            rmb3 $20
            smb5 $20
            bbr2 $20,*
            bbs7 z:$20,*+3
            wai
            stp
            """, cpu: CpuKind.Wdc65C02);

        Assert.Equal(new byte[] { 0x37, 0x20, 0xd7, 0x20, 0x2f, 0x20, 0xfd, 0xff, 0x20, 0, 0xcb, 0xdb }, result.Bytes);
    }

    [Fact]
    public void Assemble_ForcePrefixes_PreservesLowAbsoluteAddresses()
    {
        AssemblyResult result = Assembler.Assemble("""
            .org 0
            lda a:$10
            lda a:$10,x
            lda a:$10,y
            lda z:target
            jmp (a:$10)
            jmp (a:$10,x)
            target: .byte 0
            """, cpu: CpuKind.Apple65C02);

        Assert.Equal(new byte[]
        {
            0xad, 0x10, 0, 0xbd, 0x10, 0, 0xb9, 0x10, 0, 0xa5, 0x11,
            0x6c, 0x10, 0, 0x7c, 0x10, 0, 0
        }, result.Bytes);
    }

    [Fact]
    public void Assemble_ForwardLabelAtPageBoundary_KeepsDeterministicAbsoluteSize()
    {
        AssemblyResult result = Assembler.Assemble(".org $fd\nlda target\ntarget: .byte 0");

        Assert.Equal(new byte[] { 0xad, 0, 1, 0 }, result.Bytes);
        Assert.Equal(new byte[] { 0xad, 3, 0, 0 }, Assembler.Assemble(".org 0\nlda target\ntarget: .byte 0").Bytes);
    }

    [Fact]
    public void Assemble_ConstantsNumbersAndCurrentAddress_ResolvesCaseInsensitiveExpressions()
    {
        AssemblyResult result = Assembler.Assemble("""
            Base = $1234
            .org begin
            begin = 0x0800
            here = *
            lda #<base
            ldx #>(BASE + 1)
            .byte %10100101, 'A', <(-1), >(-1), +7-2
            .word here, *, -(-BASE)
            """);

        Assert.Equal(new byte[]
        {
            0xa9, 0x34, 0xa2, 0x12, 0xa5, 0x41, 0xff, 0xff, 5,
            0, 8, 0x0b, 8, 0x34, 0x12
        }, result.Bytes);
    }

    [Fact]
    public void Assemble_DataStringsAndOriginGap_EmitsExactBytesWithoutAddedTerminator()
    {
        AssemblyResult result = Assembler.Assemble("""
            .org $800
            .text "A;B", "\"\\\n\r\t\0"
            .byte "C,D", 255
            .word $1234
            .fill 2,$aa
            .org $814
            .byte 1
            """);

        Assert.Equal(new byte[]
        {
            0x41, 0x3b, 0x42, 0x22, 0x5c, 0x0a, 0x0d, 9, 0,
            0x43, 0x2c, 0x44, 0xff, 0x34, 0x12, 0xaa, 0xaa, 0, 0, 0, 1
        }, result.Bytes);
    }

    [Theory]
    [InlineData(0x800, "$0881", 0x7f)]
    [InlineData(0x800, "$0782", 0x80)]
    [InlineData(0xfffe, "$0001", 0x01)]
    [InlineData(0, "$ff82", 0x80)]
    [InlineData(0, "$ffff", 0xfd)]
    public void Assemble_BranchBoundaryAndAddressWrap_EncodesSignedOffset(int origin, string target, int offset)
    {
        Assert.Equal(new byte[] { 0xd0, (byte)offset }, Assembler.Assemble($"bne {target}", (ushort)origin).Bytes);
    }

    [Theory]
    [InlineData("bne $882")]
    [InlineData("bne $781")]
    public void Assemble_BranchOutsideSignedByteRange_ReportsLine(string instruction)
    {
        DiskException error = Assert.Throws<DiskException>(() => Assembler.Assemble(".org $800\n" + instruction));

        Assert.Equal(2, error.ExitCode);
        Assert.Contains("Line 2:", error.Message);
        Assert.Contains("out of range", error.Message);
    }

    [Fact]
    public void Assemble_AddressSpaceBoundary_AcceptsExactMaximumAndNoWrap()
    {
        Assert.Equal(new byte[] { 0xea }, Assembler.Assemble("nop", 0xffff).Bytes);
        AssemblyResult all = Assembler.Assemble(".org 0\n.fill 65536,$ff");

        Assert.Equal(65536, all.Bytes.Length);
        Assert.All(all.Bytes, value => Assert.Equal(255, value));
        Assert.Throws<DiskException>(() => Assembler.Assemble(".org $ffff\n.word 1"));
        Assert.Throws<DiskException>(() => Assembler.Assemble(".org 0\n.fill 65536\nnop"));
    }

    [Theory]
    [InlineData("nop")]
    [InlineData("label: .org $800")]
    [InlineData(".org $800\n.org $700")]
    [InlineData(".org 65536")]
    [InlineData(".org -1")]
    [InlineData(".org target\ntarget: nop")]
    [InlineData(".org 0\n.fill target\ntarget: nop")]
    public void Assemble_InvalidOriginOrLayout_RefusesAmbiguousOutput(string source)
    {
        Assert.Equal(2, Assert.Throws<DiskException>(() => Assembler.Assemble(source)).ExitCode);
    }

    [Fact]
    public void Assemble_SourceAndSuppliedOrigin_RequiresAgreement()
    {
        Assert.Equal(new byte[] { 0xea }, Assembler.Assemble(".org $800\nnop", 0x800).Bytes);
        Assert.Throws<DiskException>(() => Assembler.Assemble(".org $800\nnop", 0x900));
    }

    [Theory]
    [InlineData(".byte 256")]
    [InlineData(".byte -1")]
    [InlineData(".word 65536")]
    [InlineData("lda #256")]
    [InlineData("lda z:$100")]
    [InlineData("jmp $10000")]
    [InlineData("lda ($100),y")]
    [InlineData(".fill -1")]
    [InlineData(".fill 1,256")]
    [InlineData(".fill 0,256")]
    [InlineData(".fill 0,missing")]
    public void Assemble_OutOfRangeOperand_RejectsInsteadOfTruncating(string statement)
    {
        Assert.Equal(2, Assert.Throws<DiskException>(() => Assembler.Assemble(statement, 0)).ExitCode);
    }

    [Theory]
    [InlineData("lda missing", "Undefined symbol 'missing'", false)]
    [InlineData("x = y\ny = x\nnop", "Circular constant reference", false)]
    [InlineData("Foo: nop\nfoo: nop", "already defined on line 1", false)]
    [InlineData("foo = 1\nfoo: nop", "already defined on line 1", false)]
    [InlineData("before = *\n.org 0\n.byte before", "needs a defined origin", true)]
    public void Assemble_InvalidSymbols_ProducesUsefulDiagnostic(string source, string message, bool omitOrigin)
    {
        DiskException error = Assert.Throws<DiskException>(() => Assembler.Assemble(source, omitOrigin ? null : (ushort)0));

        Assert.Equal("assembly.invalid_source", error.Code);
        Assert.Contains("Line ", error.Message);
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData(".byte $")]
    [InlineData(".byte %102")]
    [InlineData(".byte 0x")]
    [InlineData(".byte 1+")]
    [InlineData(".byte (1")]
    [InlineData(".byte 1)")]
    [InlineData(".byte $ff, ")]
    [InlineData(".byte 9223372036854775808")]
    [InlineData(".byte 9223372036854775807+1")]
    [InlineData(".text \"é\"")]
    [InlineData(".text 1")]
    [InlineData(".text \"unterminated")]
    [InlineData(".word \"A\"")]
    [InlineData(".byte ''")]
    [InlineData("lda ($10),x")]
    [InlineData("lda ($10,y)")]
    [InlineData("lda (a:$10),y")]
    [InlineData("lda a:")]
    [InlineData(".include \"file.asm\"")]
    [InlineData("nop #1")]
    [InlineData("jmp #1")]
    [InlineData("brk 1")]
    public void Assemble_MalformedSyntax_ReportsLineWithoutUnhandledException(string statement)
    {
        DiskException error = Assert.Throws<DiskException>(() => Assembler.Assemble(".org 0\n" + statement));

        Assert.Contains("Line 2:", error.Message);
        Assert.Equal(2, error.ExitCode);
    }

    [Theory]
    [InlineData("stz $10", CpuKind.Mos6502)]
    [InlineData("rmb0 $10", CpuKind.Apple65C02)]
    [InlineData("wai", CpuKind.Apple65C02)]
    [InlineData("slo $10", CpuKind.Mos6502)]
    public void Assemble_InstructionUnavailableOnSelectedCpu_RefusesIt(string source, CpuKind cpu)
    {
        Assert.Throws<DiskException>(() => Assembler.Assemble(source, 0, cpu));
    }

    [Fact]
    public void Assemble_ExcessiveResourceUse_ReportsBoundedInputErrors()
    {
        Assert.Throws<DiskException>(() => Assembler.Assemble(new string(' ', Assembler.MaximumSourceLength + 1), 0));
        Assert.Throws<DiskException>(() => Assembler.Assemble(new string('\n', 100_000), 0));
        Assert.Throws<DiskException>(() => Assembler.Assemble(new string(' ', 16_385), 0));
        Assert.Throws<DiskException>(() => Assembler.Assemble(".byte " + new string('(', 65) + "0" + new string(')', 65), 0));
        Assert.Throws<DiskException>(() => Assembler.Assemble(".byte " + string.Join("+", Enumerable.Repeat("0", 130)), 0));
    }

    [Fact]
    public void Assemble_RepeatedConstantReferences_ResolvesWithoutExponentialWork()
    {
        string constants = string.Join("\n", Enumerable.Range(1, 100)
            .Select(index => $"value{index} = value{index - 1}+value{index - 1}"));

        Assert.Equal(new byte[] { 0 }, Assembler.Assemble($"value0 = 0\n{constants}\n.byte value100", 0).Bytes);
        Assert.Equal(new byte[] { 0xad, 0, 0, 0 },
            Assembler.Assemble($"value0 = target-target\n{constants}\nlda value100\ntarget: .byte 0", 0).Bytes);
    }

    [Fact]
    public void Assemble_Cancelled_StopsBeforeProcessingSource()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Assembler.Assemble("nop", 0,
            cancellationToken: cancellation.Token));
    }
}
