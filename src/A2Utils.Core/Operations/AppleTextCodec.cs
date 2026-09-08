using System.Text;

namespace A2Utils.Core.Operations;

/// <summary>
/// Converts text explicitly without platform code pages. Printable ASCII and tabs are preserved;
/// CR, LF, and CRLF normalize to one line ending. Other controls, NUL, DEL, and non-ASCII Unicode
/// are rejected. Host input must be valid UTF-8 and may begin with one UTF-8 BOM.
/// </summary>
public static class AppleTextCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>Clears the Apple text high bit and returns BOM-free UTF-8 with LF line endings.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        List<byte> output = new(data.Length);
        for (int index = 0; index < data.Length; index++)
        {
            byte value = (byte)(data[index] & 0x7f);
            if (value == '\r')
            {
                output.Add((byte)'\n');
                if (index + 1 < data.Length && (data[index + 1] & 0x7f) == '\n')
                {
                    index++;
                }
            }
            else if (value == '\n' || value == '\t' || value is >= 0x20 and <= 0x7e)
            {
                output.Add(value);
            }
            else
            {
                throw new DiskException("text.unsupported_character",
                    $"Apple text byte 0x{data[index]:X2} at offset {index} represents an unsupported control character.", 3);
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Encodes UTF-8 ASCII text with CR line endings: DOS 3.3 sets the high bit on every byte;
    /// ProDOS leaves it clear. No terminator, padding, or trailing newline is added.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> utf8, string fileSystem)
    {
        byte highBit = fileSystem.ToLowerInvariant() switch
        {
            "dos33" => 0x80,
            "prodos" => 0,
            _ => throw new DiskException("text.unsupported_filesystem",
                "Text import supports DOS 3.3 and ProDOS filesystems.", 3)
        };
        if (utf8.StartsWith("\ufeff"u8))
        {
            utf8 = utf8[3..];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DiskException("text.invalid_utf8", "Host text must contain valid UTF-8.", 4, exception);
        }

        List<byte> output = new(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            if (value is '\r' or '\n')
            {
                output.Add((byte)('\r' | highBit));
                if (value == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (value == '\t' || value is >= ' ' and <= '~')
            {
                output.Add((byte)(value | highBit));
            }
            else
            {
                int codePoint = char.IsHighSurrogate(value) ? char.ConvertToUtf32(value, text[index + 1]) : value;
                throw new DiskException("text.unsupported_character",
                    $"Host text character U+{codePoint:X4} at index {index} cannot be represented by the ASCII text conversion policy.", 3);
            }
        }

        return output.ToArray();
    }
}
