using A2Utils.Core.Assembly;
using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class MameIIeMemorySmokeTests
{
    [Fact]
    public void Assemble_IIeBankAndTextSmoke_FitsOneBootSector()
    {
        AssemblyResult program = Assembler.Assemble(Source);
        Assert.InRange(program.Bytes.Length, 1, 256);
        Assert.Equal(1, program.Bytes[0]);
    }

    [MameSmokeFact]
    public async Task Run_RealIIeBanksAndMouseText_CapturesPhysicalMemoryWhileCpuReadsAuxiliary()
    {
        string directory = Path.Combine(TestPaths.TemporaryRoot, "a2-iie-memory-mame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AssemblyResult program = Assembler.Assemble(Source);
            Assert.InRange(program.Bytes.Length, 1, 256);
            byte[] disk = new byte[143360];
            program.Bytes.CopyTo(disk, 0);
            string input = Path.Combine(directory, "boot.dsk");
            File.WriteAllBytes(input, disk);
            ExecutionSpec spec = new()
            {
                Name = "Real MAME IIe banks and MouseText smoke",
                EmulatorPath = Environment.GetEnvironmentVariable("A2_MAME_PATH")!,
                RomDirectory = Environment.GetEnvironmentVariable("A2_MAME_ROMS")!,
                Machine = "apple2ee",
                DiskImage = input,
                EmulatedSeconds = 6,
                HostTimeoutSeconds = 30,
                Until = new(0x0300, 1, 1, "main"),
                Memory =
                [
                    new(0x2000, "11", "main"), new(0x2000, "22", "aux"), new(0x2000, "22", "cpu"),
                    new(0xd000, "33", "lc1"), new(0xd000, "44", "lc2"),
                    new(0xe000, "55", "lc1"), new(0xe000, "55", "lc2"),
                    new(0xd000, "66", "aux-lc1"), new(0xd000, "77", "aux-lc2"),
                    new(0xe000, "88", "aux-lc1"), new(0xe000, "88", "aux-lc2")
                ],
                ObserveMemory = [new(0x0400, 2, "main"), new(0x0400, 2, "aux")],
                TextColumns = 80,
                DecodeIIeText = true,
                TextContains = ["AB{MT:00}C"]
            };

            ExecutionResult result = await ExecutionRunner.RunAsync(spec, Path.Combine(directory, "run"));

            Assert.True(result.Passed, string.Join("\n", result.Diagnostics.Select(d => $"{d.Message} Expected: {d.Expected}; actual: {d.Actual}")));
            Assert.Equal("completion_condition", result.StopReason);
            Assert.Equal("22", result.Memory[0x2000]);
            Assert.Contains(result.BankMemory, memory => memory.Bank == "main" && memory.Address == 0x0400 && memory.Hex == "C2C3");
            Assert.Contains(result.BankMemory, memory => memory.Bank == "aux" && memory.Address == 0x0400 && memory.Hex == "C140");
            Assert.StartsWith("AB{MT:00}C", result.ScreenText);
            Assert.NotNull(result.TextScreen);
            Assert.Equal(80, result.TextScreen.Columns);
            Assert.Equal(1920, result.TextScreen.Cells.Count);
            Assert.Equal("aux", result.TextScreen.Cells[2].Bank);
            Assert.Equal(0, result.TextScreen.Cells[2].MouseTextIndex);
            Assert.Equal(1, result.Video["columns80"]);
            Assert.Equal(1, result.Video["altCharset"]);
            Assert.Equal(0, result.Video["store80"]);
            Assert.Equal(0, result.Video["page2"]);
            Assert.Equal(0, result.Video["graphics"]);
            Assert.Equal(disk, File.ReadAllBytes(input));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Original self-booting sector, with no DOS/ProDOS files. Mirror the code into auxiliary
    // RAM before enabling RAMRD so the final loop executes identically in either bank.
    // ALTZP selects main/auxiliary language-card RAM; each odd LC switch is read twice
    // to enable writes. At completion the CPU maps auxiliary low RAM and main ROM.
    private const string Source = """
        .org $0800
        .byte 1
        sei
        sta $c002
        sta $c004
        sta $c008
        sta $c000
        bit $c051
        bit $c054
        sta $c00d
        sta $c00f
        lda #0
        sta $0300
        lda #$11
        sta $2000
        lda #$c2
        sta $0400
        lda #$c3
        sta $0401
        sta $c005
        ldx #0
        mirror: lda $0800,x
        sta $0800,x
        inx
        bne mirror
        lda #$22
        sta $2000
        lda #$c1
        sta $0400
        lda #$40
        sta $0401
        sta $c004
        bit $c08b
        bit $c08b
        lda #$33
        sta $d000
        lda #$55
        sta $e000
        bit $c083
        bit $c083
        lda #$44
        sta $d000
        sta $c009
        bit $c08b
        bit $c08b
        lda #$66
        sta $d000
        lda #$88
        sta $e000
        bit $c083
        bit $c083
        lda #$77
        sta $d000
        sta $c008
        bit $c082
        lda #1
        sta $0300
        sta $c003
        done: jmp done
        """;
}
