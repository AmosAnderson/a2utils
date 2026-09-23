// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.CommandLine;
using A2Utils.Core.Operations;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddDiskPlanningCommands(Command disk)
    {
        Command diff = new("diff", "Compare filesystem entries, metadata, payloads, and physical image bytes.");
        Argument<string> before = new("BEFORE");
        Argument<string> after = new("AFTER");
        Option<string?> afterOrder = new("--after-input-order") { Description = "Layout override for AFTER: dos or prodos." };
        Option<string?> afterFileSystem = new("--after-input-fs") { Description = "Filesystem override for AFTER: dos33 or prodos." };
        diff.Arguments.Add(before);
        diff.Arguments.Add(after);
        diff.Options.Add(afterOrder);
        diff.Options.Add(afterFileSystem);
        diff.SetAction(parse =>
        {
            ValidateChoice(parse.GetValue(afterOrder), "--after-input-order", "dos", "prodos");
            ValidateChoice(parse.GetValue(afterFileSystem), "--after-input-fs", "dos33", "prodos");
            DiskDifference result = DiskDiff.Compare(parse.GetValue(before)!, parse.GetValue(after)!,
                parse.GetValue(_inputOrder), parse.GetValue(_inputFs), parse.GetValue(afterOrder),
                parse.GetValue(afterFileSystem), _cancellationToken);
            return Result("diff", result, result.Identical ? "Images are byte-identical."
                : $"{result.Entries.Count} logical entries changed; {result.DifferentByteCount} physical bytes differ.");
        });
        disk.Subcommands.Add(diff);

        Command plan = new("plan", "Preflight a declarative multi-operation disk change set without committing output.");
        Argument<string> planImage = new("IMAGE");
        Argument<string> changes = new("CHANGES");
        plan.Arguments.Add(planImage);
        plan.Arguments.Add(changes);
        plan.SetAction(parse =>
        {
            DiskChangePlan result = DiskChangeSetRunner.Plan(parse.GetValue(planImage)!,
                parse.GetValue(changes)!, parse.GetValue(_inputOrder), parse.GetValue(_inputFs),
                _cancellationToken);
            return Result("disk.plan", result,
                $"Plan {result.PlanSha256}: {result.Changes.Count} logical changes; {result.FreeBytesAfter} bytes free.");
        });
        disk.Subcommands.Add(plan);

        Command apply = new("apply", "Apply a declarative change set as one validated image transaction.");
        Argument<string> applyImage = new("IMAGE");
        Argument<string> applyChanges = new("CHANGES");
        Option<string?> expected = new("--expect-sha256") { Description = "Require this input image SHA-256." };
        Option<string?> expectedPlan = new("--expect-plan-sha256") { Description = "Require this preflight plan SHA-256." };
        apply.Arguments.Add(applyImage);
        apply.Arguments.Add(applyChanges);
        apply.Options.Add(expected);
        apply.Options.Add(expectedPlan);
        WriteOptions write = AddWriteOptions(apply);
        apply.SetAction(parse =>
        {
            DiskApplyResult result = DiskChangeSetRunner.Apply(parse.GetValue(applyImage)!,
                parse.GetValue(applyChanges)!, parse.GetValue(write.Output), parse.GetValue(write.InPlace),
                parse.GetValue(write.Overwrite), parse.GetValue(expected), parse.GetValue(expectedPlan),
                parse.GetValue(_inputOrder), parse.GetValue(_inputFs), _cancellationToken);
            return Result("disk.apply", result, $"Applied plan {result.Plan.PlanSha256}; wrote {result.Write.OutputPath}.");
        });
        disk.Subcommands.Add(apply);
    }
}
