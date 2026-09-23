// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Reflection;
using System.Text.Json;
using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using A2Utils.Core.Setup;

namespace A2Utils.Core.Projects;

/// <summary>Compiles a manifest into one staged disk image; source metadata follows each payload.</summary>
public static partial class ProjectBuilder
{
    private const int MaximumBuildInputBytes = 64 * 1024 * 1024;

    public static ProjectBuildResult Build(string manifestPath, string? outputPath = null,
        bool overwrite = false, bool checkOnly = false, CancellationToken cancellationToken = default,
        bool preflight = false)
        => BuildCore(manifestPath, outputPath, overwrite, checkOnly, cancellationToken, preflight);

    internal static ProjectBuildResult BuildResolved(ResolvedProjectManifest resolved,
        bool overwrite = false, bool checkOnly = false,
        CancellationToken cancellationToken = default, bool preflight = false)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return BuildCore(resolved, overwrite, checkOnly, cancellationToken, preflight);
    }

    internal static ProjectBuildResult BuildVerified(string manifestPath, string? outputPath,
        bool overwrite, ProjectBuildResult expected, CancellationToken cancellationToken,
        Action? validateExternalInputs = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.CheckOnly || expected.Sha256.Length != 64)
            throw Error("build_changed", "A complete preflight image hash is required before committing a verified build.", 6);
        return BuildCore(manifestPath, outputPath, overwrite, false, cancellationToken, false,
            expected, validateExternalInputs);
    }

    internal static ProjectBuildResult BuildVerified(ResolvedProjectManifest resolved,
        bool overwrite, ProjectBuildResult expected, CancellationToken cancellationToken,
        Action? validateExternalInputs = null)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.CheckOnly || expected.Sha256.Length != 64)
            throw Error("build_changed", "A complete preflight image hash is required before committing a verified build.", 6);
        return BuildCore(resolved, overwrite, false, cancellationToken, false,
            expected, validateExternalInputs);
    }

    private static ProjectBuildResult BuildCore(string manifestPath, string? outputPath,
        bool overwrite, bool checkOnly, CancellationToken cancellationToken, bool preflight,
        ProjectBuildResult? expectedBuild = null, Action? validateExternalInputs = null)
    {
        if (checkOnly && preflight)
            throw Error("check_mode", "Choose either source checks or complete build preflight.");
        ResolvedProjectManifest resolved = ProjectResolver.Load(manifestPath, outputPath,
            cancellationToken);
        return BuildCore(resolved, overwrite, checkOnly, cancellationToken, preflight,
            expectedBuild, validateExternalInputs);
    }

    private static ProjectBuildResult BuildCore(ResolvedProjectManifest resolved,
        bool overwrite, bool checkOnly, CancellationToken cancellationToken, bool preflight,
        ProjectBuildResult? expectedBuild = null, Action? validateExternalInputs = null)
    {
        if (checkOnly && preflight) throw Error("check_mode", "Choose either source checks or complete build preflight.");
        string manifestFile = resolved.ManifestPath;
        byte[] manifestBytes = resolved.ManifestBytes;
        ProjectManifest manifest = resolved.Manifest;
        string root = resolved.ProjectRoot;
        ProjectExecutionSettings? executionSettings = resolved.Execution;
        string output = resolved.OutputPath;
        ImageTransactions.EnsureDistinctPaths(manifestFile, output);
        TargetProfile profile = resolved.Profile;
        string cpu = resolved.Cpu;
        CpuKind cpuKind = TargetProfiles.ParseCpu(cpu);
        string fs = resolved.FileSystem;
        string order = resolved.Order;
        string? template = resolved.TemplatePath;
        using TemporaryProjectInput? templateSnapshot = resolved.TemplateInput is null ? null
            : new(resolved.TemplateInput, "build-template");
        Dictionary<string, string> inputHashes = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [manifestFile] = ProgramFiles.Hash(manifestBytes)
        };
        if (manifest.Environment is { } profileInput)
        {
            ProjectInputSnapshot environmentInput = resolved.EnvironmentInput
                ?? throw Error("environment_snapshot", "The resolved environment snapshot is unavailable.", 6);
            ImageTransactions.EnsureDistinctPaths(profileInput, output);
            inputHashes[profileInput] = environmentInput.Sha256;
        }
        if (manifest.ToolchainLock is { } lockInput)
        {
            ProjectInputSnapshot toolchainLockInput = resolved.ToolchainLockInput
                ?? throw Error("lock_snapshot", "The resolved toolchain lock snapshot is unavailable.", 6);
            inputHashes[lockInput] = toolchainLockInput.Sha256;
        }
        if (template is not null)
        {
            ProjectInputSnapshot templateInput = resolved.TemplateInput
                ?? throw Error("template_snapshot", "The resolved template snapshot is unavailable.", 6);
            ImageTransactions.EnsureDistinctPaths(template, output);
            inputHashes[template] = templateInput.Sha256;
            if (manifest.Disk.TemplateSha256 is { } expected && !templateInput.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw Error("template_hash", "Template SHA-256 does not match the pinned value.");
            using DiskSession source = DiskSession.Open(templateSnapshot!.Path,
                manifest.Disk.Order, fs);
            order = source.Info.Order;
            ValidateStructure(source);
        }

        GeneratedAssetSet generatedAssets = PrepareAssets(manifest, root, output, ReadInput, cancellationToken);
        PreparedBoot? boot = PrepareBoot();
        List<PreparedFile> files = [];
        List<ProgramDiagnostic> diagnostics = [];
        long totalPayloadBytes = 0;
        foreach (ProjectFile item in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = Path.GetFullPath(item.Source, root);
            byte[] bytes = generatedAssets.Outputs.TryGetValue(source, out byte[]? generated) ? generated : ReadInput(source, 4 * 1024 * 1024);
            int? origin = item.Origin;
            ushort aux = item.AuxType ?? item.Origin ?? 0;
            string type = item.Type ?? (item.Kind is "basic" or "basic-labels" ? "BAS" : item.Kind == "text" ? "TXT" : "BIN");
            IReadOnlyDictionary<string, int> symbols = new Dictionary<string, int>();
            object? sourceMap = null;
            List<MemoryRegion> runtimeMemory = [];
            switch (item.Kind)
            {
                case "asm":
                    AssemblyResult assembled = Assembler.AssembleFile(source, generatedAssets.Outputs, item.Origin, cpuKind, cancellationToken);
                    bytes = assembled.Bytes;
                    origin = assembled.Origin;
                    foreach (var dependency in assembled.DependencyHashes)
                    {
                        if (generatedAssets.Outputs.ContainsKey(dependency.Key)) continue;
                        ImageTransactions.EnsureDistinctPaths(dependency.Key, output);
                        if (inputHashes.TryGetValue(dependency.Key, out string? oldHash) && oldHash != dependency.Value)
                            throw Error("source_changed", $"Input changed during compilation: {dependency.Key}", 6);
                        inputHashes[dependency.Key] = dependency.Value;
                    }
                    symbols = assembled.Symbols;
                    sourceMap = assembled.SourceMap;
                    RequireType(type, "BIN", "B", "0x06");
                    break;
                case "basic":
                case "basic-labels":
                    string listing = ProgramFiles.DecodeText(bytes);
                    if (item.Kind == "basic-labels")
                    {
                        BasicPrepareResult prepared = ApplesoftTools.Prepare(listing, file: source, cancellationToken: cancellationToken);
                        listing = prepared.Source;
                        sourceMap = prepared.Mapping;
                        diagnostics.AddRange(prepared.Diagnostics);
                    }
                    else if (item.CheckBasic)
                    {
                        BasicCheckResult check = ApplesoftTools.Check(listing, source, cancellationToken);
                        diagnostics.AddRange(check.Diagnostics);
                        if (!check.Valid)
                            throw new DiskException("project.basic_check", "BASIC source checks failed.", 2) { Diagnostics = check.Diagnostics };
                    }
                    origin ??= ApplesoftBasic.DefaultOrigin;
                    bytes = ApplesoftBasic.Compile(listing, (ushort)origin, cancellationToken);
                    sourceMap ??= MapBasicLines(listing).Select(line => line with { File = source }).ToArray();
                    RequireType(type, "BAS", "A", "0xfc");
                    break;
                case "text":
                    bytes = AppleTextCodec.Encode(bytes, fs);
                    RequireType(type, "TXT", "T", "0x04");
                    break;
                case "applesingle":
                    AppleSinglePayload decoded = AppleSingleProgram.Decode(bytes);
                    bytes = decoded.Bytes;
                    string decodedType = $"0x{decoded.FileType:x2}";
                    if (item.Type is not null && !TypesEqual(item.Type, decodedType))
                        throw Error("metadata_conflict", "Explicit type disagrees with AppleSingle metadata.");
                    if (item.AuxType is { } declaredAux && declaredAux != decoded.AuxType)
                        throw Error("metadata_conflict", "Explicit auxiliary type disagrees with AppleSingle metadata.");
                    type = decodedType;
                    aux = decoded.AuxType;
                    if (decoded.FileType is 0x06 or 0xfc) origin = decoded.AuxType;
                    if (decoded.FileType == 0xff) origin = 0x2000;
                    break;
                case "cc65":
                    Cc65Options options = manifest.Cc65 ?? throw Error("compiler", "cc65 source requires a cc65 configuration.");
                    if (options.Target == "apple2enh" && profile.Cpu != "65c02")
                        throw Error("cpu_target", "cc65 apple2enh requires an enhanced Apple II target.");
                    Cc65Result compiled = Cc65Compiler.Compile(source, options, root, cancellationToken,
                        generatedInputs: generatedAssets.Outputs.Where(pair => Path.GetExtension(pair.Key).ToLowerInvariant() is ".bin" or ".inc" or ".h")
                            .ToDictionary(pair => pair.Key, pair => pair.Value));
                    AppleSinglePayload cProgram = AppleSingleProgram.Decode(compiled.AppleSingle);
                    bytes = cProgram.Bytes;
                    type = $"0x{cProgram.FileType:x2}";
                    if (item.Type is not null && !TypesEqual(item.Type, type))
                        throw Error("metadata_conflict", "Explicit type disagrees with cc65 output metadata.");
                    if (item.AuxType is { } compilerAux && compilerAux != cProgram.AuxType)
                        throw Error("metadata_conflict", "Explicit auxiliary type disagrees with cc65 output metadata.");
                    aux = cProgram.AuxType;
                    origin = cProgram.FileType == 0xff ? 0x2000 : aux;
                    symbols = compiled.Symbols;
                    diagnostics.AddRange(compiled.Diagnostics);
                    runtimeMemory.AddRange(ProjectRuntimeMemory.FromCompiler(compiled, options, item));
                    if (!runtimeMemory.Any(region => region.Kind == "stack"))
                        diagnostics.Add(new("project.stack_budget", "warning", $"'{item.Path}' has no inferred C software-stack allocation; declare runtimeMemory with kind stack."));
                    if (!item.RuntimeMemory.Any(region => region.Kind == "heap"))
                        diagnostics.Add(new("project.heap_budget", "info", $"'{item.Path}' has no declared heap budget. If it uses allocation, reserve a runtimeMemory region with kind heap."));
                    foreach (var input in compiled.Inputs)
                    {
                        string dependencyPath = Path.GetFullPath(input.Path, root);
                        if (generatedAssets.Outputs.ContainsKey(dependencyPath)) continue;
                        if (inputHashes.TryGetValue(dependencyPath, out string? previousHash) && previousHash != input.Sha256)
                            throw Error("source_changed", $"Compiler input changed: {input.Path}", 6);
                        inputHashes[dependencyPath] = input.Sha256;
                    }
                    sourceMap = new Cc65SourceMap(compiled.Version, compiled.CompilerPath, compiled.Map,
                        compiled.Labels, compiled.Segments, compiled.SourceMap);
                    break;
                case "lores":
                case "hires":
                case "hires-color":
                    bytes = Graphics.AppleGraphics.EncodePng(bytes, item.Kind);
                    origin ??= item.Kind == "lores" ? 0x400 : 0x2000;
                    RequireType(type, "BIN", "B", "0x06");
                    break;
                case "binary":
                    if (TypesEqual(type, "BIN") || TypesEqual(type, "BAS"))
                    {
                        origin ??= item.AuxType;
                        if (origin is null) throw Error("origin_required", $"Raw BIN/BAS payload '{item.Path}' requires origin or auxType.");
                    }
                    else if (TypesEqual(type, "SYS")) origin = 0x2000;
                    break;
                default:
                    throw Error("kind", $"Unknown source kind '{item.Kind}'.");
            }
            if (DiskSession.ParseFileType(type, fs == "dos33") == 0x0f)
                throw Error("file_type", "DIR is reserved for directories.");
            if (origin is not null)
            {
                if (item.Origin is { } declared && origin != declared ||
                    !TypesEqual(type, "SYS") && item.AuxType is { } explicitAux && explicitAux != origin)
                    throw Error("metadata_conflict", $"'{item.Path}' load metadata disagrees with its compiled origin.");
                ProgramFileFormat.ValidateAddressRange(bytes.Length, (ushort)origin);
                if (!TypesEqual(type, "0xff")) aux = (ushort)origin;
            }
            else if (fs == "dos33" && TypesEqual(type, "BIN"))
                throw Error("origin_required", $"DOS binary '{item.Path}' requires an origin.");
            if (item.EntryPoint is { } entry && (origin is null || entry < origin || entry >= origin + bytes.Length))
                throw Error("entry_point", $"Entry point for '{item.Path}' is outside its payload.");
            if (item.MemoryBank != "main" && (origin is null || !TypesEqual(type, "BIN")))
                throw Error("memory_bank_payload", $"Banked payload '{item.Path}' must be a BIN file with a load address and an explicit loader.");
            if (origin is { } address)
                ValidateMemoryRegion(profile, new(item.Path, address, bytes.Length, item.MemoryBank));
            totalPayloadBytes += bytes.Length;
            if (totalPayloadBytes > 34 * 1024 * 1024) throw Error("payload_limit", "Combined project payloads exceed 34 MiB.");
            files.Add(new(item, bytes, new(item.Path, item.Kind, type, aux, origin, item.EntryPoint ?? origin,
                bytes.Length, ProgramFiles.Hash(bytes), item.Resident, symbols, sourceMap)
            {
                MemoryBank = item.MemoryBank,
                OverlayGroup = item.OverlayGroup,
                RuntimeMemory = runtimeMemory.Concat(item.RuntimeMemory).ToArray()
            }));
        }

        if (files.Any(file => file.Info.MemoryBank != "main"))
            diagnostics.Add(new("project.banked_loader", "info",
                "memoryBank describes physical residency only; disk files retain ordinary load addresses. Supply a main-memory loader that stages and copies banked payloads."));
        if (fs == "prodos" && files.Any(file => file.Info.MemoryBank.StartsWith("aux", StringComparison.Ordinal)))
            diagnostics.Add(new("project.prodos_auxiliary_memory", "warning",
                "Before using auxiliary RAM, the program must protect it from ProDOS /RAM or disconnect that RAM disk. The build does not change the runtime memory configuration."));

        if (files.Select(file => file.Info.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw Error("duplicate_path", "Project contains duplicate image paths.");
        if (manifest.Startup is { } startup)
        {
            if (template is null) throw Error("startup_template", "Startup generation requires an existing bootable OS template.");
            PreparedFile program = files.SingleOrDefault(file => file.Info.Path.Equals(startup.Program, StringComparison.OrdinalIgnoreCase))
                ?? throw Error("startup_program", "Startup program must identify one manifest file.");
            if (program.Info.MemoryBank != "main")
                throw Error("startup_bank", "Generated startup must launch a main-memory loader, not a banked payload.");
            string command = TypesEqual(program.Info.Type, "BAS") ? "RUN" : TypesEqual(program.Info.Type, "BIN") ? "BRUN"
                : throw Error("startup_type", "A BASIC launcher can RUN BASIC or BRUN BIN files.");
            if (program.Info.Path.Any(ch => ch is '"' or '\r' or '\n' or ',' or ':' || ch < 0x20 || ch > 0x7e))
                throw Error("startup_name", "Startup program name cannot be represented safely in a DOS command string.");
            if (TypesEqual(program.Info.Type, "BAS") && program.Info.Origin != ApplesoftBasic.DefaultOrigin)
                throw Error("startup_origin", "RUN startup requires BASIC at the default origin.");
            if (command == "BRUN" && program.Info.EntryPoint != program.Info.Origin)
                throw Error("startup_entry", "BRUN startup requires entry point equal to load origin.");
            string source = $"10 PRINT CHR$(4);\"{command} {program.Info.Path}\"\n";
            byte[] bytes = ApplesoftBasic.Compile(source);
            ProjectFile item = new() { Path = startup.Path, Kind = "basic", Replace = startup.Replace, Resident = false };
            files.Add(new(item, bytes, new(item.Path, "startup", "BAS", ApplesoftBasic.DefaultOrigin,
                ApplesoftBasic.DefaultOrigin, ApplesoftBasic.DefaultOrigin, bytes.Length, ProgramFiles.Hash(bytes), false,
                new Dictionary<string, int>(), null)));
            diagnostics.Add(new("project.startup_template", "info",
                "The template must already boot the configured launcher path (and contain BASIC.SYSTEM for ProDOS)."));
        }
        if (files.Select(file => file.Info.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw Error("duplicate_path", "Project contains duplicate image paths.");
        List<MemoryRegion> memory = CheckMemory(manifest, files);
        foreach (string input in inputHashes.Keys) ImageTransactions.EnsureDistinctPaths(input, output);
        CheckInputs();
        string imageHash = "";
        ProjectBuildPlan? plan = null;
        if (!checkOnly)
        {
            ImageTransactions.ValidatePath(output);
            if (preflight)
            {
                ValidatePreflightDestination(output, overwrite);
                // Resolve the trusted temporary root so the macOS /var alias does not
                // conflict with the normal transaction policy for linked paths.
                string temporaryRoot = HostFiles.ResolvePhysicalDirectory(Path.GetTempPath());
                string temporaryDirectory = Path.Combine(temporaryRoot, $"a2-build-{Guid.NewGuid():N}");
                ImageTransactions.ValidatePath(temporaryDirectory);
                Directory.CreateDirectory(temporaryDirectory);
                try
                {
                    BuildImage(Path.Combine(temporaryDirectory, "preflight.img"), false);
                }
                finally
                {
                    Cc65Compiler.CleanupGeneratedDirectoryAsync(temporaryDirectory).GetAwaiter().GetResult();
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                BuildImage(output, overwrite);
            }
        }
        return new(output, imageHash, profile.Name, cpu, fs, boot is not null ? "self-booting-unverified"
            : template is null ? "data-volume" : "template-preserved-unverified",
            typeof(ProjectBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            manifest.Timestamp, inputHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new BuildInput(pair.Key, pair.Value)).ToArray(),
            files.Select(file => file.Info).ToArray(), memory, diagnostics)
        {
            CheckOnly = checkOnly,
            Preflight = preflight,
            Plan = plan,
            Execution = executionSettings,
            Assets = generatedAssets.Reports,
            Boot = boot?.Report
        };

        void BuildImage(string destination, bool replaceOutput)
        {
            if (template is null)
                ImageTransactions.Create(destination, replaceOutput, temporary =>
                {
                    DiskSession.Create(temporary, fs, container: manifest.Disk.Container, order: order,
                        volumeName: manifest.Disk.VolumeName, volumeNumber: manifest.Disk.VolumeNumber, blocks: manifest.Disk.Blocks);
                    Populate(temporary);
                }, Validate, cancellationToken);
            else
                ImageTransactions.Write(templateSnapshot!.Path, destination, false,
                    replaceOutput, Populate, Validate, cancellationToken);
        }

        byte[] ReadInput(string path, int limit)
        {
            ImageTransactions.EnsureDistinctPaths(path, output);
            byte[] bytes = ProgramFiles.ReadBytes(path, limit, cancellationToken);
            string hash = ProgramFiles.Hash(bytes);
            if (inputHashes.TryGetValue(path, out string? old) && old != hash)
                throw Error("source_changed", $"Input changed: {path}", 6);
            inputHashes[path] = hash;
            return bytes;
        }

        void CheckInputs()
        {
            if (resolved.ToolchainLock is { } toolchainLock)
                DevelopmentEnvironment.ValidateProjectLock(manifest,
                    resolved.EnvironmentProfile!, toolchainLock, cancellationToken);
            else
                DevelopmentEnvironment.ValidateProjectLock(manifest,
                    resolved.EnvironmentProfile, cancellationToken);
            foreach (string generatedPath in generatedAssets.Outputs.Keys)
                if (File.Exists(generatedPath) || Directory.Exists(generatedPath))
                    throw Error("asset_collision", "A generated asset path appeared while building: " + generatedPath, 6);
            if (expectedBuild is not null && (inputHashes.Count != expectedBuild.Inputs.Count || expectedBuild.Inputs.Any(input =>
                !inputHashes.TryGetValue(input.Path, out string? hash) || hash != input.Sha256)))
                throw Error("build_changed", "Project inputs changed after preflight; the output was not committed.", 6);
            foreach (var input in inputHashes)
                if (ProgramFiles.Hash(ProgramFiles.ReadBytes(input.Key, MaximumBuildInputBytes,
                    cancellationToken)) != input.Value)
                    throw Error("source_changed", $"Input changed while building: {input.Key}", 6);
        }

        void Populate(string temporary)
        {
            CheckInputs();
            using DiskSession session = DiskSession.Open(temporary, order, fs, writable: true);
            DiskInfo initial = session.Info;
            List<ProjectFileChange> changes = [];
            HashSet<string> directories = session.List(recursive: true).Where(entry => entry.IsDirectory)
                .Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> createdDirectories = [];
            foreach (PreparedFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fs == "prodos")
                {
                    string[] parts = file.Info.Path.Split('/');
                    string parent = "";
                    foreach (string part in parts[..^1])
                    {
                        parent = parent.Length == 0 ? part : parent + "/" + part;
                        if (directories.Add(parent))
                        {
                            session.Mkdir(parent);
                            createdDirectories.Add(parent);
                        }
                    }
                }
                DiskEntry? existing = session.List(recursive: true).FirstOrDefault(entry => entry.Path.Equals(file.Info.Path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    if (!file.Item.Replace || existing.IsDirectory)
                        throw Error("entry_exists", $"Image entry exists: {file.Info.Path}; set replace explicitly.", 6);
                    if (!TypesEqual($"0x{existing.FileType:x2}", file.Info.Type))
                    {
                        session.Delete(existing.Path);
                        session.Add(file.Info.Path, file.Bytes, file.Info.Type, file.Info.AuxType);
                    }
                    else
                    {
                        session.Replace(existing.Path, file.Bytes);
                        session.SetAttributes(existing.Path, type: file.Info.Type, auxType: file.Info.AuxType);
                    }
                }
                else session.Add(file.Info.Path, file.Bytes, file.Info.Type, file.Info.AuxType);
                session.SetTimestamps(file.Info.Path, manifest.Timestamp, manifest.Timestamp);
                changes.Add(new(file.Info.Path, existing is null ? "add" : "replace", existing?.Length, file.Bytes.Length));
            }
            foreach (string directory in createdDirectories)
                session.SetTimestamps(directory, manifest.Timestamp, manifest.Timestamp);
            if (template is null) session.SetTimestamps("", manifest.Timestamp, manifest.Timestamp);
            if (boot is not null) session.WriteBootSectors(boot.Sectors);
            session.Flush();
            plan = new(initial.SizeBytes, initial.FreeBytes!.Value, session.Info.FreeBytes!.Value,
                changes.ToArray(), createdDirectories.ToArray());
        }

        void Validate(string temporary)
        {
            using DiskSession session = DiskSession.Open(temporary, order, fs);
            ValidateStructure(session);
            foreach (PreparedFile file in files)
            {
                DiskEntry entry = session.GetEntry(file.Info.Path);
                if (entry.AuxType != file.Info.AuxType && fs == "prodos" ||
                    !TypesEqual($"0x{entry.FileType:x2}", file.Info.Type) ||
                    !session.ReadFile(file.Info.Path).AsSpan().SequenceEqual(file.Bytes))
                    throw Error("validation", $"Stored payload or metadata differs for '{file.Info.Path}'.", 4);
                if (fs == "dos33" && TypesEqual(file.Info.Type, "BIN") && entry.AuxType != file.Info.AuxType)
                    throw Error("validation", $"Stored DOS load address differs for '{file.Info.Path}'.", 4);
            }
            if (boot is not null && !session.ReadBootSectors(boot.Report.Sectors).AsSpan().SequenceEqual(boot.Sectors))
                throw Error("boot_validation", "Stored boot sectors differ from the assembled input.", 4);
            CheckInputs();
            imageHash = ProgramFiles.Hash(ProgramFiles.ReadBytes(temporary, 34 * 1024 * 1024, cancellationToken));
            if (expectedBuild is not null && imageHash != expectedBuild.Sha256)
                throw Error("build_changed", "The staged image differs from preflight; the output was not committed.", 6);
            validateExternalInputs?.Invoke();
        }

        PreparedBoot? PrepareBoot()
        {
            if (manifest.Boot is not { } configured) return null;
            if (fs != "dos33" || manifest.Disk.Blocks != 280 || order != "dos")
                throw Error("boot_geometry", "Project boot sectors require a 140 KiB DOS-order DOS 3.3 image.");
            if (configured.Kind is not ("asm" or "binary") || string.IsNullOrWhiteSpace(configured.Source)
                || configured.Origin != 0x0800 || configured.Sectors is < 1 or > 16)
                throw Error("boot", "boot requires asm or binary source at origin $0800 and 1..16 sectors.");
            string source = Path.GetFullPath(configured.Source, root);
            byte[] bytes;
            if (configured.Kind == "asm")
            {
                AssemblyResult assembled = Assembler.AssembleFile(source, generatedAssets.Outputs,
                    configured.Origin, cpuKind, cancellationToken);
                if (assembled.Origin != configured.Origin)
                    throw Error("boot_origin", "Boot assembly must begin at $0800.");
                bytes = assembled.Bytes;
                foreach (var dependency in assembled.DependencyHashes)
                {
                    if (generatedAssets.Outputs.ContainsKey(dependency.Key)) continue;
                    ImageTransactions.EnsureDistinctPaths(dependency.Key, output);
                    if (inputHashes.TryGetValue(dependency.Key, out string? previous) && previous != dependency.Value)
                        throw Error("source_changed", $"Input changed: {dependency.Key}", 6);
                    inputHashes[dependency.Key] = dependency.Value;
                }
            }
            else
            {
                bytes = ReadInput(source, 4096);
            }
            int capacity = configured.Sectors * 256;
            if (bytes.Length > capacity)
                throw Error("boot_size", $"Boot payload exceeds its declared {configured.Sectors}-sector capacity.");
            byte[] sectors = new byte[capacity];
            bytes.CopyTo(sectors, 0);
            return new(sectors, new(source, configured.Kind, configured.Origin, configured.Sectors,
                bytes.Length, ProgramFiles.Hash(bytes)));
        }
    }

    private static void ValidatePreflightDestination(string output, bool overwrite)
    {
        for (string? parent = Path.GetDirectoryName(output); parent is not null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) throw new IOException("An output parent path is an existing file.");
        if (Directory.Exists(output))
            throw new DiskException("write.destination_exists", "The destination is an existing directory.");
        if (!File.Exists(output)) return;
        if (!overwrite)
            throw new DiskException("write.destination_exists", "The destination already exists; specify overwrite explicitly.");
        if ((File.GetAttributes(output) & FileAttributes.ReadOnly) != 0)
            throw new DiskException("write.read_only", "The destination is read-only.");
    }

    private static List<MemoryRegion> CheckMemory(ProjectManifest manifest, List<PreparedFile> files)
        => ProjectRuntimeMemory.Check(manifest, files.Select(file => (file.Item, file.Info)).ToArray());

    private static void ValidateMemoryRegion(TargetProfile profile, MemoryRegion region)
        => ProjectRuntimeMemory.Validate(profile, region);

    internal static void ValidateManifest(ProjectManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Target) || string.IsNullOrWhiteSpace(manifest.Output) ||
            manifest.Disk is null || manifest.Files is null || manifest.Reserve is null ||
            manifest.Files.Count > 1024 || manifest.Files.Count == 0 && string.IsNullOrWhiteSpace(manifest.Disk.Template) && manifest.Boot is null ||
            manifest.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Source) ||
                string.IsNullOrWhiteSpace(file.Path) || file.Path.StartsWith('/') || file.Path.Contains('\\') || file.Path.Split('/').Any(part => part is "" or "." or "..")))
            throw Error("schema", "Project requires 1..1024 source files, an explicit template, or boot sectors.");
        if (manifest.Disk.FileSystem is not ("dos33" or "prodos")) throw Error("filesystem", "Filesystem must be dos33 or prodos.");
        if (manifest.Disk.Blocks is < 280 or > 65535 || manifest.Disk.FileSystem == "dos33" && manifest.Disk.Blocks != 280)
            throw Error("geometry", "DOS requires 280 blocks; ProDOS permits 280..65535 blocks.");
        if (manifest.Disk.Order is not (null or "dos" or "prodos") || manifest.Disk.Container is not ("raw" or "2mg"))
            throw Error("disk_format", "Invalid disk order or container.");
        if (manifest.Timestamp.Year is < 1980 or > 2039 || manifest.Timestamp.Ticks % TimeSpan.TicksPerMinute != 0 || manifest.Timestamp.Kind == DateTimeKind.Local)
            throw Error("timestamp", "Use a UTC or offset-free timestamp between 1980 and 2039 with whole-minute precision.");
        if (manifest.BasicWorkspaceBytes is < 0 or > 65536) throw Error("basic_workspace", "BASIC workspace must be 0..65536 bytes.");
        if (manifest.Runtime is not ("auto" or "basic-system" or "system"))
            throw Error("runtime", "Runtime must be auto, basic-system, or system.");
        if (manifest.Reserve.Count > 1024 || manifest.Reserve.Any(region => region is null || string.IsNullOrWhiteSpace(region.Name)))
            throw Error("schema", "Reserve permits at most 1024 named memory regions.");
        if (manifest.Files.Any(file => file.RuntimeMemory is null || file.RuntimeMemory.Count > 1024 ||
            file.RuntimeMemory.Any(region => region is null || string.IsNullOrWhiteSpace(region.Name))) ||
            manifest.Files.Sum(file => file.RuntimeMemory.Count) > 4096)
            throw Error("runtime_memory", "Runtime memory requires named regions, at most 1024 per file and 4096 per project.");
        if (manifest.Files.Any(file => file.OverlayGroup is not null && (string.IsNullOrWhiteSpace(file.OverlayGroup) ||
            file.OverlayGroup.Length > 64 || file.OverlayGroup.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')))))
            throw Error("overlay_group", "Overlay group names must contain 1..64 ASCII letters, digits, underscores, hyphens or dots.");
        if (manifest.Reserve.Concat(manifest.Files.SelectMany(file => file.RuntimeMemory)).Any(region =>
            region.Kind is not ("data" or "bss" or "zero-page" or "stack" or "heap")))
            throw Error("runtime_memory", "Memory kind must be data, bss, zero-page, stack, or heap.");
        if (manifest.Files.Any(file => file.MemoryBank is not ("main" or "aux" or "lc1" or "lc2" or "aux-lc1" or "aux-lc2")) ||
            manifest.Reserve.Any(region => region.MemoryBank is not ("main" or "aux" or "lc1" or "lc2" or "aux-lc1" or "aux-lc2")))
            throw Error("memory_bank", "memoryBank must be main, aux, lc1, lc2, aux-lc1, or aux-lc2.");
        if (manifest.Startup is { } startup && (string.IsNullOrWhiteSpace(startup.Program) || string.IsNullOrWhiteSpace(startup.Path) ||
            startup.Path.StartsWith('/') || startup.Path.Contains('\\') || startup.Path.Split('/').Any(part => part is "" or "." or "..")))
            throw Error("startup", "Startup requires a program path and a relative launcher path.");
        if (manifest.Boot is { } boot && (string.IsNullOrWhiteSpace(boot.Source) || boot.Kind is not ("asm" or "binary")
            || boot.Origin != 0x0800 || boot.Sectors is < 1 or > 16))
            throw Error("boot", "boot requires asm or binary source at origin $0800 and 1..16 sectors.");
        if (manifest.Execution is { } execution && (string.IsNullOrWhiteSpace(execution.Suite) || execution.DiskDevice is not ("flop1" or "flop2" or "hard1" or "hard2")))
            throw Error("execution", "Execution requires a suite path and a diskDevice of flop1, flop2, hard1, or hard2.");
    }

    internal static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Error("schema", $"Duplicate property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static IReadOnlyList<BasicPreparedLine> MapBasicLines(string source)
    {
        List<BasicPreparedLine> lines = [];
        using StringReader reader = new(source);
        int physical = 0;
        while (reader.ReadLine() is { } text)
        {
            physical++;
            ReadOnlySpan<char> line = text.AsSpan().TrimStart();
            if (line.IsEmpty) continue;
            int length = 0;
            while (length < line.Length && char.IsAsciiDigit(line[length])) length++;
            lines.Add(new(physical, int.Parse(line[..length], System.Globalization.CultureInfo.InvariantCulture), []));
        }
        return lines;
    }

    private static void ValidateStructure(DiskSession session)
    {
        if (session.Info.IsDubious || session.Verify().Any(diagnostic => diagnostic.Severity == "error"))
            throw Error("corrupt_image", "Project image failed structural verification.", 4);
    }

    private static void RequireType(string type, params string[] accepted)
    {
        if (!accepted.Any(value => TypesEqual(type, value)))
            throw Error("file_type", $"Source kind requires file type {accepted[0]}.");
    }

    private static bool TypesEqual(string first, string second)
        => DiskSession.ParseFileType(first) == DiskSession.ParseFileType(second);

    private static DiskException Error(string code, string message, int exitCode = 2) => new("project." + code, message, exitCode);
    private sealed record PreparedFile(ProjectFile Item, byte[] Bytes, BuiltFile Info);
    private sealed record PreparedBoot(byte[] Sectors, BuiltBoot Report);
}
