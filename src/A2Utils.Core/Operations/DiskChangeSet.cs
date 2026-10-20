// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Operations;

public sealed record DiskChangeSet
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public string? ExpectedSha256 { get; init; }
    public DateTime Timestamp { get; init; } = new(2000, 1, 1);
    public IReadOnlyList<DiskChange> Changes { get; init; } = [];
}

public sealed record DiskChange
{
    public string Action { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Source { get; init; }
    public string? SourcePath { get; init; }
    public string? SourceImage { get; init; }
    public string? SourceOrder { get; init; }
    public string? SourceFileSystem { get; init; }
    public string? Format { get; init; }
    public string? Hex { get; init; }
    public string? ExpectedSourceSha256 { get; init; }
    public string? Type { get; init; }
    public ushort? AuxType { get; init; }
    public string? NewName { get; init; }
    public bool Recursive { get; init; }
    public bool Parents { get; init; }
    public bool? Locked { get; init; }
}

public sealed record DiskChangeInput(string Path, string Sha256, long Length, string Kind = "file");

public sealed record DiskChangePlan(
    int SchemaVersion, string InputPath, string InputSha256,
    string ChangeSetPath, string ChangeSetSha256, string PlanSha256,
    string CandidateSha256, string FileSystem, string Order, long? FreeBytesBefore,
    long? FreeBytesAfter, IReadOnlyList<DiskChangeInput> Inputs,
    IReadOnlyList<DiskEntryDifference> Changes);

public sealed record DiskApplyResult(ImageWriteResult Write, DiskChangePlan Plan, string OutputSha256);

/// <summary>Preflights and atomically applies a bounded declarative disk mutation set.</summary>
public static partial class DiskChangeSetRunner
{
    private const int MaximumPlanBytes = 1024 * 1024;
    private const int MaximumPayloadBytes = 32 * 1024 * 1024;
    private const long MaximumCombinedPayloadBytes = 128L * 1024 * 1024;
    private const int MaximumChanges = 1024;
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 32,
        WriteIndented = true
    };

    public static DiskChangePlan Plan(string imagePath, string changeSetPath,
        string? inputOrder = null, string? inputFileSystem = null,
        CancellationToken cancellationToken = default)
        => PlanCore(imagePath, changeSetPath, inputOrder, inputFileSystem, cancellationToken);

    internal static DiskChangePlan PlanCore(string imagePath, string changeSetPath,
        string? inputOrder = null, string? inputFileSystem = null,
        CancellationToken cancellationToken = default, Action? candidateValidated = null)
    {
        PreparedChangeSet prepared = Prepare(imagePath, changeSetPath, inputOrder,
            inputFileSystem, cancellationToken);
        string directory = HostFiles.ResolvePhysicalDirectory(Path.GetTempPath());
        string name = Path.GetFileName(prepared.ImagePath);
        string baseline = Path.Combine(directory,
            "." + name + ".plan-source-" + Guid.NewGuid().ToString("N"));
        string candidate = Path.Combine(directory,
            "." + name + ".plan-candidate-" + Guid.NewGuid().ToString("N"));
        try
        {
            ImageTransactions.Write(prepared.ImagePath, baseline, false, false,
                static _ => { }, temporary =>
                {
                    if (HashFile(temporary, cancellationToken) != prepared.ImageSha256)
                        throw new DiskException("disk_plan.changed",
                            "The input image changed while its planning snapshot was captured.", 6);
                }, cancellationToken);
            ImageTransactions.Write(baseline, candidate, false, false,
                temporary => ApplyChanges(temporary, prepared, cancellationToken),
                temporary =>
                {
                    Validate(temporary, prepared.Order, prepared.FileSystem);
                    RecheckInputs(prepared, cancellationToken);
                }, cancellationToken);
            candidateValidated?.Invoke();
            DiskDifference difference = DiskDiff.Compare(baseline, candidate,
                prepared.Order, prepared.FileSystem, prepared.Order, prepared.FileSystem, cancellationToken);
            RecheckInputs(prepared, cancellationToken);
            using DiskSession result = DiskSession.Open(candidate, prepared.Order, prepared.FileSystem);
            string planHash = ComputePlanHash(prepared.ImageSha256, prepared.ChangeSetSha256,
                prepared.Inputs, prepared.Order, prepared.FileSystem, difference.AfterSha256);
            return new(1, prepared.ImagePath, prepared.ImageSha256, prepared.ChangeSetPath,
                prepared.ChangeSetSha256, planHash, difference.AfterSha256,
                prepared.FileSystem, prepared.Order, prepared.FreeBytesBefore, result.Info.FreeBytes,
                prepared.Inputs, difference.Entries);
        }
        finally
        {
            try { File.Delete(candidate); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { File.Delete(baseline); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static DiskApplyResult Apply(string imagePath, string changeSetPath, string? outputPath,
        bool inPlace, bool overwrite, string? expectedInputSha256 = null,
        string? expectedPlanSha256 = null, string? inputOrder = null,
        string? inputFileSystem = null, CancellationToken cancellationToken = default)
        => ApplyCore(imagePath, changeSetPath, outputPath, inPlace, overwrite, expectedInputSha256,
            expectedPlanSha256, inputOrder, inputFileSystem, cancellationToken);

    internal static DiskApplyResult ApplyCore(string imagePath, string changeSetPath, string? outputPath,
        bool inPlace, bool overwrite, string? expectedInputSha256 = null,
        string? expectedPlanSha256 = null, string? inputOrder = null,
        string? inputFileSystem = null, CancellationToken cancellationToken = default,
        Action? candidateValidated = null)
    {
        if (expectedInputSha256 is not null && !IsSha256(expectedInputSha256))
            throw new DiskException("disk_plan.invalid", "--expect-sha256 must contain exactly 64 hexadecimal characters.", 2);
        if (expectedPlanSha256 is not null && !IsSha256(expectedPlanSha256))
            throw new DiskException("disk_plan.invalid", "--expect-plan-sha256 must contain exactly 64 hexadecimal characters.", 2);
        DiskChangePlan plan = Plan(imagePath, changeSetPath, inputOrder, inputFileSystem, cancellationToken);
        if (expectedInputSha256 is not null && !expectedInputSha256.Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase))
            throw new DiskException("disk_plan.input_hash", "The input image hash does not match --expect-sha256.", 6);
        if (expectedPlanSha256 is not null && !expectedPlanSha256.Equals(plan.PlanSha256, StringComparison.OrdinalIgnoreCase))
            throw new DiskException("disk_plan.plan_hash", "The declarative plan hash does not match --expect-plan-sha256.", 6);
        PreparedChangeSet prepared = Prepare(imagePath, changeSetPath, inputOrder,
            inputFileSystem, cancellationToken);
        if (prepared.ImageSha256 != plan.InputSha256 ||
            prepared.ChangeSetSha256 != plan.ChangeSetSha256 ||
            prepared.Order != plan.Order || prepared.FileSystem != plan.FileSystem ||
            !InputFingerprintsEqual(prepared.Inputs, plan.Inputs))
            throw new DiskException("disk_plan.changed", "The image, change set, or payload inputs changed after preflight.", 6);
        if (!inPlace && !string.IsNullOrWhiteSpace(outputPath))
        {
            ImageTransactions.EnsureDistinctPaths(prepared.ChangeSetPath, outputPath);
            foreach (DiskChangeInput input in prepared.Inputs)
            {
                if (input.Kind == "file") ImageTransactions.EnsureDistinctPaths(input.Path, outputPath);
                else if (IsWithinDirectory(outputPath, input.Path))
                    throw new DiskException("disk_plan.output_source_directory",
                        "The output image must be outside imported source directories.", 6);
            }
        }
        ImageWriteResult write = ImageTransactions.Write(prepared.ImagePath, outputPath, inPlace,
            overwrite, temporary => ApplyChanges(temporary, prepared, cancellationToken),
            temporary =>
            {
                Validate(temporary, prepared.Order, prepared.FileSystem);
                candidateValidated?.Invoke();
                RecheckInputs(prepared, cancellationToken);
                string candidateHash = HashFile(temporary, cancellationToken);
                if (candidateHash != plan.CandidateSha256)
                    throw new DiskException("disk_plan.nonreproducible", "Applied changes differ from the preflight candidate.", 6);
            }, cancellationToken);
        return new(write, plan, HashFile(write.OutputPath, cancellationToken));
    }

    private static PreparedChangeSet Prepare(string imagePath, string changeSetPath,
        string? inputOrder, string? inputFileSystem, CancellationToken cancellationToken)
    {
        string image = Path.GetFullPath(imagePath);
        string planPath = Path.GetFullPath(changeSetPath);
        ImageTransactions.ValidatePath(image);
        ImageTransactions.ValidatePath(planPath);
        ImageTransactions.EnsureDistinctPaths(image, planPath);
        byte[] planBytes = ProgramFiles.ReadBytes(planPath, MaximumPlanBytes, cancellationToken);
        DiskChangeSet plan;
        try
        {
            plan = JsonSerializer.Deserialize<DiskChangeSet>(planBytes, JsonOptions)
                ?? throw new JsonException("The change set must be an object.");
        }
        catch (JsonException ex)
        {
            throw new DiskException("disk_plan.invalid", ex.Message, 2, ex);
        }
        if (plan.SchemaVersion != 1 || plan.Changes is null || plan.Changes.Count is < 1 or > MaximumChanges || plan.Changes.Any(change => change is null))
            throw new DiskException("disk_plan.invalid", $"A version 1 change set requires 1..{MaximumChanges} changes.", 2);
        if (plan.ExpectedSha256 is not null && !IsSha256(plan.ExpectedSha256))
            throw new DiskException("disk_plan.invalid", "expectedSha256 must contain exactly 64 hexadecimal characters.", 2);
        if (plan.Timestamp.Year is < 1980 or > 2039 ||
            plan.Timestamp.Ticks % TimeSpan.TicksPerMinute != 0 ||
            plan.Timestamp.Kind == DateTimeKind.Local)
            throw new DiskException("disk_plan.timestamp",
                "Use a UTC or offset-free timestamp between 1980 and 2039 with whole-minute precision.", 2);
        byte[] imageBytes = ProgramFiles.ReadBytes(image, 34 * 1024 * 1024,
            cancellationToken);
        string imageHash = ProgramFiles.Hash(imageBytes);
        if (plan.ExpectedSha256 is not null && !plan.ExpectedSha256.Equals(imageHash, StringComparison.OrdinalIgnoreCase))
            throw new DiskException("disk_plan.input_hash", "The input image hash does not match expectedSha256.", 6);
        string snapshot = Path.Combine(HostFiles.ResolvePhysicalDirectory(Path.GetTempPath()),
            $"a2-plan-inspect-{Guid.NewGuid():N}-{Path.GetFileName(image)}");
        string order;
        string fileSystem;
        long? freeBytes;
        try
        {
            using (FileStream output = new(snapshot, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.SequentialScan))
            {
                output.Write(imageBytes);
                output.Flush(flushToDisk: true);
            }
            using DiskSession session = DiskSession.Open(snapshot, inputOrder, inputFileSystem);
            order = session.Info.Order;
            fileSystem = session.Info.FileSystem;
            freeBytes = session.Info.FreeBytes;
        }
        finally
        {
            try { File.Delete(snapshot); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        string root = Path.GetDirectoryName(planPath)!;
        List<DiskChangeInput> inputs = [];
        IReadOnlyList<PreparedChange> changes = CaptureSources(plan.Changes, root,
            image, planPath, inputs, cancellationToken);
        return new(image, planPath, plan, planBytes, imageHash, ProgramFiles.Hash(planBytes),
            order, fileSystem, freeBytes, inputs, changes);
    }

    private static void ValidateChange(DiskChange change)
    {
        if (change.Action is not ("add" or "replace" or "delete" or "rename" or "mkdir" or "attr"
            or "copy" or "move" or "import" or "restore"))
            throw new DiskException("disk_plan.action",
                "Change action must be add, replace, delete, rename, mkdir, attr, copy, move, import, or restore.", 2);
        if (string.IsNullOrWhiteSpace(change.Path)) throw new DiskException("disk_plan.path", "Every change requires path.", 2);
        if (change.Source is not null && change.Hex is not null)
            throw new DiskException("disk_plan.payload", "Specify source or hex, not both.", 2);
        if (change.ExpectedSourceSha256 is not null &&
            ((change.Source is null && change.SourceImage is null) || change.Action == "import" ||
                !IsSha256(change.ExpectedSourceSha256)))
            throw new DiskException("disk_plan.source_hash",
                "expectedSourceSha256 requires a source file or sourceImage and exactly 64 hexadecimal characters.", 2);
        if (change.Action is "add" or "replace" && (change.Source is null) == (change.Hex is null))
            throw new DiskException("disk_plan.payload", "Add and replace require exactly one of source or hex.", 2);
        if (change.Action is not ("add" or "replace" or "import" or "restore") && change.Source is not null ||
            change.Action is not ("add" or "replace") && change.Hex is not null)
            throw new DiskException("disk_plan.payload",
                "Only add, replace, import, and restore accept source; only add and replace accept hex.", 2);
        if (change.Action is "import" or "restore" && string.IsNullOrWhiteSpace(change.Source))
            throw new DiskException("disk_plan.source", "Import and restore require source.", 2);
        if (change.Action == "add" && string.IsNullOrWhiteSpace(change.Type) ||
            change.Action == "import" && change.Format != "text" && string.IsNullOrWhiteSpace(change.Type))
            throw new DiskException("disk_plan.type", "Add and binary import require a file type.", 2);
        if (change.Action is not ("add" or "attr" or "import") && (change.Type is not null || change.AuxType is not null))
            throw new DiskException("disk_plan.attributes", "Only add, attr, and import accept type or auxType.", 2);
        if (change.Action == "rename" && string.IsNullOrWhiteSpace(change.NewName))
            throw new DiskException("disk_plan.rename", "Rename requires newName.", 2);
        if (change.Action != "rename" && change.NewName is not null)
            throw new DiskException("disk_plan.rename", "Only rename accepts newName.", 2);
        if (change.Action is not ("delete" or "copy" or "import") && change.Recursive)
            throw new DiskException("disk_plan.recursive", "Only delete, copy, and import accept recursive.", 2);
        if (change.Action is not ("add" or "mkdir") && change.Parents)
            throw new DiskException("disk_plan.parents", "Only add and mkdir accept parents.", 2);
        if (change.Action != "attr" && change.Locked is not null)
            throw new DiskException("disk_plan.attributes", "Only attr accepts locked.", 2);
        if (change.Action == "attr" && change.Locked is null && change.Type is null && change.AuxType is null)
            throw new DiskException("disk_plan.attributes", "Attr requires locked, type, or auxType.", 2);
        if (change.Action is "copy" or "move")
        {
            if (string.IsNullOrWhiteSpace(change.SourcePath))
                throw new DiskException("disk_plan.source_path", "Copy and move require sourcePath.", 2);
        }
        else if (change.SourcePath is not null)
            throw new DiskException("disk_plan.source_path", "Only copy and move accept sourcePath.", 2);
        if (change.SourceImage is not null && (change.Action != "copy" || string.IsNullOrWhiteSpace(change.SourceImage)))
            throw new DiskException("disk_plan.source_image", "Only copy accepts a nonempty sourceImage.", 2);
        if (change.SourceImage is null && (change.SourceOrder is not null || change.SourceFileSystem is not null))
            throw new DiskException("disk_plan.source_image", "Source interpretation overrides require sourceImage.", 2);
        if (change.Format is not null && (change.Action != "import" || change.Format is not ("binary" or "text")))
            throw new DiskException("disk_plan.format", "Only import accepts format, which must be binary or text.", 2);
        if (change.Action == "import" && change.Format == "text" &&
            (change.Type is not null && !new[] { "T", "TXT", "0x04" }.Contains(change.Type, StringComparer.OrdinalIgnoreCase) ||
                change.AuxType is not null))
            throw new DiskException("disk_plan.attributes", "Text import requires TXT and no auxiliary type.", 2);
        if (change.Action == "restore" && change.Path != "/")
            throw new DiskException("disk_plan.restore_path", "Restore requires path '/' and retains manifest paths.", 2);
        if (change.Hex is not null) _ = MameAdapter.ParseHex(change.Hex);
    }

    private static void ApplyChanges(string path, PreparedChangeSet prepared,
        CancellationToken cancellationToken)
    {
        using DiskSession session = DiskSession.Open(path, prepared.Order, prepared.FileSystem, writable: true);
        foreach (PreparedChange operation in prepared.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiskChange change = operation.Change;
            byte[]? payload = change.Action is "add" or "replace"
                ? operation.Source?.Files[0].Bytes ?? MameAdapter.ParseHex(change.Hex!) : null;
            switch (change.Action)
            {
                case "add":
                    if (change.Parents)
                    {
                        string? parent = change.Path.Contains('/') ? change.Path[..change.Path.LastIndexOf('/')] : null;
                        if (!string.IsNullOrEmpty(parent)) EnsureDirectories(session, parent, prepared.Plan.Timestamp);
                    }
                    session.Add(change.Path, payload!, change.Type!, change.AuxType ?? 0);
                    session.SetTimestamps(change.Path, prepared.Plan.Timestamp, prepared.Plan.Timestamp);
                    break;
                case "replace": session.Replace(change.Path, payload!); break;
                case "delete": session.Delete(change.Path, change.Recursive); break;
                case "rename": session.Rename(change.Path, change.NewName!); break;
                case "mkdir":
                    if (change.Parents) EnsureDirectories(session, change.Path, prepared.Plan.Timestamp);
                    else
                    {
                        session.Mkdir(change.Path);
                        session.SetTimestamps(change.Path, prepared.Plan.Timestamp, prepared.Plan.Timestamp);
                    }
                    break;
                case "attr": session.SetAttributes(change.Path, change.Locked, change.Type, change.AuxType); break;
                case "move": session.Move(change.SourcePath!, change.Path); break;
                case "copy":
                    if (operation.Source is null)
                        session.Copy(change.SourcePath!, change.Path, change.Recursive, cancellationToken);
                    else
                        WithMaterializedSource(operation.Source, root =>
                        {
                            using DiskSession source = DiskSession.Open(
                                Path.Combine(root, operation.Source.PrimaryName),
                                change.SourceOrder, change.SourceFileSystem);
                            session.CopyFrom(source, change.SourcePath!, change.Path,
                                change.Recursive, cancellationToken);
                        }, cancellationToken);
                    break;
                case "import":
                    HashSet<string> existing = session.List(recursive: true)
                        .Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
                    WithMaterializedSource(operation.Source!, root =>
                        DirectoryImport.Import(session, root, change.Path,
                            change.Type ?? "TXT", change.AuxType ?? 0, change.Recursive,
                            change.Format == "text" ? bytes => AppleTextCodec.Encode(bytes, prepared.FileSystem) : null,
                            cancellationToken), cancellationToken);
                    foreach (DiskEntry entry in session.List(recursive: true).Where(entry => !existing.Contains(entry.Path)))
                        session.SetTimestamps(entry.Path, prepared.Plan.Timestamp, prepared.Plan.Timestamp);
                    break;
                case "restore":
                    WithMaterializedSource(operation.Source!, root =>
                        FileTransfer.Restore(session, Path.Combine(root, operation.Source!.PrimaryName),
                            cancellationToken), cancellationToken);
                    break;
            }
        }
    }

    private static void EnsureDirectories(DiskSession session, string path, DateTime timestamp)
    {
        HashSet<string> existing = session.List(recursive: true)
            .Where(entry => entry.IsDirectory)
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        session.EnsureDirectory(path);
        string current = "";
        foreach (string part in parts)
        {
            current = current.Length == 0 ? part : current + "/" + part;
            if (!existing.Contains(current)) session.SetTimestamps(current, timestamp, timestamp);
        }
    }

    private static void RecheckInputs(PreparedChangeSet prepared, CancellationToken cancellationToken)
    {
        if (HashFile(prepared.ImagePath, cancellationToken) != prepared.ImageSha256
            || ProgramFiles.Hash(ProgramFiles.ReadBytes(prepared.ChangeSetPath, MaximumPlanBytes, cancellationToken)) != prepared.ChangeSetSha256)
            throw new DiskException("disk_plan.changed", "The image or change-set file changed during apply.", 6);
        foreach (DiskChangeInput input in prepared.Inputs)
        {
            ImageTransactions.ValidatePath(input.Path);
            string currentHash = input.Kind == "directory"
                ? DirectoryFingerprint(ReadSourceDirectory(input.Path, cancellationToken))
                : HashFile(input.Path, cancellationToken);
            if (currentHash != input.Sha256)
                throw new DiskException("disk_plan.source_changed", $"Source changed during apply: {input.Path}", 6);
        }
    }

    private static void Validate(string path, string order, string fileSystem)
    {
        using DiskSession session = DiskSession.Open(path, order, fileSystem);
        if (session.Info.IsDubious || session.Verify().Any(diagnostic => diagnostic.Severity == "error"))
            throw new DiskException("disk_plan.validation", "The candidate image failed structural validation.", 4);
    }

    private static string ComputePlanHash(string imageHash, string changeSetHash,
        IReadOnlyList<DiskChangeInput> inputs, string order, string fileSystem,
        string candidateHash)
    {
        StringBuilder text = new StringBuilder("A2DISKPLAN1\n")
            .Append("input\t").AppendLine(imageHash)
            .Append("changes\t").AppendLine(changeSetHash)
            .Append("order\t").AppendLine(order)
            .Append("filesystem\t").AppendLine(fileSystem)
            .Append("candidate\t").AppendLine(candidateHash);
        foreach (DiskChangeInput input in inputs)
            text.Append(input.Kind).Append('\t').Append(input.Length).Append('\t').AppendLine(input.Sha256);
        return ProgramFiles.Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static bool InputFingerprintsEqual(IReadOnlyList<DiskChangeInput> first,
        IReadOnlyList<DiskChangeInput> second)
        => first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.Path == pair.Second.Path && pair.First.Kind == pair.Second.Kind &&
            pair.First.Length == pair.Second.Length && pair.First.Sha256 == pair.Second.Sha256);

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));

    private static string HashFile(string path, CancellationToken cancellationToken, int maximum = 34 * 1024 * 1024)
        => ProgramFiles.Hash(ProgramFiles.ReadBytes(path, maximum, cancellationToken));

    private sealed record PreparedChangeSet(string ImagePath, string ChangeSetPath,
        DiskChangeSet Plan, byte[] PlanBytes, string ImageSha256, string ChangeSetSha256,
        string Order, string FileSystem, long? FreeBytesBefore,
        IReadOnlyList<DiskChangeInput> Inputs, IReadOnlyList<PreparedChange> Changes);
}
