using System.Buffers.Binary;
using A2Utils.Core.Backends;
using DiskArc;
using static DiskArc.Defs;

namespace A2Utils.Core.Operations;

/// <summary>Changes the storage layout without interpreting or reallocating filesystem data.</summary>
public static class ImageConverter
{
    private const int FloppyLength = 35 * 16 * 256;
    private const int MaximumPayloadLength = 65536 * 512;

    public static ImageWriteResult Convert(
        string inputPath,
        string outputPath,
        string container,
        string order,
        string? inputOrder = null,
        bool overwrite = false,
        bool allowMetadataLoss = false,
        CancellationToken cancellationToken = default)
    {
        string targetContainer = NormalizeContainer(container);
        SectorOrder targetOrder = ParseOrder(order);
        if (inputOrder is not null)
        {
            _ = ParseOrder(inputOrder);
        }

        byte[]? expectedPayload = null;
        return ImageTransactions.Write(inputPath, outputPath, false, overwrite, temporary =>
        {
            Layout layout = ReadLayout(temporary, inputOrder);
            ValidateGeometry(layout.PayloadLength, targetOrder);
            if (layout.Container == "2mg" && targetContainer == "raw" && !allowMetadataLoss)
            {
                throw new DiskException("convert.metadata_loss", "Raw output discards the 2IMG creator signature, flags, comments, creator data, and any extra container bytes. Specify allow-metadata-loss explicitly.");
            }

            byte[] payload = new byte[layout.PayloadLength];
            using (FileStream source = File.OpenRead(temporary))
            {
                source.Position = layout.PayloadOffset;
                source.ReadExactly(payload);
            }

            expectedPayload = Reorder(payload, layout.Order, targetOrder);
            cancellationToken.ThrowIfCancellationRequested();
            if (targetContainer == "raw")
            {
                File.WriteAllBytes(temporary, expectedPayload);
            }
            else if (layout.Container == "raw")
            {
                byte[] header = CreateHeader(targetOrder, layout.PayloadLength);
                using FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
                output.Write(header);
                output.Write(expectedPayload);
            }
            else
            {
                // Retain every existing byte outside the payload, except the layout descriptor.
                using FileStream output = new(temporary, FileMode.Open, FileAccess.Write, FileShare.None);
                output.Position = layout.PayloadOffset;
                output.Write(expectedPayload);
                Span<byte> format = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(format, targetOrder == SectorOrder.DOS_Sector ? 0u : 1u);
                output.Position = 12;
                output.Write(format);
                if (targetOrder == SectorOrder.ProDOS_Block && layout.BlockCount == 0)
                {
                    // DOS permits zero here; ProDOS-order containers require a block count.
                    BinaryPrimitives.WriteUInt32LittleEndian(format, (uint)(layout.PayloadLength / 512));
                    output.Position = 20;
                    output.Write(format);
                }
            }
        }, temporary =>
        {
            Layout result = ReadLayout(temporary, order);
            if (result.Container != targetContainer || result.Order != targetOrder)
            {
                throw new DiskException("convert.validation_failed", "The converted container or sector order is inconsistent.", 4);
            }

            byte[] payload = new byte[result.PayloadLength];
            using FileStream stream = File.OpenRead(temporary);
            stream.Position = result.PayloadOffset;
            stream.ReadExactly(payload);
            if (!payload.AsSpan().SequenceEqual(expectedPayload))
            {
                throw new DiskException("convert.validation_failed", "The converted image did not preserve its expected sector data.", 4);
            }
        }, cancellationToken);
    }

    private static byte[] Reorder(byte[] payload, SectorOrder sourceOrder, SectorOrder targetOrder)
    {
        if (sourceOrder == targetOrder)
        {
            return payload;
        }

        byte[] result = new byte[payload.Length];
        using MemoryStream sourceStream = new(payload, writable: false);
        using MemoryStream targetStream = new(result, writable: true);
        GeneralChunkAccess source = new(sourceStream, 0, 35, 16, sourceOrder);
        GeneralChunkAccess target = new(targetStream, 0, 35, 16, targetOrder);
        byte[] sector = new byte[256];
        for (uint track = 0; track < 35; track++)
        {
            for (uint index = 0; index < 16; index++)
            {
                source.ReadSector(track, index, sector, 0);
                target.WriteSector(track, index, sector, 0);
            }
        }

        return result;
    }

    private static Layout ReadLayout(string path, string? inputOrder)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] header = new byte[64];
        if (stream.Length < header.Length)
        {
            throw new DiskException("image.truncated", "The image is too short to contain a supported disk.", 4);
        }

        stream.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("2IMG"u8))
        {
            ValidateGeometry(stream.Length, SectorOrder.ProDOS_Block);

            if (inputOrder is not null)
            {
                SectorOrder explicitOrder = ParseOrder(inputOrder);
                ValidateGeometry(stream.Length, explicitOrder);
                return new Layout("raw", explicitOrder, 0, (uint)(stream.Length / 512), (int)stream.Length);
            }

            using DiskSession session = DiskSession.Open(path);
            SectorOrder detectedOrder = ParseOrder(session.Info.Order);
            ValidateGeometry(stream.Length, detectedOrder);
            return new Layout("raw", detectedOrder, 0, (uint)(stream.Length / 512), (int)stream.Length);
        }

        ushort headerLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
        uint format = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28));
        if (headerLength < 64 || offset < headerLength || (long)offset + length > stream.Length)
        {
            throw new DiskException("image.invalid_header", "The 2IMG header contains an invalid payload range.", 4);
        }

        if (version > 1 || format > 1)
        {
            throw new DiskException("image.container_unsupported", "Only version 0/1 sector-data 2IMG containers are supported for conversion.", 3);
        }

        SectorOrder order = format == 0 ? SectorOrder.DOS_Sector : SectorOrder.ProDOS_Block;
        ValidateGeometry(length, order);

        if ((blocks != 0 && blocks != length / 512) || (format == 1 && blocks == 0))
        {
            throw new DiskException("image.invalid_header", "The 2IMG block count does not match its payload.", 4);
        }

        (long Start, long End)? comment = CheckMetadata(header, 32, offset + (long)length, stream.Length);
        (long Start, long End)? creator = CheckMetadata(header, 40, offset + (long)length, stream.Length);
        if (comment.HasValue && creator.HasValue && comment.Value.Start < creator.Value.End && creator.Value.Start < comment.Value.End)
        {
            throw new DiskException("image.invalid_header", "The 2IMG comment and creator data overlap.", 4);
        }

        if (inputOrder is not null && ParseOrder(inputOrder) != order)
        {
            throw new DiskException("image.order_conflict", "The requested input order conflicts with the 2IMG header.", 3);
        }

        return new Layout("2mg", order, offset, blocks, (int)length);
    }

    private static void ValidateGeometry(long length, SectorOrder order)
    {
        if (length < FloppyLength || length > MaximumPayloadLength || length % 512 != 0)
        {
            throw new DiskException("image.geometry_unsupported", "Conversion supports contiguous images from 280 through 65,536 blocks of 512 bytes.", 3);
        }

        if (order == SectorOrder.DOS_Sector && length != FloppyLength)
        {
            throw new DiskException("image.geometry_unsupported", "DOS sector order requires a 35-track, 16-sector, 140 KiB image.", 3);
        }
    }

    private static (long Start, long End)? CheckMetadata(byte[] header, int fieldOffset, long payloadEnd, long fileLength)
    {
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(fieldOffset));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(fieldOffset + 4));
        if (offset == 0 && length == 0)
        {
            return null;
        }

        long end = offset + (long)length;
        if (offset == 0 || length == 0 || offset < payloadEnd || end > fileLength)
        {
            throw new DiskException("image.invalid_header", "The 2IMG metadata has an invalid or overlapping range.", 4);
        }

        return (offset, end);
    }

    private static byte[] CreateHeader(SectorOrder order, int payloadLength)
    {
        byte[] header = new byte[64];
        "2IMG"u8.CopyTo(header);
        "A2UT"u8.CopyTo(header.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), order == SectorOrder.DOS_Sector ? 0u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)(payloadLength / 512));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)payloadLength);
        return header;
    }

    private static string NormalizeContainer(string container) => container.ToLowerInvariant() switch
    {
        "raw" => "raw",
        "2mg" or "2img" => "2mg",
        _ => throw new DiskException("image.container_unsupported", "The container must be raw or 2mg.", 3),
    };

    private static SectorOrder ParseOrder(string order) => order.ToLowerInvariant() switch
    {
        "dos" => SectorOrder.DOS_Sector,
        "prodos" => SectorOrder.ProDOS_Block,
        _ => throw new DiskException("image.order_unsupported", "The sector order must be dos or prodos.", 3),
    };

    private sealed record Layout(string Container, SectorOrder Order, long PayloadOffset, uint BlockCount, int PayloadLength);
}
