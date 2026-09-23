// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text;
using System.Text.RegularExpressions;

namespace A2Utils.Core.Projects;

/// <summary>Validates a single-output ld65 configuration before passing it to the compiler.</summary>
public static class Cc65LinkerConfiguration
{
    public static IReadOnlyDictionary<string, string> Validate(string source)
    {
        if (source.Length > 1024 * 1024) throw Error("Linker configuration exceeds 1 MiB.");
        StringBuilder normalized = new();
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (current == '#')
            {
                while (index < source.Length && source[index] is not ('\r' or '\n')) index++;
                normalized.Append('\n');
            }
            else if (current == '"')
            {
                int end = source.IndexOf('"', index + 1);
                if (end < 0) throw Error("Unclosed linker configuration string.");
                string value = source[(index + 1)..end];
                if (value is not ("" or "%O"))
                    throw Error("Linker output strings must be empty or %O; additional files and path interpolation are unsupported.");
                normalized.Append(value.Length == 0 ? "\"\"" : "%O");
                index = end;
            }
            else normalized.Append(current);
        }
        string text = normalized.ToString();
        Dictionary<string, string> kinds = new(StringComparer.Ordinal);
        int offset = 0;
        foreach (Match section in Regex.Matches(text, @"([A-Za-z_][A-Za-z0-9_]*)\s*\{([^{}]*)\}", RegexOptions.CultureInvariant))
        {
            if (!string.IsNullOrWhiteSpace(text[offset..section.Index])) throw Error("Unsupported linker configuration syntax.");
            offset = section.Index + section.Length;
            string name = section.Groups[1].Value.ToUpperInvariant();
            if (name is not ("MEMORY" or "SEGMENTS" or "FEATURES" or "SYMBOLS" or "FILES"))
                throw Error($"Unsupported linker configuration section '{name}'.");
            string body = section.Groups[2].Value;
            if (name == "FILES" && Regex.IsMatch(body, @"(?:^|;)\s*(?!%O\s*:)[^\s]", RegexOptions.CultureInvariant))
                throw Error("FILES entries may refer only to %O.");
            if (name == "MEMORY")
                foreach (Match file in Regex.Matches(body, @"\bfile\b\s*=?\s*([^,;\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    if (file.Groups[1].Value is not ("%O" or "\"\"")) throw Error("MEMORY file attributes must use %O or an empty string.");
            if (name == "SEGMENTS")
                foreach (Match segment in Regex.Matches(body, @"(?:^|;)\s*([A-Za-z_][A-Za-z0-9_]*):([^;]*)", RegexOptions.CultureInvariant))
                {
                    Match type = Regex.Match(segment.Groups[2].Value, @"\btype\s*=?\s*(\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    string kind = type.Groups[1].Value.ToLowerInvariant() switch { "zp" => "zero-page", "bss" => "bss", _ => "data" };
                    kinds[segment.Groups[1].Value] = segment.Groups[1].Value == "EXEHDR" ? "header" : kind;
                }
        }
        if (!string.IsNullOrWhiteSpace(text[offset..]) || offset == 0) throw Error("Unsupported linker configuration syntax.");
        return kinds;
    }

    private static DiskException Error(string message) => new("cc65.linker_config", message, 2);
}
