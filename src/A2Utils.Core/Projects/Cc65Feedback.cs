using System.Globalization;
using System.Text.RegularExpressions;
using A2Utils.Core.Assembly;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

public sealed record Cc65Segment(string Name, int Start, int Length, string Kind);

/// <summary>Stable compiler metadata and source ranges retained in project build evidence.</summary>
public sealed record Cc65SourceMap(string CompilerVersion, string CompilerPath, string Map, string Labels,
    IReadOnlyList<Cc65Segment> Segments, IReadOnlyList<AssemblySourceMapEntry> Entries)
{
    public string Format { get; init; } = "cc65-dbg-2.0";
}

/// <summary>Parses bounded, textual cc65 feedback without inferring missing source locations.</summary>
public static class Cc65Feedback
{
    private const int MaximumDebugRecords = 131072;
    private const long MaximumMappedBytes = 1024 * 1024;

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

    /// <summary>Parses the bounded ld65 debug-info records needed for source-level address mapping.</summary>
    public static IReadOnlyList<AssemblySourceMapEntry> ParseDebugMap(string debug, string stagedRoot, string projectRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(debug);
        string stage = Path.GetFullPath(stagedRoot);
        string root = Path.GetFullPath(projectRoot);
        Dictionary<int, DebugFile> files = [];
        Dictionary<int, DebugSegment> segments = [];
        Dictionary<int, DebugSpan> spans = [];
        Dictionary<int, DebugLine> lines = [];
        bool version = false;
        int recordCount = 0;
        foreach (string line in Lines(debug))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (++recordCount > MaximumDebugRecords) throw Error("debug", "Compiler debug record count exceeds 131072.");
            int separator = line.IndexOf('\t');
            if (separator < 1) throw Error("debug", "Malformed compiler debug record.");
            string kind = line[..separator];
            if (kind is not "version" and not "file" and not "seg" and not "span" and not "line") continue;
            Dictionary<string, string> fields = ParseFields(line[(separator + 1)..]);
            switch (kind)
            {
                case "version":
                    if (version || Number(fields, "major") != 2 || Number(fields, "minor") != 0)
                        throw Error("debug", "Only one cc65 debug format version 2.0 record is supported.");
                    version = true;
                    break;
                case "file":
                    {
                        int id = Identifier(fields, "id");
                        string name = Required(fields, "name");
                        if (name.Length is 0 or > 4096 || name.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
                            !files.TryAdd(id, new(name)))
                            throw Error("debug", "Invalid or duplicate compiler debug source file.");
                        break;
                    }
                case "seg":
                    {
                        int id = Identifier(fields, "id");
                        long start = Number(fields, "start");
                        long size = Number(fields, "size");
                        if (start is < 0 or > 65535 || size < 0 || size > 65536 - start ||
                            !segments.TryAdd(id, new((int)start, (int)size)))
                            throw Error("debug", "Invalid or duplicate compiler debug segment.");
                        break;
                    }
                case "span":
                    {
                        int id = Identifier(fields, "id");
                        int segment = Identifier(fields, "seg");
                        long start = Number(fields, "start");
                        long size = Number(fields, "size");
                        if (start is < 0 or > 65536 || size is < 0 or > 65536 ||
                            !spans.TryAdd(id, new(segment, (int)start, (int)size)))
                            throw Error("debug", "Invalid or duplicate compiler debug span.");
                        break;
                    }
                case "line":
                    {
                        int id = Identifier(fields, "id");
                        int file = Identifier(fields, "file");
                        long sourceLine = Number(fields, "line");
                        int[] lineSpans = fields.TryGetValue("span", out string? value) ? Identifiers(value) : [];
                        if (sourceLine is < 1 or > int.MaxValue || !lines.TryAdd(id, new(file, (int)sourceLine, lineSpans)))
                            throw Error("debug", "Invalid or duplicate compiler debug line.");
                        break;
                    }
            }
        }
        if (!version) throw Error("debug", "Compiler debug output has no version record.");

        Dictionary<int, ResolvedFile?> resolvedFiles = [];
        Dictionary<string, string[]> sourceLines = new(PathComparer);
        List<AssemblySourceMapEntry> result = [];
        long mappedBytes = 0;
        foreach (DebugLine line in lines.OrderBy(pair => pair.Key).Select(pair => pair.Value))
        {
            if (!files.ContainsKey(line.File)) throw Error("debug", "Compiler debug line references an unknown file.");
            if (!resolvedFiles.TryGetValue(line.File, out ResolvedFile? resolved))
            {
                resolved = Resolve(files[line.File].Name);
                resolvedFiles[line.File] = resolved;
            }
            foreach (int spanId in line.Spans)
            {
                if (!spans.TryGetValue(spanId, out DebugSpan? span) || !segments.TryGetValue(span.Segment, out DebugSegment? segment))
                    throw Error("debug", "Compiler debug line references an unknown span or segment.");
                long address = (long)segment.Start + span.Start;
                if (span.Start > segment.Length || span.Length > segment.Length - span.Start ||
                    address > 65535 || span.Length > 65536 - address)
                    throw Error("debug", "Compiler debug span exceeds its 16-bit segment bounds.");
                if (resolved is null || span.Length == 0) continue;
                mappedBytes += span.Length;
                if (mappedBytes > MaximumMappedBytes)
                    throw Error("debug", "Compiler debug source ranges exceed the 1 MiB mapping limit.");
                if (!sourceLines.TryGetValue(resolved.StagedPath, out string[]? text))
                {
                    text = ProgramFiles.ReadText(resolved.StagedPath, cancellationToken).Split('\n')
                        .Select(item => item.TrimEnd('\r')).ToArray();
                    sourceLines[resolved.StagedPath] = text;
                }
                if (line.Line > text.Length) throw Error("debug", "Compiler debug line exceeds its source file.");
                result.Add(new(resolved.OriginalPath, line.Line, (int)address, span.Length)
                {
                    Source = text[line.Line - 1]
                });
                if (result.Count > 65536) throw Error("debug", "Compiler source-map entry count exceeds 65536.");
            }
        }
        return result.Distinct().OrderBy(entry => entry.Address).ThenBy(entry => entry.File, PathComparer)
            .ThenBy(entry => entry.Line).ThenBy(entry => entry.Length).ToArray();

        ResolvedFile? Resolve(string name)
        {
            string staged;
            try { staged = Path.GetFullPath(name, stage); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw Error("debug", "Compiler debug source path is invalid.");
            }
            string relative = Path.GetRelativePath(stage, staged);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            string original = Path.GetFullPath(relative, root);
            if (!File.Exists(original) || !File.Exists(staged)) return null;
            return new(staged, original);
        }
    }

    private static Dictionary<string, string> ParseFields(string text)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        int position = 0;
        while (position < text.Length)
        {
            int equals = text.IndexOf('=', position);
            if (equals <= position) throw Error("debug", "Malformed compiler debug field.");
            string name = text[position..equals];
            if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ||
                !char.IsAsciiLetter(name[0]))
                throw Error("debug", "Malformed compiler debug field name.");
            position = equals + 1;
            string value;
            if (position < text.Length && text[position] == '"')
            {
                int end = text.IndexOf('"', ++position);
                if (end < 0) throw Error("debug", "Unterminated compiler debug string.");
                value = text[position..end];
                position = end + 1;
                if (position < text.Length && text[position] != ',')
                    throw Error("debug", "Malformed compiler debug string field.");
            }
            else
            {
                int end = text.IndexOf(',', position);
                if (end < 0) end = text.Length;
                value = text[position..end];
                position = end;
            }
            if (!fields.TryAdd(name, value)) throw Error("debug", "Duplicate compiler debug field.");
            if (position < text.Length) position++;
        }
        return fields;
    }

    private static string Required(IReadOnlyDictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out string? value) ? value : throw Error("debug", $"Compiler debug record is missing '{name}'.");

    private static int Identifier(IReadOnlyDictionary<string, string> fields, string name)
    {
        long value = Number(fields, name);
        return value is >= 0 and <= 65535 ? (int)value : throw Error("debug", "Compiler debug identifier exceeds 65535.");
    }

    private static int[] Identifiers(string value)
    {
        if (value.Length == 0) throw Error("debug", "Compiler debug span list is empty.");
        string[] values = value.Split('+');
        if (values.Length > 4096) throw Error("debug", "Compiler debug span list exceeds 4096 entries.");
        return values.Select(item =>
        {
            if (!long.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id is < 0 or > 65535)
                throw Error("debug", "Compiler debug span identifier is invalid.");
            return (int)id;
        }).ToArray();
    }

    private static long Number(IReadOnlyDictionary<string, string> fields, string name)
    {
        string value = Required(fields, name);
        NumberStyles style = NumberStyles.None;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            style = NumberStyles.HexNumber;
        }
        if (value.Length == 0 || !long.TryParse(value, style, CultureInfo.InvariantCulture, out long result))
            throw Error("debug", $"Compiler debug field '{name}' is not a valid number.");
        return result;
    }

    private static IEnumerable<string> Lines(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw Error("feedback_limit", "Compiler feedback exceeds 4 MiB.");
        return text.Split('\n').Select(line => line.TrimEnd('\r'));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record DebugFile(string Name);
    private sealed record DebugSegment(int Start, int Length);
    private sealed record DebugSpan(int Segment, int Start, int Length);
    private sealed record DebugLine(int File, int Line, IReadOnlyList<int> Spans);
    private sealed record ResolvedFile(string StagedPath, string OriginalPath);

    private static DiskException Error(string code, string message) => new("cc65." + code, message, 2);
}
