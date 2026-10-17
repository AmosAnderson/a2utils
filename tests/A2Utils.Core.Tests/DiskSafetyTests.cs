// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Diagnostics;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class DiskSafetyTests
{
    [Theory]
    [InlineData("prodos", "FILE", "0x0f", null)]
    [InlineData("prodos", "SUB", "TXT", null)]
    [InlineData("prodos", "/", null, 123)]
    [InlineData("dos33", "/", null, null)]
    [InlineData("dos33", "FILE", null, 123)]
    public void SetAttributes_UnrepresentableChange_RefusesBeforeUnlockingOrWriting(
        string fileSystem, string path, string? type, int? auxiliary)
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("image.dsk");
        DiskSession.Create(input, fileSystem);
        using (DiskSession setup = DiskSession.Open(input, writable: true))
        {
            setup.Add("FILE", "TEXT"u8.ToArray(), "TXT");
            setup.SetAttributes("FILE", locked: true);
            if (fileSystem == "prodos") setup.Mkdir("SUB");
        }
        byte[] original = File.ReadAllBytes(input);

        using (DiskSession session = DiskSession.Open(input, writable: true))
        {
            DiskException error = Assert.Throws<DiskException>(() => session.SetAttributes(path,
                locked: false, type: type, auxType: auxiliary is null ? null : (ushort)auxiliary));
            Assert.Contains(error.Code, new[] { "invalid_file_type", "unsupported_attributes" });
        }

        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public void Edit_OversizedImage_RefusesBeforeCopyOrCallbackAndPreservesDestination()
    {
        using FixtureWorkspace workspace = new();
        string input = workspace.NewPath("oversized.2mg");
        using (FileStream file = File.Create(input)) file.SetLength(DiskSession.MaximumImageFileSize + 1L);
        string output = workspace.NewPath("output.po");
        File.WriteAllBytes(output, [1, 2, 3]);
        bool called = false;

        Assert.Equal("image_too_large", Assert.Throws<DiskException>(() =>
            DiskSession.Open(input)).Code);
        Assert.Equal("image_too_large", Assert.Throws<DiskException>(() =>
            DiskEditor.Edit(input, _ => called = true, outputPath: output, overwrite: true)).Code);

        Assert.False(called);
        Assert.Equal(DiskSession.MaximumImageFileSize + 1L, new FileInfo(input).Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, ".*.a2-*"));
    }

    [Fact]
    public void Open_UnixSymbolicLinkToImage_ReadsTargetWhileEditingRefusesLink()
    {
        if (OperatingSystem.IsWindows()) return;
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        byte[] original = File.ReadAllBytes(source);
        string alias = workspace.NewPath("alias.do");
        File.CreateSymbolicLink(alias, source);

        using (DiskSession disk = DiskSession.Open(alias))
        {
            Assert.Equal("dos33", disk.Info.FileSystem);
            Assert.Equal(new byte[] { 0x00, 0x7f, 0x80, 0xff, 0x0d }, disk.ReadFile("HELLO.BIN"));
        }
        Assert.Equal("dos33", DiskSession.Inspect(alias).FileSystem);
        bool called = false;
        DiskException error = Assert.Throws<DiskException>(() =>
            DiskEditor.Edit(alias, _ => called = true, inPlace: true));

        Assert.Equal("write.link_refused", error.Code);
        Assert.False(called);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.bak"));
    }

    [Fact]
    public void Open_UnixSymbolicLinkToOversizedImage_RefusesTargetSize()
    {
        if (OperatingSystem.IsWindows()) return;
        using FixtureWorkspace workspace = new();
        string target = workspace.NewPath("oversized.2mg");
        using (FileStream file = File.Create(target)) file.SetLength(DiskSession.MaximumImageFileSize + 1L);
        string intermediate = workspace.NewPath("intermediate.2mg");
        string alias = workspace.NewPath("alias.2mg");
        File.CreateSymbolicLink(intermediate, target);
        File.CreateSymbolicLink(alias, intermediate);

        Assert.Equal("image_too_large", Assert.Throws<DiskException>(() => DiskSession.Open(alias)).Code);
        Assert.Equal("image_too_large", Assert.Throws<DiskException>(() => DiskSession.Inspect(alias)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_OversizedManifestOrPayload_RefusesBeforeWriting(bool payload)
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.CopyDisk();
        string extracted = workspace.NewPath("extracted");
        ExtractionManifest manifest;
        using (DiskSession session = DiskSession.Open(source)) manifest = FileTransfer.Extract(session, extracted);
        string manifestPath = Path.Combine(extracted, FileTransfer.ManifestName);
        string oversized = payload ? Path.Combine(extracted, manifest.Entries[0].HostFile!) : manifestPath;
        using (FileStream file = File.OpenWrite(oversized)) file.SetLength(32 * 1024 * 1024 + 1L);
        string input = workspace.NewPath("empty.do");
        DiskSession.Create(input, "dos33");
        byte[] original = File.ReadAllBytes(input);
        string output = workspace.NewPath("restored.do");

        DiskException error = Assert.Throws<DiskException>(() => DiskEditor.Edit(input,
            session => FileTransfer.Restore(session, manifestPath), outputPath: output));

        Assert.Equal("program.too_large", error.Code);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_UnixNamedPipeOrLink_RejectsInBoundedChildProcess(bool symbolicLink)
    {
        if (OperatingSystem.IsWindows()) return;
        using FixtureWorkspace workspace = new();
        string pipe = workspace.NewPath("pipe.dsk");
        using (Process create = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo")
        {
            UseShellExecute = false,
            ArgumentList = { pipe }
        })!)
        {
            await create.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, create.ExitCode);
        }
        string input = pipe;
        if (symbolicLink)
        {
            input = workspace.NewPath("alias.dsk");
            File.CreateSymbolicLink(input, pipe);
        }
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "A2Utils.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string host = Path.Combine(root.FullName, "tests", "A2Utils.ExecutionTestHost", "bin",
            configuration, "net10.0", "A2Utils.ExecutionTestHost");
        using Process process = Process.Start(new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "--probe-disk", input }
        })!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
