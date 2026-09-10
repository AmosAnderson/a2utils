using System.Text.Json;
using System.Text.Json.Serialization;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

public sealed record ProjectManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Target { get; init; } = "apple2e";
    public string? Cpu { get; init; }
    public string Output { get; init; } = "build.po";
    public ProjectDisk Disk { get; init; } = new();
    public DateTime Timestamp { get; init; } = new(2000, 1, 1);
    public List<ProjectFile> Files { get; init; } = [];
    public List<MemoryRegion> Reserve { get; init; } = [];
    public bool CheckMemory { get; init; } = true;
    public int BasicWorkspaceBytes { get; init; }
    public ProjectStartup? Startup { get; init; }
    public Cc65Options? Cc65 { get; init; }
}

public sealed record ProjectDisk
{
    public string FileSystem { get; init; } = "prodos";
    public string? Template { get; init; }
    public string? TemplateSha256 { get; init; }
    public int Blocks { get; init; } = 280;
    public string Container { get; init; } = "raw";
    public string? Order { get; init; }
    public string VolumeName { get; init; } = "A2PROJECT";
    public int VolumeNumber { get; init; } = 254;
}

public sealed record ProjectFile
{
    public string Source { get; init; } = "";
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "binary";
    public string? Type { get; init; }
    public ushort? Origin { get; init; }
    public ushort? AuxType { get; init; }
    public ushort? EntryPoint { get; init; }
    public bool Replace { get; init; }
    public bool Resident { get; init; } = true;
    public bool CheckBasic { get; init; } = true;
}

/// <summary>Startup is an explicit BASIC launcher for an existing OS template.</summary>
public sealed record ProjectStartup
{
    public string Path { get; init; } = "HELLO";
    public string Program { get; init; } = "";
    public bool Replace { get; init; }
}

public sealed record MemoryRegion(string Name, int Start, int Length);
public sealed record BuildInput(string Path, string Sha256);
public sealed record BuiltFile(string Path, string Kind, string Type, ushort AuxType,
    int? Origin, int? EntryPoint, int Length, string Sha256, bool Resident,
    IReadOnlyDictionary<string, int> Symbols, object? SourceMap);
public sealed record ProjectBuildResult(string OutputPath, string Sha256, string Target, string Cpu,
    string FileSystem, string Bootability, string ToolVersion, DateTime Timestamp,
    IReadOnlyList<BuildInput> Inputs, IReadOnlyList<BuiltFile> Files,
    IReadOnlyList<MemoryRegion> Memory, IReadOnlyList<ProgramDiagnostic> Diagnostics);

public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        MaxDepth = 32
    };
}
