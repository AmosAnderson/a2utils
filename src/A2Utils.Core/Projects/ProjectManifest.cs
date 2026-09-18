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
    public List<ProjectAsset> Assets { get; init; } = [];
    public List<MemoryRegion> Reserve { get; init; } = [];
    public bool CheckMemory { get; init; } = true;
    public int BasicWorkspaceBytes { get; init; }
    public string Runtime { get; init; } = "auto";
    public ProjectStartup? Startup { get; init; }
    public ProjectBoot? Boot { get; init; }
    public Cc65Options? Cc65 { get; init; }
    public ProjectExecutionSettings? Execution { get; init; }
    public string? Environment { get; init; }
    public string? ToolchainLock { get; init; }
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
    public string MemoryBank { get; init; } = "main";
    public string? OverlayGroup { get; init; }
    public List<MemoryRegion> RuntimeMemory { get; init; } = [];
    public bool CheckBasic { get; init; } = true;
}

/// <summary>Startup is an explicit BASIC launcher for an existing OS template.</summary>
public sealed record ProjectStartup
{
    public string Path { get; init; } = "HELLO";
    public string Program { get; init; } = "";
    public bool Replace { get; init; }
}

/// <summary>Original boot code written to one or more track-zero sectors of a DOS-order floppy.</summary>
public sealed record ProjectBoot
{
    public string Source { get; init; } = "";
    public string Kind { get; init; } = "asm";
    public ushort Origin { get; init; } = 0x0800;
    public int Sectors { get; init; } = 1;
}

public sealed record MemoryRegion(string Name, int Start, int Length, string MemoryBank = "main", string Kind = "data");
public sealed record BuildInput(string Path, string Sha256);
public sealed record BuiltFile(string Path, string Kind, string Type, ushort AuxType,
    int? Origin, int? EntryPoint, int Length, string Sha256, bool Resident,
    IReadOnlyDictionary<string, int> Symbols, object? SourceMap)
{
    public string MemoryBank { get; init; } = "main";
    public string? OverlayGroup { get; init; }
    public IReadOnlyList<MemoryRegion> RuntimeMemory { get; init; } = [];
}
public sealed record ProjectFileChange(string Path, string Action, long? PreviousLength, int Length);
public sealed record ProjectBuildPlan(long ImageSizeBytes, long FreeBytesBefore, long FreeBytesAfter,
    IReadOnlyList<ProjectFileChange> Files, IReadOnlyList<string> CreatedDirectories);
public sealed record ProjectBuildResult(string OutputPath, string Sha256, string Target, string Cpu,
    string FileSystem, string Bootability, string ToolVersion, DateTime Timestamp,
    IReadOnlyList<BuildInput> Inputs, IReadOnlyList<BuiltFile> Files,
    IReadOnlyList<MemoryRegion> Memory, IReadOnlyList<ProgramDiagnostic> Diagnostics)
{
    public IReadOnlyList<ProjectAssetReport> Assets { get; init; } = [];
    public bool CheckOnly { get; init; }
    public bool Preflight { get; init; }
    public ProjectBuildPlan? Plan { get; init; }
    public ProjectExecutionSettings? Execution { get; init; }
    public BuiltBoot? Boot { get; init; }
    public bool CacheHit { get; init; }
}

public sealed record BuiltBoot(string Source, string Kind, int Origin, int Sectors,
    int Length, string Sha256);

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
