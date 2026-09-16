using System.Security.Cryptography;
using System.Text;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Assembly;

public static partial class Assembler
{
    private sealed record SourceLine(string Text, string? File, int Line);

    private sealed class SourceDocument
    {
        public List<SourceLine> Lines { get; } = [];
        public Dictionary<string, string> Hashes { get; } = new(PathComparer);
        public IEnumerable<string> Dependencies => Hashes.Keys;
        public string Text => string.Join('\n', Lines.Select(line => line.Text));
        private int _characters;
        private int _binaryBytes;
        private readonly Dictionary<string, byte[]> _files = new(PathComparer);
        private readonly HashSet<string> _active = new(PathComparer);
        private IReadOnlyDictionary<string, byte[]> _generated = new Dictionary<string, byte[]>();
        private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static SourceDocument FromText(string source)
        {
            if (source.Length > MaximumSourceLength)
                throw Error(1, $"Source exceeds {MaximumSourceLength} characters.");
            SourceDocument document = new();
            string[] lines = SplitLines(source);
            for (int index = 0; index < lines.Length; index++)
                document.Add(new SourceLine(lines[index], null, index + 1));
            return document;
        }

        public static SourceDocument FromFile(string path, CancellationToken cancellationToken,
            IReadOnlyDictionary<string, byte[]>? generatedInputs = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(path);
            SourceDocument document = new();
            if (generatedInputs is not null)
            {
                if (generatedInputs.Count > 512 || generatedInputs.Values.Any(bytes => bytes is null)
                    || generatedInputs.Values.Sum(bytes => (long)bytes.Length) > 32 * 1024 * 1024)
                    throw LocatedError(path, 1, "Generated assembly inputs exceed their count or size limit.", "assembly.include_limit");
                document._generated = generatedInputs.ToDictionary(pair => Path.GetFullPath(pair.Key), pair => pair.Value.ToArray(), PathComparer);
            }
            document.Expand(fullPath, Path.GetDirectoryName(fullPath)!, cancellationToken);
            return document;
        }

        private static string[] SplitLines(string source) =>
            source.TrimStart('\ufeff').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private void Add(SourceLine line)
        {
            if (line.Text.Length > 16_384 || Lines.Count >= 100_000 ||
                (long)_characters + line.Text.Length + 1 > MaximumSourceLength)
                throw LocatedError(line.File, line.Line, "Expanded source exceeds its character, line, or line-length limit.", "assembly.source_limit");
            _characters += line.Text.Length + 1;
            Lines.Add(line);
        }

        private byte[] Read(string path, int maximum, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_files.TryGetValue(path, out byte[]? existing))
            {
                if (existing.Length > maximum) throw LocatedError(path, 1, "Included input exceeds its size limit.", "assembly.include_limit");
                return existing;
            }
            if (_files.Count >= 256)
                throw LocatedError(path, 1, "Assembly accepts at most 256 input files.", "assembly.include_limit");
            // Refuse links in every ancestor before opening a potentially special file.
            FileSystemInfo? entry = new FileInfo(path);
            while (entry is not null)
            {
                if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
                    throw LocatedError(path, 1, "Assembly input paths cannot contain links.", "assembly.include_path");
                entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent;
            }
            if (_generated.TryGetValue(path, out byte[]? generated))
            {
                if (File.Exists(path) || Directory.Exists(path))
                    throw LocatedError(path, 1, "Generated assembly input collides with an existing path.", "assembly.include_path");
                if (generated.Length > maximum)
                    throw LocatedError(path, 1, "Generated assembly input exceeds its size limit.", "assembly.include_limit");
                _files.Add(path, generated);
                Hashes.Add(path, Convert.ToHexStringLower(SHA256.HashData(generated)));
                return generated;
            }
            HostFiles.EnsureRegularFile(path, cancellationToken);
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > maximum)
                throw LocatedError(path, 1, "Included input exceeds its size limit.", "assembly.include_limit");
            byte[] bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1)
                throw LocatedError(path, 1, "The assembly input changed while being read.", "assembly.source_changed");
            cancellationToken.ThrowIfCancellationRequested();
            _files.Add(path, bytes);
            Hashes.Add(path, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            return bytes;
        }

        private void Expand(string path, string root, CancellationToken cancellationToken)
        {
            if (_active.Count >= 32)
                throw LocatedError(path, 1, "Source includes exceed 32 levels.", "assembly.include_limit");
            if (!_active.Add(path))
                throw LocatedError(path, 1, "Source include cycle detected.", "assembly.include_cycle");
            try
            {
                string source;
                try { source = new UTF8Encoding(false, true).GetString(Read(path, MaximumSourceLength, cancellationToken)); }
                catch (DecoderFallbackException)
                {
                    throw LocatedError(path, 1, "Program source must be valid UTF-8.", "program.invalid_utf8");
                }
                string[] lines = SplitLines(source);
                for (int index = 0; index < lines.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int number = index + 1;
                    string text;
                    try { text = StripComment(lines[index], number).Trim(); }
                    catch (DiskException exception)
                    {
                        throw LocatedError(path, number, exception.Diagnostics[0].Message, exception.Diagnostics[0].Code);
                    }
                    int split = 0;
                    while (split < text.Length && !char.IsWhiteSpace(text[split])) split++;
                    string operation = text[..split].ToUpperInvariant();
                    if (operation is not (".INCLUDE" or ".INCBIN"))
                    {
                        Add(new SourceLine(lines[index], path, number));
                        continue;
                    }
                    string operand = text[split..].Trim();
                    if (operand.Length < 2 || operand[0] != '"' || operand[^1] != '"' || operand[1..^1].Contains('"'))
                        throw LocatedError(path, number, "Include directives require one quoted relative path.", "assembly.include_path");
                    string relative = operand[1..^1];
                    if (relative.Length == 0 || Path.IsPathRooted(relative))
                        throw LocatedError(path, number, "Include paths must be relative to their source file.", "assembly.include_path");
                    string target = Path.GetFullPath(relative, Path.GetDirectoryName(path)!);
                    string inside = Path.GetRelativePath(root, target);
                    if (inside == ".." || inside.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(inside))
                        throw LocatedError(path, number, "Includes must stay inside the main source directory.", "assembly.include_path");
                    if (operation == ".INCLUDE")
                    {
                        Add(new SourceLine("; " + lines[index], path, number));
                        Expand(target, root, cancellationToken);
                    }
                    else
                    {
                        byte[] bytes = Read(target, 65536, cancellationToken);
                        if ((long)_binaryBytes + bytes.Length > 65536)
                            throw LocatedError(path, number, "Binary includes exceed 65536 bytes in total.", "assembly.include_limit");
                        _binaryBytes += bytes.Length;
                        if (bytes.Length == 0) Add(new SourceLine(".fill 0", path, number));
                        for (int offset = 0; offset < bytes.Length; offset += 64)
                        {
                            string values = string.Join(',', bytes.Skip(offset).Take(64).Select(value => "$" + value.ToString("X2")));
                            Add(new SourceLine(".byte " + values, path, number));
                        }
                    }
                }
            }
            finally { _active.Remove(path); }
        }
    }

    private static DiskException LocatedError(string? file, int line, string message, string code) =>
        new(code.StartsWith("program.", StringComparison.Ordinal) ? code : "assembly.invalid_source",
            $"{file ?? "<source>"}: Line {line}: {message}", 2)
        {
            Diagnostics = [new ProgramDiagnostic(code, "error", message, File: file, Line: line)]
        };

    private static string QualifyLocals(string text, string? scope, int line)
    {
        StringBuilder result = new();
        char quote = '\0';
        bool escaped = false;
        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            if (quote != '\0')
            {
                if (!escaped && value == quote) quote = '\0';
                escaped = !escaped && value == '\\' && quote == '"';
            }
            else if (value is '\'' or '"') quote = value;
            else if (value == '@' && (index == 0 || !AssemblyExpression.IsIdentifierPart(text[index - 1])))
            {
                if (scope is null || index + 1 == text.Length || !AssemblyExpression.IsIdentifierStart(text[index + 1]))
                    throw Error(line, "A local @name requires a preceding global label and a valid name.", "assembly.local_scope");
                result.Append(scope);
            }
            result.Append(value);
        }
        return result.ToString();
    }
}
