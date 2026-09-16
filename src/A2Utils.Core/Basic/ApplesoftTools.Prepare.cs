using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Basic;

public sealed record BasicPreparedLine(int SourceLine, int BasicLine, IReadOnlyList<string> Labels)
{
    public string? File { get; init; }
}
public sealed record BasicPrepareResult(string Source, IReadOnlyList<BasicPreparedLine> Mapping,
    IReadOnlyList<ProgramDiagnostic> Diagnostics);

public static partial class ApplesoftTools
{
    /// <summary>Expands unnumbered source with explicit @labels into ordinary numbered Applesoft.</summary>
    public static BasicPrepareResult Prepare(string source, int start = 10, int step = 10,
        string? file = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > ApplesoftBasic.MaximumSourceLength)
            throw new DiskException("basic.source_too_large", "Symbolic BASIC source exceeds 1 MiB.", 2);
        if (start is < 0 or > ApplesoftBasic.MaximumLineNumber || step <= 0)
            throw new DiskException("basic.prepare_range", "Start must be 0..63999 and step must be positive.", 2);

        List<PreparedStatement> statements = [];
        Dictionary<string, (int SourceLine, int? Number)> labels = new(StringComparer.OrdinalIgnoreCase);
        List<string> pendingLabels = [];
        List<ProgramDiagnostic> diagnostics = [];
        int physicalLine = 0;
        using StringReader reader = new(source.TrimStart('\ufeff'));
        while (reader.ReadLine() is { } text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            physicalLine++;
            if (text.Length > 16384)
                throw new DiskException("basic.source_line_too_long", "Symbolic source lines are limited to 16384 characters.", 2)
                {
                    Diagnostics = [LabelDiagnostic("source_line_too_long", "Symbolic source line exceeds 16384 characters.", file, physicalLine, 1)]
                };
            int bodyStart = 0;
            while (bodyStart < text.Length && text[bodyStart] is ' ' or '\t') bodyStart++;
            while (bodyStart < text.Length && text[bodyStart] == '@')
            {
                Match definition = Regex.Match(text[bodyStart..], @"^@([A-Za-z_][A-Za-z0-9_]{0,63}):", RegexOptions.CultureInvariant);
                if (!definition.Success)
                {
                    diagnostics.Add(LabelDiagnostic("label_definition", "Expected @name: at the start of the line; label names have 1..64 ASCII letters, digits, or underscores and cannot begin with a digit.", file, physicalLine, bodyStart + 1));
                    break;
                }
                string name = definition.Groups[1].Value;
                if (!labels.TryAdd(name, (physicalLine, null)))
                    diagnostics.Add(LabelDiagnostic("duplicate_label", $"Label '@{name}' was already defined on source line {labels[name].SourceLine}.", file, physicalLine, bodyStart + 1, name));
                else pendingLabels.Add(name);
                bodyStart += definition.Length;
                while (bodyStart < text.Length && text[bodyStart] is ' ' or '\t') bodyStart++;
            }
            if (bodyStart == text.Length) continue;
            if (char.IsAsciiDigit(text[bodyStart]))
                diagnostics.Add(LabelDiagnostic("numbered_source", "Prepare accepts unnumbered statements; use renumber for numbered source.", file, physicalLine, bodyStart + 1));
            long nextNumber = (long)start + (long)statements.Count * step;
            if (nextNumber > ApplesoftBasic.MaximumLineNumber)
                throw new DiskException("basic.prepare_range", "Prepared line numbers would exceed 63999.", 2);
            int number = (int)nextNumber;
            foreach (string name in pendingLabels) labels[name] = (labels[name].SourceLine, number);
            statements.Add(new(physicalLine, number, text[bodyStart..], bodyStart, [.. pendingLabels]));
            pendingLabels.Clear();
        }
        foreach (string name in pendingLabels)
            diagnostics.Add(LabelDiagnostic("label_without_statement", $"Label '@{name}' has no following statement.", file, labels[name].SourceLine, 1, name));
        ThrowPreparationErrors(diagnostics);

        StringBuilder output = new();
        List<BasicPreparedLine> mapping = [];
        foreach (PreparedStatement statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<(int Start, int Length, string Label)> references = CollectLabelReferences(statement, file, diagnostics);
            char[] masked = statement.Body.ToCharArray();
            foreach (var reference in references)
            {
                Array.Fill(masked, ' ', reference.Start, reference.Length);
                masked[reference.Start] = '0';
            }
            SourceLine maskedLine = new(statement.SourceLine, 0, new string(masked), statement.Number, 0, 0);
            Lex(maskedLine, 0, file, []);
            Analysis branches = new();
            FindReferences(maskedLine, file, branches);
            HashSet<int> branchPositions = branches.References.Select(reference => reference.Token.Start).ToHashSet();
            // RUN is deliberately kept outside the symbolic branch grammar.
            foreach (int index in Enumerable.Range(0, maskedLine.Tokens.Count).Where(index => maskedLine.Tokens[index].Text == "RUN"))
                if (index + 1 < maskedLine.Tokens.Count) branchPositions.Remove(maskedLine.Tokens[index + 1].Start);
            StringBuilder body = new(statement.Body);
            foreach (var reference in references.OrderByDescending(reference => reference.Start))
            {
                if (!branchPositions.Contains(reference.Start))
                {
                    diagnostics.Add(LabelDiagnostic("label_context", "Symbolic references must be complete literal GOTO/GOSUB/THEN or ON branch targets.",
                        file, statement.SourceLine, statement.BodyStart + reference.Start + 1, reference.Label));
                }
                else if (!labels.TryGetValue(reference.Label, out var label) || label.Number is null)
                {
                    diagnostics.Add(LabelDiagnostic("undefined_label", $"Label '@{reference.Label}' is not defined.",
                        file, statement.SourceLine, statement.BodyStart + reference.Start + 1, reference.Label));
                }
                else body.Remove(reference.Start, reference.Length).Insert(reference.Start, label.Number.Value.ToString(CultureInfo.InvariantCulture));
            }
            output.Append(statement.Number.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(body).Append('\n');
            mapping.Add(new(statement.SourceLine, statement.Number, statement.Labels) { File = file });
        }
        ThrowPreparationErrors(diagnostics);
        string numbered = output.ToString();
        BasicCheckResult check = Check(numbered, file, cancellationToken);
        foreach (ProgramDiagnostic diagnostic in check.Diagnostics)
        {
            if (diagnostic.Line is { } line && line >= 1 && line <= statements.Count)
            {
                PreparedStatement statement = statements[line - 1];
                diagnostics.Add(diagnostic with
                {
                    Line = statement.SourceLine,
                    // Expanding labels changes columns; use the source statement start rather than a misleading precise offset.
                    Column = diagnostic.Column is null ? null : statement.BodyStart + 1
                });
            }
            else diagnostics.Add(diagnostic);
        }
        ThrowPreparationErrors(diagnostics);
        return new(numbered, mapping, diagnostics);
    }

    private static List<(int Start, int Length, string Label)> CollectLabelReferences(PreparedStatement statement,
        string? file, List<ProgramDiagnostic> diagnostics)
    {
        List<(int Start, int Length, string Label)> references = [];
        string source = statement.Body;
        bool quoted = false;
        bool data = false;
        int index = 0;
        while (index < source.Length)
        {
            char value = source[index];
            if (value == '"')
            {
                quoted = !quoted;
                index++;
                continue;
            }
            if (quoted) { index++; continue; }
            if (data)
            {
                if (value == ':') data = false;
                index++;
                continue;
            }
            if (value == '@')
            {
                Match reference = Regex.Match(source[index..], @"^@([A-Za-z_][A-Za-z0-9_]{0,63})", RegexOptions.CultureInvariant);
                if (!reference.Success || (index + reference.Length < source.Length &&
                    (char.IsAsciiLetterOrDigit(source[index + reference.Length]) || source[index + reference.Length] == '_')))
                {
                    diagnostics.Add(LabelDiagnostic("label_reference", "Expected @name with 1..64 ASCII letters, digits, or underscores.",
                        file, statement.SourceLine, statement.BodyStart + index + 1));
                    index++;
                }
                else
                {
                    references.Add((index, reference.Length, reference.Groups[1].Value));
                    // Label names are frontend identifiers: @data/@rem must not enter BASIC lexical states.
                    index += reference.Length;
                }
                continue;
            }
            if (ApplesoftBasic.TryMatchToken(source, index, out byte token, out int end))
            {
                string name = ApplesoftBasic.TokenName(token);
                if (name == "REM") break;
                data = name == "DATA";
                index = end;
            }
            else index++;
        }
        return references;
    }

    private static ProgramDiagnostic LabelDiagnostic(string code, string message, string? file,
        int line, int column, string? symbol = null) =>
        new($"basic.{code}", "error", message, file, line, column, Symbol: symbol);

    private static void ThrowPreparationErrors(List<ProgramDiagnostic> diagnostics)
    {
        if (diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
            throw new DiskException("basic.prepare_invalid_source", "Symbolic BASIC source could not be prepared.", 2) { Diagnostics = diagnostics };
    }

    private sealed record PreparedStatement(int SourceLine, int Number, string Body, int BodyStart, IReadOnlyList<string> Labels);
}
