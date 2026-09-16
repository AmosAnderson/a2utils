# Speaker capture and assertions

Set `audio` in an execution specification to preserve `audio.wav` and compute
normalized RMS and peak amplitudes. The adapter supplies MAME `-wavwrite` and
`-samplerate 44100` while retaining headless `-sound none`.

```json
{
  "audio": {
    "afterSeconds": 3,
    "durationSeconds": 1,
    "nonSilent": true,
    "minRms": 0.01,
    "maxPeak": 1
  }
}
```

This fragment belongs in an ordinary [execution specification](execution.md).
Choose an analysis window after startup to exclude the boot beep. If omitted,
`afterSeconds` is zero and analysis extends through the available capture, up
to 120 seconds. A requested `durationSeconds` must be entirely present; an
early completion cannot silently shorten an asserted window.

Audio-enabled runs allow at most 120 emulated seconds. The WAV parser enforces
a 64 MiB file limit and a maximum two-second shutdown allowance; analyzed
audio never extends beyond 120 seconds. MAME emits signed 16-bit PCM. One or
two channels are supported, with RMS computed over every sample in both
channels and peak taken from the largest absolute sample. Values are normalized
by 32768. `nonSilent` means at least one nonzero sample; use `minRms` to reject
low-level noise as well. `nonSilent: false` asserts exact digital silence.

`minRms`, `maxRms`, `minPeak`, and `maxPeak` are optional inclusive bounds from
zero to one. Missing, malformed, truncated, or incomplete requested audio is
a failed execution. `result.json` contains the WAV hash, sample rate, channel
count, recording duration, analyzed interval, sample-frame count, and measured
amplitudes. The WAV remains an artifact for listening or independent analysis.
An empty `audio` object requests capture without an amplitude assertion.

MAME 0.289 records the speaker stream before host audio effects. Its
[`sound_manager::streams_update`](https://github.com/mamedev/mame/blob/mame0289/src/emu/sound.cpp)
updates those streams and writes the WAV independently of the disabled host
sound output. Tests validate the parser and metric calculations using known
PCM samples; actual speaker output still requires a local MAME and ROM run.
