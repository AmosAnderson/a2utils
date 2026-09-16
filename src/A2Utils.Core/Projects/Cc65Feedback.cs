using System.Globalization;
using System.Text.RegularExpressions;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

public sealed record Cc65Segment(string Name, int Start, int Length, string Kind);

/// <summary>Parses bounded, textual cc65 feedback without inferring missing source locations.</summary>
public static class Cc65Feedback
{
    public static IReadOnlyDictionary<string, int> ParseLabels(string text)
    {
        Dictionary<string, int> labels = new(StringComparer.Ordinal);
        foreach (string line in Lines(text))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Match match = Regex.Match(line, @"^\s*al\s+([0-9a-fA-F]{1,8})\s+\.?([^\s]+)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success) throw Error("labels", "Unrecognized VICE label record.");
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int address) || address is < 0 or > 65535)
                continue; // Linker constants may exceed the 16-bit CPU address space.
            string name = match.Groups[2].Value;
            if (labels.TryGetValue(name, out int previous) && previous != address)
                throw Error("labels", $"Ambiguous exported compiler label '{name}'.");
            labels[name] = address;
            if (labels.Count > 65536) throw Error("labels", "Compiler label count exceeds 65536.");
        }
        return labels;
    }

    public static IReadOnlyList<Cc65Segment> ParseSegments(string map, IReadOnlyDictionary<string, string>? kinds = null)
    {
        List<Cc65Segment> result = [];
        bool segmentList = false;
        foreach (string line in Lines(map))
        {
            if (line.Trim() == "Segment list:") { segmentList = true; continue; }
            if (!segmentList) continue;
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.TrimEnd().EndsWith(':')) break;
            Match match = Regex.Match(line, @"^\s*([^\s]+)\s+([0-9a-fA-F]{4,8})\s+([0-9a-fA-F]{4,8})\s+([0-9a-fA-F]{4,8})\s+([0-9a-fA-F]+)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.All(character => character == '-') || trimmed.StartsWith("Name ", StringComparison.Ordinal)) continue;
                throw Error("map", "Unrecognized record in the linker segment list.");
            }
            string name = match.Groups[1].Value;
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int start) ||
                !int.TryParse(match.Groups[4].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int length) ||
                start < 0 || start > 65535 || length < 0 || length > 65536 - start)
                throw Error("map", $"Compiler segment '{name}' exceeds the 16-bit address space.");
            if (!int.TryParse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int end) ||
                length > 0 && end != start + length - 1)
                throw Error("map", $"Compiler segment '{name}' has inconsistent bounds.");
            string kind = kinds?.GetValueOrDefault(name) ?? name switch
            {
                "EXEHDR" => "header",
                "ZEROPAGE" => "zero-page",
                "BSS" or "LOWBSS" => "bss",
                _ => "data"
            };
            if (result.Any(segment => segment.Name == name)) throw Error("map", $"Duplicate compiler segment '{name}'.");
            result.Add(new(name, start, length, kind));
            if (result.Count > 4096) throw Error("map", "Compiler segment count exceeds 4096.");
        }
        return result;
    }

    public static IReadOnlyList<ProgramDiagnostic> ParseDiagnostics(string output, string stagedRoot, string projectRoot,
        bool failed = false)
    {
        List<ProgramDiagnostic> diagnostics = [];
        foreach (string line in Lines(output))
        {
            Match match = Regex.Match(line, @"^(.*?)(?:\((\d+)(?:,(\d+))?\)|:(\d+)(?::(\d+))?):\s*(?:(Warning|Error|Fatal error|Note):\s*)?(.+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                string path = match.Groups[1].Value.Trim();
                string full = Path.GetFullPath(path, stagedRoot);
                string relative = Path.GetRelativePath(stagedRoot, full);
                bool staged = relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
                string original = staged ? Path.GetFullPath(relative, projectRoot) : full;
                string severity = match.Groups[6].Value.ToLowerInvariant() switch
                {
                    "warning" => "warning",
                    "note" => "info",
                    "" => failed ? "error" : "info",
                    _ => "error"
                };
                if (!int.TryParse(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value,
                    CultureInfo.InvariantCulture, out int number) || number < 1)
                    throw Error("diagnostic", "Compiler diagnostic line is outside the supported source range.");
                string columnText = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[5].Value;
                int? column = null;
                if (columnText.Length > 0)
                {
                    if (!int.TryParse(columnText, CultureInfo.InvariantCulture, out int parsedColumn) || parsedColumn < 1)
                        throw Error("diagnostic", "Compiler diagnostic column is outside the supported source range.");
                    column = parsedColumn;
                }
                diagnostics.Add(new("cc65." + severity, severity, match.Groups[7].Value, original, number, column));
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                string severity = line.Contains("warning", StringComparison.OrdinalIgnoreCase) ? "warning" : failed ? "error" : "info";
                diagnostics.Add(new("cc65." + severity, severity, line.Replace(stagedRoot, projectRoot, StringComparison.Ordinal)));
            }
        }
        return diagnostics;
    }

    private static IEnumerable<string> Lines(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw Error("feedback_limit", "Compiler feedback exceeds 4 MiB.");
        return text.Split('\n').Select(line => line.TrimEnd('\r'));
    }

    private static DiskException Error(string code, string message) => new("cc65." + code, message, 2);
}
