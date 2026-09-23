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
    public string? Hex { get; init; }
    public string? ExpectedSourceSha256 { get; init; }
    public string? Type { get; init; }
    public ushort? AuxType { get; init; }
    public string? NewName { get; init; }
    public bool Recursive { get; init; }
    public bool Parents { get; init; }
    public bool? Locked { get; init; }
}

public sealed record DiskChangeInput(string Path, string Sha256, long Length);

public sealed record DiskChangePlan(
    int SchemaVersion, string InputPath, string InputSha256,
    string ChangeSetPath, string ChangeSetSha256, string PlanSha256,
    string CandidateSha256, string FileSystem, string Order, long? FreeBytesBefore,
    long? FreeBytesAfter, IReadOnlyList<DiskChangeInput> Inputs,
    IReadOnlyList<DiskEntryDifference> Changes);

public sealed record DiskApplyResult(ImageWriteResult Write, DiskChangePlan Plan, string OutputSha256);

/// <summary>Preflights and atomically applies a bounded declarative disk mutation set.</summary>
public static class DiskChangeSetRunner
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
                ImageTransactions.EnsureDistinctPaths(input.Path, outputPath);
        }
        ImageWriteResult write = ImageTransactions.Write(prepared.ImagePath, outputPath, inPlace,
            overwrite, temporary => ApplyChanges(temporary, prepared, cancellationToken),
            temporary =>
            {
                Validate(temporary, prepared.Order, prepared.FileSystem);
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
        long combinedPayloadBytes = 0;
        foreach (DiskChange change in plan.Changes)
        {
            ValidateChange(change);
            if (change.Source is null) continue;
            string source = Path.GetFullPath(change.Source, root);
            ImageTransactions.ValidatePath(source);
            ImageTransactions.EnsureDistinctPaths(source, image);
            ImageTransactions.EnsureDistinctPaths(source, planPath);
            byte[] bytes = ProgramFiles.ReadBytes(source, MaximumPayloadBytes, cancellationToken);
            string hash = ProgramFiles.Hash(bytes);
            combinedPayloadBytes = checked(combinedPayloadBytes + bytes.LongLength);
            if (combinedPayloadBytes > MaximumCombinedPayloadBytes)
                throw new DiskException("disk_plan.payload_limit",
                    $"Combined source payloads exceed {MaximumCombinedPayloadBytes} bytes.", 2);
            if (change.ExpectedSourceSha256 is not null && !change.ExpectedSourceSha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new DiskException("disk_plan.source_hash", $"Payload hash does not match: {source}", 6);
            inputs.Add(new(source, hash, bytes.LongLength));
        }
        return new(image, planPath, plan, planBytes, imageHash, ProgramFiles.Hash(planBytes),
            order, fileSystem, freeBytes, inputs);
    }

    private static void ValidateChange(DiskChange change)
    {
        if (change.Action is not ("add" or "replace" or "delete" or "rename" or "mkdir" or "attr"))
            throw new DiskException("disk_plan.action", "Change action must be add, replace, delete, rename, mkdir, or attr.", 2);
        if (string.IsNullOrWhiteSpace(change.Path)) throw new DiskException("disk_plan.path", "Every change requires path.", 2);
        if (change.Source is not null && change.Hex is not null)
            throw new DiskException("disk_plan.payload", "Specify source or hex, not both.", 2);
        if (change.ExpectedSourceSha256 is not null &&
            (change.Source is null || !IsSha256(change.ExpectedSourceSha256)))
            throw new DiskException("disk_plan.source_hash",
                "expectedSourceSha256 requires source and exactly 64 hexadecimal characters.", 2);
        if (change.Action is "add" or "replace" && (change.Source is null) == (change.Hex is null))
            throw new DiskException("disk_plan.payload", "Add and replace require exactly one of source or hex.", 2);
        if (change.Action is not ("add" or "replace") && (change.Source is not null || change.Hex is not null))
            throw new DiskException("disk_plan.payload", "Only add and replace accept source or hex payloads.", 2);
        if (change.Action == "add" && string.IsNullOrWhiteSpace(change.Type))
            throw new DiskException("disk_plan.type", "Add requires a file type.", 2);
        if (change.Action is not ("add" or "attr") && (change.Type is not null || change.AuxType is not null))
            throw new DiskException("disk_plan.attributes", "Only add and attr accept type or auxType.", 2);
        if (change.Action == "rename" && string.IsNullOrWhiteSpace(change.NewName))
            throw new DiskException("disk_plan.rename", "Rename requires newName.", 2);
        if (change.Action != "rename" && change.NewName is not null)
            throw new DiskException("disk_plan.rename", "Only rename accepts newName.", 2);
        if (change.Action != "delete" && change.Recursive)
            throw new DiskException("disk_plan.recursive", "Only delete accepts recursive.", 2);
        if (change.Action is not ("add" or "mkdir") && change.Parents)
            throw new DiskException("disk_plan.parents", "Only add and mkdir accept parents.", 2);
        if (change.Action != "attr" && change.Locked is not null)
            throw new DiskException("disk_plan.attributes", "Only attr accepts locked.", 2);
        if (change.Action == "attr" && change.Locked is null && change.Type is null && change.AuxType is null)
            throw new DiskException("disk_plan.attributes", "Attr requires locked, type, or auxType.", 2);
        if (change.Hex is not null) _ = MameAdapter.ParseHex(change.Hex);
    }

    private static void ApplyChanges(string path, PreparedChangeSet prepared,
        CancellationToken cancellationToken)
    {
        using DiskSession session = DiskSession.Open(path, prepared.Order, prepared.FileSystem, writable: true);
        int sourceIndex = 0;
        foreach (DiskChange change in prepared.Plan.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? payload = null;
            if (change.Source is not null)
            {
                DiskChangeInput input = prepared.Inputs[sourceIndex++];
                payload = ProgramFiles.ReadBytes(input.Path, MaximumPayloadBytes, cancellationToken);
                if (ProgramFiles.Hash(payload) != input.Sha256)
                    throw new DiskException("disk_plan.source_changed", $"Payload changed during apply: {input.Path}", 6);
            }
            else if (change.Hex is not null) payload = MameAdapter.ParseHex(change.Hex);
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
            if (HashFile(input.Path, cancellationToken, MaximumPayloadBytes) != input.Sha256)
                throw new DiskException("disk_plan.source_changed", $"Payload changed during apply: {input.Path}", 6);
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
        foreach (DiskChangeInput input in inputs) text.Append(input.Length).Append('\t').AppendLine(input.Sha256);
        return ProgramFiles.Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static bool InputFingerprintsEqual(IReadOnlyList<DiskChangeInput> first,
        IReadOnlyList<DiskChangeInput> second)
        => first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.Length == pair.Second.Length && pair.First.Sha256 == pair.Second.Sha256);

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));

    private static string HashFile(string path, CancellationToken cancellationToken, int maximum = 34 * 1024 * 1024)
        => ProgramFiles.Hash(ProgramFiles.ReadBytes(path, maximum, cancellationToken));

    private sealed record PreparedChangeSet(string ImagePath, string ChangeSetPath,
        DiskChangeSet Plan, byte[] PlanBytes, string ImageSha256, string ChangeSetSha256,
        string Order, string FileSystem, long? FreeBytesBefore,
        IReadOnlyList<DiskChangeInput> Inputs);
}
