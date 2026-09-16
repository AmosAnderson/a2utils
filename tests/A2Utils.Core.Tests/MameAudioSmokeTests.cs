using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class MameAudioSmokeTests
{
    [MameSmokeFact]
    public async Task Run_SpeakerToggleRoutine_CapturesNonSilentPcmWithoutHostAudio()
    {
        string directory = Path.Combine(TestPaths.TemporaryRoot, "a2-audio-mame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "speaker.asm");
            File.WriteAllText(source, """
                .org $6000
                ldx #64
                outer: ldy #255
                tone: bit $c030
                dey
                bne tone
                dex
                bne outer
                rts
                """);
            ExecutionSpec spec = new()
            {
                Name = "real MAME speaker recording",
                EmulatorPath = Environment.GetEnvironmentVariable("A2_MAME_PATH")!,
                RomDirectory = Environment.GetEnvironmentVariable("A2_MAME_ROMS")!,
                Machine = "apple2ee",
                EmulatedSeconds = 6,
                HostTimeoutSeconds = 30,
                Routine = new() { Source = source, StartAfterSeconds = 1, MaxCycles = 500000 },
                Audio = new() { AfterSeconds = 1.02, NonSilent = true }
            };
            ExecutionResult result = await ExecutionRunner.RunAsync(spec, Path.Combine(directory, "run"));
            Assert.True(result.Passed, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.NotNull(result.Audio);
            Assert.True(result.Audio.NonSilent);
            Assert.True(result.Audio.Rms > 0);
            Assert.Equal(44100, result.Audio.SampleRate);
            Assert.Contains(result.Audio.Path, result.Artifacts);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
