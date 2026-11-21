// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Operations;

public sealed record MigrationFile(string Source, string Destination, string TextMode = "preserve");
public sealed record MigrationSelection(int SchemaVersion, IReadOnlyList<MigrationFile> Files)
{
    public static MigrationSelection Load(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return JsonSerializer.Deserialize<MigrationSelection>(ProgramFiles.ReadBytes(path, 1024 * 1024, cancellationToken),
                ExecutionSpec.JsonOptions) ?? throw new JsonException("A selection object is required.");
        }
        catch (JsonException exception)
        {
            throw new DiskException("migration.invalid_selection", exception.Message, 2);
        }
    }
}

public sealed record MigrationFilePlan(MigrationFile File, string? Type, ushort? AuxType, bool? Locked,
    int? PayloadLength, string? PayloadSha256, IReadOnlyList<string> MetadataLosses, IReadOnlyList<DiskDiagnostic> Diagnostics);

public sealed record BatchMigrationPlan(int SchemaVersion, string SourceImage, string SourceSha256, string SourceFileSystem,
    string DestinationImage, string DestinationSha256, string DestinationFileSystem, IReadOnlyList<MigrationFilePlan> Files,
    bool CanApply, long? FreeBytesBefore, long? FreeBytesAfter, string? CandidateSha256, string PlanSha256);
public sealed record BatchMigrationResult(ImageWriteResult Write, BatchMigrationPlan Plan);

public static partial class DiskMigration
{
    /// <summary>A null selection maps every source file, recursively, to a literal destination root filename.</summary>
    public static BatchMigrationPlan PlanBatch(string sourceImage, string destinationImage,
        MigrationSelection? selection = null, DiskMigrationOptions? options = null, CancellationToken cancellationToken = default)
        => PrepareBatch(sourceImage, destinationImage, selection, options ?? new(), cancellationToken).Plan;

    public static BatchMigrationResult ApplyBatch(string sourceImage, string destinationImage, MigrationSelection? selection = null,
        string? outputPath = null, bool inPlace = false, bool overwrite = false, bool allowMetadataLoss = false,
        string? expectedPlanSha256 = null, DiskMigrationOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (outputPath is not null) ImageTransactions.EnsureDistinctPaths(sourceImage, outputPath);
        var (plan, candidate) = PrepareBatch(sourceImage, destinationImage, selection, options ?? new(), cancellationToken);
        if (!plan.CanApply || candidate is null)
            throw new DiskException("migration.batch_blocked", "The batch cannot be applied. Review the diagnostics from --preflight.", 3);
        if (!allowMetadataLoss && plan.Files.Any(file => file.MetadataLosses.Count != 0))
            throw new DiskException("migration.metadata_loss", "Review the file-level losses and use --allow-metadata-loss to accept them.", 3);
        if (expectedPlanSha256 is not null && !expectedPlanSha256.Equals(plan.PlanSha256, StringComparison.OrdinalIgnoreCase))
            throw new DiskException("migration.plan_changed", "The batch differs from the reviewed plan.", 6);
        ImageWriteResult write = Commit(plan.SourceImage, plan.SourceSha256, plan.DestinationImage, plan.DestinationSha256,
            plan.CandidateSha256!, candidate, outputPath, inPlace, overwrite, cancellationToken);
        return new(write, plan);
    }

    private static (BatchMigrationPlan Plan, byte[]? Candidate) PrepareBatch(string sourceImage, string destinationImage,
        MigrationSelection? selection, DiskMigrationOptions options, CancellationToken cancellationToken)
    {
        if (selection is not null && (selection.SchemaVersion != 1 || selection.Files is null))
            throw new DiskException("migration.invalid_selection", "A version 1 selection with a files array is required.", 2);
        if (options.TextMode != "preserve")
            throw new DiskException("migration.text_mode", "Use textMode on individual batch mappings to convert text.", 2);
        ImageTransactions.EnsureDistinctPaths(sourceImage, destinationImage);
        byte[] sourceBytes = ProgramFiles.ReadBytes(sourceImage, MaximumImageBytes, cancellationToken);
        byte[] destinationBytes = ProgramFiles.ReadBytes(destinationImage, MaximumImageBytes, cancellationToken);
        string directory = HostFiles.ResolvePhysicalDirectory(Directory.CreateTempSubdirectory("a2-batch-").FullName);
        try
        {
            string snapshot = Path.Combine(directory, "source.img");
            string staged = Path.Combine(directory, "candidate.img");
            File.WriteAllBytes(snapshot, sourceBytes);
            File.WriteAllBytes(staged, destinationBytes);
            using DiskSession source = DiskSession.Open(snapshot, options.SourceOrder, options.SourceFileSystem);
            DiskInfo sourceInfo = source.Info;
            DiskInfo destinationInfo;
            List<MigrationFilePlan> files = [];
            long? freeAfter = null;
            using (DiskSession destination = DiskSession.Open(staged, options.DestinationOrder, options.DestinationFileSystem, writable: true))
            {
                destinationInfo = destination.Info;
                ValidateMigrationImages(sourceInfo, destinationInfo);
                IReadOnlyList<MigrationFile> mappings = selection?.Files ?? source.List(recursive: true)
                    .Where(entry => !entry.IsDirectory).Select(entry => new MigrationFile(entry.Path, entry.Name)).ToArray();
                if (mappings.Count is 0 or > 1024 || mappings.Any(file => file is null ||
                    string.IsNullOrEmpty(file.Source) || string.IsNullOrEmpty(file.Destination) || file.TextMode is not ("preserve" or "convert")))
                    throw new DiskException("migration.invalid_selection", "Select 1 to 1024 files with source, destination and a supported textMode.", 2);
                StringComparer comparer = destinationInfo.FileSystem == "dos33" ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                string Key(MigrationFile file) => destinationInfo.FileSystem == "dos33" ? file.Destination : file.Destination.Trim('/');
                var collisions = mappings.GroupBy(Key, comparer).Where(group => group.Count() > 1)
                    .Select(group => group.Key).ToHashSet(comparer);
                bool stageHealthy = true;
                foreach (MigrationFile mapping in mappings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    List<DiskDiagnostic> issues = [];
                    if (collisions.Contains(Key(mapping)))
                        issues.Add(new("migration.name_collision", "error", "Multiple selected files map to this destination."));
                    try
                    {
                        if (selection is null) destination.ValidateFileName(mapping.Destination);
                        destination.ValidateNewFilePath(mapping.Destination);
                    }
                    catch (DiskException ex) { issues.Add(new(ex.Code, "error", ex.Message)); }

                    MappedFile? mapped = null;
                    try
                    {
                        mapped = ReadMappedFile(source, mapping.Source, destinationInfo.FileSystem, mapping.TextMode);
                        if (selection is null && sourceInfo.FileSystem == "prodos" && mapping.Source.Contains('/'))
                            mapped.Losses.Add("directoryPath");
                    }
                    catch (DiskException ex) { issues.Add(new(ex.Code, "error", ex.Message)); }
                    if (issues.Count == 0)
                    {
                        if (stageHealthy)
                        {
                            try { WriteMappedFile(destination, mapping.Destination, mapped!); }
                            catch (DiskException ex)
                            {
                                issues.Add(new(ex.Code, "error", ex.Message));
                                stageHealthy = false;
                            }
                        }
                        else issues.Add(new("migration.fit_unchecked", "error", "Fit could not be checked after an earlier staged write failed."));
                    }
                    files.Add(new(mapping, mapped?.Type, mapped?.Aux, mapped?.Entry.IsLocked, mapped?.Payload.Length,
                        mapped is null ? null : ProgramFiles.Hash(mapped.Payload), mapped?.Losses ?? [], issues));
                }
                if (files.All(file => file.Diagnostics.Count == 0)) freeAfter = destination.Info.FreeBytes;
            }
            byte[]? candidate = null;
            if (freeAfter.HasValue)
            {
                using DiskSession reopened = DiskSession.Open(staged, options.DestinationOrder, options.DestinationFileSystem);
                if (reopened.Info.IsDubious || reopened.Verify().Any(item => item.Severity == "error"))
                    throw new DiskException("migration.validation_failed", "The reopened destination failed filesystem verification.", 4);
                foreach (MigrationFilePlan file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DiskEntry entry = reopened.GetEntry(file.File.Destination);
                    if (entry.FileType != reopened.ValidateFileType(file.Type!, file.AuxType) ||
                        entry.AuxType != file.AuxType || entry.IsLocked != file.Locked ||
                        ProgramFiles.Hash(reopened.ReadFile(file.File.Destination)) != file.PayloadSha256)
                        throw new DiskException("migration.validation_failed", $"Reopened file differs from its mapping: {file.File.Destination}.", 4);
                }
                candidate = ProgramFiles.ReadBytes(staged, MaximumImageBytes, cancellationToken);
            }
            BatchMigrationPlan plan = new(1, Path.GetFullPath(sourceImage), ProgramFiles.Hash(sourceBytes), sourceInfo.FileSystem,
                Path.GetFullPath(destinationImage), ProgramFiles.Hash(destinationBytes), destinationInfo.FileSystem, files,
                candidate is not null, destinationInfo.FreeBytes, freeAfter, candidate is null ? null : ProgramFiles.Hash(candidate), "");
            Recheck(plan.SourceImage, plan.SourceSha256, plan.DestinationImage, plan.DestinationSha256, cancellationToken);
            return (plan with { PlanSha256 = ProgramFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(plan, FileTransfer.JsonOptions)) }, candidate);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
