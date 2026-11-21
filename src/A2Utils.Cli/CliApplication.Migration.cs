// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.CommandLine;
using A2Utils.Core;
using A2Utils.Core.Operations;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddMigrationCommand(Command disk)
    {
        Command command = new("migrate", "Preview or migrate one TXT, BIN, or Applesoft file between DOS and ProDOS images.");
        Argument<string> image = new("IMAGE") { Description = "Destination image." };
        Argument<string> source = new("SOURCE") { Description = "Source image entry." };
        Argument<string> destination = new("DESTINATION") { Description = "Exact new destination entry; parent must exist." };
        Option<string> from = new("--from") { Required = true, Description = "Separate source image." };
        Option<string?> sourceOrder = new("--source-order");
        Option<string?> sourceFs = new("--source-fs");
        Option<string> textMode = new("--text-mode") { DefaultValueFactory = _ => "preserve", Description = "preserve payload bytes, or convert Apple text encoding." };
        Option<bool> preflight = new("--preflight") { Description = "Validate and report the migration without committing output." };
        Option<bool> allowLoss = new("--allow-metadata-loss") { Description = "Accept metadata losses reported by preflight." };
        Option<string?> expected = new("--expect-plan-sha256") { Description = "Require the exact reviewed migration plan." };
        command.Arguments.Add(image);
        command.Arguments.Add(source);
        command.Arguments.Add(destination);
        foreach (Option option in new Option[] { from, sourceOrder, sourceFs, textMode, preflight, allowLoss, expected })
            command.Options.Add(option);
        WriteOptions write = AddWriteOptions(command);
        command.SetAction(parse =>
        {
            DiskMigrationOptions options = new()
            {
                SourceOrder = parse.GetValue(sourceOrder),
                SourceFileSystem = parse.GetValue(sourceFs),
                DestinationOrder = parse.GetValue(_inputOrder),
                DestinationFileSystem = parse.GetValue(_inputFs),
                TextMode = parse.GetValue(textMode)!
            };
            if (parse.GetValue(preflight))
            {
                if (parse.GetValue(write.Output) is not null || parse.GetValue(write.InPlace) ||
                    parse.GetValue(write.Overwrite) || parse.GetValue(allowLoss) || parse.GetValue(expected) is not null)
                    throw new DiskException("invalid_arguments", "Preflight does not accept output, in-place, overwrite, acceptance, or expected-plan options.", 2);
                DiskMigrationPlan plan = DiskMigration.Plan(parse.GetValue(from)!, parse.GetValue(source)!,
                    parse.GetValue(image)!, parse.GetValue(destination)!, options, _cancellationToken);
                return Result("disk.migrate.plan", plan,
                    $"Plan {plan.PlanSha256}: {plan.Type}, {plan.PayloadLength} bytes; metadata losses: "
                    + (plan.MetadataLosses.Count == 0 ? "none" : string.Join(", ", plan.MetadataLosses)));
            }
            DiskMigrationResult result = DiskMigration.Apply(parse.GetValue(from)!, parse.GetValue(source)!,
                parse.GetValue(image)!, parse.GetValue(destination)!, parse.GetValue(write.Output),
                parse.GetValue(write.InPlace), parse.GetValue(write.Overwrite), parse.GetValue(allowLoss),
                parse.GetValue(expected), options, _cancellationToken);
            return Result("disk.migrate", result, $"Migrated {result.Plan.SourcePath} to {result.Write.OutputPath}:{result.Plan.DestinationPath}.");
        });
        disk.Subcommands.Add(command);
    }
}
