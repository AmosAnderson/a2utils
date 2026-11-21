// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.CommandLine;
using A2Utils.Core;
using A2Utils.Core.Operations;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddBatchMigrationCommand(Command disk)
    {
        Command command = new("migrate-batch", "Preflight or atomically migrate a selection of DOS/ProDOS files.");
        Argument<string> image = new("IMAGE") { Description = "Destination image." };
        Option<string> from = new("--from") { Required = true, Description = "Source image." };
        Option<bool> all = new("--all") { Description = "Select all files recursively and map their names to the destination root." };
        Option<string?> selection = new("--selection") { Description = "Versioned JSON file with explicit source and destination paths." };
        Option<string?> sourceOrder = new("--source-order");
        Option<string?> sourceFs = new("--source-fs");
        Option<bool> preflight = new("--preflight") { Description = "Report file mappings, conflicts, free space and losses." };
        Option<bool> loss = new("--allow-metadata-loss") { Description = "Accept the reported metadata losses." };
        Option<string?> expected = new("--expect-plan-sha256") { Description = "Require the reviewed plan hash." };
        command.Arguments.Add(image);
        foreach (Option option in new Option[] { from, all, selection, sourceOrder, sourceFs, preflight, loss, expected })
            command.Options.Add(option);
        WriteOptions write = AddWriteOptions(command);
        command.SetAction(parse =>
        {
            string? selectionPath = parse.GetValue(selection);
            if (parse.GetValue(all) == (selectionPath is not null))
                throw new DiskException("invalid_arguments", "Choose --all or --selection FILE.", 2);
            string source = parse.GetValue(from)!;
            string destination = parse.GetValue(image)!;
            string? output = parse.GetValue(write.Output);
            bool inPlace = parse.GetValue(write.InPlace);
            bool overwrite = parse.GetValue(write.Overwrite);
            bool allowLoss = parse.GetValue(loss);
            string? planHash = parse.GetValue(expected);
            if (parse.GetValue(preflight) && (output is not null || inPlace || overwrite || allowLoss || planHash is not null))
                throw new DiskException("invalid_arguments", "Preflight does not take write or approval options.", 2);
            if (selectionPath is not null && (inPlace || output is not null))
                ImageTransactions.EnsureDistinctPaths(selectionPath, inPlace ? destination : output!);
            MigrationSelection? selected = selectionPath is null ? null : MigrationSelection.Load(selectionPath, _cancellationToken);
            DiskMigrationOptions options = new()
            {
                SourceOrder = parse.GetValue(sourceOrder),
                SourceFileSystem = parse.GetValue(sourceFs),
                DestinationOrder = parse.GetValue(_inputOrder),
                DestinationFileSystem = parse.GetValue(_inputFs)
            };
            if (parse.GetValue(preflight))
            {
                BatchMigrationPlan plan = DiskMigration.PlanBatch(source, destination, selected, options, _cancellationToken);
                List<string> lines = [$"Plan {plan.PlanSha256}", $"Can apply: {plan.CanApply}; free bytes before: {plan.FreeBytesBefore}; after: {plan.FreeBytesAfter?.ToString() ?? "unknown"}"];
                foreach (MigrationFilePlan item in plan.Files)
                {
                    lines.Add($"{item.File.Source} -> {item.File.Destination} ({item.Type ?? "unknown"}, {item.PayloadLength} bytes)");
                    if (item.MetadataLosses.Count > 0) lines.Add("  Metadata losses: " + string.Join(", ", item.MetadataLosses));
                    lines.AddRange(item.Diagnostics.Select(diagnostic => $"  {diagnostic.Code}: {diagnostic.Message}"));
                }
                Result("disk.migrate-batch.plan", plan, string.Join(Environment.NewLine, lines));
                return plan.CanApply ? 0 : 3;
            }
            BatchMigrationResult result = DiskMigration.ApplyBatch(source, destination, selected, output, inPlace,
                overwrite, allowLoss, planHash, options, _cancellationToken);
            return Result("disk.migrate-batch", result, $"Migrated {result.Plan.Files.Count} files to {result.Write.OutputPath}.");
        });
        disk.Subcommands.Add(command);
    }
}
