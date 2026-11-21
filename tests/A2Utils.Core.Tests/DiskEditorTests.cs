// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class DiskEditorTests
{
    [Fact]
    public void Edit_OutputCopy_VerifiesReopenedContentsAndPreservesSourceAndUntouchedFile()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.CopyDisk();
        string output = workspace.NewPath("edited.do");
        byte[] original = File.ReadAllBytes(input);
        byte[] untouched;
        using (DiskSession source = DiskSession.Open(input)) untouched = source.ReadFile("HELLO.BIN", raw: true);
        DiskSession? editing = null;
        bool validated = false;

        ImageWriteResult result = DiskEditor.Edit(input, session =>
        {
            editing = session;
            session.Replace("README", "UPDATED\r"u8.ToArray());
            session.Add("NEW", [0xa9, 0x01, 0x60], "BIN", 0x2000);
        }, outputPath: output, validate: session =>
        {
            Assert.NotSame(editing, session);
            Assert.True(session.Info.IsReadOnly);
            Assert.Equal("UPDATED\r"u8.ToArray(), session.ReadFile("README"));
            Assert.Equal(new byte[] { 0xa9, 0x01, 0x60 }, session.ReadFile("NEW"));
            Assert.Equal(0x2000, session.GetEntry("NEW").AuxType);
            Assert.Equal(untouched, session.ReadFile("HELLO.BIN", raw: true));
            validated = true;
        });

        Assert.True(validated);
        Assert.Equal(output, result.OutputPath);
        Assert.Null(result.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(input));
        using DiskSession reopened = DiskSession.Open(output);
        Assert.Equal(untouched, reopened.ReadFile("HELLO.BIN", raw: true));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, ".*.a2-*"));
    }

    [Fact]
    public void Edit_InPlace_KeepsOriginalImageBackup()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(input);

        ImageWriteResult result = DiskEditor.Edit(input,
            session => session.Delete("README"), inPlace: true);

        Assert.NotNull(result.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(result.BackupPath));
        using DiskSession reopened = DiskSession.Open(input);
        Assert.DoesNotContain(reopened.List(), entry => entry.Path == "README");
    }

    [Theory]
    [InlineData(false, "edit")]
    [InlineData(true, "edit")]
    [InlineData(false, "postcondition")]
    [InlineData(true, "postcondition")]
    [InlineData(false, "read-only")]
    [InlineData(true, "read-only")]
    public void Edit_CallbackFailure_PreservesInputAndExistingDestination(bool inPlace, string failure)
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(input);
        string output = workspace.NewPath("existing.do");
        File.WriteAllBytes(output, [1, 2, 3]);

        Assert.Throws<DiskException>(() => DiskEditor.Edit(input, session =>
        {
            session.Delete("README");
            if (failure == "edit") throw new DiskException("test.edit", "Reject edit.");
        }, outputPath: inPlace ? null : output, inPlace: inPlace, overwrite: true, validate: session =>
        {
            if (failure == "read-only") session.Add("ILLEGAL", [1], "BIN", 0x2000);
            if (!session.List().Any(entry => entry.Path == "README"))
                throw new DiskException("test.postcondition", "README must still exist.");
        }));

        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.bak"));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, ".*.a2-*"));
    }

    [Fact]
    public void Edit_DamagedFilesystemAfterRawBootWrite_RefusesCommit()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("prodos.po");
        DiskSession.Create(input, "prodos", order: "prodos");
        byte[] original = File.ReadAllBytes(input);
        string output = workspace.NewPath("edited.po");

        Assert.Throws<DiskException>(() => DiskEditor.Edit(input,
            session => session.WriteBootSectors(new byte[4096]), outputPath: output));

        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, ".*.a2-*"));
    }

    [Fact]
    public void Edit_CancellationAfterPostconditions_RefusesCommitAndBackup()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(input);
        using CancellationTokenSource cancellation = new();

        Assert.Throws<OperationCanceledException>(() => DiskEditor.Edit(input,
            session => session.Delete("README"), inPlace: true,
            validate: _ => cancellation.Cancel(), cancellationToken: cancellation.Token));

        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.bak"));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, ".*.a2-*"));
    }
}
