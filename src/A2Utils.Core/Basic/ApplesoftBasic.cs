using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace A2Utils.Core.Basic;

/// <summary>
/// Converts numbered ASCII source and linked Applesoft BASIC program payloads. This is a
/// tokenizer, not a syntax checker or machine-code compiler. DOS length headers are external.
/// </summary>
public static class ApplesoftBasic
{
    public const ushort DefaultOrigin = 0x0801;
    public const int MaximumLineNumber = 63999;
    public const int MaximumTokenizedLineLength = 250;
    public const int MaximumSourceLength = 1024 * 1024;

    private const byte DataToken = 0x83;
    private const byte RemToken = 0xb2;
    private const byte PrintToken = 0xba;
    private const byte AtToken = 0xc5;

    // Applesoft ROM token-name table at $D0D0, in search order. HGR2 precedes HGR;
    // AT has an additional ambiguity rule in the ROM tokenizer at $D5BA.
    private static readonly string[] Tokens =
    [
        "END", "FOR", "NEXT", "DATA", "INPUT", "DEL", "DIM", "READ",
        "GR", "TEXT", "PR#", "IN#", "CALL", "PLOT", "HLIN", "VLIN",
        "HGR2", "HGR", "HCOLOR=", "HPLOT", "DRAW", "XDRAW", "HTAB", "HOME",
        "ROT=", "SCALE=", "SHLOAD", "TRACE", "NOTRACE", "NORMAL", "INVERSE", "FLASH",
        "COLOR=", "POP", "VTAB", "HIMEM:", "LOMEM:", "ONERR", "RESUME", "RECALL",
        "STORE", "SPEED=", "LET", "GOTO", "RUN", "IF", "RESTORE", "&",
        "GOSUB", "RETURN", "REM", "STOP", "ON", "WAIT", "LOAD", "SAVE",
        "DEF", "POKE", "PRINT", "CONT", "LIST", "CLEAR", "GET", "NEW",
        "TAB(", "TO", "FN", "SPC(", "THEN", "AT", "NOT", "STEP",
        "+", "-", "*", "/", "^", "AND", "OR", ">", "=", "<",
        "SGN", "INT", "ABS", "USR", "FRE", "SCRN(", "PDL", "POS",
        "SQR", "RND", "LOG", "EXP", "COS", "SIN", "TAN", "ATN",
        "PEEK", "LEN", "STR$", "VAL", "ASC", "CHR$", "LEFT$", "RIGHT$", "MID$"
    ];

    /// <summary>
    /// Tokenizes strictly increasing numbered lines. Blank physical lines are ignored; a bare
    /// line number is rejected. Spaces/tabs outside literals are discarded, even inside keywords.
    /// Strings, DATA fields, and REM tails retain their spelling and whitespace.
    /// </summary>
    public static byte[] Compile(string source, ushort origin = DefaultOrigin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOrigin(origin);
        if (source.Length > MaximumSourceLength)
        {
            throw new DiskException("basic.source_too_large",
                $"Applesoft source exceeds {MaximumSourceLength} characters.", 2);
        }

        if (source.StartsWith('\ufeff'))
        {
            source = source[1..];
        }

        List<byte> program = [];
        using StringReader reader = new(source);
        int previousNumber = -1;
        int sourceLine = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceLine++;
            ValidateSourceCharacters(line, sourceLine);
            int index = 0;
            SkipWhitespace(line, ref index);
            if (index == line.Length)
            {
                continue;
            }

            int numberStart = index;
            int number = 0;
            while (index < line.Length && char.IsAsciiDigit(line[index]))
            {
                number = number * 10 + line[index++] - '0';
                if (number > MaximumLineNumber)
                {
                    throw SourceError("line_number", sourceLine,
                        $"The BASIC line number must be between 0 and {MaximumLineNumber}.");
                }
            }

            if (numberStart == index)
            {
                throw SourceError("line_number", sourceLine, "A decimal BASIC line number is required.");
            }
            if (number <= previousNumber)
            {
                throw SourceError("line_order", sourceLine,
                    $"BASIC line {number} must be greater than the preceding line {previousNumber}; duplicates are not allowed.");
            }

            byte[] body = Tokenize(line.AsSpan(index), sourceLine, cancellationToken);
            if (body.Length == 0)
            {
                throw SourceError("empty_line", sourceLine,
                    $"BASIC line {number} has no statement; bare numbers are interactive deletion commands.");
            }

            int nextOffset = program.Count + 4 + body.Length + 1;
            if (nextOffset + 2 > 0x10000 - origin)
            {
                throw SourceError("program_too_large", sourceLine,
                    $"The tokenized program at ${origin:X4} would extend beyond the 16-bit address space.");
            }

            ushort nextAddress = (ushort)(origin + nextOffset);
            program.Add((byte)nextAddress);
            program.Add((byte)(nextAddress >> 8));
            program.Add((byte)number);
            program.Add((byte)(number >> 8));
            program.AddRange(body);
            program.Add(0);
            previousNumber = number;
        }

        program.Add(0);
        program.Add(0);
        return program.ToArray();
    }

    /// <summary>
    /// Returns a canonical LF-terminated source listing. Links must describe contiguous lines
    /// at the supplied origin. Rejects bytes or token combinations that would change on recompilation.
    /// </summary>
    public static string Decompile(ReadOnlySpan<byte> data, ushort origin = DefaultOrigin,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOrigin(origin);
        if (data.Length < 2 || data.Length > 0x10000 - origin)
        {
            throw ProgramError("The program must contain its two-byte terminator and fit in the 16-bit address space.");
        }

        StringBuilder listing = new();
        int offset = 0;
        int previousNumber = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length - offset < 2)
            {
                throw ProgramError($"The program terminator is truncated at offset {offset}.");
            }

            ushort nextAddress = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            if (nextAddress == 0)
            {
                if (offset + 2 != data.Length)
                {
                    throw ProgramError($"Unexpected trailing bytes after the program terminator at offset {offset}.");
                }
                return listing.ToString();
            }

            if (data.Length - offset < 5)
            {
                throw ProgramError($"The line header or body is truncated at offset {offset}.");
            }
            int nextOffset = nextAddress - origin;
            if (nextOffset <= offset + 5 || nextOffset > data.Length - 2)
            {
                throw ProgramError($"Invalid next-line link ${nextAddress:X4} at offset {offset}; links must advance to a complete line or terminator.");
            }

            ushort number = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            if (number > MaximumLineNumber || number <= previousNumber)
            {
                throw ProgramError($"BASIC line {number} at offset {offset} is out of range, duplicated, or out of order.");
            }

            int bodyLength = nextOffset - offset - 5;
            if (bodyLength > MaximumTokenizedLineLength)
            {
                throw ProgramError($"BASIC line {number} exceeds {MaximumTokenizedLineLength} tokenized body bytes.");
            }
            if (data[nextOffset - 1] != 0 || data.Slice(offset + 4, bodyLength).Contains((byte)0))
            {
                throw ProgramError($"The link for BASIC line {number} does not point immediately after its zero-terminated body.");
            }

            ReadOnlySpan<byte> body = data.Slice(offset + 4, bodyLength);
            string text = Detokenize(body, number);
            if (!body.SequenceEqual(Tokenize(text.AsSpan(), number, cancellationToken)))
            {
                throw new DiskException("basic.noncanonical_program",
                    $"BASIC line {number} contains raw text or token combinations that cannot be represented without changing its bytes.", 3);
            }

            listing.Append(number.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(text).Append('\n');
            previousNumber = number;
            offset = nextOffset;
        }
    }

    private static byte[] Tokenize(ReadOnlySpan<char> line, int sourceLine,
        CancellationToken cancellationToken)
    {
        List<byte> body = [];
        bool inString = false;
        bool inData = false;
        bool inRem = false;
        int index = 0;
        while (index < line.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            char value = line[index];
            if (!inString && !inData && !inRem && IsWhitespace(value))
            {
                index++;
                continue;
            }

            if (inRem || inString || value == '"' || inData)
            {
                body.Add((byte)value);
                index++;
                if (!inRem && value == '"')
                {
                    inString = !inString;
                }
                else if (!inString && !inRem && value == ':')
                {
                    inData = false;
                }
            }
            else if (value == '?')
            {
                body.Add(PrintToken);
                index++;
            }
            else if (TryMatchToken(line, index, out byte token, out int end))
            {
                body.Add(token);
                index = end;
                inData = token == DataToken;
                inRem = token == RemToken;
            }
            else
            {
                body.Add((byte)char.ToUpperInvariant(value));
                index++;
            }

            if (body.Count > MaximumTokenizedLineLength)
            {
                throw SourceError("line_too_long", sourceLine,
                    $"A BASIC line cannot exceed {MaximumTokenizedLineLength} tokenized body bytes.");
            }
        }

        return body.ToArray();
    }

    internal static string TokenName(byte token) => Tokens[token - 0x80];

    internal static bool TryMatchToken(ReadOnlySpan<char> line, int start, out byte token, out int end)
    {
        for (int tokenIndex = 0; tokenIndex < Tokens.Length; tokenIndex++)
        {
            string keyword = Tokens[tokenIndex];
            int index = start;
            int letter = 0;
            while (letter < keyword.Length)
            {
                while (index < line.Length && IsWhitespace(line[index]))
                {
                    index++;
                }
                if (index == line.Length || char.ToUpperInvariant(line[index]) != keyword[letter])
                {
                    break;
                }
                index++;
                letter++;
            }
            if (letter != keyword.Length)
            {
                continue;
            }

            token = (byte)(0x80 + tokenIndex);
            if (token == AtToken && index < line.Length &&
                char.ToUpperInvariant(line[index]) is 'N' or 'O')
            {
                continue;
            }

            end = index;
            return true;
        }

        token = 0;
        end = start;
        return false;
    }

    private static string Detokenize(ReadOnlySpan<byte> body, int number)
    {
        StringBuilder text = new();
        bool inString = false;
        bool inData = false;
        bool inRem = false;
        for (int index = 0; index < body.Length; index++)
        {
            byte value = body[index];
            bool literal = inString || inData || inRem;
            if (value >= 0x80 && !literal)
            {
                if (value > 0xea)
                {
                    throw UnsupportedByte(number, index, value);
                }
                if (text.Length > 0 && text[^1] != ' ')
                {
                    text.Append(' ');
                }
                text.Append(Tokens[value - 0x80]);
                inData = value == DataToken;
                inRem = value == RemToken;
                if (!inData && !inRem && index + 1 < body.Length)
                {
                    text.Append(' ');
                }
            }
            else
            {
                if (value != '\t' && value is not (>= 0x20 and <= 0x7e))
                {
                    throw UnsupportedByte(number, index, value);
                }
                text.Append((char)value);
                if (!inRem && value == '"')
                {
                    inString = !inString;
                }
                else if (!inString && !inRem && value == ':')
                {
                    inData = false;
                }
            }
        }
        return text.ToString();
    }

    private static void ValidateSourceCharacters(string line, int sourceLine)
    {
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] != '\t' && line[index] is not (>= ' ' and <= '~'))
            {
                throw SourceError("unsupported_character", sourceLine,
                    $"Character U+{(int)line[index]:X4} in column {index + 1} is unsupported; use printable ASCII or tabs.");
            }
        }
    }

    private static void ValidateOrigin(ushort origin)
    {
        if (origin < 0x0100 || origin == 0xffff)
        {
            throw new DiskException("basic.origin",
                "An Applesoft program origin must be between $0100 and $FFFE; the normal origin is $0801.", 2);
        }
    }

    private static bool IsWhitespace(char value) => value is ' ' or '\t';

    private static void SkipWhitespace(string line, ref int index)
    {
        while (index < line.Length && IsWhitespace(line[index]))
        {
            index++;
        }
    }

    private static DiskException SourceError(string code, int line, string message) =>
        new($"basic.{code}", $"Source line {line}: {message}", 2);

    private static DiskException ProgramError(string message) => new("basic.invalid_program", message, 4);

    private static DiskException UnsupportedByte(int line, int offset, byte value) =>
        new("basic.unsupported_byte",
            $"BASIC line {line} contains unsupported byte 0x{value:X2} at body offset {offset}.", 3);
}
