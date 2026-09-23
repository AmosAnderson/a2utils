// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Collections.Concurrent;
using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class DiskChangeSetTests
{
    [Fact]
    public void PlanAndApply_MultipleChanges_CommitsOnceAndMatchesCandidateHash()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.CopyDisk();
        string payload = workspace.NewPath("notes.txt");
        File.WriteAllBytes(payload, "UPDATED\r"u8.ToArray());
        string changes = WritePlan(workspace, new()
        {
            Changes =
            [
                new() { Action = "replace", Path = "README", Source = "notes.txt" },
                new() { Action = "add", Path = "NEWFILE", Hex = "2A A5", Type = "B", AuxType = 0x3000 }
            ]
        });

        DiskChangePlan plan = DiskChangeSetRunner.Plan(input, changes);

        Assert.Equal(2, plan.Changes.Count);
        Assert.Equal("dos", plan.Order);
        Assert.Equal("dos33", plan.FileSystem);
        Assert.NotEqual(plan.CandidateSha256, plan.PlanSha256);
        Assert.False(File.Exists(workspace.NewPath("output.do")));
        DiskApplyResult applied = DiskChangeSetRunner.Apply(input, changes, workspace.NewPath("output.do"),
            inPlace: false, overwrite: false, expectedInputSha256: plan.InputSha256,
            expectedPlanSha256: plan.PlanSha256);
        Assert.Equal(plan.CandidateSha256, applied.OutputSha256);
        using DiskSession original = DiskSession.Open(input);
        using DiskSession output = DiskSession.Open(applied.Write.OutputPath);
        Assert.NotEqual(original.ReadFile("README"), output.ReadFile("README"));
        Assert.Equal(new byte[] { 0x2a, 0xa5 }, output.ReadFile("NEWFILE"));
        Assert.Equal(0x3000, output.GetEntry("NEWFILE").AuxType);
    }

    [Fact]
    public void PlanAndApply_VolumeRootAttributes_ReportsChangeAndPreservesSource()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("input.po");
        DiskSession.Create(input, "prodos", volumeName: "PLAN");
        byte[] original = File.ReadAllBytes(input);
        string changes = WritePlan(workspace, new()
        {
            Changes = [new() { Action = "attr", Path = "/", Locked = true }]
        });

        DiskChangePlan plan = DiskChangeSetRunner.Plan(input, changes);

        DiskEntryDifference change = Assert.Single(plan.Changes);
        Assert.Equal("/", change.Path);
        Assert.Equal("modified", change.Action);
        Assert.Equal(["access"], change.ChangedFields);
        Assert.NotEqual(change.Before!.Access, change.After!.Access);
        Assert.Equal(original, File.ReadAllBytes(input));

        DiskApplyResult result = DiskChangeSetRunner.Apply(input, changes,
            workspace.NewPath("output.po"), inPlace: false, overwrite: false,
            expectedPlanSha256: plan.PlanSha256);

        Assert.Equal(plan.CandidateSha256, result.OutputSha256);
        Assert.Equal(original, File.ReadAllBytes(input));
        using DiskSession output = DiskSession.Open(result.Write.OutputPath);
        Assert.True(output.GetEntry("/").IsLocked);
    }

    [Fact]
    public void Apply_ChangedPayloadAfterPlan_RefusesAndPreservesDestination()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.CopyDisk();
        string payload = workspace.NewPath("notes.txt");
        File.WriteAllBytes(payload, [1, 2, 3]);
        string changes = WritePlan(workspace, new()
        {
            Changes = [new() { Action = "replace", Path = "README", Source = "notes.txt" }]
        });
        DiskChangePlan plan = DiskChangeSetRunner.Plan(input, changes);
        File.WriteAllBytes(payload, [4, 5, 6]);
        string output = workspace.NewPath("output.do");

        DiskException error = Assert.Throws<DiskException>(() => DiskChangeSetRunner.Apply(input,
            changes, output, inPlace: false, overwrite: false, expectedPlanSha256: plan.PlanSha256));

        Assert.Equal("disk_plan.plan_hash", error.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Diff_ModifiedPayload_ReportsLogicalAndPhysicalChanges()
    {
        using FixtureWorkspace workspace = new();
        string before = workspace.CopyDisk(name: $"diff-{Guid.NewGuid():N}.do");
        string after = workspace.NewPath("after.do");
        ImageTransactions.Write(before, after, false, false, temporary =>
        {
            using DiskSession disk = DiskSession.Open(temporary, "dos", "dos33", writable: true);
            disk.Replace("README", "CHANGED\r"u8.ToArray());
        });

        DiskDifference result = DiskDiff.Compare(before, after);

        Assert.False(result.Identical);
        Assert.Contains(result.Entries, entry => entry.Path == "README" &&
            entry.Action == "modified" && entry.ChangedFields.Contains("payload"));
        Assert.True(result.DifferentByteCount > 0);
        Assert.NotEmpty(result.ByteRanges);
    }

    [Fact]
    public void Diff_SnapshotsInputs_ReportsOriginalPaths()
    {
        using FixtureWorkspace workspace = new();
        string before = workspace.CopyDisk();
        string after = workspace.NewPath("after.do");
        File.Copy(before, after);

        DiskDifference result = DiskDiff.Compare(before, after);

        Assert.Equal(Path.GetFullPath(before), result.BeforeInfo.Path);
        Assert.Equal(Path.GetFullPath(after), result.AfterInfo.Path);
        Assert.Equal(result.BeforePath, result.BeforeInfo.Path);
        Assert.Equal(result.AfterPath, result.AfterInfo.Path);
        Assert.True(File.Exists(result.BeforeInfo.Path));
        Assert.True(File.Exists(result.AfterInfo.Path));
    }

    [Fact]
    public void Diff_ProDosCaseOnlyRename_ReportsPathMetadataChange()
    {
        using FixtureWorkspace workspace = new();
        string before = workspace.NewPath("before.po");
        string after = workspace.NewPath("after.po");
        DiskSession.Create(before, "prodos", volumeName: "DIFF");
        using (DiskSession disk = DiskSession.Open(before, inputFs: "prodos", writable: true))
        {
            disk.Add("MixedName", [0x60], "BIN", 0x2000);
            disk.Flush();
        }
        ImageTransactions.Write(before, after, false, false, temporary =>
        {
            using DiskSession disk = DiskSession.Open(temporary, inputFs: "prodos", writable: true);
            disk.Rename("MixedName", "MIXEDNAME");
            disk.Flush();
        });

        DiskDifference result = DiskDiff.Compare(before, after,
            beforeFileSystem: "prodos", afterFileSystem: "prodos");

        DiskEntryDifference change = Assert.Single(result.Entries);
        Assert.Equal("modified", change.Action);
        Assert.Equal("MixedName", change.Before?.Path);
        Assert.Equal("MIXEDNAME", change.After?.Path);
        Assert.Contains("path", change.ChangedFields);
        Assert.True(result.DifferentByteCount > 0);
    }

    [Fact]
    public void Diff_CrossFileSystemCaseCollision_DoesNotDropDosEntry()
    {
        using FixtureWorkspace workspace = new();
        string before = workspace.NewPath("before.do");
        string after = workspace.NewPath("after.po");
        DiskSession.Create(before, "dos33", volumeNumber: 1);
        using (DiskSession disk = DiskSession.Open(before, inputFs: "dos33", writable: true))
        {
            disk.Add("Mixed", [0x01], "B", 0x2000);
            disk.Add("MIXED", [0x02], "B", 0x2000);
            disk.Flush();
        }
        DiskSession.Create(after, "prodos", volumeName: "DIFF");
        using (DiskSession disk = DiskSession.Open(after, inputFs: "prodos", writable: true))
        {
            disk.Add("mixed", [0x03], "BIN", 0x2000);
            disk.Flush();
        }

        DiskDifference result = DiskDiff.Compare(before, after,
            beforeFileSystem: "dos33", afterFileSystem: "prodos");

        Assert.Equal(4, result.Entries.Count);
        Assert.Contains(result.Entries, entry => entry.Path == "/" && entry.Action == "added");
        Assert.Contains(result.Entries, entry => entry.Path == "Mixed" && entry.Action == "removed");
        Assert.Contains(result.Entries, entry => entry.Path == "MIXED" && entry.Action == "removed");
        Assert.Contains(result.Entries, entry => entry.Path == "mixed" && entry.Action == "added");
    }

    [Fact]
    public async Task Diff_SourceMutatesAfterCapture_LogicalAndPhysicalEvidenceUsesCapturedBytes()
    {
        using FixtureWorkspace workspace = new();
        string before = workspace.CopyDisk(name: $"diff-mutation-{Guid.NewGuid():N}.do");
        string after = workspace.NewPath("same.do");
        File.Copy(before, after);
        string originalHash = ProgramFiles.Hash(File.ReadAllBytes(before));
        TaskCompletionSource<bool> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FileSystemWatcher watcher = new(HostFiles.ResolvePhysicalDirectory(Path.GetTempPath()))
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        string suffix = "-" + Path.GetFileName(before);
        watcher.Created += (_, args) =>
        {
            if (args.Name?.StartsWith("a2-diff-before-", StringComparison.Ordinal) != true ||
                !args.Name.EndsWith(suffix, StringComparison.Ordinal)) return;
            try
            {
                using FileStream file = new(before, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite);
                int value = file.ReadByte();
                file.Position = 0;
                file.WriteByte((byte)(value ^ 0xff));
                file.Flush(true);
                changed.TrySetResult(true);
            }
            catch (Exception exception)
            {
                changed.TrySetException(exception);
            }
        };

        DiskDifference result = DiskDiff.Compare(before, after);

        Assert.True(await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(originalHash, result.BeforeSha256);
        Assert.Equal(result.BeforeSha256, result.AfterSha256);
        Assert.True(result.Identical);
        Assert.Empty(result.Entries);
        Assert.NotEqual(originalHash, ProgramFiles.Hash(File.ReadAllBytes(before)));
    }

    [Fact]
    public void Plan_FieldThatActionWouldIgnore_RefusesStrictContract()
    {
        using FixtureWorkspace workspace = new();
        string changes = WritePlan(workspace, new()
        {
            Changes = [new() { Action = "delete", Path = "README", Hex = "00" }]
        });

        DiskException error = Assert.Throws<DiskException>(() =>
            DiskChangeSetRunner.Plan(workspace.CopyDisk(), changes));

        Assert.Equal("disk_plan.payload", error.Code);
    }

    [Fact]
    public async Task Plan_ReadOnlySourceDirectory_DoesNotStageBesideInput()
    {
        using FixtureWorkspace workspace = new();
        string sourceDirectory = workspace.NewPath("read-only-source");
        Directory.CreateDirectory(sourceDirectory);
        string input = Path.Combine(sourceDirectory, "input.do");
        File.Copy(FixtureWorkspace.FindFixture("independent-dos33.do"), input);
        string changes = WritePlan(workspace, new()
        {
            Changes = [new() { Action = "delete", Path = "README" }]
        });
        ConcurrentQueue<string> siblingWrites = new();
        string temporaryPrefix = "." + Path.GetFileName(input) + ".";
        using FileSystemWatcher watcher = new(sourceDirectory)
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        void Observe(object? _, FileSystemEventArgs args)
        {
            if (args.Name?.StartsWith(temporaryPrefix, StringComparison.Ordinal) == true)
                siblingWrites.Enqueue(args.Name);
        }
        watcher.Created += Observe;
        watcher.Renamed += Observe;

        FileAttributes? originalAttributes = null;
        UnixFileMode? originalMode = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                originalAttributes = File.GetAttributes(input);
                File.SetAttributes(input, originalAttributes.Value | FileAttributes.ReadOnly);
            }
            else
            {
                originalMode = File.GetUnixFileMode(sourceDirectory);
                UnixFileMode writes = UnixFileMode.UserWrite | UnixFileMode.GroupWrite |
                    UnixFileMode.OtherWrite;
                File.SetUnixFileMode(sourceDirectory, originalMode.Value & ~writes);
            }

            DiskChangePlan plan = DiskChangeSetRunner.Plan(input, changes);
            await Task.Delay(250);

            Assert.Equal(Path.GetFullPath(input), plan.InputPath);
            Assert.Empty(siblingWrites);
            Assert.Equal([input], Directory.EnumerateFiles(sourceDirectory).ToArray());
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && originalMode is not null)
                File.SetUnixFileMode(sourceDirectory, originalMode.Value);
            if (originalAttributes is not null)
                File.SetAttributes(input, originalAttributes.Value);
        }
    }

    [Fact]
    public void Apply_MalformedExpectedHash_RefusesBeforeWriting()
    {
        using FixtureWorkspace workspace = new();
        string changes = WritePlan(workspace, new()
        {
            Changes = [new() { Action = "delete", Path = "README" }]
        });
        string output = workspace.NewPath("output.do");

        DiskException error = Assert.Throws<DiskException>(() => DiskChangeSetRunner.Apply(
            workspace.CopyDisk(), changes, output, inPlace: false, overwrite: false,
            expectedPlanSha256: "not-a-sha256"));

        Assert.Equal("disk_plan.invalid", error.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PlanAndApply_NewProDosEntries_UseDeterministicTimestamp()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("input.po");
        DiskSession.Create(input, "prodos", volumeName: "PLAN");
        string changes = WritePlan(workspace, new()
        {
            Changes = [new()
            {
                Action = "add", Path = "TOOLS/BUILD/MAIN", Hex = "60", Type = "BIN",
                AuxType = 0x2000, Parents = true
            }]
        });

        DiskChangePlan first = DiskChangeSetRunner.Plan(input, changes);
        DiskChangePlan second = DiskChangeSetRunner.Plan(input, changes);
        string output = workspace.NewPath("output.po");
        DiskChangeSetRunner.Apply(input, changes, output, inPlace: false, overwrite: false,
            expectedPlanSha256: first.PlanSha256);

        Assert.Equal(first.CandidateSha256, second.CandidateSha256);
        Assert.Equal(first.PlanSha256, second.PlanSha256);
        using DiskSession disk = DiskSession.Open(output, inputFs: "prodos");
        DateTime expected = new(2000, 1, 1);
        foreach (string path in new[] { "TOOLS", "TOOLS/BUILD", "TOOLS/BUILD/MAIN" })
        {
            DiskEntry entry = disk.GetEntry(path);
            Assert.Equal(expected, entry.Created);
            Assert.Equal(expected, entry.Modified);
        }
    }

    [Fact]
    public void Plan_TimestampWithSubminutePrecision_Refuses()
    {
        using FixtureWorkspace workspace = new();
        string changes = WritePlan(workspace, new()
        {
            Timestamp = new DateTime(2000, 1, 1, 0, 0, 1),
            Changes = [new() { Action = "delete", Path = "README" }]
        });

        DiskException error = Assert.Throws<DiskException>(() =>
            DiskChangeSetRunner.Plan(workspace.CopyDisk(), changes));

        Assert.Equal("disk_plan.timestamp", error.Code);
    }

    [Fact]
    public async Task Plan_ChangeSetMutatesAfterCandidateValidation_RefusesIncoherentApproval()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath($"large-{Guid.NewGuid():N}.po");
        DiskSession.Create(input, "prodos", order: "prodos", volumeName: "PLAN",
            blocks: 65535);
        string changes = WritePlan(workspace, new()
        {
            Changes = [new() { Action = "mkdir", Path = "TOOLS" }]
        });
        TaskCompletionSource<bool> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FileSystemWatcher watcher = new(HostFiles.ResolvePhysicalDirectory(Path.GetTempPath()))
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        string candidatePrefix = "." + Path.GetFileName(input) + ".plan-candidate-";
        void Mutate(object? _, FileSystemEventArgs args)
        {
            if (args.Name?.StartsWith(candidatePrefix, StringComparison.Ordinal) != true) return;
            try
            {
                File.AppendAllText(changes, "\n");
                changed.TrySetResult(true);
            }
            catch (Exception exception)
            {
                changed.TrySetException(exception);
            }
        }
        watcher.Created += Mutate;
        watcher.Renamed += Mutate;

        Exception? exception = Record.Exception(() => DiskChangeSetRunner.Plan(input, changes));

        Assert.True(await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        DiskException error = Assert.IsType<DiskException>(exception);
        Assert.Equal("disk_plan.changed", error.Code);
        Assert.Empty(Directory.EnumerateFiles(workspace.DirectoryPath,
            "." + Path.GetFileName(input) + ".plan-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_OutputAliasesDeclarativeInput_RefusesAndPreservesInput(bool aliasPayload)
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.CopyDisk();
        string payload = workspace.NewPath("notes.txt");
        File.WriteAllBytes(payload, [1, 2, 3]);
        string changes = WritePlan(workspace, new()
        {
            Changes = [new() { Action = "replace", Path = "README", Source = "notes.txt" }]
        });
        string destination = aliasPayload ? payload : changes;
        byte[] original = File.ReadAllBytes(destination);

        DiskException error = Assert.Throws<DiskException>(() => DiskChangeSetRunner.Apply(
            input, changes, destination, inPlace: false, overwrite: true));

        Assert.Equal("write.source_alias", error.Code);
        Assert.Equal(original, File.ReadAllBytes(destination));
    }

    private static string WritePlan(FixtureWorkspace workspace, DiskChangeSet plan)
    {
        string path = workspace.NewPath("changes.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        return path;
    }
}
