// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Globalization;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

public static partial class ExecutionRunner
{
    private static ExecutionAudioResult? EvaluateAudio(ExecutionSpec spec, string artifacts,
        List<ProgramDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (spec.Audio is not { } options) return null;
        string path = Path.Combine(artifacts, "audio.wav");
        try
        {
            ExecutionAudioResult result = ExecutionAudio.Analyze(path, options, cancellationToken);
            if (options.NonSilent is { } expected && expected != result.NonSilent)
                diagnostics.Add(new("execution.audio_silence", "error", expected ? "The audio analysis window is silent." : "The audio analysis window contains nonzero samples.", File: path));
            Check("rms", result.Rms, options.MinRms, options.MaxRms);
            Check("peak", result.Peak, options.MinPeak, options.MaxPeak);
            return result;
        }
        catch (Exception error) when (error is DiskException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new("execution.audio_invalid", "error", "Cannot inspect the requested audio capture: " + error.Message, File: path));
            return null;
        }

        void Check(string name, double actual, double? minimum, double? maximum)
        {
            if (minimum is { } low && actual < low || maximum is { } high && actual > high)
                diagnostics.Add(new("execution.audio_" + name, "error", $"Audio {name} is outside the requested bounds.", File: path,
                    Expected: (minimum ?? 0).ToString("R", CultureInfo.InvariantCulture) + ".." + (maximum ?? 1).ToString("R", CultureInfo.InvariantCulture),
                    Actual: actual.ToString("R", CultureInfo.InvariantCulture)));
        }
    }
}
