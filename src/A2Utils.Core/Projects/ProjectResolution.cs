// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using A2Utils.Core.Setup;

namespace A2Utils.Core.Projects;

public sealed record ProjectResolvedSettings(string Target, string Cpu, string Runtime,
    bool CheckMemory, int BasicWorkspaceBytes, string? EnvironmentPath,
    string? ToolchainLockPath, ProjectExecutionSettings? Execution);

public sealed record ProjectResolvedDisk(string FileSystem, string Container, string Order,
    int Blocks, string VolumeName, int VolumeNumber, string? TemplatePath);

public sealed record ProjectResolvedBoot(string SourcePath, string Kind, int Origin, int Sectors,
    int PayloadLength, string PayloadSha256, string Action);

public sealed record ProjectResolvedFile(string SourcePath, string ImagePath, string Kind,
    string Type, int? Origin, int? EntryPoint, int Length, string MemoryBank,
    string? OverlayGroup, bool Resident, bool Replace);

public sealed record ProjectResolvedAsset(string Name, string SourcePath, string Kind,
    IReadOnlyList<string> VirtualOutputs);

public sealed record ProjectDependency(string Role, string Path, string? Sha256, bool Exists);

public sealed record ProjectToolRequirement(string Id, bool Required, string Purpose,
    string? Path, string? ExpectedVersion, bool? Available);

public sealed record ProjectPlannedEntry(string ImagePath, string? SourcePath, string Kind,
    string Action, string Type, int? Origin, int Length);

public sealed record ProjectResolutionDiskPlan(long? ImageSizeBytes, long? FreeBytesBefore,
    long CompiledPayloadBytes, long BootSectorBytes, IReadOnlyList<ProjectPlannedEntry> Entries,
    IReadOnlyList<string> Directories);

public sealed record ProjectResolutionResult(int SchemaVersion, string ManifestPath,
    string ProjectRoot, string OutputPath, ProjectResolvedSettings Settings,
    ProjectResolvedDisk Disk, ProjectResolvedBoot? Boot, IReadOnlyList<ProjectResolvedFile> Files,
    IReadOnlyList<ProjectResolvedAsset> Assets, IReadOnlyList<ProjectDependency> Dependencies,
    IReadOnlyList<ProjectToolRequirement> ToolRequirements, IReadOnlyList<MemoryRegion> Memory,
    ProjectResolutionDiskPlan DiskPlan, IReadOnlyList<ProgramDiagnostic> Diagnostics);

internal sealed record ResolvedProjectManifest(string ManifestPath, string ProjectRoot,
    byte[] ManifestBytes, ProjectManifest Manifest, string OutputPath,
    ProjectExecutionSettings? Execution, TargetProfile Profile, string Cpu,
    string FileSystem, string Order, string? TemplatePath,
    ProjectInputSnapshot? EnvironmentInput, DevelopmentEnvironmentProfile? EnvironmentProfile,
    ProjectInputSnapshot? ToolchainLockInput, DevelopmentEnvironmentLock? ToolchainLock,
    ProjectInputSnapshot? TemplateInput);

internal sealed record ProjectInputSnapshot(string Path, byte[] Bytes, string Sha256)
{
    public static ProjectInputSnapshot Capture(string path, int maximumBytes,
        CancellationToken cancellationToken)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        byte[] bytes = ProgramFiles.ReadBytes(fullPath, maximumBytes, cancellationToken);
        return new(fullPath, bytes, ProgramFiles.Hash(bytes));
    }
}

internal sealed class TemporaryProjectInput : IDisposable
{
    public TemporaryProjectInput(ProjectInputSnapshot snapshot, string purpose)
    {
        string extension = System.IO.Path.GetExtension(snapshot.Path);
        string temporaryRoot = HostFiles.ResolvePhysicalDirectory(
            System.IO.Path.GetTempPath());
        Path = System.IO.Path.Combine(temporaryRoot,
            $"a2-{purpose}-{Guid.NewGuid():N}{extension}");
        try
        {
            using FileStream output = new(Path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None);
            output.Write(snapshot.Bytes);
            output.Flush(flushToDisk: true);
        }
        catch
        {
            try { File.Delete(Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public string Path { get; }

    public void Dispose()
    {
        try { File.Delete(Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Loads the effective project configuration and inspects a compiled plan without committing output.</summary>
public static class ProjectResolver
{
    public static ProjectResolutionResult Inspect(string manifestPath,
        CancellationToken cancellationToken = default)
        => InspectCore(manifestPath, null, null, cancellationToken);

    internal static ProjectResolutionResult InspectForTest(string manifestPath,
        Action? afterLoad, Action? afterBuild, CancellationToken cancellationToken = default)
        => InspectCore(manifestPath, afterLoad, afterBuild, cancellationToken);

    private static ProjectResolutionResult InspectCore(string manifestPath, Action? afterLoad,
        Action? afterBuild, CancellationToken cancellationToken)
    {
        ResolvedProjectManifest resolved = Load(manifestPath, null, cancellationToken);
        afterLoad?.Invoke();
        ProjectBuildResult check = ProjectBuilder.BuildResolved(resolved, checkOnly: true,
            cancellationToken: cancellationToken);
        afterBuild?.Invoke();
        ProjectManifest manifest = resolved.Manifest;
        StringComparer imageComparer = StringComparer.OrdinalIgnoreCase;
        StringComparer hostComparer = HostPathComparer;

        Dictionary<string, ProjectFile> declaredFiles = manifest.Files.ToDictionary(
            file => file.Path, imageComparer);
        ProjectResolvedFile[] files = check.Files.Select(file =>
        {
            declaredFiles.TryGetValue(file.Path, out ProjectFile? declared);
            string source = declared is null ? ""
                : Path.GetFullPath(declared.Source, resolved.ProjectRoot);
            return new ProjectResolvedFile(source, file.Path, file.Kind, file.Type,
                file.Origin, file.EntryPoint, file.Length, file.MemoryBank,
                file.OverlayGroup, file.Resident, declared?.Replace ?? manifest.Startup?.Replace == true);
        }).ToArray();

        ProjectResolvedAsset[] assets = manifest.Assets.Select(asset => new ProjectResolvedAsset(
            asset.Name, Path.GetFullPath(asset.Source, resolved.ProjectRoot), asset.Kind,
            AssetOutputs(asset).ToArray())).ToArray();
        ProjectResolvedBoot? boot = check.Boot is { } builtBoot
            ? new(builtBoot.Source, builtBoot.Kind, builtBoot.Origin, builtBoot.Sectors,
                builtBoot.Length, builtBoot.Sha256, "write-sectors")
            : null;

        string diskContainer = manifest.Disk.Container;
        string diskOrder = resolved.Order;
        int diskBlocks = manifest.Disk.Blocks;
        string diskVolumeName = manifest.Disk.VolumeName;
        int diskVolumeNumber = manifest.Disk.VolumeNumber;
        long imageSize = (long)diskBlocks * 512;
        long? freeBefore = null;
        HashSet<string> existingEntries = new(imageComparer);
        HashSet<string> existingDirectories = new(imageComparer);
        if (resolved.TemplatePath is not null)
        {
            ProjectInputSnapshot templateInput = resolved.TemplateInput
                ?? throw Error("template_snapshot", "The resolved template snapshot is unavailable.");
            using TemporaryProjectInput snapshot = new(templateInput, "resolve-template");
            using DiskSession session = DiskSession.Open(snapshot.Path, manifest.Disk.Order,
                resolved.FileSystem);
            imageSize = session.Info.SizeBytes;
            freeBefore = session.Info.FreeBytes;
            diskContainer = session.Info.Container;
            diskOrder = session.Info.Order;
            diskBlocks = checked((int)(session.Info.SizeBytes / 512));
            diskVolumeName = session.Info.VolumeName;
            diskVolumeNumber = session.Info.VolumeNumber ?? manifest.Disk.VolumeNumber;
            foreach (DiskEntry entry in session.List(recursive: true))
            {
                (entry.IsDirectory ? existingDirectories : existingEntries).Add(entry.Path);
            }
        }

        List<ProjectDependency> dependencies = check.Inputs.Select(input => new ProjectDependency(
            DependencyRole(input.Path, resolved, manifest), input.Path, input.Sha256, true)).ToList();
        ResolvedExecutionInputs? executionInputs = resolved.Execution is { } execution
            ? ResolveExecutionInputs(execution, check, imageSize, cancellationToken)
            : null;
        if (executionInputs is not null) dependencies.AddRange(executionInputs.Dependencies);

        List<ProjectToolRequirement> tools =
        [
            new("a2utils", true, "Manifest resolution, built-in compilation, assets, and disk planning.",
                null, null, true)
        ];
        bool needsCc65 = manifest.Files.Any(file => file.Kind == "cc65");
        if (needsCc65 || manifest.Cc65 is not null)
        {
            string? compiler = manifest.Cc65?.Compiler;
            tools.Add(new("cc65", needsCc65, "Compile C and ca65 project sources.", compiler,
                manifest.Cc65?.ExpectedVersion, needsCc65 ? true : ToolAvailability(compiler)));
        }
        if (executionInputs is { MameSpecs.Count: > 0 })
        {
            string[] emulatorPaths = executionInputs.MameSpecs.Select(spec => spec.EmulatorPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(hostComparer).ToArray();
            string[] expectedVersions = executionInputs.MameSpecs.Select(spec => spec.ExpectedVersion)
                .Where(version => !string.IsNullOrWhiteSpace(version)).Distinct(StringComparer.Ordinal).ToArray();
            tools.Add(new("mame", true, "Run the configured project execution suite.",
                emulatorPaths.Length == 1 ? emulatorPaths[0] : null,
                expectedVersions.Length == 1 ? expectedVersions[0] : null,
                executionInputs.MameSpecs.All(spec => !string.IsNullOrWhiteSpace(spec.EmulatorPath)
                    && File.Exists(spec.EmulatorPath))));
            string[] romDirectories = executionInputs.MameSpecs.Select(spec => spec.RomDirectory)
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(hostComparer).ToArray();
            tools.Add(new("mame-roms", true,
                "Supply Apple II ROM files for every configured MAME execution case.",
                romDirectories.Length == 1 ? romDirectories[0] : null, null,
                executionInputs.MameSpecs.All(spec => !string.IsNullOrWhiteSpace(spec.RomDirectory)
                    && Directory.Exists(spec.RomDirectory))));
        }

        string[] directories = check.Files.SelectMany(file => ParentDirectories(file.Path))
            .Distinct(imageComparer).Where(path => !existingDirectories.Contains(path))
            .OrderBy(path => path, imageComparer).ToArray();
        ProjectPlannedEntry[] entries = files.Select(file => new ProjectPlannedEntry(
            file.ImagePath, string.IsNullOrEmpty(file.SourcePath) ? null : file.SourcePath,
            file.Kind, existingEntries.Contains(file.ImagePath) ? "replace" : "add", file.Type, file.Origin,
            file.Length)).ToArray();
        ProjectResolutionDiskPlan plan = new(imageSize, freeBefore,
            files.Sum(file => (long)file.Length) + (boot?.PayloadLength ?? 0),
            (long)(boot?.Sectors ?? 0) * 256, entries, directories);

        return new(1, resolved.ManifestPath, resolved.ProjectRoot, resolved.OutputPath,
            new(resolved.Profile.Name, resolved.Cpu, manifest.Runtime, manifest.CheckMemory,
                manifest.BasicWorkspaceBytes, manifest.Environment, manifest.ToolchainLock,
                resolved.Execution),
            new(resolved.FileSystem, diskContainer, diskOrder, diskBlocks,
                diskVolumeName, diskVolumeNumber,
                resolved.TemplatePath), boot, files, assets,
            dependencies.OrderBy(dependency => dependency.Path, hostComparer)
                .ThenBy(dependency => dependency.Role, StringComparer.Ordinal).ToArray(), tools,
            check.Memory, plan, check.Diagnostics);
    }

    internal static ResolvedProjectManifest Load(string manifestPath, string? outputPath,
        CancellationToken cancellationToken)
    {
        string manifestFile = Path.GetFullPath(manifestPath);
        byte[] manifestBytes = ProgramFiles.ReadBytes(manifestFile, 1024 * 1024,
            cancellationToken);
        ProjectManifest manifest;
        try
        {
            using JsonDocument json = JsonDocument.Parse(manifestBytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int schemaVersion) || schemaVersion != 1)
            {
                throw new JsonException("Project schemaVersion must be 1.");
            }
            ProjectBuilder.RejectDuplicateProperties(json.RootElement);
            manifest = JsonSerializer.Deserialize<ProjectManifest>(manifestBytes,
                ProjectJson.Options) ?? throw new JsonException("Project must be an object.");
        }
        catch (JsonException exception)
        {
            int? line = exception.LineNumber is { } lineNumber && lineNumber < int.MaxValue
                ? (int)lineNumber + 1 : null;
            int? column = exception.BytePositionInLine is { } bytePosition &&
                bytePosition < int.MaxValue ? (int)bytePosition + 1 : null;
            ProgramDiagnostic diagnostic = new("project.schema", "error", exception.Message,
                manifestFile, line, column)
            {
                JsonPointer = exception.Path,
                Phase = "resolve",
                Tool = "a2.project"
            };
            throw new DiskException("project.schema", exception.Message, 2)
            {
                Diagnostics = [diagnostic]
            };
        }

        string root = Path.GetDirectoryName(manifestFile)!;
        ProjectInputSnapshot? environmentInput = null;
        DevelopmentEnvironmentProfile? environmentProfile = null;
        ProjectInputSnapshot? toolchainLockInput = null;
        DevelopmentEnvironmentLock? toolchainLock = null;
        if (manifest.ToolchainLock is not null && manifest.Environment is null)
            throw Error("environment", "toolchainLock requires an environment profile.");
        if (manifest.Environment is { } environment)
        {
            string environmentFile = Path.GetFullPath(environment, root);
            environmentInput = ProjectInputSnapshot.Capture(environmentFile,
                4 * 1024 * 1024, cancellationToken);
            environmentProfile = DevelopmentEnvironmentProfile.Load(environmentFile,
                environmentInput.Bytes);
            string? toolchainLockFile = manifest.ToolchainLock is null ? null
                : Path.GetFullPath(manifest.ToolchainLock, root);
            if (toolchainLockFile is not null)
            {
                toolchainLockInput = ProjectInputSnapshot.Capture(toolchainLockFile,
                    4 * 1024 * 1024, cancellationToken);
                toolchainLock = DevelopmentEnvironment.ReadJson<DevelopmentEnvironmentLock>(
                    toolchainLockFile, toolchainLockInput.Bytes, out _);
            }
            manifest = manifest with
            {
                Environment = environmentFile,
                ToolchainLock = toolchainLockFile,
                Disk = manifest.Disk is null ? null! : manifest.Disk with
                {
                    Template = manifest.Disk.Template is null ? null
                        : Path.GetFullPath(manifest.Disk.Template, root)
                },
                Cc65 = manifest.Cc65 is not { } compilerOptions ? null : compilerOptions with
                {
                    Compiler = compilerOptions.Compiler is { } compilerName &&
                        compilerName.IndexOfAny(['/', '\\']) >= 0
                        ? Path.GetFullPath(compilerName, root) : compilerOptions.Compiler,
                    ToolchainRoot = compilerOptions.ToolchainRoot is null ? null
                        : Path.GetFullPath(compilerOptions.ToolchainRoot, root)
                }
            };
            manifest = DevelopmentEnvironment.Apply(manifest, environmentProfile);
            if (toolchainLock is not null)
                DevelopmentEnvironment.ValidateProjectLock(manifest, environmentProfile,
                    toolchainLock, cancellationToken);
        }
        ProjectBuilder.ValidateManifest(manifest);
        ProjectExecutionSettings? executionSettings = manifest.Execution is { } execution
            ? execution with { Suite = Path.GetFullPath(execution.Suite, root) } : null;
        string output = Path.GetFullPath(outputPath ?? manifest.Output, root);
        TargetProfile profile = TargetProfiles.Get(manifest.Target);
        string cpu = manifest.Cpu ?? profile.Cpu;
        _ = TargetProfiles.ParseCpu(cpu);
        if (cpu == "w65c02" || cpu == "65c02" && profile.Cpu == "6502")
            throw Error("cpu_target", $"CPU '{cpu}' is incompatible with target '{profile.Name}'.");
        string fs = manifest.Disk.FileSystem;
        string order = manifest.Disk.Order ?? (fs == "dos33" ? "dos" : "prodos");
        string? template = manifest.Disk.Template is null ? null
            : Path.GetFullPath(manifest.Disk.Template, root);
        ProjectInputSnapshot? templateInput = template is null ? null
            : ProjectInputSnapshot.Capture(template, 34 * 1024 * 1024,
                cancellationToken);
        return new(manifestFile, root, manifestBytes, manifest, output, executionSettings,
            profile, cpu, fs, order, template, environmentInput, environmentProfile,
            toolchainLockInput, toolchainLock, templateInput);
    }

    private static IEnumerable<string> AssetOutputs(ProjectAsset asset)
    {
        yield return asset.Output;
        if (asset.AssemblyInclude is not null) yield return asset.AssemblyInclude;
        if (asset.CHeader is not null) yield return asset.CHeader;
        if (asset.MetadataOutput is not null) yield return asset.MetadataOutput;
    }

    private static IEnumerable<string> ParentDirectories(string path)
    {
        string[] parts = path.Split('/');
        string parent = "";
        foreach (string part in parts[..^1])
        {
            parent = parent.Length == 0 ? part : parent + "/" + part;
            yield return parent;
        }
    }

    private static string DependencyRole(string path, ResolvedProjectManifest resolved,
        ProjectManifest manifest)
    {
        StringComparison comparison = HostPathComparison;
        if (path.Equals(resolved.ManifestPath, comparison)) return "manifest";
        if (path.Equals(manifest.Environment, comparison)) return "environment";
        if (path.Equals(manifest.ToolchainLock, comparison)) return "toolchain-lock";
        if (path.Equals(resolved.TemplatePath, comparison)) return "disk-template";
        if (manifest.Assets.Any(asset => path.Equals(
            Path.GetFullPath(asset.Source, resolved.ProjectRoot), comparison))) return "asset-source";
        if (manifest.Boot is { } boot && path.Equals(
            Path.GetFullPath(boot.Source, resolved.ProjectRoot), comparison)) return "boot-source";
        if (manifest.Files.Any(file => path.Equals(
            Path.GetFullPath(file.Source, resolved.ProjectRoot), comparison))) return "source";
        return "transitive-input";
    }

    private static bool? ToolAvailability(string? path)
        => path is null || !Path.IsPathFullyQualified(path) ? null : File.Exists(path);

    private static ResolvedExecutionInputs ResolveExecutionInputs(ProjectExecutionSettings settings,
        ProjectBuildResult build, long imageSizeBytes,
        CancellationToken cancellationToken)
    {
        List<ProjectDependency> dependencies = [];
        List<ExecutionSpec> mameSpecs = [];
        StringComparer comparer = HostPathComparer;

        ExecutionSuite suite = ExecutionSuite.Load(settings.Suite,
            (path, hash) => AddKnown("execution-suite", path, hash));
        foreach (string specPath in suite.Tests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<(string Path, string Hash)> loadedInputs = [];
            ExecutionSpec spec = ExecutionSpec.Load(specPath,
                (path, hash) => loadedInputs.Add((path, hash)));
            foreach ((string path, string hash) in loadedInputs)
                AddKnown(comparer.Equals(path, specPath) ? "execution-spec"
                    : spec.ToolchainLock is not null && comparer.Equals(path, spec.ToolchainLock)
                        ? "execution-toolchain-lock" : "execution-environment", path, hash);
            (string Path, string Sha256)? screenshotInput = ExecutionVisual.ValidateExpected(
                spec, cancellationToken);
            if (screenshotInput is { } screenshot)
                AddKnown("execution-screenshot", screenshot.Path, screenshot.Sha256);
            PreparedGraphicsExecution graphics = ExecutionGraphicsMemory.Prepare(spec,
                cancellationToken);
            foreach (PreparedGraphicsAssertion assertion in graphics.Assertions)
                AddKnown("execution-graphics", assertion.SourcePath, assertion.SourceSha256);

            _ = BuildExecution.BindForValidation(spec, build, imageSizeBytes,
                settings.DiskDevice);
            if (screenshotInput is { } stableScreenshot && ProjectWorkflow.HashExecutionInput(
                stableScreenshot.Path, 32 * 1024 * 1024, cancellationToken) != stableScreenshot.Sha256)
                throw new DiskException("project.execution_changed",
                    $"Execution input changed while resolving: {stableScreenshot.Path}", 6);
            foreach (PreparedGraphicsAssertion assertion in graphics.Assertions)
                if (ProjectWorkflow.HashExecutionInput(assertion.SourcePath,
                    32 * 1024 * 1024, cancellationToken) != assertion.SourceSha256)
                    throw new DiskException("project.execution_changed",
                        $"Execution input changed while resolving: {assertion.SourcePath}", 6);
            if (spec.Engine == "mame") mameSpecs.Add(spec);

            if (spec.Routine is not null)
            {
                // A routine-only projection lets the existing harness validate and discover
                // source/include inputs without binding project symbols or starting MAME.
                ExecutionSpec routineSpec = new()
                {
                    Name = spec.Name,
                    Engine = "cpu",
                    Machine = spec.Machine,
                    EmulatedSeconds = spec.EmulatedSeconds,
                    HostTimeoutSeconds = spec.HostTimeoutSeconds,
                    Routine = spec.Routine
                };
                PreparedRoutine routine = RoutineHarness.Assemble(routineSpec, cancellationToken);
                foreach (var input in routine.InputHashes)
                    AddKnown(comparer.Equals(input.Key, spec.Routine.Source)
                        ? "execution-routine-source" : "execution-routine-include", input.Key, input.Value);
            }

            foreach (ExecutionDisk disk in spec.GetDisks()
                .Where(disk => disk.Device != settings.DiskDevice))
            {
                string hash = AddHashed("execution-auxiliary-disk", disk.Image,
                    64 * 1024 * 1024);
                if (disk.ExpectedSha256 is { } expected &&
                    !expected.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DiskException("execution.disk_hash",
                        $"Execution input hash does not match: {disk.Image}", 6);
                }
            }
        }

        return new(dependencies, mameSpecs);

        void AddKnown(string role, string path, string hash)
        {
            string fullPath = Path.GetFullPath(path);
            ProjectDependency? samePath = dependencies.FirstOrDefault(item =>
                comparer.Equals(item.Path, fullPath));
            if (samePath is not null && !hash.Equals(samePath.Sha256,
                StringComparison.OrdinalIgnoreCase))
                throw new DiskException("project.execution_changed",
                    $"Execution input changed while resolving: {fullPath}", 6);
            ProjectDependency? existing = dependencies.FirstOrDefault(item => item.Role == role
                && comparer.Equals(item.Path, fullPath));
            if (existing is not null)
            {
                return;
            }
            dependencies.Add(new(role, fullPath, hash, true));
        }

        string AddHashed(string role, string path, int maximumBytes)
        {
            string fullPath = Path.GetFullPath(path);
            ProjectDependency? existing = dependencies.FirstOrDefault(item => item.Role == role
                && comparer.Equals(item.Path, fullPath));
            if (existing?.Sha256 is { } known) return known;
            string hash = ProjectWorkflow.HashExecutionInput(fullPath, maximumBytes, cancellationToken);
            AddKnown(role, fullPath, hash);
            return hash;
        }
    }

    private sealed record ResolvedExecutionInputs(IReadOnlyList<ProjectDependency> Dependencies,
        IReadOnlyList<ExecutionSpec> MameSpecs);

    private static StringComparer HostPathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison HostPathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static DiskException Error(string code, string message)
        => new("project." + code, message, 2);
}
