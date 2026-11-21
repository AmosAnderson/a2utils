// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace A2Utils.Core.Backends;

/// <summary>Read-only view of a native NuFX archive. Record indices are one-based and stable within a snapshot.</summary>
public sealed class ShrinkItArchive : IDisposable
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private const int MaxRecords = 16384;
    private static ReadOnlySpan<byte> Signature => [0x4e, 0xf5, 0x46, 0xe9, 0x6c, 0xe5];
    private static readonly (string Name, FilePart Part)[] PartTypes =
        [("data", FilePart.DataFork), ("resource", FilePart.RsrcFork), ("disk", FilePart.DiskImage)];

    private readonly ArchiveInput _source;
    private readonly NuFX _archive;
    private readonly IFileEntry[] _records;

    public ShrinkItListing Listing { get; }

    private ShrinkItArchive(string path, byte[] bytes, ArchiveInput source, NuFX archive)
    {
        _source = source;
        _archive = archive;
        _records = archive.ToArray();
        var entries = _records.Select((record, i) =>
        {
            List<ShrinkItPart> parts = [];
            foreach (var (name, part) in PartTypes)
            {
                if (record.GetPartInfo(part, out long length, out long storedLength, out CompressionFormat compression))
                {
                    // DiskArc's historical disk-length calculation uses 32-bit arithmetic.
                    if (part == FilePart.DiskImage)
                        length = (long)((NuFX_FileEntry)record).ExtraType * 512;
                    parts.Add(new(name, length, storedLength, compression.ToString()));
                }
                else if (!record.IsDiskImage && !record.IsDamaged && part != FilePart.DiskImage)
                {
                    // GS/ShrinkIt may omit the thread for an empty fork. DiskArc uses the
                    // original storage type to distinguish an omitted fork from an absent one.
                    try
                    {
                        using var empty = archive.OpenPart(record, part);
                        parts.Add(new(name, 0, 0, nameof(CompressionFormat.Uncompressed)));
                    }
                    catch (FileNotFoundException) { }
                }
            }
            return new ShrinkItEntry(i + 1, record.FileName, record.FileType, record.AuxType, record.Access,
                record.IsDamaged || record.IsDubious, parts.AsReadOnly());
        }).ToArray();
        var diagnostics = archive.Notes.GetNotes().Select(note => new DiskDiagnostic(
            "archive." + note.Type.ToString().ToLowerInvariant(), note.Type.ToString().ToLowerInvariant(), note.Text)).ToArray();
        Listing = new(Path.GetFullPath(path), ProgramFiles.Hash(bytes), Array.AsReadOnly(entries), diagnostics);
    }

    public static ShrinkItArchive Open(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = ProgramFiles.ReadBytes(path, MaxBytes, cancellationToken);
        if (!bytes.AsSpan().StartsWith(Signature))
            throw new DiskException("archive.unsupported", "Expected a native ShrinkIt (NuFX) archive.", 3);
        if (bytes.Length < 48)
            throw new DiskException("archive.invalid", "Truncated NuFX master header.", 4);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) > MaxRecords)
            throw new DiskException("archive.too_many_records", $"Archive exceeds {MaxRecords} records.", 3);

        var source = new ArchiveInput(bytes);
        NuFX? archive = null;
        try
        {
            archive = NuFX.OpenArchive(source, new AppHook(new SimpleMessageLog()));
            if (archive.IsDubious || archive.Notes.ErrorCount != 0)
            {
                string reason = string.Join(" ", archive.Notes.GetNotes().Select(n => n.Text).TakeLast(3));
                throw new DiskException("archive.invalid", $"Invalid NuFX archive. {reason}", 4);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(path, bytes, source, archive);
        }
        catch (Exception ex)
        {
            archive?.Dispose();
            source.Dispose();
            if (ex is IOException or InvalidDataException or ArgumentException or OverflowException or NotSupportedException)
                throw new DiskException("archive.invalid", $"Invalid NuFX archive. {ex.Message}", 4, ex);
            throw;
        }
    }

    public ShrinkItExtraction Extract(string destination, string? name = null, int? index = null,
        string? part = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ImageTransactions.EnsureDistinctPaths(Listing.ArchivePath, destination);
        var entry = Select(name, index, disksOnly: false);
        part ??= entry.Parts.Any(p => p.Name == "disk") ? "disk" : "data";
        byte[] bytes = ReadPart(entry, part, MaxBytes, cancellationToken);
        string hash = ProgramFiles.Hash(bytes);
        ImageWriteResult result = ImageTransactions.Create(destination, overwrite, temporary =>
        {
            using FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
        }, temporary =>
        {
            using FileStream stream = File.OpenRead(temporary);
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != hash)
                throw new DiskException("archive.output_mismatch", "Extracted file differs from the decoded archive member.", 4);
        }, cancellationToken);
        return new(Listing.ArchivePath, entry.Index, entry.Name, part, result.OutputPath, bytes.Length, hash);
    }

    public ShrinkItDiskCatalog Catalog(string? name = null, int? index = null, bool recursive = false,
        string? inputOrder = null, string? inputFs = null, CancellationToken cancellationToken = default)
    {
        var entry = Select(name, index, disksOnly: true);
        byte[] bytes = ReadPart(entry, "disk", DiskSession.MaximumImageFileSize, cancellationToken);
        var directory = Directory.CreateTempSubdirectory("a2utils-shrinkit-");
        try
        {
            string path = Path.Combine(HostFiles.ResolvePhysicalDirectory(directory.FullName), "image.po");
            using (FileStream staged = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                staged.Write(bytes);
            using var disk = DiskSession.Open(path, inputOrder ?? "prodos", inputFs);
            DiskInfo info = disk.Info;
            var entries = disk.List(recursive: recursive);
            cancellationToken.ThrowIfCancellationRequested();
            return new(Listing.ArchivePath, entry.Index, entry.Name, ProgramFiles.Hash(bytes), info.FileSystem,
                info.Order, info.VolumeName, entries, Listing.Diagnostics.Concat(info.Diagnostics).ToArray());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private ShrinkItEntry Select(string? name, int? index, bool disksOnly)
    {
        if (name is not null && index.HasValue)
            throw new DiskException("archive.invalid_selector", "Specify an archive entry name or a one-based index.", 2);
        IEnumerable<ShrinkItEntry> matches = Listing.Entries;
        if (index.HasValue)
            matches = matches.Where(e => e.Index == index);
        else if (name is not null)
            matches = matches.Where(e => e.Name.Equals(name, StringComparison.Ordinal));
        else if (disksOnly)
            matches = matches.Where(e => e.Parts.Any(p => p.Name == "disk"));
        var found = matches.Take(2).ToArray();
        if (found.Length == 0)
            throw new DiskException("archive.entry_not_found", "No matching archive entry.", 2);
        if (found.Length > 1)
            throw new DiskException("archive.ambiguous_entry", "Multiple entries match. Select one using its index from archive ls.", 2);
        if (found[0].IsDubious)
            throw new DiskException("archive.invalid_entry", $"Archive record {found[0].Index} has structural errors.", 4);
        return found[0];
    }

    private byte[] ReadPart(ShrinkItEntry entry, string name, int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var part = entry.Parts.FirstOrDefault(p => p.Name == name)
            ?? throw new DiskException("archive.part_not_found", $"Entry {entry.Index} has no '{name}' part.", 2);
        if (part.Length < 0 || part.Length > maximum)
            throw new DiskException("archive.part_too_large", $"The {name} part exceeds the {maximum}-byte limit.", 3);
        if (part.Compression is not ("Uncompressed" or "Squeeze" or "NuLZW1" or "NuLZW2" or "LZC12" or "LZC16"))
            throw new DiskException("archive.unsupported_compression", $"Unsupported NuFX compression: {part.Compression}.", 3);
        FilePart kind = PartTypes.First(p => p.Name == name).Part;
        lock (_archive)
        {
            ObjectDisposedException.ThrowIf(!_archive.IsValid, this);
            try
            {
                using var stream = _archive.OpenPart(_records[entry.Index - 1], kind);
                // NuFX positions the source at the thread start. Isolate codecs that read ahead.
                _source.ReadEnd = _source.Position + part.StoredLength;
                byte[] bytes = new byte[(int)part.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = stream.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                    if (read == 0)
                        throw new InvalidDataException("The decoded part ended before its declared length.");
                    offset += read;
                }
                if (stream.ReadByte() != -1)
                    throw new InvalidDataException("The decoded part exceeds its declared length.");
                return bytes;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                throw new DiskException("archive.invalid_part", $"Cannot decode record {entry.Index} ({name}). {ex.Message}", 4, ex);
            }
            finally
            {
                _source.ReadEnd = _source.Length;
            }
        }
    }

    private sealed class ArchiveInput(byte[] data) : MemoryStream(data, writable: false)
    {
        public long ReadEnd { get; set; } = data.Length;
        private int Available(int count) => (int)Math.Clamp(ReadEnd - Position, 0, count);
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Available(count));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Available(buffer.Length)]);
        public override int ReadByte() => Position < ReadEnd ? base.ReadByte() : -1;
    }

    public void Dispose()
    {
        lock (_archive)
        {
            _archive.Dispose();
            _source.Dispose();
        }
    }
}
