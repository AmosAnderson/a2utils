using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Basic;

public sealed record BasicCheckResult(bool Valid, IReadOnlyList<ProgramDiagnostic> Diagnostics);
public sealed record BasicLineMapping(int SourceLine, int OldLine, int NewLine);
public sealed record BasicRenumberResult(string Source, IReadOnlyList<BasicLineMapping> Mapping,
    IReadOnlyList<ProgramDiagnostic> Diagnostics);

/// <summary>Conservative source checks and source-preserving literal line-reference rewriting.</summary>
public static partial class ApplesoftTools
{
    public static BasicCheckResult Check(string source, string? file = null,
        CancellationToken cancellationToken = default)
    {
        Analysis analysis = Analyze(source, file, cancellationToken);
        return new(!analysis.Diagnostics.Any(d => d.Severity == "error"), analysis.Diagnostics);
    }

    public static BasicRenumberResult Renumber(string source, int start = 10, int step = 10,
        string? file = null, CancellationToken cancellationToken = default)
    {
        if (start is < 0 or > ApplesoftBasic.MaximumLineNumber || step <= 0)
        {
            throw new DiskException("basic.renumber_range", "Start must be 0..63999 and step must be positive.", 2);
        }
        Analysis analysis = Analyze(source, file, cancellationToken);
        List<ProgramDiagnostic> diagnostics = [.. analysis.Diagnostics];
        foreach (SourceLine line in analysis.Lines)
        {
            foreach (Lexeme token in line.Tokens.Where(t => t.Text is "LIST" or "DEL"))
            {
                int index = line.Tokens.IndexOf(token);
                if (index + 1 < line.Tokens.Count && line.Tokens[index + 1].Text != ":")
                {
                    diagnostics.Add(Diagnostic("renumber_range_reference", "error",
                        "Renumbering LIST/DEL ranges is unsupported; remove the range before renumbering.", file, line, token));
                }
            }
        }
        diagnostics.AddRange(analysis.DynamicReferences.Select(reference =>
            Diagnostic("renumber_computed_target", "error",
                "A computed line target cannot be safely renumbered; use a literal existing line number.",
                file, reference.Line, reference.Token)));
        if (diagnostics.Any(d => d.Severity == "error"))
        {
            throw new DiskException("basic.renumber_invalid_source", "BASIC source cannot be safely renumbered.", 2)
            {
                Diagnostics = diagnostics
            };
        }
        if (analysis.Lines.Count > 0 && (long)start + (long)(analysis.Lines.Count - 1) * step > ApplesoftBasic.MaximumLineNumber)
        {
            throw new DiskException("basic.renumber_range", "Renumbered lines would exceed 63999.", 2);
        }
        List<BasicLineMapping> mapping = analysis.Lines.Select((line, index) =>
            new BasicLineMapping(line.PhysicalLine, line.Number, start + index * step)).ToList();
        Dictionary<int, int> numbers = mapping.ToDictionary(m => m.OldLine, m => m.NewLine);
        List<(int Start, int Length, string Text)> replacements = analysis.Lines.Select(line =>
            (line.Start + line.NumberStart, line.NumberLength, numbers[line.Number].ToString(CultureInfo.InvariantCulture))).ToList();
        replacements.AddRange(analysis.References.Select(reference =>
            (reference.Line.Start + reference.Token.Start, reference.Token.End - reference.Token.Start,
                numbers[reference.Target].ToString(CultureInfo.InvariantCulture))));
        StringBuilder result = new(source);
        foreach (var replacement in replacements.OrderByDescending(r => r.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Text);
        }
        string rewritten = result.ToString();
        // New line-number spelling can increase the tokenized body size. Validate before returning.
        ApplesoftBasic.Compile(rewritten, cancellationToken: cancellationToken);
        return new(rewritten, mapping, diagnostics);
    }

    private static Analysis Analyze(string source, string? file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        Analysis analysis = new();
        try
        {
            ApplesoftBasic.Compile(source, cancellationToken: cancellationToken);
        }
        catch (DiskException exception)
        {
            Match position = Regex.Match(exception.Message, @"Source line (\d+):", RegexOptions.CultureInvariant);
            int? line = position.Success ? int.Parse(position.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            analysis.Diagnostics.Add(new(exception.Code, "error", exception.Message, file, line));
            return analysis;
        }
        int offset = 0;
        int physicalLine = 0;
        while (offset < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = source.IndexOfAny(['\r', '\n'], offset);
            if (end < 0) end = source.Length;
            string text = source[offset..end];
            physicalLine++;
            int index = 0;
            if (offset == 0 && text.StartsWith('\ufeff')) index++;
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index < text.Length)
            {
                int numberStart = index;
                while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
                int number = int.Parse(text[numberStart..index], CultureInfo.InvariantCulture);
                SourceLine line = new(physicalLine, offset, text, number, numberStart, index - numberStart);
                Lex(line, index, file, analysis.Diagnostics);
                analysis.Lines.Add(line);
            }
            offset = end;
            if (offset < source.Length && source[offset] == '\r') offset++;
            if (offset < source.Length && source[offset] == '\n') offset++;
        }
        HashSet<int> numbers = analysis.Lines.Select(line => line.Number).ToHashSet();
        Dictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceLine line in analysis.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindReferences(line, file, analysis);
            CheckStatements(line, file, analysis.Diagnostics);
            CheckVariables(line, file, analysis.Diagnostics, variables);
        }
        foreach (Reference reference in analysis.References.Where(r => !numbers.Contains(r.Target)))
        {
            analysis.Diagnostics.Add(Diagnostic("missing_target", "error",
                $"Target line {reference.Target} does not exist.", file, reference.Line, reference.Token,
                actual: reference.Target.ToString(CultureInfo.InvariantCulture)));
        }
        return analysis;
    }

    private static void Lex(SourceLine line, int index, string? file, List<ProgramDiagnostic> diagnostics)
    {
        string text = line.Text;
        while (index < text.Length)
        {
            if (text[index] is ' ' or '\t') { index++; continue; }
            int start = index;
            if (text[index] == '"')
            {
                index++;
                while (index < text.Length && text[index] != '"') index++;
                bool closed = index < text.Length;
                if (closed) index++;
                Lexeme quoted = new("string", text[start..index], start, index);
                line.Tokens.Add(quoted);
                if (!closed)
                {
                    diagnostics.Add(Diagnostic("unterminated_string", "warning",
                        "String extends to the end of the line; add a closing quote to make the intent explicit.", file, line, quoted));
                }
                continue;
            }
            if (ApplesoftBasic.TryMatchToken(text, index, out byte value, out int tokenEnd))
            {
                string name = ApplesoftBasic.TokenName(value);
                line.Tokens.Add(new("keyword", name, start, tokenEnd));
                index = tokenEnd;
                if (name == "REM") break;
                if (name == "DATA")
                {
                    bool quoted = false;
                    while (index < text.Length)
                    {
                        if (text[index] == '"') quoted = !quoted;
                        if (!quoted && text[index] == ':') break;
                        index++;
                    }
                }
                continue;
            }
            if (text[index] == '?')
            {
                line.Tokens.Add(new("keyword", "PRINT", index, ++index));
                continue;
            }
            if (char.IsAsciiLetter(text[index]))
            {
                StringBuilder name = new();
                do
                {
                    name.Append(char.ToUpperInvariant(text[index++]));
                    while (index < text.Length && text[index] is ' ' or '\t') index++;
                }
                while (index < text.Length && char.IsAsciiLetterOrDigit(text[index]) &&
                    !ApplesoftBasic.TryMatchToken(text, index, out _, out _));
                if (index < text.Length && text[index] is '$' or '%') name.Append(text[index++]);
                line.Tokens.Add(new("identifier", name.ToString(), start, index));
                continue;
            }
            if (char.IsAsciiDigit(text[index]) || text[index] == '.')
            {
                StringBuilder number = new();
                bool exponent = false;
                int numberEnd = index;
                while (index < text.Length)
                {
                    char current = char.ToUpperInvariant(text[index]);
                    if (current == 'E' && ApplesoftBasic.TryMatchToken(text, index, out _, out _)) break;
                    if (char.IsAsciiDigit(current) || current == '.') number.Append(current);
                    else if (current == 'E' && !exponent) { exponent = true; number.Append(current); }
                    else if (current is '+' or '-' && number.Length > 0 && number[^1] == 'E') number.Append(current);
                    else if (current is not (' ' or '\t')) break;
                    if (current is not (' ' or '\t')) numberEnd = index + 1;
                    index++;
                }
                line.Tokens.Add(new("number", number.ToString(), start, numberEnd));
                continue;
            }
            line.Tokens.Add(new("punctuation", text[index].ToString(), index, ++index));
        }
    }

    private static void FindReferences(SourceLine line, string? file, Analysis analysis)
    {
        List<Lexeme> tokens = line.Tokens;
        for (int index = 0; index < tokens.Count; index++)
        {
            Lexeme keyword = tokens[index];
            if (keyword.Text is not ("GOTO" or "GOSUB" or "THEN" or "RUN")) continue;
            bool then = keyword.Text == "THEN";
            if (index + 1 >= tokens.Count || tokens[index + 1].Text == ":")
            {
                if (keyword.Text != "RUN") analysis.Diagnostics.Add(Diagnostic("target_required", "error",
                    $"{keyword.Text} requires a target or statement.", file, line, keyword));
                continue;
            }
            if (then && tokens[index + 1].Kind != "number") continue;
            int statementStart = index;
            while (statementStart > 0 && tokens[statementStart - 1].Text != ":") statementStart--;
            bool on = tokens.Skip(statementStart).Take(index - statementStart).Any(t => t.Text == "ON");
            int targetIndex = index + 1;
            do
            {
                Lexeme target = tokens[targetIndex];
                int next = targetIndex + 1;
                bool ends = next == tokens.Count || tokens[next].Text == ":" || (on && tokens[next].Text == ",");
                if (target.Kind == "number" && int.TryParse(target.Text, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int number) && ends)
                {
                    analysis.References.Add(new(line, target, number));
                }
                else
                {
                    analysis.DynamicReferences.Add(new(line, target));
                    analysis.Diagnostics.Add(Diagnostic("computed_target", "warning",
                        "This target is not a literal line number; its destination is not checked.", file, line, target));
                    break;
                }
                if (!on || next == tokens.Count || tokens[next].Text != ",") break;
                targetIndex = next + 1;
                if (targetIndex >= tokens.Count || tokens[targetIndex].Text == ":")
                {
                    analysis.Diagnostics.Add(Diagnostic("target_required", "error",
                        "ON target list ends with a comma.", file, line, tokens[next]));
                    break;
                }
            }
            while (targetIndex < tokens.Count);
        }
    }

    private static void CheckStatements(SourceLine line, string? file, List<ProgramDiagnostic> diagnostics)
    {
        int start = 0;
        for (int end = 0; end <= line.Tokens.Count; end++)
        {
            if (end != line.Tokens.Count && line.Tokens[end].Text != ":") continue;
            if (end > start) CheckStatement(line, start, end, file, diagnostics);
            start = end + 1;
        }
    }

    private static void CheckStatement(SourceLine line, int start, int end, string? file, List<ProgramDiagnostic> diagnostics)
    {
        List<Lexeme> tokens = line.Tokens;
        Lexeme first = tokens[start];
        string command = first.Text;
        if (command is "DATA" or "REM") return;
        if (command == "IF")
        {
            int then = Find(tokens, start + 1, end, "THEN");
            bool directGoto = false;
            if (then < 0)
            {
                then = Find(tokens, start + 1, end, "GOTO");
                directGoto = then >= 0;
            }
            if (then < 0)
            {
                diagnostics.Add(Diagnostic("if_then", "error", "IF requires THEN or GOTO.", file, line, first));
                return;
            }
            CheckExpression(line, start + 1, then, file, diagnostics);
            if (directGoto) CheckStatement(line, then, end, file, diagnostics);
            else if (then + 1 < end && tokens[then + 1].Kind != "number") CheckStatement(line, then + 1, end, file, diagnostics);
            return;
        }
        if (command == "PRINT")
        {
            int part = start + 1;
            int depth = 0;
            for (int index = part; index <= end; index++)
            {
                if (index < end && tokens[index].Text.EndsWith('(')) depth++;
                else if (index < end && tokens[index].Text == ")") depth--;
                if (index == end || (depth == 0 && tokens[index].Text is ";" or ","))
                {
                    if (index > part) CheckExpression(line, part, index, file, diagnostics, printSequence: true);
                    part = index + 1;
                }
            }
            return;
        }
        if (command == "FOR")
        {
            int equal = Find(tokens, start + 1, end, "=");
            int to = Find(tokens, start + 1, end, "TO");
            int step = Find(tokens, start + 1, end, "STEP");
            if (equal < 0 || to < equal || equal != start + 2 || tokens[start + 1].Kind != "identifier")
            {
                diagnostics.Add(Diagnostic("for_syntax", "error", "FOR requires a variable, initial value, and TO limit.", file, line, first));
                return;
            }
            CheckExpression(line, equal + 1, to, file, diagnostics);
            CheckExpression(line, to + 1, step < 0 ? end : step, file, diagnostics);
            if (step >= 0) CheckExpression(line, step + 1, end, file, diagnostics);
            return;
        }
        if (command is "ON" or "ONERR")
        {
            int branch = Enumerable.Range(start + 1, end - start - 1).FirstOrDefault(i => tokens[i].Text is "GOTO" or "GOSUB", -1);
            if (branch < 0) diagnostics.Add(Diagnostic("on_syntax", "error", "ON requires GOTO/GOSUB; ONERR requires GOTO.", file, line, first));
            else if (command == "ON") CheckExpression(line, start + 1, branch, file, diagnostics);
            else if (branch != start + 1 || tokens[branch].Text != "GOTO") diagnostics.Add(Diagnostic("on_syntax", "error", "ONERR requires GOTO.", file, line, first));
            return;
        }
        if (command is "GOTO" or "GOSUB" || (command == "RUN" && start + 1 < end))
        {
            CheckExpression(line, start + 1, end, file, diagnostics);
            return;
        }
        int assignmentStart = command == "LET" ? start + 1 : start;
        if (command == "LET" || first.Kind == "identifier")
        {
            int equal = Find(tokens, assignmentStart, end, "=");
            if (equal < 0)
            {
                diagnostics.Add(Diagnostic("assignment", "error", "An assignment requires '=' and a value.", file, line, first));
                return;
            }
            CheckExpression(line, assignmentStart, equal, file, diagnostics, variableOnly: true);
            CheckExpression(line, equal + 1, end, file, diagnostics);
            return;
        }
        if (first.Kind != "keyword")
        {
            diagnostics.Add(Diagnostic("statement", "error", "Expected a BASIC statement or variable assignment.", file, line, first));
            return;
        }
        // Other statements receive delimiter checks only; this is not a full ROM grammar.
        int parentheses = 0;
        for (int index = start + 1; index < end; index++)
        {
            if (tokens[index].Text.EndsWith('(')) parentheses++;
            else if (tokens[index].Text == ")") parentheses--;
            if (parentheses < 0) break;
        }
        if (parentheses != 0) diagnostics.Add(Diagnostic("parentheses", "error", "Unbalanced expression parentheses.", file, line, first));
    }

    private static void CheckExpression(SourceLine line, int start, int end, string? file,
        List<ProgramDiagnostic> diagnostics, bool variableOnly = false, bool printSequence = false)
    {
        ExpressionParser parser = new(line.Tokens, start, end);
        if (!(printSequence ? parser.ParsePrintSequence() : parser.Parse(variableOnly)))
        {
            Lexeme token = line.Tokens[Math.Min(parser.Position, line.Tokens.Count - 1)];
            diagnostics.Add(Diagnostic("expression", "error", "Invalid or incomplete expression.", file, line, token));
        }
    }

    private static void CheckVariables(SourceLine line, string? file, List<ProgramDiagnostic> diagnostics,
        Dictionary<string, string> variables)
    {
        for (int index = 0; index < line.Tokens.Count; index++)
        {
            Lexeme token = line.Tokens[index];
            if (token.Kind != "identifier") continue;
            string name = token.Text;
            string suffix = name.EndsWith('$') || name.EndsWith('%') ? name[^1..] : "";
            string letters = suffix.Length == 0 ? name : name[..^1];
            string key = letters[..Math.Min(2, letters.Length)] + suffix;
            if (index > 0 && line.Tokens[index - 1].Text == "FN") key = "FN " + key;
            if (index + 1 < line.Tokens.Count && line.Tokens[index + 1].Text == "(") key += "()";
            if (variables.TryGetValue(key, out string? previous) && previous != name)
            {
                diagnostics.Add(Diagnostic("variable_collision", "warning",
                    $"'{name}' and '{previous}' share Applesoft variable identity '{key}'.", file, line, token, name));
            }
            else variables[key] = name;
        }
        // Detect readable assignment names that ROM keyword tokenization splits, e.g. SCORE -> SC OR E.
        foreach (Match match in Regex.Matches(line.Text, @"(?:^\s*\d+\s*|:\s*|\bLET\s+|\bFOR\s+)([A-Za-z][A-Za-z0-9]*[$%]?)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            Group name = match.Groups[1];
            if (!line.Tokens.Any(t => t.Start >= name.Index && t.Start < name.Index + name.Length && t.Kind == "keyword")) continue;
            Lexeme token = new("identifier", name.Value, name.Index, name.Index + name.Length);
            diagnostics.Add(Diagnostic("keyword_collision", "warning",
                $"'{name.Value}' contains an Applesoft keyword and will not be tokenized as one variable name.", file, line, token, name.Value));
        }
    }

    private static int Find(List<Lexeme> tokens, int start, int end, string value)
    {
        for (int index = start; index < end; index++) if (tokens[index].Text == value) return index;
        return -1;
    }

    private static ProgramDiagnostic Diagnostic(string code, string severity, string message, string? file,
        SourceLine line, Lexeme token, string? symbol = null, string? actual = null) =>
        new($"basic.{code}", severity, message, file, line.PhysicalLine, token.Start + 1, line.Number, symbol, Actual: actual);

    private sealed record Lexeme(string Kind, string Text, int Start, int End);
    private sealed record SourceLine(int PhysicalLine, int Start, string Text, int Number, int NumberStart, int NumberLength)
    {
        public List<Lexeme> Tokens { get; } = [];
    }
    private sealed record Reference(SourceLine Line, Lexeme Token, int Target);
    private sealed record DynamicReference(SourceLine Line, Lexeme Token);
    private sealed class Analysis
    {
        public List<SourceLine> Lines { get; } = [];
        public List<ProgramDiagnostic> Diagnostics { get; } = [];
        public List<Reference> References { get; } = [];
        public List<DynamicReference> DynamicReferences { get; } = [];
    }

    private sealed class ExpressionParser(List<Lexeme> tokens, int start, int end)
    {
        public int Position { get; private set; } = start;
        public bool Parse(bool variableOnly) => (variableOnly ? Variable() : Expression()) && Position == end;

        public bool ParsePrintSequence()
        {
            do
            {
                if (!Expression()) return false;
            }
            while (Position < end);
            return true;
        }

        private bool Expression(int depth = 0)
        {
            if (depth > 64 || !Primary(depth + 1)) return false;
            while (Position < end && tokens[Position].Text is "+" or "-" or "*" or "/" or "^" or "AND" or "OR" or "=" or "<" or ">")
            {
                string operation = tokens[Position++].Text;
                if (operation is "<" or ">" or "=" && Position < end && tokens[Position].Text is "<" or ">" or "=" && tokens[Position].Text != operation) Position++;
                if (!Primary(depth + 1)) return false;
            }
            return true;
        }

        private bool Primary(int depth)
        {
            if (depth > 64 || Position >= end) return false;
            Lexeme token = tokens[Position];
            if (token.Text is "+" or "-" or "NOT") { Position++; return Primary(depth + 1); }
            if (token.Kind == "number")
            {
                Position++;
                return double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number);
            }
            if (token.Kind == "string") { Position++; return true; }
            if (token.Kind == "identifier") return Variable(depth);
            if (token.Text == "(")
            {
                Position++;
                return Expression(depth + 1) && Consume(")");
            }
            if (token.Text == "FN")
            {
                Position++;
                return Position < end && tokens[Position].Kind == "identifier" && Variable(depth);
            }
            if (token.Text is "SGN" or "INT" or "ABS" or "USR" or "FRE" or "SCRN(" or "PDL" or "POS" or "SQR" or "RND" or "LOG" or "EXP" or "COS" or "SIN" or "TAN" or "ATN" or "PEEK" or "LEN" or "STR$" or "VAL" or "ASC" or "CHR$" or "LEFT$" or "RIGHT$" or "MID$" or "TAB(" or "SPC(")
            {
                Position++;
                if (!token.Text.EndsWith('(') && !Consume("(")) return false;
                return Arguments(depth);
            }
            return false;
        }

        private bool Variable(int depth = 0)
        {
            if (Position >= end || tokens[Position].Kind != "identifier") return false;
            Position++;
            return !Consume("(") || Arguments(depth);
        }

        private bool Arguments(int depth)
        {
            if (!Expression(depth + 1)) return false;
            while (Consume(",")) if (!Expression(depth + 1)) return false;
            return Consume(")");
        }

        private bool Consume(string text)
        {
            if (Position >= end || tokens[Position].Text != text) return false;
            Position++;
            return true;
        }
    }
}
