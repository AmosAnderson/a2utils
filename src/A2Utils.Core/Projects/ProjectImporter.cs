using System.Text;
using System.Text.Json;
using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

public sealed record ImportedProjectFile(string ImagePath, string SourcePath, string Kind,
    bool Editable, string? Reason);

public sealed record ProjectImportResult(int SchemaVersion, string Directory, string ProjectPath,
    string TemplatePath, string InputSha256, IReadOnlyList<ImportedProjectFile> Files,
    IReadOnlyList<string> Diagnostics);

/// <summary>Adopts an existing supported disk as a hash-pinned, editable project without altering it.</summary>
public static class ProjectImporter
{
    private const int MaximumImageBytes = 34 * 1024 * 1024;

    public static ProjectImportResult Import(string imagePath, string destination,
        bool disassembleBinaries = false, string target = "apple2e",
        string? inputOrder = null, string? inputFileSystem = null,
        CancellationToken cancellationToken = default)
        => ImportCore(imagePath, destination, disassembleBinaries, target,
            inputOrder, inputFileSystem, null, cancellationToken);

    internal static ProjectImportResult ImportForTest(string imagePath, string destination,
        Action<string> afterCapture, bool disassembleBinaries = false, string target = "apple2e",
        string? inputOrder = null, string? inputFileSystem = null,
        CancellationToken cancellationToken = default)
        => ImportCore(imagePath, destination, disassembleBinaries, target,
            inputOrder, inputFileSystem, afterCapture, cancellationToken);

    private static ProjectImportResult ImportCore(string imagePath, string destination,
        bool disassembleBinaries, string target, string? inputOrder,
        string? inputFileSystem, Action<string>? afterCapture,
        CancellationToken cancellationToken)
    {
        _ = TargetProfiles.Get(target);
        string image = Path.GetFullPath(imagePath);
        string output = Path.GetFullPath(destination);
        ImageTransactions.ValidatePath(image);
        ImageTransactions.ValidatePath(output);
        if (Directory.Exists(output) || File.Exists(output))
            throw new DiskException("project.import_destination", "Project import requires a new destination directory.", 2);
        if (image.StartsWith(output + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new DiskException("project.import_alias", "The source image cannot be inside the new project directory.", 2);
        byte[] input = ProgramFiles.ReadBytes(image, MaximumImageBytes, cancellationToken);
        string inputHash = ProgramFiles.Hash(input);
        string snapshot = Path.Combine(HostFiles.ResolvePhysicalDirectory(Path.GetTempPath()),
            $"a2-project-import-{Guid.NewGuid():N}-{Path.GetFileName(image)}");
        try
        {
            WriteSnapshot(snapshot, input, cancellationToken);
            afterCapture?.Invoke(snapshot);
            using DiskSession disk = DiskSession.Open(snapshot, inputOrder, inputFileSystem);
            if (disk.Info.IsDubious || disk.Verify().Any(item => item.Severity == "error"))
                throw new DiskException("project.import_image", "The source image must pass structural verification.", 4);
            string extension = disk.Info.Container == "2mg" ? ".2mg" : disk.Info.FileSystem == "dos33" ? ".do" : ".po";
            string parent = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(parent);
            string staging = Path.Combine(parent, ".a2-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string templateName = "template" + extension;
                File.WriteAllBytes(Path.Combine(staging, templateName), input);
                Directory.CreateDirectory(Path.Combine(staging, "sources"));
                Directory.CreateDirectory(Path.Combine(staging, "reference"));
                List<ProjectFile> projectFiles = [];
                List<ImportedProjectFile> imported = [];
                List<string> diagnostics = [];
                int index = 0;
                foreach (DiskEntry entry in disk.List(recursive: true).Where(entry => !entry.IsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index++;
                    byte[] payload = disk.ReadFile(entry.Path);
                    (string extensionName, string kind, byte[] contents) = Convert(entry, payload,
                        disassembleBinaries, TargetProfiles.ParseCpu(TargetProfiles.Get(target).Cpu),
                        cancellationToken, diagnostics);
                    string filename = $"{index:D4}_{SafeName(entry.Path)}{extensionName}";
                    bool editable = !entry.IsLocked;
                    string folder = editable ? "sources" : "reference";
                    string relative = folder + "/" + filename;
                    File.WriteAllBytes(Path.Combine(staging, folder, filename), contents);
                    imported.Add(new(entry.Path, relative, kind, editable,
                        editable ? null : "The template entry is locked; its source is exported for reference and omitted from replacements."));
                    if (!editable) continue;
                    projectFiles.Add(new()
                    {
                        Source = relative,
                        Path = entry.Path,
                        Kind = kind,
                        Type = $"0x{entry.FileType:x2}",
                        AuxType = entry.AuxType,
                        Replace = true,
                        Resident = false
                    });
                }
                ProjectManifest manifest = new()
                {
                    Target = target,
                    Output = "build/imported" + extension,
                    Disk = new()
                    {
                        FileSystem = disk.Info.FileSystem,
                        Template = templateName,
                        TemplateSha256 = inputHash,
                        Container = disk.Info.Container,
                        Order = disk.Info.Order,
                        VolumeName = disk.Info.VolumeName,
                        VolumeNumber = disk.Info.VolumeNumber ?? 254,
                        Blocks = checked((int)(disk.Info.SizeBytes / 512))
                    },
                    Files = projectFiles
                };
                string projectName = "project.a2.json";
                File.WriteAllText(Path.Combine(staging, projectName),
                    JsonSerializer.Serialize(manifest, ProjectJson.Options) + "\n", new UTF8Encoding(false));
                ProjectImportResult stagedResult = new(1, output, Path.Combine(output, projectName),
                    Path.Combine(output, templateName), inputHash, imported, diagnostics);
                File.WriteAllText(Path.Combine(staging, "import-report.json"),
                    JsonSerializer.Serialize(stagedResult, ProjectJson.Options) + "\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(staging, "README.md"), Readme(projectFiles.Count,
                    imported.Count(file => !file.Editable), extension), new UTF8Encoding(false));
                if (ProgramFiles.Hash(ProgramFiles.ReadBytes(image, MaximumImageBytes, cancellationToken)) != inputHash)
                    throw new DiskException("project.import_changed", "The source image changed during import.", 6);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(staging, output);
                return stagedResult;
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        finally
        {
            try { File.Delete(snapshot); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void WriteSnapshot(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.SequentialScan);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static (string Extension, string Kind, byte[] Contents) Convert(DiskEntry entry,
        byte[] payload, bool disassembleBinaries, CpuKind cpu, CancellationToken cancellationToken,
        List<string> diagnostics)
    {
        if (entry.Type is "A" or "BAS")
        {
            try
            {
                string source = ApplesoftBasic.Decompile(payload,
                    entry.AuxType == 0 ? ApplesoftBasic.DefaultOrigin : entry.AuxType, cancellationToken);
                return (".bas", "basic", Encoding.UTF8.GetBytes(source));
            }
            catch (DiskException exception)
            {
                diagnostics.Add($"{entry.Path}: BASIC decompilation was unavailable ({exception.Code}); preserved as binary.");
            }
        }
        if (entry.Type is "T" or "TXT")
        {
            try { return (".txt", "text", AppleTextCodec.Decode(payload)); }
            catch (DiskException exception)
            {
                diagnostics.Add($"{entry.Path}: text decoding was unavailable ({exception.Code}); preserved as binary.");
            }
        }
        if (disassembleBinaries && entry.Type is ("B" or "BIN") && entry.AuxType != 0)
        {
            string source = Disassembler.Disassemble(payload, entry.AuxType, cpu, cancellationToken);
            return (".asm", "asm", Encoding.UTF8.GetBytes(source));
        }
        return (".bin", "binary", payload);
    }

    private static string SafeName(string path)
    {
        string value = new string(path.Take(80).Select(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-' ? character : '_').ToArray()).Trim('.', '_');
        return value.Length == 0 ? "FILE" : value;
    }

    private static string Readme(int editable, int locked, string extension) => "# Imported Apple II project\n\n"
        + "This project preserves the original image as a hash-pinned template. Editable entries are rebuilt and replaced transactionally; locked entries remain untouched in the template and their exported files are under `reference/`.\n\n"
        + $"Editable files: {editable}. Locked reference files: {locked}.\n\n"
        + "```sh\n"
        + "a2 project resolve project.a2.json --json\n"
        + "a2 build project.a2.json --preflight --json\n"
        + "a2 build project.a2.json --json\n"
        + $"a2 disk diff template{extension} build/imported{extension} --json\n"
        + "```\n";
}
