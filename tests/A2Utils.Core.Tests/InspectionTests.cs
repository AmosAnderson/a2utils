// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using A2Utils.Core.Backends;

namespace A2Utils.Core.Tests;

public sealed class InspectionTests
{
    [Fact]
    public void Inspect_UnknownRawImage_ReportsUnknownFilesystemAndOrder()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("blank.dsk");
        File.WriteAllBytes(path, new byte[143360]);

        DiskInfo info = DiskSession.Inspect(path);

        Assert.Equal("unknown", info.FileSystem);
        Assert.Equal("unknown", info.Order);
        Assert.Null(info.FreeBytes);
        Assert.Equal(143360, info.SizeBytes);
        Assert.Contains(info.Diagnostics, diagnostic => diagnostic.Code == "unrecognized_filesystem");
        Assert.Throws<DiskException>(() => DiskSession.Open(path));
    }

    [Fact]
    public void Inspect_UnknownRawWithExplicitOrder_PreservesOverride()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("blank.dsk");
        File.WriteAllBytes(path, new byte[143360]);

        Assert.Equal("prodos", DiskSession.Inspect(path, "prodos").Order);
        Assert.Throws<DiskException>(() => DiskSession.Inspect(path, inputFs: "dos33"));
    }

    [Fact]
    public void Inspect_UnknownTwoImg_UsesDeclaredOrderAndProtection()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("blank.2mg");
        byte[] bytes = new byte[64 + 143360];
        "2IMG"u8.CopyTo(bytes);
        "A2UT"u8.CopyTo(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x80000000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 280);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 143360);
        File.WriteAllBytes(path, bytes);

        DiskInfo info = DiskSession.Inspect(path);

        Assert.Equal("2mg", info.Container);
        Assert.Equal("unknown", info.FileSystem);
        Assert.Equal("prodos", info.Order);
        Assert.True(info.ImageWriteProtected);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Inspect_TruncatedRawImage_DoesNotMaskCorruptionAsUnknown()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("truncated.dsk");
        File.WriteAllBytes(path, new byte[143359]);

        DiskException error = Assert.Throws<DiskException>(() => DiskSession.Inspect(path));

        Assert.Equal(4, error.ExitCode);
    }

    [Fact]
    public void ReadFile_DosNameWithSlashAndDotSegments_PreservesLiteralName()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] bytes = File.ReadAllBytes(path);
        const int catalogName = (17 * 16 + 15) * 256 + 0x0b + 3;
        bytes.AsSpan(catalogName, 30).Fill(0xa0);
        const string name = "A/../FILE/";
        for (int index = 0; index < name.Length; index++) bytes[catalogName + index] = (byte)(name[index] | 0x80);
        File.WriteAllBytes(path, bytes);

        using DiskSession session = DiskSession.Open(path);

        Assert.Contains(session.List(), entry => entry.Name == name);
        Assert.Equal(new byte[] { 0x00, 0x7f, 0x80, 0xff, 0x0d }, session.ReadFile(name));
    }
}
