// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

/// <summary>A reproducible, bounded Apple II machine execution. Times are seconds since power-on.</summary>
public sealed record ExecutionSpec
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "run";
    public string Engine { get; init; } = "mame";
    public string EmulatorPath { get; init; } = "";
    public string ExpectedVersion { get; init; } = MameAdapter.ApiVersion;
    public string Machine { get; init; } = "";
    public string RomDirectory { get; init; } = "";
    public string? Environment { get; init; }
    public string? ToolchainLock { get; init; }
    public string StorageProfile { get; init; } = "floppy";
    public string GamePort { get; init; } = "none";
    public string DiskImage { get; init; } = "";
    public string DiskDevice { get; init; } = "flop1";
    public IReadOnlyList<ExecutionDisk> Disks { get; init; } = [];
    public IReadOnlyList<DiskFileAssertion> DiskAssertions { get; init; } = [];
    public IReadOnlyList<SymbolicMemoryAssertion> SymbolicMemory { get; init; } = [];
    public SymbolicCompletionCondition? SymbolicUntil { get; init; }
    public double EmulatedSeconds { get; init; } = 15;
    public double HostTimeoutSeconds { get; init; } = 60;
    public IReadOnlyList<ExecutionKeys> Keys { get; init; } = [];
    public IReadOnlyList<ExecutionStep> Steps { get; init; } = [];
    public IReadOnlyList<string> TextNotContains { get; init; } = [];
    public bool CheckBasicRuntime { get; init; }
    public IReadOnlyList<MemoryAssertion> Memory { get; init; } = [];
    public IReadOnlyList<GraphicsMemoryAssertion> GraphicsMemory { get; init; } = [];
    public IReadOnlyList<MemoryCapture> ObserveMemory { get; init; } = [];
    public IReadOnlyList<RegisterAssertion> Registers { get; init; } = [];
    public IReadOnlyList<string> TextContains { get; init; } = [];
    public CompletionCondition? Until { get; init; }
    public int TextPage { get; init; } = 1;
    public int TextColumns { get; init; } = 40;
    public bool DecodeIIeText { get; init; }
    public ExecutionRoutine? Routine { get; init; }
    public ExecutionCycleMeasurement? Cycles { get; init; }
    public ExecutionDebug? Debug { get; init; }
    public bool Screenshot { get; init; }
    public ScreenshotAssertion? ScreenshotAssertion { get; init; }
    public ExecutionAudioOptions? Audio { get; init; }
    public bool Trace { get; init; }

    [JsonIgnore]
    internal ExecutionEnvironmentSnapshot? EnvironmentSnapshot { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 32,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ExecutionSpec Load(string path)
        => Load(path, null);

    internal static ExecutionSpec Load(string path, Action<string, string>? inputLoaded)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            byte[] bytes = ProgramFiles.ReadBytes(fullPath, 4 * 1024 * 1024);
            inputLoaded?.Invoke(fullPath, ProgramFiles.Hash(bytes));
            ExecutionSpec spec = JsonSerializer.Deserialize<ExecutionSpec>(bytes, JsonOptions)
                ?? throw new JsonException("Execution specification must be an object.");
            ExecutionSpec resolved = spec.ResolvePaths(Path.GetDirectoryName(fullPath)!);
            if (resolved.Environment is null) return resolved;
            byte[] profileBytes = ProgramFiles.ReadBytes(resolved.Environment, 4 * 1024 * 1024);
            Setup.DevelopmentEnvironmentProfile profile = Setup.DevelopmentEnvironmentProfile.Load(
                resolved.Environment, profileBytes);
            inputLoaded?.Invoke(resolved.Environment, profile.InputSha256);
            byte[]? lockBytes = resolved.ToolchainLock is null ? null
                : ProgramFiles.ReadBytes(resolved.ToolchainLock, 4 * 1024 * 1024);
            string? lockHash = lockBytes is null ? null : ProgramFiles.Hash(lockBytes);
            if (lockHash is not null) inputLoaded?.Invoke(resolved.ToolchainLock!, lockHash);
            ExecutionEnvironmentSnapshot environment = new(resolved.Environment, profileBytes, profile,
                resolved.ToolchainLock, lockBytes, lockHash);
            return Setup.DevelopmentEnvironment.Apply(resolved, profile) with { EnvironmentSnapshot = environment };
        }
        catch (JsonException ex)
        {
            throw new DiskException("execution.invalid_spec", ex.Message, 2);
        }
    }

    public ExecutionSpec ResolvePaths(string directory) => this with
    {
        EmulatorPath = Resolve(EmulatorPath, directory),
        RomDirectory = Resolve(RomDirectory, directory),
        Environment = Environment is null ? null : Resolve(Environment, directory),
        ToolchainLock = ToolchainLock is null ? null : Resolve(ToolchainLock, directory),
        DiskImage = Resolve(DiskImage, directory),
        Routine = Routine is null ? null : Routine with { Source = Resolve(Routine.Source, directory) },
        ScreenshotAssertion = ScreenshotAssertion is { } screenshot
            ? screenshot with { ExpectedImage = Resolve(screenshot.ExpectedImage, directory) } : null,
        GraphicsMemory = GraphicsMemory?.Select(assertion => assertion is null ? null! : assertion with
        {
            ExpectedImage = Resolve(assertion.ExpectedImage, directory)
        }).ToArray()!,
        Steps = Steps?.Select(step => step is null || step.Condition is null ? step : step with
        {
            Condition = step.Condition with
            {
                GraphicsMemory = step.Condition.GraphicsMemory?.Select(assertion => assertion is null ? null! : assertion with
                {
                    ExpectedImage = Resolve(assertion.ExpectedImage, directory)
                }).ToArray()!
            }
        }).ToArray()!,
        Disks = Disks?.Select(disk => disk is null ? null! : disk with { Image = Resolve(disk.Image, directory) }).ToArray()!
    };

    /// <summary>Returns explicit mounts, or the legacy single-disk mount. Validate before using untrusted specifications.</summary>
    public IReadOnlyList<ExecutionDisk> GetDisks()
        => Disks is { Count: > 0 } ? Disks : Routine is not null && string.IsNullOrEmpty(DiskImage) ? [] : [new(DiskDevice, DiskImage)];

    private static string Resolve(string path, string directory)
        => string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path, directory);
}

public sealed record ExecutionDisk(string Device, string Image, string? ExpectedSha256 = null,
    string? InputOrder = null, string? InputFileSystem = null, bool Verify = false);

/// <summary>Assertions compare logical file payload bytes and logical lengths, excluding DOS host headers.</summary>
public sealed record DiskFileAssertion(string Device, string Path, bool Exists = true,
    string? Sha256 = null, string? Hex = null, string? Type = null, int? AuxType = null, long? Length = null);

public sealed record ExecutionKeys(double AtSeconds, string Text);
public sealed record MemoryAssertion(int Address, string Hex, string Bank = "cpu");
public sealed record MemoryCapture(int Address, int Length, string Bank = "cpu");
public sealed record RegisterAssertion(string Name, long Value);
public sealed record CompletionCondition(int Address, int Value, double AfterSeconds = 0, string Bank = "cpu");

public sealed record ExecutionSuite
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<string> Tests { get; init; } = [];

    public static ExecutionSuite Load(string path)
        => Load(path, null);

    internal static ExecutionSuite Load(string path, Action<string, string>? inputLoaded)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            byte[] bytes = ProgramFiles.ReadBytes(fullPath, 4 * 1024 * 1024);
            inputLoaded?.Invoke(fullPath, ProgramFiles.Hash(bytes));
            ExecutionSuite suite = JsonSerializer.Deserialize<ExecutionSuite>(bytes,
                ExecutionSpec.JsonOptions)
                ?? throw new JsonException("Test suite must be an object.");
            if (suite.SchemaVersion != 1 || suite.Tests is null || suite.Tests.Count == 0 || suite.Tests.Count > 128
                || suite.Tests.Any(string.IsNullOrWhiteSpace))
            {
                throw new JsonException("A version 1 suite must contain between 1 and 128 specification paths in tests.");
            }
            string directory = Path.GetDirectoryName(fullPath)!;
            return suite with { Tests = suite.Tests.Select(p => Path.GetFullPath(p, directory)).ToArray() };
        }
        catch (JsonException ex)
        {
            throw new DiskException("execution.invalid_suite", ex.Message, 2);
        }
    }
}
