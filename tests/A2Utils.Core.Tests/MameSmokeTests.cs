using A2Utils.Core.Assembly;
using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class MameSmokeTests
{
    [MameSmokeFact]
    public async Task Run_RealMame_BootsAssembledCodeAndObservesKeyboardMemoryRegistersAndScreen()
    {
        string directory = Path.Combine(TestPaths.TemporaryRoot, "a2-real-mame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Original self-booting sector. No DOS/ProDOS system files are needed or distributed.
            AssemblyResult program = Assembler.Assemble("""
                .org $0800
                .byte 1
                lda #0
                sta $0300
                sta $0301
                lda #$c1
                sta $0400
                lda #$b2
                sta $0401
                lda #$a0
                sta $0402
                lda #$d0
                sta $0403
                lda #$c1
                sta $0404
                lda #$d3
                sta $0405
                sta $0406
                bit $c010
                wait: lda $c000
                bpl wait
                bit $c010
                cmp #$d8
                bne wait
                sta $0301
                lda #$2a
                sta $0300
                done: jmp done
                """);
            byte[] disk = new byte[143360];
            program.Bytes.CopyTo(disk, 0);
            string input = Path.Combine(directory, "boot.dsk");
            File.WriteAllBytes(input, disk);
            ExecutionSpec spec = new()
            {
                Name = "Real MAME keyboard boot smoke",
                EmulatorPath = Environment.GetEnvironmentVariable("A2_MAME_PATH")!,
                RomDirectory = Environment.GetEnvironmentVariable("A2_MAME_ROMS")!,
                Machine = "apple2ee",
                DiskImage = input,
                EmulatedSeconds = 6,
                HostTimeoutSeconds = 30,
                Keys = [new(2.5, "X")],
                Until = new(768, 42, 2),
                Memory = [new(768, "2AD8")],
                Registers = [new("A", 42)],
                TextContains = ["A2 PASS"],
                Screenshot = true,
                Trace = true
            };
            ExecutionResult result = await ExecutionRunner.RunAsync(spec, Path.Combine(directory, "run"));
            Assert.True(result.Passed, string.Join("\n", result.Diagnostics.Select(d => $"{d.Message} Expected: {d.Expected}; actual: {d.Actual}")));
            Assert.Equal("completion_condition", result.StopReason);
            Assert.Equal("0.289", result.EmulatorVersion);
            Assert.Equal(disk, File.ReadAllBytes(input));
            Assert.Contains(result.Artifacts, path => Path.GetFileName(path) == "screen.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class MameSmokeFactAttribute : FactAttribute
{
    public MameSmokeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("A2_MAME_PATH"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("A2_MAME_ROMS")))
            Skip = "Set A2_MAME_PATH and A2_MAME_ROMS for the optional real MAME 0.289 Apple IIe boot test.";
    }
}
