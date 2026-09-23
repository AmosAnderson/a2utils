// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class ExecutionAudioTests
{
    [Fact]
    public void Analyze_KnownSignedSamples_ComputesNormalizedRmsAndPeak()
    {
        ExecutionAudioResult result = ExecutionAudio.Analyze(Wave([-32768, 16384, 0, 0]), new());
        Assert.Equal(Math.Sqrt(0.3125), result.Rms, 12);
        Assert.Equal(1, result.Peak);
        Assert.True(result.NonSilent);
        Assert.Equal(4, result.SampleFrames);
        Assert.Equal(64, result.Sha256.Length);
    }

    [Fact]
    public void Analyze_Window_ExcludesBootSamples()
    {
        ExecutionAudioResult result = ExecutionAudio.Analyze(Wave([32767, -32768, 0, 0]), new()
        {
            AfterSeconds = 2.0 / 8000,
            DurationSeconds = 2.0 / 8000
        });
        Assert.False(result.NonSilent);
        Assert.Equal(0, result.Rms);
        Assert.Equal(0, result.Peak);
        Assert.Equal(2, result.SampleFrames);
    }

    [Fact]
    public void Analyze_Stereo_AggregatesBothChannels()
    {
        ExecutionAudioResult result = ExecutionAudio.Analyze(Wave([0, 16384, 0, -16384], 2), new());
        Assert.Equal(0.5 / Math.Sqrt(2), result.Rms, 12);
        Assert.Equal(0.5, result.Peak);
        Assert.Equal(2, result.Channels);
        Assert.Equal(2, result.SampleFrames);
    }

    [Fact]
    public void Analyze_OddLengthUnknownChunk_UsesPaddedChunkBoundary()
    {
        byte[] pcm = Wave([1, 2]);
        byte[] wave = new byte[pcm.Length + 12];
        pcm.AsSpan(0, 12).CopyTo(wave);
        "JUNK"u8.CopyTo(wave.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 3);
        pcm.AsSpan(12).CopyTo(wave.AsSpan(24));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)wave.Length - 8);
        Assert.Equal(2, ExecutionAudio.Analyze(wave, new()).SampleFrames);
    }

    [Fact]
    public void Analyze_MalformedChunkLength_RejectsBeforeReadingOutsideBuffer()
    {
        byte[] wave = Wave([1, 2]);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), uint.MaxValue);
        Assert.Throws<DiskException>(() => ExecutionAudio.Analyze(wave, new()));
    }

    [Fact]
    public void Analyze_UnsupportedFormatAndPartialFrame_RejectsCapture()
    {
        byte[] wave = Wave([1, 2]);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 3);
        Assert.Throws<DiskException>(() => ExecutionAudio.Analyze(wave, new()));
        Assert.Throws<DiskException>(() => ExecutionAudio.Analyze(Wave([1, 2, 3], 2), new()));
        Assert.Throws<DiskException>(() => ExecutionAudio.Analyze(Wave([]), new()));
    }

    [Fact]
    public void Analyze_IncompleteRequestedWindow_DoesNotSilentlyTruncate()
        => Assert.Throws<DiskException>(() => ExecutionAudio.Analyze(Wave([1, 2]), new() { DurationSeconds = 1 }));

    [Fact]
    public void Validate_UnboundedOrContradictoryOptions_RejectsBeforeExecution()
    {
        Assert.Throws<DiskException>(() => ExecutionAudio.Validate(new(), 121));
        Assert.Throws<DiskException>(() => ExecutionAudio.Validate(new() { MinRms = 0.7, MaxRms = 0.2 }, 10));
        Assert.Throws<DiskException>(() => ExecutionAudio.Validate(new() { MinPeak = double.NaN }, 10));
        Assert.Throws<DiskException>(() => ExecutionAudio.Validate(new() { AfterSeconds = 8, DurationSeconds = 3 }, 10));
    }

    [Fact]
    public void Analyze_CancelledWork_StopsBeforeSampleProcessing()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ExecutionAudio.Analyze(Wave([1, 2]), new(), cancellationToken: cancellation.Token));
    }

    private static byte[] Wave(short[] samples, ushort channels = 1)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write(channels);
        writer.Write(8000);
        writer.Write(8000 * channels * 2);
        writer.Write((ushort)(channels * 2));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (short sample in samples) writer.Write(sample);
        return stream.ToArray();
    }
}
