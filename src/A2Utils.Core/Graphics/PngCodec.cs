// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Buffers.Binary;
using System.IO.Compression;

namespace A2Utils.Core.Graphics;

public sealed record RasterImage(int Width, int Height, byte[] Rgb);

/// <summary>Bounded static PNG codec. Supports noninterlaced 8-bit RGB/RGBA/gray and 1/2/4/8-bit indexed/gray inputs.</summary>
public static class PngCodec
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static RasterImage Decode(ReadOnlySpan<byte> png)
    {
        if (!png.StartsWith(Signature) || png.Length > 32 * 1024 * 1024) throw Invalid("Invalid or oversized PNG.");
        int offset = 8, width = 0, height = 0, depth = 0, channels = 0, color = -1;
        byte[]? palette = null, transparency = null;
        bool sawData = false, dataEnded = false, ended = false;
        using MemoryStream compressed = new();
        while (offset < png.Length)
        {
            if (png.Length - offset < 12) throw Invalid("Truncated chunk.");
            uint size = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (size > png.Length - offset - 12) throw Invalid("Chunk exceeds input.");
            int length = (int)size;
            ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
            if (type.IndexOfAnyExceptInRange((byte)'A', (byte)'z') >= 0 || type.ToArray().Any(value => value is > (byte)'Z' and < (byte)'a'))
                throw Invalid("Invalid chunk type.");
            ReadOnlySpan<byte> data = png.Slice(offset + 8, length);
            if (Crc(png.Slice(offset + 4, length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8 + length)..]))
                throw Invalid("PNG chunk checksum mismatch.");
            if (offset == 8 && !type.SequenceEqual("IHDR"u8)) throw Invalid("IHDR must be first.");
            if (type.SequenceEqual("IHDR"u8))
            {
                if (width != 0 || length != 13) throw Invalid("Invalid IHDR.");
                uint w = BinaryPrimitives.ReadUInt32BigEndian(data), h = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                if (w is < 1 or > 2048 || h is < 1 or > 2048) throw Invalid("PNG dimensions must be 1..2048.");
                width = (int)w; height = (int)h; depth = data[8]; color = data[9];
                channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                if (channels == 0 || depth != 8 && !(color is 0 or 3 && depth is 1 or 2 or 4) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                    throw new DiskException("png.unsupported", "Use a noninterlaced 8-bit PNG, or 1/2/4-bit grayscale/indexed PNG.", 3);
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (palette is not null || sawData || color is 0 or 4 || length is < 3 or > 768 || length % 3 != 0)
                    throw Invalid("Invalid palette.");
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                if (transparency is not null || sawData || color is 4 or 6 ||
                    color == 0 && length != 2 || color == 2 && length != 6 ||
                    color == 3 && (palette is null || length > palette.Length / 3)) throw Invalid("Invalid transparency chunk.");
                transparency = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (dataEnded || color == 3 && palette is null) throw Invalid("Invalid image data ordering.");
                sawData = true;
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !sawData || offset + 12 != png.Length) throw Invalid("Invalid image end.");
                ended = true;
            }
            else
            {
                if (type.SequenceEqual("acTL"u8)) throw new DiskException("png.animation", "Animated PNG inputs are unsupported.", 3);
                if ((type[0] & 0x20) == 0) throw new DiskException("png.critical_chunk", "Unknown critical PNG chunk.", 3);
                if (sawData) dataEnded = true;
            }
            offset += length + 12;
        }
        if (!ended) throw Invalid("Missing IEND.");
        int stride = (width * channels * depth + 7) / 8;
        byte[] filtered = new byte[(stride + 1) * height];
        byte[] zlibBytes = compressed.GetBuffer();
        int compressedLength = checked((int)compressed.Length);
        if (compressedLength < 6 || (zlibBytes[0] & 15) != 8 || zlibBytes[0] >> 4 > 7 ||
            (zlibBytes[1] & 0x20) != 0 || (zlibBytes[0] * 256 + zlibBytes[1]) % 31 != 0)
            throw Invalid("Invalid PNG zlib header.");
        try
        {
            using CompressedInputStream input = new(zlibBytes, compressedLength);
            using ZLibStream zlib = new(input, CompressionMode.Decompress, true);
            zlib.ReadExactly(filtered);
            if (zlib.ReadByte() != -1) throw Invalid("Inflated data exceeds declared dimensions.");
            if (input.Position != input.Length) throw Invalid("Trailing data follows the PNG zlib stream.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            throw new DiskException("png.invalid", "Invalid or truncated PNG compression stream.", 4, exception);
        }
        int bpp = Math.Max(1, channels * depth / 8);
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = filtered[y * (stride + 1)];
            if (filter > 4) throw Invalid("Invalid PNG filter.");
            for (int x = 0; x < stride; x++)
            {
                int left = x >= bpp ? pixels[y * stride + x - bpp] : 0;
                int up = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                int corner = y > 0 && x >= bpp ? pixels[(y - 1) * stride + x - bpp] : 0;
                int predictor = filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, corner) };
                pixels[y * stride + x] = (byte)(filtered[y * (stride + 1) + x + 1] + predictor);
            }
        }
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int source = y * stride + x * channels;
                int destination = (y * width + x) * 3;
                int value = depth == 8 ? pixels[source] : (pixels[y * stride + x * depth / 8] >> (8 - depth - x * depth % 8)) & ((1 << depth) - 1);
                int r, g, b, alpha = 255;
                if (color == 3)
                {
                    if (value >= palette!.Length / 3) throw Invalid("Palette index is out of range.");
                    r = palette[value * 3]; g = palette[value * 3 + 1]; b = palette[value * 3 + 2];
                    if (transparency is not null && value < transparency.Length) alpha = transparency[value];
                }
                else if (color is 0 or 4)
                {
                    r = g = b = value * 255 / ((1 << depth) - 1);
                    if (color == 4) alpha = pixels[source + 1];
                    else if (transparency is not null && value == BinaryPrimitives.ReadUInt16BigEndian(transparency)) alpha = 0;
                }
                else
                {
                    r = pixels[source]; g = pixels[source + 1]; b = pixels[source + 2];
                    if (color == 6) alpha = pixels[source + 3];
                    else if (transparency is not null && r == BinaryPrimitives.ReadUInt16BigEndian(transparency) &&
                        g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) && b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4))) alpha = 0;
                }
                rgb[destination] = (byte)(r * alpha / 255);
                rgb[destination + 1] = (byte)(g * alpha / 255);
                rgb[destination + 2] = (byte)(b * alpha / 255);
            }
        return new(width, height, rgb);
    }

    public static byte[] Encode(RasterImage image)
    {
        if (image.Width is < 1 or > 2048 || image.Height is < 1 or > 2048 || image.Rgb.Length != image.Width * image.Height * 3)
            throw new ArgumentException("Invalid RGB image dimensions.", nameof(image));
        using MemoryStream output = new();
        output.Write(Signature);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; header[9] = 2;
        Chunk(output, "IHDR"u8, header);
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, true))
        {
            for (int y = 0; y < image.Height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(image.Rgb.AsSpan(y * image.Width * 3, image.Width * 3));
            }
        }
        Chunk(output, "IDAT"u8, compressed.ToArray());
        Chunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        byte[] chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        type.CopyTo(chunk.AsSpan(4)); data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc(chunk.AsSpan(4, data.Length + 4)));
        output.Write(chunk);
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    private static int Paeth(int left, int up, int corner)
    {
        int p = left + up - corner;
        int a = Math.Abs(p - left), b = Math.Abs(p - up), c = Math.Abs(p - corner);
        return a <= b && a <= c ? left : b <= c ? up : corner;
    }

    private sealed class CompressedInputStream(byte[] bytes, int length) : MemoryStream(bytes, 0, length, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, ReadLimit(count));

        public override int Read(Span<byte> buffer)
            => base.Read(buffer[..ReadLimit(buffer.Length)]);

        private int ReadLimit(int requested)
        {
            if (requested == 0) return 0;
            long remaining = Length - Position;
            // A complete zlib stream finishes without asking its input for EOF. .NET otherwise
            // permits a missing terminator/checksum unless a process-wide switch is enabled.
            if (remaining == 0) throw new InvalidDataException("Truncated zlib stream.");
            // Keep the last four bytes out of inflater read-ahead. This lets Position prove
            // that its checked Adler-32 footer ends exactly at the end of the IDAT payload.
            return remaining <= 4 ? 1 : (int)Math.Min(requested, remaining - 4);
        }
    }

    private static DiskException Invalid(string message) => new("png.invalid", message, 4);
}
