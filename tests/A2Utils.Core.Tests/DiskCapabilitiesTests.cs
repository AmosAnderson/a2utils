// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using A2Utils.Core.Backends;

namespace A2Utils.Core.Tests;

public sealed class DiskCapabilitiesTests
{
    [Theory]
    [InlineData("dos33", false)]
    [InlineData("prodos", true)]
    public void Inspect_ReadOnlySession_ReportsStructuralWriteEligibility(string fileSystem, bool directories)
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.img");
        DiskSession.Create(path, fileSystem);
        byte[] before = File.ReadAllBytes(path);
        DiskInfo info = DiskSession.Inspect(path);
        Assert.True(info.IsReadOnly);
        Assert.True(Operation(info, "edit").Supported);
        Assert.Equal(directories, Operation(info, "createDirectories").Supported);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Inspect_WriteProtectedContainer_ReportsReasonWithoutMutation()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.2mg");
        DiskSession.Create(path, "prodos", container: "2mg");
        byte[] bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x80000000);
        File.WriteAllBytes(path, bytes);
        DiskInfo info = DiskSession.Inspect(path);
        Assert.False(Operation(info, "edit").Supported);
        Assert.Contains("image_read_only", Operation(info, "edit").Reasons);
        Assert.True(Operation(info, "readFile").Supported);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Inspect_UnknownOrder_RequiresOverrideForConversion()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.NewPath("disk.dsk");
        File.WriteAllBytes(path, new byte[143360]);
        Assert.Contains("unknown_order", Operation(DiskSession.Inspect(path), "convert").Reasons);
        DiskInfo explicitOrder = DiskSession.Inspect(path, "dos");
        Assert.True(Operation(explicitOrder, "convert").Supported);
        Assert.Contains("unrecognized_filesystem", Operation(explicitOrder, "edit").Reasons);
        Assert.Empty(explicitOrder.Capabilities.MetadataFields);
    }

    [Fact]
    public void Inspect_DamagedAllocation_ReportsEditRefusal()
    {
        using FixtureWorkspace workspace = new();
        string path = workspace.CopyDisk();
        byte[] bytes = File.ReadAllBytes(path);
        bytes[(18 * 16 + 13) * 256 + 13] = 14;
        File.WriteAllBytes(path, bytes);
        DiskInfo info = DiskSession.Inspect(path);
        Assert.False(Operation(info, "edit").Supported);
        Assert.Contains("corrupt_image", Operation(info, "edit").Reasons);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static DiskOperationCapability Operation(DiskInfo info, string operation)
        => Assert.Single(info.Capabilities.Operations, item => item.Operation == operation);
}
