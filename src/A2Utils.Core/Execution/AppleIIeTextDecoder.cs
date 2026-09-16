using System.Text;

namespace A2Utils.Core.Execution;

/// <summary>Decodes selected Apple IIe text pages while retaining display attributes and MouseText codes.</summary>
public static class AppleIIeTextDecoder
{
    /// <summary>Equivalent byte-to-text decoding for condition-driven Lua text observations.</summary>
    public static string CreateLuaDecoder() => """
        local function decode_text_byte(value, alternate, mouse_supported)
            assert(math.type(value) == "integer" and value >= 0 and value <= 255, "Invalid text byte")
            if alternate and mouse_supported and value >= 0x40 and value <= 0x5f then
                return string.format("{MT:%02X}", value - 0x40)
            end
            local character = value % 128
            if not alternate and value >= 0x40 and value <= 0x7f then character = character % 64 end
            if character < 32 then character = character + 64 end
            return string.char(character)
        end
        """;

    /// <summary>
    /// Decodes 1 KiB pages from physical main/auxiliary memory. In 80 columns the auxiliary byte is the
    /// left character of each pair. MouseText uses stable {MT:00} through {MT:1F} tokens, not approximate
    /// Unicode glyphs. Set mouseTextSupported to false for an unenhanced IIe character ROM.
    /// </summary>
    public static AppleIIeTextScreen Decode(ReadOnlySpan<byte> mainPage, ReadOnlySpan<byte> auxiliaryPage,
        int columns, bool alternateCharacterSet, bool mouseTextSupported = true)
    {
        if (columns is not 40 and not 80)
            throw new ArgumentOutOfRangeException(nameof(columns), "Text pages have 40 or 80 columns.");
        if (mainPage.Length != 1024)
            throw new ArgumentException("Main text memory must contain exactly 1024 bytes.", nameof(mainPage));
        if (columns == 80 && auxiliaryPage.Length != 1024 || columns == 40 && auxiliaryPage.Length is not 0 and not 1024)
            throw new ArgumentException("80-column text requires exactly 1024 auxiliary bytes.", nameof(auxiliaryPage));

        List<AppleIIeTextCell> cells = new(24 * columns);
        StringBuilder text = new();
        for (int row = 0; row < 24; row++)
        {
            if (row != 0) text.Append('\n');
            int rowOffset = row % 8 * 128 + row / 8 * 40;
            for (int column = 0; column < columns; column++)
            {
                bool auxiliary = columns == 80 && column % 2 == 0;
                int offset = rowOffset + (columns == 80 ? column / 2 : column);
                byte value = auxiliary ? auxiliaryPage[offset] : mainPage[offset];
                AppleIIeTextCell cell = DecodeCell(row, column, auxiliary ? "aux" : "main", offset,
                    value, alternateCharacterSet, mouseTextSupported);
                cells.Add(cell);
                text.Append(cell.Text);
            }
        }
        return new(columns, 24, text.ToString(), cells);
    }

    private static AppleIIeTextCell DecodeCell(int row, int column, string bank, int offset, byte value,
        bool alternateCharacterSet, bool mouseTextSupported)
    {
        if (alternateCharacterSet && mouseTextSupported && value is >= 0x40 and <= 0x5f)
        {
            int index = value - 0x40;
            return new(row, column, bank, offset, value, $"{{MT:{index:X2}}}", "mousetext", index);
        }

        string displayMode = value >= 0x80 ? "normal"
            : !alternateCharacterSet && value >= 0x40 ? "flash" : "inverse";
        int character = value & 0x7f;
        if (!alternateCharacterSet && value is >= 0x40 and <= 0x7f)
            character &= 0x3f;
        if (character < 0x20)
            character += 0x40;
        return new(row, column, bank, offset, value, ((char)character).ToString(), displayMode, null);
    }
}

public sealed record AppleIIeTextScreen(int Columns, int Rows, string Text, IReadOnlyList<AppleIIeTextCell> Cells);

/// <summary>Zero-based row, column, and offset within the selected physical 1 KiB page.</summary>
public sealed record AppleIIeTextCell(int Row, int Column, string Bank, int Offset, byte Value,
    string Text, string DisplayMode, int? MouseTextIndex);
