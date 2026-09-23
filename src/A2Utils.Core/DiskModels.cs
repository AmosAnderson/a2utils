// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

namespace A2Utils.Core;

public sealed record DiskDiagnostic(string Code, string Severity, string Message);

public sealed record SparseExtent(long Offset, long Length);

/// <summary>IsReadOnly describes the current session; ImageWriteProtected is the container flag.
/// FreeBytes is null when no supported filesystem was recognized.</summary>
public sealed record DiskInfo(
    string Path, string Container, string Order, string FileSystem, long SizeBytes,
    long? FreeBytes, string VolumeName, int? VolumeNumber, bool IsReadOnly,
    bool IsDubious, IReadOnlyList<DiskDiagnostic> Diagnostics,
    bool ImageWriteProtected = false);

public sealed record DiskEntry(
    string Path, string Name, bool IsDirectory, string Type, byte FileType,
    ushort AuxType, byte Access, bool IsLocked, long Length, long StoredLength,
    long StorageSize, byte[] RawName, DateTime? Created, DateTime? Modified,
    bool IsDubious, bool HasResourceFork);

/// <summary>A stable diagnostic and CLI exit status without engine-specific types.</summary>
public sealed class DiskException(string code, string message, int exitCode = 6,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
    public IReadOnlyList<Programs.ProgramDiagnostic> Diagnostics { get; init; } = [];
}
