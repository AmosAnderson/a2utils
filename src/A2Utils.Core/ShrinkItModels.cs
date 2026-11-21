// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

namespace A2Utils.Core;

public sealed record ShrinkItPart(string Name, long Length, long StoredLength, string Compression);

public sealed record ShrinkItEntry(int Index, string Name, byte FileType, ushort AuxType, byte Access,
    bool IsDubious, IReadOnlyList<ShrinkItPart> Parts);

public sealed record ShrinkItListing(string ArchivePath, string Sha256, IReadOnlyList<ShrinkItEntry> Entries,
    IReadOnlyList<DiskDiagnostic> Diagnostics);

public sealed record ShrinkItExtraction(string ArchivePath, int Index, string Name, string Part, string OutputPath,
    long Length, string Sha256);

public sealed record ShrinkItDiskCatalog(string ArchivePath, int Index, string Name, string Sha256, string FileSystem,
    string Order, string VolumeName, IReadOnlyList<DiskEntry> Entries, IReadOnlyList<DiskDiagnostic> Diagnostics);
