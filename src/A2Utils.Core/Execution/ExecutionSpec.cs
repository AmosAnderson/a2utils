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
    public string EmulatorPath { get; init; } = "";
    public string ExpectedVersion { get; init; } = MameAdapter.ApiVersion;
    public string Machine { get; init; } = "";
    public string RomDirectory { get; init; } = "";
    public string DiskImage { get; init; } = "";
    public string DiskDevice { get; init; } = "flop1";
    public double EmulatedSeconds { get; init; } = 15;
    public double HostTimeoutSeconds { get; init; } = 60;
    public IReadOnlyList<ExecutionKeys> Keys { get; init; } = [];
    public IReadOnlyList<MemoryAssertion> Memory { get; init; } = [];
    public IReadOnlyList<RegisterAssertion> Registers { get; init; } = [];
    public IReadOnlyList<string> TextContains { get; init; } = [];
    public CompletionCondition? Until { get; init; }
    public int TextPage { get; init; } = 1;
    public bool Screenshot { get; init; }
    public bool Trace { get; init; }

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
    {
        try
        {
            ExecutionSpec spec = JsonSerializer.Deserialize<ExecutionSpec>(ProgramFiles.ReadText(path), JsonOptions)
                ?? throw new JsonException("Execution specification must be an object.");
            return spec.ResolvePaths(Path.GetDirectoryName(Path.GetFullPath(path))!);
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
        DiskImage = Resolve(DiskImage, directory)
    };

    private static string Resolve(string path, string directory)
        => string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path, directory);
}

public sealed record ExecutionKeys(double AtSeconds, string Text);
public sealed record MemoryAssertion(int Address, string Hex);
public sealed record RegisterAssertion(string Name, long Value);
public sealed record CompletionCondition(int Address, int Value, double AfterSeconds = 0);

public sealed record ExecutionSuite
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<string> Tests { get; init; } = [];

    public static ExecutionSuite Load(string path)
    {
        try
        {
            ExecutionSuite suite = JsonSerializer.Deserialize<ExecutionSuite>(ProgramFiles.ReadText(path), ExecutionSpec.JsonOptions)
                ?? throw new JsonException("Test suite must be an object.");
            if (suite.SchemaVersion != 1 || suite.Tests is null || suite.Tests.Count == 0 || suite.Tests.Count > 128
                || suite.Tests.Any(string.IsNullOrWhiteSpace))
            {
                throw new JsonException("A version 1 suite must contain between 1 and 128 specification paths in tests.");
            }
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            return suite with { Tests = suite.Tests.Select(p => Path.GetFullPath(p, directory)).ToArray() };
        }
        catch (JsonException ex)
        {
            throw new DiskException("execution.invalid_suite", ex.Message, 2);
        }
    }
}
