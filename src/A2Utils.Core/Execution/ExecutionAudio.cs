using System.Buffers.Binary;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

public sealed record ExecutionAudioOptions
{
    public double AfterSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public bool? NonSilent { get; init; }
    public double? MinRms { get; init; }
    public double? MaxRms { get; init; }
    public double? MinPeak { get; init; }
    public double? MaxPeak { get; init; }
}

public sealed record ExecutionAudioResult(string Path, string Sha256, int SampleRate, int Channels,
    double DurationSeconds, double AnalyzedFromSeconds, double AnalyzedDurationSeconds,
    long SampleFrames, double Rms, double Peak, bool NonSilent);

/// <summary>Bounded signed 16-bit PCM WAV inspection for headless speaker assertions.</summary>
public static class ExecutionAudio
{
    public const int MaximumWaveBytes = 64 * 1024 * 1024;

    public static void Validate(ExecutionAudioOptions options, double emulatedSeconds)
    {
        if (!double.IsFinite(emulatedSeconds) || emulatedSeconds is <= 0 or > 120
            || !double.IsFinite(options.AfterSeconds) || options.AfterSeconds < 0 || options.AfterSeconds >= emulatedSeconds
            || options.DurationSeconds is { } duration && (!double.IsFinite(duration) || duration <= 0 || options.AfterSeconds + duration > emulatedSeconds)
            || new[] { options.MinRms, options.MaxRms, options.MinPeak, options.MaxPeak }.Any(value => value is { } threshold && (!double.IsFinite(threshold) || threshold is < 0 or > 1))
            || options.MinRms > options.MaxRms || options.MinPeak > options.MaxPeak)
            throw new DiskException("execution.invalid_audio", "Audio runs are limited to 120 emulated seconds; the analysis window must fit the run and normalized RMS/peak bounds must be ordered values from 0 to 1.", 2);
    }

    public static ExecutionAudioResult Analyze(string path, ExecutionAudioOptions options, CancellationToken cancellationToken = default)
    {
        byte[] bytes = ProgramFiles.ReadBytes(path, MaximumWaveBytes, cancellationToken);
        return Analyze(bytes, options, Path.GetFullPath(path), cancellationToken);
    }

    public static ExecutionAudioResult Analyze(byte[] wave, ExecutionAudioOptions options, string path = "audio.wav",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wave);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options, 120);
        if (wave.Length is < 44 or > MaximumWaveBytes || !wave.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !wave.AsSpan(8, 4).SequenceEqual("WAVE"u8) || BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(4)) != wave.Length - 8)
            throw Invalid("Expected a complete RIFF/WAVE file no larger than 64 MiB.");
        int channels = 0, sampleRate = 0, blockAlign = 0, dataOffset = -1, dataLength = 0;
        bool formatSeen = false;
        int offset = 12;
        while (offset < wave.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (wave.Length - offset < 8) throw Invalid("Truncated WAV chunk header.");
            ReadOnlySpan<byte> name = wave.AsSpan(offset, 4);
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(offset + 4));
            long end = offset + 8L + chunkLength;
            if (end > wave.Length || end + (chunkLength & 1) > wave.Length) throw Invalid("WAV chunk exceeds its container.");
            if (name.SequenceEqual("fmt "u8))
            {
                if (formatSeen || chunkLength < 16) throw Invalid("Missing or duplicate PCM format fields.");
                formatSeen = true;
                ReadOnlySpan<byte> format = wave.AsSpan(offset + 8, (int)chunkLength);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                uint rate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1 || channels is < 1 or > 2
                    || rate is < 8000 or > 96000 || BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) != 16
                    || blockAlign != channels * 2 || BinaryPrimitives.ReadUInt32LittleEndian(format[8..]) != rate * blockAlign)
                    throw Invalid("Expected signed 16-bit PCM, one or two channels, and a consistent sample rate of 8000–96000 Hz.");
                sampleRate = (int)rate;
            }
            else if (name.SequenceEqual("data"u8))
            {
                if (dataOffset >= 0) throw Invalid("Multiple WAV data chunks are unsupported.");
                dataOffset = offset + 8;
                dataLength = (int)chunkLength;
            }
            offset = (int)(end + (chunkLength & 1));
        }
        if (!formatSeen || dataOffset < 0 || dataLength == 0 || dataLength % blockAlign != 0)
            throw Invalid("WAV audio is missing or contains incomplete sample frames.");
        int frames = dataLength / blockAlign;
        double durationSeconds = (double)frames / sampleRate;
        // MAME's frame-driven deadline and shutdown can include a short final tail.
        // The analysis itself is capped at 120 seconds and the whole file is bounded.
        if (durationSeconds > 122) throw Invalid("Audio recording exceeds the 120-second run plus its bounded shutdown allowance.");
        int first = (int)Math.Ceiling(options.AfterSeconds * sampleRate);
        int last = options.DurationSeconds is { } requested
            ? (int)Math.Floor((options.AfterSeconds + requested) * sampleRate) : Math.Min(frames, 120 * sampleRate);
        if (first >= frames || last > frames || last <= first) throw Invalid("The WAV does not contain the requested complete analysis window.");
        double sumSquares = 0, peak = 0;
        for (int frame = first; frame < last; frame++)
        {
            if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (int channel = 0; channel < channels; channel++)
            {
                double sample = BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(dataOffset + frame * blockAlign + channel * 2)) / 32768.0;
                sumSquares += sample * sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
        }
        double rms = Math.Sqrt(sumSquares / ((long)(last - first) * channels));
        return new(path, ProgramFiles.Hash(wave), sampleRate, channels, durationSeconds, (double)first / sampleRate,
            (double)(last - first) / sampleRate, last - first, rms, peak, peak > 0);
    }

    private static DiskException Invalid(string message) => new("execution.invalid_audio", message, 2);
}
