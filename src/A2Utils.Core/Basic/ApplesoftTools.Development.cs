// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Globalization;
using System.Text.RegularExpressions;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Basic;

public static partial class ApplesoftTools
{
    private static void CheckDevelopment(Analysis analysis, string? file, CancellationToken cancellationToken)
    {
        List<(SourceLine Line, Lexeme Token, string Variable)> loops = [];
        Dictionary<string, int[]> dimensions = new(StringComparer.Ordinal);
        bool hasGosub = analysis.Lines.Any(line => line.Tokens.Any(token => token.Text == "GOSUB"));
        bool hasReturn = analysis.Lines.Any(line => line.Tokens.Any(token => token.Text == "RETURN"));
        foreach (SourceLine line in analysis.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Lexeme> tokens = line.Tokens;
            for (int index = 0; index < tokens.Count; index++)
            {
                Lexeme token = tokens[index];
                if (token.Text == "FOR" && index + 1 < tokens.Count && tokens[index + 1].Kind == "identifier")
                {
                    loops.Add((line, token, VariableIdentity(tokens[index + 1].Text)));
                }
                else if (token.Text == "NEXT")
                {
                    int next = index + 1;
                    do
                    {
                        string? variable = next < tokens.Count && tokens[next].Kind == "identifier"
                            ? VariableIdentity(tokens[next++].Text) : null;
                        int match = variable is null ? loops.Count - 1 : loops.FindLastIndex(loop => loop.Variable == variable);
                        if (match < 0)
                            analysis.Diagnostics.Add(Diagnostic("next_without_for", "warning",
                                "NEXT has no preceding lexical FOR match; verify the runtime loop stack and branches.", file, line, token, variable));
                        else loops.RemoveRange(match, loops.Count - match);
                        if (next >= tokens.Count || tokens[next].Text != ",") break;
                        next++;
                    }
                    while (next < tokens.Count);
                }
                else if (token.Text == "RETURN" && !hasGosub)
                    analysis.Diagnostics.Add(Diagnostic("return_without_gosub", "warning",
                        "RETURN appears without any GOSUB in this source; an external caller must supply its return context.", file, line, token));
                else if (token.Text == "GOSUB" && !hasReturn)
                    analysis.Diagnostics.Add(Diagnostic("gosub_without_return", "warning",
                        "This source has GOSUB but no RETURN; verify that terminating or leaving the subroutine is intentional.", file, line, token));

                if (token.Kind == "identifier" && index + 1 < tokens.Count && tokens[index + 1].Text == "("
                    && TryLiteralArguments(tokens, index + 2, out int[] values, out _))
                {
                    string name = VariableIdentity(token.Text);
                    int statement = index;
                    while (statement > 0 && tokens[statement - 1].Text is not (":" or "THEN")) statement--;
                    bool dim = tokens[statement].Text == "DIM";
                    if (values.Any(value => value < 0))
                        analysis.Diagnostics.Add(Diagnostic("negative_subscript", "error",
                            "A literal array subscript or dimension is negative.", file, line, token, token.Text));
                    if (dim) dimensions[name] = values;
                    else if (dimensions.TryGetValue(name, out int[]? upper) &&
                        (values.Length != upper.Length || values.Where((value, dimension) => dimension < upper.Length && value > upper[dimension]).Any()))
                        analysis.Diagnostics.Add(Diagnostic("array_bounds", "warning",
                            "Literal subscripts exceed a preceding DIM's inclusive bounds or dimension count; verify declaration execution and control flow.",
                            file, line, token, token.Text, string.Join(',', values)));
                }
                if (token.Text is "SQR" or "LOG" or "CHR$" && index + 1 < tokens.Count && tokens[index + 1].Text == "("
                    && TryLiteralArguments(tokens, index + 2, out int[] arguments, out _) && arguments.Length == 1)
                {
                    int value = arguments[0];
                    if (token.Text == "SQR" && value < 0 || token.Text == "LOG" && value <= 0
                        || token.Text == "CHR$" && value is < 0 or > 255)
                        analysis.Diagnostics.Add(Diagnostic("numeric_argument", "error",
                            $"{token.Text} cannot accept the literal argument {value}.", file, line, token));
                }
            }
        }
        foreach (var loop in loops)
            analysis.Diagnostics.Add(Diagnostic("for_without_next", "warning",
                "FOR has no following lexical NEXT match; verify that exiting the loop through another path is intentional.",
                file, loop.Line, loop.Token, loop.Variable));
    }

    private static string VariableIdentity(string name)
    {
        string suffix = name.EndsWith('$') || name.EndsWith('%') ? name[^1..] : "";
        string letters = suffix.Length == 0 ? name : name[..^1];
        return letters[..Math.Min(2, letters.Length)] + suffix;
    }

    private static bool TryLiteralArguments(List<Lexeme> tokens, int start, out int[] values, out int end)
    {
        List<int> result = [];
        end = start;
        values = [];
        while (end < tokens.Count)
        {
            int sign = 1;
            if (tokens[end].Text is "+" or "-") sign = tokens[end++].Text == "-" ? -1 : 1;
            if (end >= tokens.Count || tokens[end].Kind != "number"
                || !int.TryParse(tokens[end++].Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) return false;
            result.Add(sign * value);
            if (end >= tokens.Count) return false;
            if (tokens[end].Text == ")") { values = result.ToArray(); return true; }
            if (tokens[end++].Text != ",") return false;
        }
        return false;
    }

    /// <summary>Maps explicitly requested screen error detection to original numbered or labeled BASIC source.</summary>
    public static IReadOnlyList<ProgramDiagnostic> RuntimeDiagnostics(string screenText, ProjectBuildResult build, string? program = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        return RuntimeDiagnosticsCore(screenText, build, program);
    }

    /// <summary>Recognizes explicitly requested Applesoft screen errors when no build source map is available.</summary>
    public static IReadOnlyList<ProgramDiagnostic> RuntimeDiagnostics(string screenText)
        => RuntimeDiagnosticsCore(screenText, null, null);

    private static IReadOnlyList<ProgramDiagnostic> RuntimeDiagnosticsCore(string screenText, ProjectBuildResult? build, string? program)
    {
        ArgumentNullException.ThrowIfNull(screenText);
        if (screenText.Length > 65536) throw new DiskException("basic.screen_too_large", "BASIC error observations are limited to 65536 characters.", 2);
        List<ProgramDiagnostic> diagnostics = [];
        HashSet<(string, int)> seen = [];
        foreach (Match error in BasicRuntimeError().Matches(screenText))
        {
            string message = Regex.Replace(error.Groups[1].Value, @"\s+", " ").Trim();
            int number = int.Parse(error.Groups[2].Value, CultureInfo.InvariantCulture);
            if (!seen.Add((message, number))) continue;
            var matches = (build?.Files ?? []).Where(file => file.Kind is "basic" or "basic-labels"
                    && (program is null || file.Path.Equals(program, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(file => (file.SourceMap as IEnumerable<BasicPreparedLine> ?? [])
                    .Where(line => line.BasicLine == number).Select(line => (File: file, Line: line))).ToArray();
            if (matches.Length == 1)
                diagnostics.Add(new("basic.runtime_error", "error", $"Applesoft reported {message} ERROR IN {number}.",
                    matches[0].Line.File, matches[0].Line.SourceLine, BasicLine: number, Symbol: matches[0].File.Path));
            else
                diagnostics.Add(new("basic.runtime_error", "error", $"Applesoft reported {message} ERROR IN {number}; "
                    + (build is null ? "no project source map is available." : matches.Length == 0 ? "no matching BASIC source line was found." : "multiple BASIC programs match this line number."), BasicLine: number));
        }
        return diagnostics;
    }

    [GeneratedRegex(@"(?:^|[\r\n])\s*\?(NEXT WITHOUT FOR|SYNTAX|RETURN WITHOUT GOSUB|OUT OF DATA|ILLEGAL QUANTITY|OVERFLOW|OUT OF MEMORY|UNDEF'D STATEMENT|BAD SUBSCRIPT|REDIM'D ARRAY|DIVISION BY ZERO|TYPE MISMATCH|STRING TOO LONG|FORMULA TOO COMPLEX|CAN'T CONTINUE|UNDEF'D FUNCTION)\s+ERROR\s+IN\s+(\d{1,5})(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex BasicRuntimeError();
}
