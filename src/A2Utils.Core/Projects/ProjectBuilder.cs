using System.Reflection;
using System.Text.Json;
using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

/// <summary>Compiles a manifest into one staged disk image; source metadata follows each payload.</summary>
public static class ProjectBuilder
{
    public static ProjectBuildResult Build(string manifestPath, string? outputPath = null,
        bool overwrite = false, bool checkOnly = false, CancellationToken cancellationToken = default)
    {
        string manifestFile = Path.GetFullPath(manifestPath);
        byte[] manifestBytes = ProgramFiles.ReadBytes(manifestFile, 1024 * 1024, cancellationToken);
        ProjectManifest manifest;
        try
        {
            using JsonDocument json = JsonDocument.Parse(manifestBytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schemaVersion) || schemaVersion != 1)
                throw Error("schema", "Project schemaVersion must be 1.");
            RejectDuplicateProperties(json.RootElement);
            manifest = JsonSerializer.Deserialize<ProjectManifest>(manifestBytes, ProjectJson.Options)
                ?? throw Error("schema", "Project must be an object.");
        }
        catch (JsonException exception) { throw Error("schema", exception.Message); }
        ValidateManifest(manifest);
        string root = Path.GetDirectoryName(manifestFile)!;
        string output = Path.GetFullPath(outputPath ?? manifest.Output, root);
        ImageTransactions.EnsureDistinctPaths(manifestFile, output);
        TargetProfile profile = TargetProfiles.Get(manifest.Target);
        string cpu = manifest.Cpu ?? profile.Cpu;
        CpuKind cpuKind = TargetProfiles.ParseCpu(cpu);
        if (cpu == "w65c02" || cpu == "65c02" && profile.Cpu == "6502")
            throw Error("cpu_target", $"CPU '{cpu}' is incompatible with target '{profile.Name}'.");
        string fs = manifest.Disk.FileSystem;
        string order = manifest.Disk.Order ?? (fs == "dos33" ? "dos" : "prodos");
        string? template = manifest.Disk.Template is null ? null : Path.GetFullPath(manifest.Disk.Template, root);
        Dictionary<string, string> inputHashes = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [manifestFile] = ProgramFiles.Hash(manifestBytes)
        };
        if (template is not null)
        {
            byte[] image = ReadInput(template, 34 * 1024 * 1024);
            if (manifest.Disk.TemplateSha256 is { } expected && !ProgramFiles.Hash(image).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw Error("template_hash", "Template SHA-256 does not match the pinned value.");
            using DiskSession source = DiskSession.Open(template, manifest.Disk.Order, fs);
            order = source.Info.Order;
            ValidateStructure(source);
        }

        List<PreparedFile> files = [];
        List<ProgramDiagnostic> diagnostics = [];
        long totalPayloadBytes = 0;
        foreach (ProjectFile item in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = Path.GetFullPath(item.Source, root);
            byte[] bytes = ReadInput(source, 4 * 1024 * 1024);
            int? origin = item.Origin;
            ushort aux = item.AuxType ?? item.Origin ?? 0;
            string type = item.Type ?? (item.Kind is "basic" or "basic-labels" ? "BAS" : item.Kind == "text" ? "TXT" : "BIN");
            IReadOnlyDictionary<string, int> symbols = new Dictionary<string, int>();
            object? sourceMap = null;
            switch (item.Kind)
            {
                case "asm":
                    AssemblyResult assembled = Assembler.AssembleFile(source, item.Origin, cpuKind, cancellationToken);
                    bytes = assembled.Bytes;
                    origin = assembled.Origin;
                    foreach (var dependency in assembled.DependencyHashes)
                    {
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
                    sourceMap ??= MapBasicLines(listing);
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
                    Cc65Result compiled = Cc65Compiler.Compile(source, options, root, cancellationToken);
                    AppleSinglePayload cProgram = AppleSingleProgram.Decode(compiled.AppleSingle);
                    bytes = cProgram.Bytes;
                    type = $"0x{cProgram.FileType:x2}";
                    if (item.Type is not null && !TypesEqual(item.Type, type))
                        throw Error("metadata_conflict", "Explicit type disagrees with cc65 output metadata.");
                    if (item.AuxType is { } compilerAux && compilerAux != cProgram.AuxType)
                        throw Error("metadata_conflict", "Explicit auxiliary type disagrees with cc65 output metadata.");
                    aux = cProgram.AuxType;
                    origin = cProgram.FileType == 0xff ? 0x2000 : aux;
                    foreach (var input in compiled.Inputs)
                    {
                        string dependencyPath = Path.GetFullPath(input.Path, root);
                        if (inputHashes.TryGetValue(dependencyPath, out string? previousHash) && previousHash != input.Sha256)
                            throw Error("source_changed", $"Compiler input changed: {input.Path}", 6);
                        inputHashes[dependencyPath] = input.Sha256;
                    }
                    sourceMap = new { compilerVersion = compiled.Version, map = compiled.Map, labels = compiled.Labels };
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
            totalPayloadBytes += bytes.Length;
            if (totalPayloadBytes > 34 * 1024 * 1024) throw Error("payload_limit", "Combined project payloads exceed 34 MiB.");
            files.Add(new(item, bytes, new(item.Path, item.Kind, type, aux, origin, item.EntryPoint ?? origin,
                bytes.Length, ProgramFiles.Hash(bytes), item.Resident, symbols, sourceMap)));
        }

        if (files.Select(file => file.Info.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw Error("duplicate_path", "Project contains duplicate image paths.");
        if (manifest.Startup is { } startup)
        {
            if (template is null) throw Error("startup_template", "Startup generation requires an existing bootable OS template.");
            PreparedFile program = files.SingleOrDefault(file => file.Info.Path.Equals(startup.Program, StringComparison.OrdinalIgnoreCase))
                ?? throw Error("startup_program", "Startup program must identify one manifest file.");
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
        if (!checkOnly)
        {
            ImageTransactions.ValidatePath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (template is null)
                ImageTransactions.Create(output, overwrite, temporary =>
                {
                    DiskSession.Create(temporary, fs, container: manifest.Disk.Container, order: order,
                        volumeName: manifest.Disk.VolumeName, volumeNumber: manifest.Disk.VolumeNumber, blocks: manifest.Disk.Blocks);
                    Populate(temporary);
                }, Validate, cancellationToken);
            else
                ImageTransactions.Write(template, output, false, overwrite, Populate, Validate, cancellationToken);
        }
        return new(output, imageHash, profile.Name, cpu, fs, template is null ? "data-volume" : "template-preserved-unverified",
            typeof(ProjectBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            manifest.Timestamp, inputHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new BuildInput(pair.Key, pair.Value)).ToArray(),
            files.Select(file => file.Info).ToArray(), memory, diagnostics);

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
            foreach (var input in inputHashes)
                if (ProgramFiles.Hash(ProgramFiles.ReadBytes(input.Key, 34 * 1024 * 1024, cancellationToken)) != input.Value)
                    throw Error("source_changed", $"Input changed while building: {input.Key}", 6);
        }

        void Populate(string temporary)
        {
            CheckInputs();
            using DiskSession session = DiskSession.Open(temporary, order, fs, writable: true);
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
            }
            foreach (string directory in createdDirectories)
                session.SetTimestamps(directory, manifest.Timestamp, manifest.Timestamp);
            if (template is null) session.SetTimestamps("", manifest.Timestamp, manifest.Timestamp);
            session.Flush();
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
            CheckInputs();
            imageHash = ProgramFiles.Hash(ProgramFiles.ReadBytes(temporary, 34 * 1024 * 1024, cancellationToken));
        }
    }

    private static List<MemoryRegion> CheckMemory(ProjectManifest manifest, List<PreparedFile> files)
    {
        List<MemoryRegion> occupied = files.Where(file => file.Info.Resident && file.Info.Origin.HasValue)
            .Select(file => new MemoryRegion(file.Info.Path, file.Info.Origin!.Value,
                file.Bytes.Length + (TypesEqual(file.Info.Type, "BAS") ? manifest.BasicWorkspaceBytes : 0))).ToList();
        IReadOnlyList<MemoryRegion> builtIn = TargetProfiles.Reserved(manifest.Disk.FileSystem);
        MemoryRegion displayPage = builtIn.Single(region => region.Name == "display page 1");
        List<MemoryRegion> reserved = builtIn.Concat(manifest.Reserve).ToList();
        foreach (MemoryRegion region in occupied.Concat(reserved))
            if (region.Start < 0 || region.Length < 0 || region.Start > 65536 - region.Length)
                throw Error("memory_range", $"Memory region '{region.Name}' exceeds the address space.");
        if (!manifest.CheckMemory) return occupied;
        for (int index = 0; index < occupied.Count; index++)
        {
            MemoryRegion region = occupied[index];
            foreach (MemoryRegion other in reserved.Concat(occupied.Take(index)))
            {
                // Lo-res assets intentionally occupy the display page; all other overlaps need an explicit opt-out.
                bool loresPage = ReferenceEquals(other, displayPage) && files.Any(file => file.Info.Path == region.Name && file.Item.Kind == "lores");
                if (!loresPage && region.Length > 0 && other.Length > 0 && region.Start < other.Start + other.Length && other.Start < region.Start + region.Length)
                    throw new DiskException("project.memory_overlap", $"'{region.Name}' overlaps '{other.Name}'.", 2)
                    {
                        Diagnostics = [new("project.memory_overlap", "error", "Resident memory ranges overlap.", Symbol: region.Name,
                        Expected: $"Outside {other.Name} [${other.Start:X4}, ${other.Start + other.Length:X5})",
                        Actual: $"[${region.Start:X4}, ${region.Start + region.Length:X5})")]
                    };
            }
        }
        return occupied;
    }

    private static void ValidateManifest(ProjectManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Target) || string.IsNullOrWhiteSpace(manifest.Output) ||
            manifest.Disk is null || manifest.Files is null || manifest.Reserve is null ||
            manifest.Files.Count is < 1 or > 1024 || manifest.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Source) ||
                string.IsNullOrWhiteSpace(file.Path) || file.Path.StartsWith('/') || file.Path.Contains('\\') || file.Path.Split('/').Any(part => part is "" or "." or "..")))
            throw Error("schema", "Project requires 1..1024 source files with relative image paths.");
        if (manifest.Disk.FileSystem is not ("dos33" or "prodos")) throw Error("filesystem", "Filesystem must be dos33 or prodos.");
        if (manifest.Disk.Blocks is < 280 or > 65535 || manifest.Disk.FileSystem == "dos33" && manifest.Disk.Blocks != 280)
            throw Error("geometry", "DOS requires 280 blocks; ProDOS permits 280..65535 blocks.");
        if (manifest.Disk.Order is not (null or "dos" or "prodos") || manifest.Disk.Container is not ("raw" or "2mg"))
            throw Error("disk_format", "Invalid disk order or container.");
        if (manifest.Timestamp.Year is < 1980 or > 2039 || manifest.Timestamp.Ticks % TimeSpan.TicksPerMinute != 0 || manifest.Timestamp.Kind == DateTimeKind.Local)
            throw Error("timestamp", "Use a UTC or offset-free timestamp between 1980 and 2039 with whole-minute precision.");
        if (manifest.BasicWorkspaceBytes is < 0 or > 65536) throw Error("basic_workspace", "BASIC workspace must be 0..65536 bytes.");
        if (manifest.Reserve.Count > 1024 || manifest.Reserve.Any(region => region is null || string.IsNullOrWhiteSpace(region.Name)))
            throw Error("schema", "Reserve permits at most 1024 named memory regions.");
        if (manifest.Startup is { } startup && (string.IsNullOrWhiteSpace(startup.Program) || string.IsNullOrWhiteSpace(startup.Path) ||
            startup.Path.StartsWith('/') || startup.Path.Contains('\\') || startup.Path.Split('/').Any(part => part is "" or "." or "..")))
            throw Error("startup", "Startup requires a program path and a relative launcher path.");
    }

    private static void RejectDuplicateProperties(JsonElement value)
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
}
