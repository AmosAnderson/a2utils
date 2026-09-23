// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using A2Utils.Core.Backends;

namespace A2Utils.Core.Execution;

public static partial class MameAdapter
{
    internal static bool IsStorageDevice(string profile, string device) => profile switch
    {
        "floppy" => device is "flop1" or "flop2",
        "cffa2" => device is "flop1" or "flop2" or "hard1" or "hard2",
        _ => false
    };

    private static void ValidateStorage(ExecutionSpec spec)
    {
        if (spec.StorageProfile is not ("floppy" or "cffa2"))
            throw StorageError("profile", "storageProfile must be floppy or cffa2.");
        if (spec.StorageProfile == "cffa2" && spec.Machine is not ("apple2e" or "apple2ee"))
            throw StorageError("machine", "The CFFA2 slot-7 profile requires apple2e or apple2ee.");
        foreach (ExecutionDisk disk in spec.Disks)
            if (disk is { Device: "hard1" or "hard2" } && (disk.InputOrder is not (null or "prodos") || disk.InputFileSystem is not (null or "prodos")))
                throw StorageError("format", "CFFA2 hard disks require ProDOS block order and a single ProDOS volume.");
    }

    internal static string StorageCopyExtension(ExecutionDisk disk, byte[] snapshot)
    {
        if (disk.Device is not ("hard1" or "hard2")) return Path.GetExtension(disk.Image);
        return snapshot.AsSpan().StartsWith("2IMG"u8) ? ".2mg" : ".hdv";
    }

    internal static void ValidateStorageCopy(ExecutionDisk disk, string copy, byte[] snapshot)
    {
        bool hard = disk.Device is "hard1" or "hard2";
        long payloadLength = snapshot.Length;
        if (snapshot.AsSpan().StartsWith("2IMG"u8))
        {
            if (snapshot.Length < 64) throw StorageError("format", "The 2IMG header is truncated.");
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(24));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(28));
            payloadLength = length;
            if (hard && (BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(8)) < 64 ||
                BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(10)) > 1 ||
                BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(12)) != 1 ||
                offset < BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(8)) ||
                (long)offset + length != snapshot.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(20)) != length / 512))
                throw StorageError("format", "CFFA2 requires a complete block-order 2IMG payload with no trailing metadata or extra bytes.");
        }
        if (!hard)
        {
            if (payloadLength > 143360) throw StorageError("geometry", "Images larger than a 140 KiB floppy require the cffa2 profile and hard1/hard2.");
            return;
        }
        if (snapshot.AsSpan().StartsWith("MComprHD"u8)) throw StorageError("format", "CHD images are outside the supported CFFA2 single-volume profile.");
        if (payloadLength < 280 * 512 || payloadLength > 65535L * 512 || payloadLength % 512 != 0)
            throw StorageError("geometry", "CFFA2 images must contain 280..65535 complete 512-byte blocks.");
        try
        {
            using DiskSession session = DiskSession.Open(copy, "prodos", "prodos");
            DiskInfo info = session.Info;
            if (info.FileSystem != "prodos" || info.Order != "prodos" || info.SizeBytes != payloadLength || info.IsDubious ||
                session.Verify().Any(diagnostic => diagnostic.Severity == "error"))
                throw StorageError("format", "CFFA2 execution requires a structurally valid, unpartitioned ProDOS volume.");
        }
        catch (DiskException exception) when (!exception.Code.StartsWith("execution.storage_", StringComparison.Ordinal))
        {
            throw StorageError("format", "CFFA2 image validation failed: " + exception.Message);
        }
    }

    private static DiskException StorageError(string code, string message) => new("execution.storage_" + code, message, 2);
}
