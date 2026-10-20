// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Operations;

public sealed record DiskMigrationOptions
{
    public string TextMode { get; init; } = "preserve";
    public string? SourceOrder { get; init; }
    public string? SourceFileSystem { get; init; }
    public string? DestinationOrder { get; init; }
    public string? DestinationFileSystem { get; init; }
}

public sealed record DiskMigrationPlan(int SchemaVersion, string SourceImage, string SourceSha256,
    string DestinationImage, string DestinationSha256, string SourceFileSystem, string DestinationFileSystem,
    string SourcePath, string DestinationPath, string Type, ushort AuxType, bool Locked,
    string TextMode, int PayloadLength, string PayloadSha256, IReadOnlyList<string> MetadataLosses,
    long? FreeBytesAfter, string CandidateSha256)
{
    public string PlanSha256 { get; init; } = "";
}

public sealed record DiskMigrationResult(ImageWriteResult Write, DiskMigrationPlan Plan);

/// <summary>Explicit single-file DOS/ProDOS migration with immutable snapshots and a reviewable plan.</summary>
public static class DiskMigration
{
    private const int MaximumImageBytes = 34 * 1024 * 1024;

    public static DiskMigrationPlan Plan(string sourceImage, string sourcePath, string destinationImage,
        string destinationPath, DiskMigrationOptions? options = null, CancellationToken cancellationToken = default)
        => Prepare(sourceImage, sourcePath, destinationImage, destinationPath, options ?? new(), cancellationToken).Plan;

    public static DiskMigrationResult Apply(string sourceImage, string sourcePath, string destinationImage,
        string destinationPath, string? outputPath = null, bool inPlace = false, bool overwrite = false,
        bool allowMetadataLoss = false, string? expectedPlanSha256 = null, DiskMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
        => ApplyCore(sourceImage, sourcePath, destinationImage, destinationPath, outputPath, inPlace,
            overwrite, allowMetadataLoss, expectedPlanSha256, options, cancellationToken);

    internal static DiskMigrationResult ApplyCore(string sourceImage, string sourcePath, string destinationImage,
        string destinationPath, string? outputPath = null, bool inPlace = false, bool overwrite = false,
        bool allowMetadataLoss = false, string? expectedPlanSha256 = null, DiskMigrationOptions? options = null,
        CancellationToken cancellationToken = default, Action? candidatePrepared = null)
    {
        if (outputPath is not null) ImageTransactions.EnsureDistinctPaths(sourceImage, outputPath);
        PreparedMigration prepared = Prepare(sourceImage, sourcePath, destinationImage, destinationPath,
            options ?? new(), cancellationToken);
        DiskMigrationPlan plan = prepared.Plan;
        if (plan.MetadataLosses.Count != 0 && !allowMetadataLoss)
            throw new DiskException("migration.metadata_loss",
                "Review --preflight and use --allow-metadata-loss to accept: " + string.Join(", ", plan.MetadataLosses), 3);
        if (expectedPlanSha256 is not null &&
            !expectedPlanSha256.Equals(plan.PlanSha256, StringComparison.OrdinalIgnoreCase))
            throw new DiskException("migration.plan_changed", "The migration differs from the expected plan.", 6);
        candidatePrepared?.Invoke();
        Recheck(plan, cancellationToken);
        ImageWriteResult write = ImageTransactions.Write(destinationImage, outputPath, inPlace, overwrite,
            temporary =>
            {
                if (Hash(temporary, cancellationToken) != plan.DestinationSha256)
                    throw new DiskException("migration.input_changed", "The destination image changed after planning.", 6);
                File.WriteAllBytes(temporary, prepared.Candidate);
            },
            temporary =>
            {
                if (Hash(temporary, cancellationToken) != plan.CandidateSha256)
                    throw new DiskException("migration.validation_failed", "The staged migration differs from its plan.", 4);
                Recheck(plan, cancellationToken);
            }, cancellationToken);
        return new(write, plan);
    }

    private static PreparedMigration Prepare(string sourceImage, string sourcePath, string destinationImage,
        string destinationPath, DiskMigrationOptions options, CancellationToken cancellationToken)
    {
        if (options.TextMode is not ("preserve" or "convert"))
            throw new DiskException("migration.text_mode", "textMode must be preserve or convert.", 2);
        ImageTransactions.EnsureDistinctPaths(sourceImage, destinationImage);
        byte[] sourceBytes = ProgramFiles.ReadBytes(sourceImage, MaximumImageBytes, cancellationToken);
        byte[] destinationBytes = ProgramFiles.ReadBytes(destinationImage, MaximumImageBytes, cancellationToken);
        string directory = Path.Combine(HostFiles.ResolvePhysicalDirectory(Path.GetTempPath()), "a2-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourceSnapshot = Path.Combine(directory, "source.img");
            string candidatePath = Path.Combine(directory, "candidate.img");
            File.WriteAllBytes(sourceSnapshot, sourceBytes);
            File.WriteAllBytes(candidatePath, destinationBytes);
            DiskMigrationPlan plan;
            byte[] payload;
            using (DiskSession source = DiskSession.Open(sourceSnapshot, options.SourceOrder, options.SourceFileSystem))
            using (DiskSession destination = DiskSession.Open(candidatePath, options.DestinationOrder, options.DestinationFileSystem, writable: true))
            {
                DiskInfo sourceInfo = source.Info;
                DiskInfo destinationInfo = destination.Info;
                if (sourceInfo.FileSystem == destinationInfo.FileSystem)
                    throw new DiskException("migration.same_filesystem", "Use disk copy for matching filesystems.", 2);
                if (sourceInfo.IsDubious || sourceInfo.Diagnostics.Any(item => item.Severity == "error"))
                    throw new DiskException("corrupt_image", "Migration requires an undamaged source image.", 4);
                DiskEntry entry = source.GetEntry(sourcePath);
                if (entry.IsDirectory || entry.FileType is not (0x04 or 0x06 or 0xfc))
                    throw new DiskException("migration.file_type", "Migration supports individual TXT, BIN, and Applesoft BAS files.", 3);
                if (entry.FileType == 0xfc && entry.AuxType != ApplesoftBasic.DefaultOrigin)
                    throw new DiskException("migration.basic_origin", "BASIC migration requires origin $0801; relocate the program explicitly first.", 3);
                payload = source.ReadFile(sourcePath);
                if (entry.FileType == 0x04 && sourceInfo.FileSystem == "dos33" &&
                    source.ReadFile(sourcePath, raw: true).AsSpan(payload.Length).ContainsAnyExcept((byte)0))
                    throw new DiskException("migration.text_records",
                        "DOS text contains nonzero bytes after its logical EOF; use stored-byte extraction to preserve random-access records.", 3);
                if (entry.FileType == 0x04 && options.TextMode == "convert")
                    payload = AppleTextCodec.Encode(AppleTextCodec.Decode(payload), destinationInfo.FileSystem);
                else if (entry.FileType != 0x04 && options.TextMode == "convert")
                    throw new DiskException("migration.text_type", "Text conversion requires a TXT file.", 2);
                if (destinationInfo.FileSystem == "dos33" && entry.FileType is 0x06 or 0xfc && payload.Length > ushort.MaxValue)
                    throw new DiskException("migration.payload_length", "DOS program headers support at most 65,535 payload bytes.", 3);
                List<string> losses = [];
                if (destinationInfo.FileSystem == "dos33")
                {
                    if (entry.Created is not null) losses.Add("created");
                    if (entry.Modified is not null) losses.Add("modified");
                    if (entry.FileType == 0x04 && entry.AuxType != 0) losses.Add("auxType");
                }
                string type = entry.FileType switch { 0x04 => "TXT", 0x06 => "BIN", _ => "BAS" };
                ushort aux = entry.FileType == 0x04 && destinationInfo.FileSystem == "dos33" ? (ushort)0 : entry.AuxType;
                destination.Add(destinationPath, payload, type, aux);
                destination.SetTimestamps(destinationPath, entry.Created ?? new DateTime(2000, 1, 1),
                    entry.Modified ?? new DateTime(2000, 1, 1));
                destination.SetAttributes(destinationPath, locked: entry.IsLocked);
                DiskEntry result = destination.GetEntry(destinationPath);
                if (destinationInfo.FileSystem == "dos33" && entry.Access != result.Access)
                    losses.Add("access");
                if (!destination.ReadFile(destinationPath).AsSpan().SequenceEqual(payload) ||
                    result.FileType != entry.FileType || result.AuxType != aux || result.IsLocked != entry.IsLocked)
                    throw new DiskException("migration.unrepresentable",
                        "The destination cannot preserve the logical payload and mapped attributes.", 3);
                plan = new(1, Path.GetFullPath(sourceImage), ProgramFiles.Hash(sourceBytes),
                    Path.GetFullPath(destinationImage), ProgramFiles.Hash(destinationBytes),
                    sourceInfo.FileSystem, destinationInfo.FileSystem, sourcePath, destinationPath,
                    type, aux, entry.IsLocked, options.TextMode, payload.Length, ProgramFiles.Hash(payload),
                    losses, destination.Info.FreeBytes, "");
            }
            using (DiskSession reopened = DiskSession.Open(candidatePath, options.DestinationOrder, options.DestinationFileSystem))
            {
                if (reopened.Info.IsDubious || reopened.Verify().Any(item => item.Severity == "error") ||
                    !reopened.ReadFile(destinationPath).AsSpan().SequenceEqual(payload))
                    throw new DiskException("migration.validation_failed", "The reopened migration failed validation.", 4);
            }
            byte[] candidate = ProgramFiles.ReadBytes(candidatePath, MaximumImageBytes, cancellationToken);
            plan = plan with { CandidateSha256 = ProgramFiles.Hash(candidate) };
            plan = plan with { PlanSha256 = ProgramFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(plan, FileTransfer.JsonOptions)) };
            Recheck(plan, cancellationToken);
            return new(plan, candidate);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Recheck(DiskMigrationPlan plan, CancellationToken cancellationToken)
    {
        if (Hash(plan.SourceImage, cancellationToken) != plan.SourceSha256 ||
            Hash(plan.DestinationImage, cancellationToken) != plan.DestinationSha256)
            throw new DiskException("migration.input_changed", "A migration input changed after its snapshot was captured.", 6);
    }

    private static string Hash(string path, CancellationToken cancellationToken)
        => ProgramFiles.Hash(ProgramFiles.ReadBytes(path, MaximumImageBytes, cancellationToken));

    private sealed record PreparedMigration(DiskMigrationPlan Plan, byte[] Candidate);
}
