// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using A2Utils.Core.Backends;
using static DiskArc.Defs;

namespace A2Utils.Core.Operations;

public sealed record DiskInspectionRange(long FileOffset, int PayloadOffset, int Block, int BlockOffset,
    int? Track, int? Sector, string Hex, string Ascii);

public sealed record DiskInspectionResult(string Image, string Container, string Order, long PayloadStart,
    int PayloadLength, long? RequestedFileOffset, IReadOnlyList<DiskInspectionRange> Ranges);

/// <summary>Reads a ProDOS block, DOS logical sector, or the unit containing a host-file offset.</summary>
public static class DiskInspection
{
    // For a 16-sector track the DOS logical and ProDOS block-order slot maps are inverses of themselves.
    private static ReadOnlySpan<int> OtherOrder => [0, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 15];

    public static DiskInspectionResult Read(string path, int? block = null, int? track = null, int? sector = null,
        long? fileOffset = null, string? inputOrder = null, string? inputFileSystem = null,
        CancellationToken cancellationToken = default)
    {
        bool bySector = track.HasValue || sector.HasValue;
        if (track.HasValue != sector.HasValue || (block.HasValue ? 1 : 0) + (bySector ? 1 : 0) + (fileOffset.HasValue ? 1 : 0) != 1)
            throw new DiskException("inspect.address", "Choose --block, --track with --sector, or --offset.", 2);
        DiskSession.ValidateImageFile(path, cancellationToken);
        using FileStream stream = File.OpenRead(path);
        ImageConverter.Layout layout = ImageConverter.ReadLayout(stream, inputOrder);
        bool floppy = layout.PayloadLength == 35 * 16 * 256;
        SectorOrder order = layout.Order;
        // Imaging tools also need to inspect broken filesystems. Probe only when the
        // caller requests filesystem validation or a raw floppy's order needs detection.
        if (inputFileSystem is not null || (floppy && order == SectorOrder.Unknown))
        {
            DiskInfo info = DiskSession.Inspect(path, inputOrder, inputFileSystem);
            order = info.Order switch
            {
                "dos" => SectorOrder.DOS_Sector,
                "prodos" => SectorOrder.ProDOS_Block,
                _ => throw new DiskException("inspect.order_required", "Specify --input-order dos or prodos for an unidentified raw floppy.", 2)
            };
        }
        bool dosOrder = order == SectorOrder.DOS_Sector;
        if (block is < 0 || block >= layout.PayloadLength / 512 ||
            bySector && (!floppy || track is < 0 or >= 35 || sector is < 0 or >= 16) ||
            fileOffset < layout.PayloadOffset || fileOffset >= layout.PayloadOffset + layout.PayloadLength)
            throw new DiskException("inspect.range", "The requested address is outside the sector payload or geometry.", 2);

        List<DiskInspectionRange> ranges = [];
        if (floppy)
        {
            if (block is { } b)
            {
                AddFloppySlot(b / 8, b % 8 * 2);
                AddFloppySlot(b / 8, b % 8 * 2 + 1);
            }
            else if (bySector)
                AddFloppySlot(track!.Value, OtherOrder[sector!.Value]);
            else
            {
                int slot = (int)((fileOffset!.Value - layout.PayloadOffset) / 256);
                AddFloppySlot(slot / 16, dosOrder ? OtherOrder[slot % 16] : slot % 16);
            }
        }
        else
        {
            int b = block ?? (int)((fileOffset!.Value - layout.PayloadOffset) / 512);
            AddRange(b * 512, 512, b, 0, null, null);
        }
        return new(Path.GetFullPath(path), layout.Container, dosOrder ? "dos" : "prodos", layout.PayloadOffset, layout.PayloadLength, fileOffset, ranges);

        void AddFloppySlot(int t, int prodosSlot)
        {
            int dosSector = OtherOrder[prodosSlot];
            int fileSlot = dosOrder ? dosSector : prodosSlot;
            AddRange((t * 16 + fileSlot) * 256, 256, t * 8 + prodosSlot / 2, prodosSlot % 2 * 256, t, dosSector);
        }

        void AddRange(int offset, int length, int b, int blockOffset, int? t, int? s)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = layout.PayloadOffset + offset;
            byte[] bytes = new byte[length];
            stream.ReadExactly(bytes);
            ranges.Add(new(layout.PayloadOffset + offset, offset, b, blockOffset, t, s,
                Convert.ToHexString(bytes), new string(bytes.Select(value => value is >= 32 and < 127 ? (char)value : '.').ToArray())));
        }
    }
}
