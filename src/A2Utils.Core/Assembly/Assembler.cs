// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Collections.ObjectModel;
using System.Text;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Assembly;

public sealed record AssemblySourceMapEntry(string? File, int Line, int Address, int Length)
{
    public string Source { get; init; } = "";
}

public sealed record AssemblyResult(ushort Origin, byte[] Bytes)
{
    public IReadOnlyDictionary<string, int> Symbols { get; init; } = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
    public IReadOnlyList<AssemblySourceMapEntry> SourceMap { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyDictionary<string, string> DependencyHashes { get; init; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}

/// <summary>Assembles a bounded, contiguous 6502 program with source and binary includes.</summary>
public static partial class Assembler
{
    public const int MaximumSourceLength = 4 * 1024 * 1024;

    public static AssemblyResult Assemble(string source, ushort? origin = null,
        CpuKind cpu = CpuKind.Mos6502, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        return AssembleDocument(SourceDocument.FromText(source), origin, cpu, cancellationToken);
    }

    public static AssemblyResult AssembleFile(string path, ushort? origin = null,
        CpuKind cpu = CpuKind.Mos6502, CancellationToken cancellationToken = default) =>
        AssembleDocument(SourceDocument.FromFile(path, cancellationToken), origin, cpu, cancellationToken);

    /// <summary>Assembles with generated inputs held in memory; virtual paths obey the usual include boundary.</summary>
    public static AssemblyResult AssembleFile(string path, IReadOnlyDictionary<string, byte[]> generatedInputs,
        ushort? origin = null, CpuKind cpu = CpuKind.Mos6502, CancellationToken cancellationToken = default) =>
        AssembleDocument(SourceDocument.FromFile(path, cancellationToken, generatedInputs), origin, cpu, cancellationToken);

    private static AssemblyResult AssembleDocument(SourceDocument document, ushort? origin,
        CpuKind cpu, CancellationToken cancellationToken)
    {
        try
        {
            return AssembleCore(document, origin, cpu, cancellationToken);
        }
        catch (DiskException exception) when (exception.Diagnostics.Count > 0)
        {
            ProgramDiagnostic[] diagnostics = exception.Diagnostics.Select(diagnostic =>
            {
                if (diagnostic.Line is not { } line || line < 1 || line > document.Lines.Count)
                    return diagnostic;
                SourceLine location = document.Lines[line - 1];
                return diagnostic with { File = location.File, Line = location.Line };
            }).ToArray();
            ProgramDiagnostic first = diagnostics[0];
            string message = first.File is null ? exception.Message
                : $"{first.File}: Line {first.Line}: {first.Message}";
            throw new DiskException(exception.Code, message, exception.ExitCode, exception) { Diagnostics = diagnostics };
        }
    }

    private static AssemblyResult AssembleCore(SourceDocument document, ushort? origin,
        CpuKind cpu, CancellationToken cancellationToken)
    {
        string source = document.Text;
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > MaximumSourceLength)
        {
            throw Error(1, $"Source exceeds {MaximumSourceLength} characters.");
        }
        Dictionary<(string Mnemonic, AddressingMode Mode), Instruction> instructions =
            InstructionSet.GetInstructions(cpu).ToDictionary(instruction => (instruction.Mnemonic, instruction.Mode));
        Dictionary<string, AssemblySymbol> symbols = new(StringComparer.OrdinalIgnoreCase);
        List<Statement> statements = ParseStatements(source, symbols, cancellationToken);
        AssemblyResolver layoutResolver = new(symbols, requireKnown: false, cancellationToken);
        int? address = origin;
        int? firstAddress = origin;
        bool emittedBytes = false;
        bool sawOrigin = false;

        foreach (Statement statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statement.Address = address;
            foreach (string label in statement.Labels)
            {
                RequireOrigin(address, statement.Line);
                symbols[label].Address = address;
            }
            if (statement.Symbol is not null)
            {
                statement.Symbol.Address = address;
                continue;
            }
            if (statement.Operation.Length == 0)
            {
                continue;
            }
            if (statement.Operation == ".ORG")
            {
                AssemblyExpression expression = AssemblyExpression.Parse(statement.Operand, statement.Line);
                int target = RequireRange(EvaluateLayout(expression, layoutResolver, address, statement.Line),
                    0xffff, statement.Line, "Origin");
                if (!emittedBytes && !sawOrigin && origin.HasValue && origin.Value != target)
                {
                    throw Error(statement.Line, "Source origin conflicts with the supplied origin.");
                }
                if (!firstAddress.HasValue)
                {
                    firstAddress = target;
                    address = target;
                    statement.Address = target;
                }
                else
                {
                    if (target < address)
                    {
                        throw Error(statement.Line, "An origin cannot move backwards or overlap preceding output.");
                    }
                    statement.Length = target - address!.Value;
                }
                address = target;
                sawOrigin = true;
            }
            else
            {
                RequireOrigin(address, statement.Line);
                LayoutStatement(statement, instructions, layoutResolver);
                if ((long)address!.Value + statement.Length > 0x10000)
                {
                    throw Error(statement.Line, "Output extends past address $FFFF; address wrapping is not allowed.");
                }
                address += statement.Length;
            }
            emittedBytes |= statement.Length > 0;
        }

        RequireOrigin(firstAddress, 1);
        AssemblyResolver finalResolver = new(symbols, requireKnown: true, cancellationToken);
        foreach (AssemblySymbol symbol in symbols.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (symbol.Expression is not null)
            {
                finalResolver.Resolve(symbol.Name, symbol.Line);
            }
        }
        byte[] output = new byte[address!.Value - firstAddress!.Value];
        foreach (Statement statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement.Operation.Length == 0 || statement.Symbol is not null || statement.Operation == ".ORG")
            {
                continue;
            }
            Span<byte> target = output.AsSpan(statement.Address!.Value - firstAddress.Value, statement.Length);
            EmitStatement(statement, finalResolver, target);
        }
        Dictionary<string, int> exportedSymbols = new(StringComparer.OrdinalIgnoreCase);
        foreach (AssemblySymbol symbol in symbols.Values)
        {
            long value = finalResolver.Resolve(symbol.Name, symbol.Line).Number;
            if (value is >= int.MinValue and <= int.MaxValue)
                exportedSymbols.Add(symbol.Name, (int)value);
        }
        return new AssemblyResult((ushort)firstAddress.Value, output)
        {
            Symbols = new ReadOnlyDictionary<string, int>(exportedSymbols),
            SourceMap = statements.Where(statement => statement.Address.HasValue).Select(statement =>
            {
                SourceLine location = document.Lines[statement.Line - 1];
                return new AssemblySourceMapEntry(location.File, location.Line, statement.Address!.Value, statement.Length)
                {
                    Source = location.Text
                };
            }).ToArray(),
            Dependencies = document.Dependencies.ToArray(),
            DependencyHashes = new ReadOnlyDictionary<string, string>(document.Hashes)
        };
    }

    internal static DiskException Error(int line, string message, string code = "assembly.invalid_source",
        string? symbol = null, string? expected = null, string? actual = null) =>
        new("assembly.invalid_source", $"Line {line}: {message}", 2)
        {
            Diagnostics = [new ProgramDiagnostic(code, "error", message, Line: line, Symbol: symbol,
                Expected: expected, Actual: actual)]
        };

    public static string CreateListing(AssemblyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        StringBuilder listing = new();
        foreach (AssemblySourceMapEntry entry in result.SourceMap)
        {
            int count = Math.Max(entry.Length, 1);
            for (int offset = 0; offset < count; offset += 8)
            {
                int length = Math.Min(8, entry.Length - offset);
                string bytes = length > 0 ? Convert.ToHexString(result.Bytes.AsSpan(entry.Address - result.Origin + offset, length)) : "";
                listing.Append($"{entry.Address + offset:X4}  {bytes,-16}  ");
                if (offset == 0) listing.Append($"{entry.File ?? "<source>"}:{entry.Line}  {entry.Source}");
                listing.Append('\n');
            }
        }
        return listing.ToString();
    }

    private static List<Statement> ParseStatements(string source, Dictionary<string, AssemblySymbol> symbols,
        CancellationToken cancellationToken)
    {
        string[] lines = source.TrimStart('\ufeff').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > 100_000)
        {
            throw Error(1, "Source exceeds 100000 lines.");
        }
        List<Statement> statements = new(lines.Length);
        string? scope = null;
        for (int index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int line = index + 1;
            if (lines[index].Length > 16_384)
            {
                throw Error(line, "Source line exceeds 16384 characters.");
            }
            string text = StripComment(lines[index], line).Trim();
            List<string> labels = [];
            while (text.Length > 0 && (AssemblyExpression.IsIdentifierStart(text[0]) || text[0] == '@'))
            {
                int nameLength = IdentifierLength(text);
                int colon = nameLength;
                while (colon < text.Length && char.IsWhiteSpace(text[colon]))
                {
                    colon++;
                }
                if (colon == text.Length || text[colon] != ':')
                {
                    break;
                }
                string label = text[..nameLength];
                if (label[0] == '@') label = QualifyLocals(label, scope, line);
                else scope = label;
                AddSymbol(new AssemblySymbol(label, line, null), symbols);
                labels.Add(label);
                text = text[(colon + 1)..].TrimStart();
            }
            text = QualifyLocals(text, scope, line);
            if (text.Length == 0)
            {
                statements.Add(new Statement(line, labels, "", ""));
                continue;
            }
            int equal = text.IndexOf('=');
            if (equal >= 0)
            {
                string name = text[..equal].Trim();
                if (name == "*")
                {
                    statements.Add(new Statement(line, labels, ".ORG", text[(equal + 1)..].Trim()));
                    continue;
                }
                if (IsIdentifier(name))
                {
                    AssemblySymbol symbol = new(name, line, AssemblyExpression.Parse(text[(equal + 1)..], line));
                    AddSymbol(symbol, symbols);
                    statements.Add(new Statement(line, labels, "=", "") { Symbol = symbol });
                    continue;
                }
            }
            int operationEnd = 0;
            while (operationEnd < text.Length && !char.IsWhiteSpace(text[operationEnd]))
            {
                operationEnd++;
            }
            statements.Add(new Statement(line, labels, text[..operationEnd].ToUpperInvariant(),
                text[operationEnd..].Trim()));
        }
        return statements;
    }

    private static void LayoutStatement(Statement statement,
        Dictionary<(string Mnemonic, AddressingMode Mode), Instruction> instructions, AssemblyResolver resolver)
    {
        switch (statement.Operation)
        {
            case ".BYTE":
            case ".WORD":
            case ".TEXT":
                bool words = statement.Operation == ".WORD";
                foreach (string item in SplitOperands(statement.Operand, statement.Line))
                {
                    if (item.StartsWith('"'))
                    {
                        if (words)
                        {
                            throw Error(statement.Line, ".word requires numeric expressions.");
                        }
                        byte[] bytes = ParseString(item, statement.Line);
                        statement.Items.Add(new DataItem(bytes, null));
                        statement.Length += bytes.Length;
                    }
                    else
                    {
                        if (statement.Operation == ".TEXT")
                        {
                            throw Error(statement.Line, ".text requires quoted ASCII strings.");
                        }
                        statement.Items.Add(new DataItem(null, AssemblyExpression.Parse(item, statement.Line)));
                        statement.Length += words ? 2 : 1;
                    }
                }
                return;
            case ".FILL":
                List<string> fill = SplitOperands(statement.Operand, statement.Line);
                if (fill.Count is < 1 or > 2)
                {
                    throw Error(statement.Line, ".fill expects count[,value].");
                }
                statement.Length = RequireRange(EvaluateLayout(AssemblyExpression.Parse(fill[0], statement.Line),
                    resolver, statement.Address, statement.Line), 0x10000, statement.Line, "Fill count");
                statement.Expressions = [AssemblyExpression.Parse(fill.Count == 2 ? fill[1] : "0", statement.Line)];
                return;
            case ".ALIGN":
                List<string> align = SplitOperands(statement.Operand, statement.Line);
                if (align.Count is < 1 or > 2)
                    throw Error(statement.Line, ".align expects boundary[,value].");
                int boundary = RequireRange(EvaluateLayout(AssemblyExpression.Parse(align[0], statement.Line),
                    resolver, statement.Address, statement.Line), 0x10000, statement.Line, "Alignment");
                if (boundary == 0 || (boundary & (boundary - 1)) != 0)
                    throw Error(statement.Line, "Alignment must be a power of two in 1..65536.", "assembly.invalid_alignment");
                statement.Length = (-statement.Address!.Value) & (boundary - 1);
                statement.Expressions = [AssemblyExpression.Parse(align.Count == 2 ? align[1] : "0", statement.Line)];
                return;
            case ".ASSERT":
                List<string> assertion = SplitOperands(statement.Operand, statement.Line);
                if (assertion.Count is < 1 or > 2)
                    throw Error(statement.Line, ".assert expects expression[,\"message\"].");
                statement.Expressions = [AssemblyExpression.Parse(assertion[0], statement.Line)];
                statement.AssertionMessage = assertion.Count == 2
                    ? Encoding.ASCII.GetString(ParseString(assertion[1], statement.Line)) : "Assembly assertion failed.";
                return;
        }

        string mnemonic = statement.Operation;
        bool Supports(AddressingMode mode) => instructions.ContainsKey((mnemonic, mode));
        if (!instructions.Keys.Any(key => key.Mnemonic == mnemonic))
        {
            throw Error(statement.Line, $"Unknown directive or instruction '{mnemonic}' for this CPU.", "assembly.unknown_operation", mnemonic);
        }
        string operand = statement.Operand;
        AddressingMode mode;
        if (operand.Length == 0)
        {
            mode = Supports(AddressingMode.Implied) ? AddressingMode.Implied : AddressingMode.Accumulator;
        }
        else if (operand.Equals("A", StringComparison.OrdinalIgnoreCase) && Supports(AddressingMode.Accumulator))
        {
            mode = AddressingMode.Accumulator;
        }
        else if (Supports(AddressingMode.Relative))
        {
            mode = AddressingMode.Relative;
            statement.Expressions = [AssemblyExpression.Parse(operand, statement.Line)];
        }
        else if (Supports(AddressingMode.ZeroPageRelative))
        {
            List<string> parts = SplitOperands(operand, statement.Line);
            if (parts.Count != 2)
            {
                throw Error(statement.Line, $"{mnemonic} expects zero-page address,branch target.");
            }
            string zeroPage = RemoveForce(parts[0], out char force);
            if (force == 'a')
            {
                throw Error(statement.Line, $"{mnemonic} requires a zero-page first operand.");
            }
            mode = AddressingMode.ZeroPageRelative;
            statement.Expressions = [AssemblyExpression.Parse(zeroPage, statement.Line),
                AssemblyExpression.Parse(parts[1], statement.Line)];
        }
        else if (operand.StartsWith('#'))
        {
            mode = AddressingMode.Immediate;
            statement.Expressions = [AssemblyExpression.Parse(operand[1..], statement.Line)];
        }
        else
        {
            mode = LayoutMemoryOperand(statement, instructions, resolver);
        }
        if (!instructions.TryGetValue((mnemonic, mode), out Instruction? instruction))
        {
            throw Error(statement.Line, $"{mnemonic} does not support {mode} addressing on this CPU.");
        }
        statement.Instruction = instruction;
        statement.Length = instruction.Length;
    }

    private static AddressingMode LayoutMemoryOperand(Statement statement,
        Dictionary<(string Mnemonic, AddressingMode Mode), Instruction> instructions, AssemblyResolver resolver)
    {
        string operand = statement.Operand;
        AddressingMode shortMode;
        AddressingMode longMode;
        string expression;
        int close = operand.StartsWith('(') ? FindCloseParenthesis(operand, statement.Line) : -1;
        if (close >= 0 && (close == operand.Length - 1 || operand[(close + 1)..].TrimStart().StartsWith(',')))
        {
            List<string> inner = SplitOperands(operand[1..close], statement.Line);
            string suffix = operand[(close + 1)..].Trim();
            if (suffix.Length > 0)
            {
                if (inner.Count != 1 || !suffix[1..].Trim().Equals("Y", StringComparison.OrdinalIgnoreCase))
                {
                    throw Error(statement.Line, "Indirect indexed addressing requires (expression),Y.");
                }
                shortMode = longMode = AddressingMode.IndirectIndexed;
            }
            else if (inner.Count == 2 && inner[1].Equals("X", StringComparison.OrdinalIgnoreCase))
            {
                shortMode = AddressingMode.IndexedIndirect;
                longMode = AddressingMode.AbsoluteIndexedIndirect;
            }
            else if (inner.Count == 1)
            {
                shortMode = AddressingMode.ZeroPageIndirect;
                longMode = AddressingMode.Indirect;
            }
            else
            {
                throw Error(statement.Line, "Invalid indirect addressing operand.");
            }
            expression = inner[0];
        }
        else
        {
            List<string> parts = SplitOperands(operand, statement.Line);
            if (parts.Count == 1)
            {
                shortMode = AddressingMode.ZeroPage;
                longMode = AddressingMode.Absolute;
            }
            else if (parts.Count == 2 && parts[1].Equals("X", StringComparison.OrdinalIgnoreCase))
            {
                shortMode = AddressingMode.ZeroPageX;
                longMode = AddressingMode.AbsoluteX;
            }
            else if (parts.Count == 2 && parts[1].Equals("Y", StringComparison.OrdinalIgnoreCase))
            {
                shortMode = AddressingMode.ZeroPageY;
                longMode = AddressingMode.AbsoluteY;
            }
            else
            {
                throw Error(statement.Line, "Expected an address optionally indexed by X or Y.");
            }
            expression = parts[0];
        }
        expression = RemoveForce(expression, out char force);
        AssemblyExpression parsed = AssemblyExpression.Parse(expression, statement.Line);
        statement.Expressions = [parsed];
        bool hasShort = instructions.ContainsKey((statement.Operation, shortMode));
        bool hasLong = instructions.ContainsKey((statement.Operation, longMode));
        if (shortMode == longMode)
        {
            if (force == 'a')
            {
                throw Error(statement.Line, "This indirect addressing form requires a zero-page address.");
            }
            return shortMode;
        }
        if (force == 'z')
        {
            return shortMode;
        }
        if (force == 'a')
        {
            return longMode;
        }
        if (!hasShort)
        {
            return longMode;
        }
        if (!hasLong)
        {
            return shortMode;
        }
        ExpressionValue value = parsed.Evaluate(resolver, statement.Address, statement.Line);
        // Unknown forward references remain absolute in both passes; use z: to request zero page.
        return value.IsKnown && value.Number is >= 0 and <= 0xff ? shortMode : longMode;
    }

    private static void EmitStatement(Statement statement, AssemblyResolver resolver, Span<byte> output)
    {
        long Evaluate(AssemblyExpression expression) =>
            EvaluateFinal(expression, resolver, statement.Address, statement.Line);
        if (statement.Operation == ".ASSERT")
        {
            if (Evaluate(statement.Expressions[0]) == 0)
                throw Error(statement.Line, statement.AssertionMessage!, "assembly.assertion_failed", expected: "nonzero", actual: "0");
            return;
        }
        if (statement.Operation is ".FILL" or ".ALIGN")
        {
            output.Fill((byte)RequireRange(Evaluate(statement.Expressions[0]), 0xff, statement.Line, "Fill byte"));
            return;
        }
        if (statement.Instruction is null)
        {
            int offset = 0;
            bool words = statement.Operation == ".WORD";
            foreach (DataItem item in statement.Items)
            {
                if (item.Bytes is not null)
                {
                    item.Bytes.CopyTo(output[offset..]);
                    offset += item.Bytes.Length;
                }
                else
                {
                    int value = RequireRange(EvaluateFinal(item.Expression!, resolver,
                        statement.Address!.Value + offset, statement.Line), words ? 0xffff : 0xff,
                        statement.Line, words ? "Word" : "Byte");
                    output[offset++] = (byte)value;
                    if (words)
                    {
                        output[offset++] = (byte)(value >> 8);
                    }
                }
            }
            return;
        }
        Instruction instruction = statement.Instruction;
        output[0] = instruction.Opcode;
        if (instruction.Mode is AddressingMode.Implied or AddressingMode.Accumulator)
        {
            return;
        }
        if (instruction.Mode == AddressingMode.Relative)
        {
            output[1] = BranchOffset(Evaluate(statement.Expressions[0]), statement);
        }
        else if (instruction.Mode == AddressingMode.ZeroPageRelative)
        {
            output[1] = (byte)RequireRange(Evaluate(statement.Expressions[0]), 0xff, statement.Line, "Zero-page address");
            output[2] = BranchOffset(Evaluate(statement.Expressions[1]), statement);
        }
        else
        {
            int value = RequireRange(Evaluate(statement.Expressions[0]), instruction.Length == 2 ? 0xff : 0xffff,
                statement.Line, instruction.Length == 2 ? "Byte operand" : "Address");
            output[1] = (byte)value;
            if (instruction.Length == 3)
            {
                output[2] = (byte)(value >> 8);
            }
        }
    }

    private static byte BranchOffset(long target, Statement statement)
    {
        int destination = RequireRange(target, 0xffff, statement.Line, "Branch target");
        int nextAddress = (statement.Address!.Value + statement.Length) & 0xffff;
        int difference = (destination - nextAddress) & 0xffff;
        if (difference >= 0x8000)
        {
            difference -= 0x10000;
        }
        if (difference is < -128 or > 127)
        {
            throw Error(statement.Line, $"Branch target is out of range ({difference}; expected -128 through 127 bytes).",
                "assembly.branch_range", expected: "-128..127", actual: difference.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return unchecked((byte)(sbyte)difference);
    }

    private static string RemoveForce(string operand, out char force)
    {
        operand = operand.Trim();
        force = '\0';
        if (operand.Length >= 2 && operand[1] == ':' && char.ToLowerInvariant(operand[0]) is 'a' or 'z')
        {
            force = char.ToLowerInvariant(operand[0]);
            return operand[2..].Trim();
        }
        return operand;
    }

    private static long EvaluateLayout(AssemblyExpression expression, AssemblyResolver resolver, int? address, int line)
    {
        ExpressionValue value = expression.Evaluate(resolver, address, line);
        if (!value.IsKnown)
        {
            throw Error(line, "Layout expression must resolve here; use a numeric value or previously defined label.");
        }
        return value.Number;
    }

    private static long EvaluateFinal(AssemblyExpression expression, AssemblyResolver resolver, int? address, int line)
    {
        ExpressionValue value = expression.Evaluate(resolver, address, line);
        if (!value.IsKnown)
        {
            throw Error(line, "Expression requires a defined origin.");
        }
        return value.Number;
    }

    private static int RequireRange(long value, int maximum, int line, string description)
    {
        if (value < 0 || value > maximum)
        {
            throw Error(line, $"{description} {value} is outside 0..{maximum}; use < or > for explicit byte extraction.",
                "assembly.value_range", expected: $"0..{maximum}", actual: value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return (int)value;
    }

    private static void RequireOrigin(int? address, int line)
    {
        if (!address.HasValue)
        {
            throw Error(line, "An origin is required; use .org $0800, * = $0800, or supply an origin.");
        }
    }

    private static void AddSymbol(AssemblySymbol symbol, Dictionary<string, AssemblySymbol> symbols)
    {
        if (symbols.Count >= 65_536)
        {
            throw Error(symbol.Line, "Source exceeds 65536 symbols.");
        }
        if (!symbols.TryAdd(symbol.Name, symbol))
        {
            throw Error(symbol.Line, $"Symbol '{symbol.Name}' is already defined on line {symbols[symbol.Name].Line}.",
                "assembly.duplicate_symbol", symbol.Name);
        }
    }

    private static int IdentifierLength(string value)
    {
        int length = 1;
        while (length < value.Length && AssemblyExpression.IsIdentifierPart(value[length]))
        {
            length++;
        }
        return length;
    }

    private static bool IsIdentifier(string value) => value.Length > 0 &&
        AssemblyExpression.IsIdentifierStart(value[0]) && IdentifierLength(value) == value.Length;

    private static string StripComment(string text, int line)
    {
        char quote = '\0';
        bool escaped = false;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (quote != '\0')
            {
                if (!escaped && current == quote)
                {
                    quote = '\0';
                }
                escaped = !escaped && current == '\\' && quote == '"';
            }
            else if (current == ';')
            {
                return text[..index];
            }
            else if (current is '"' or '\'')
            {
                quote = current;
            }
        }
        if (quote != '\0')
        {
            throw Error(line, "Unclosed string or character literal.");
        }
        return text;
    }

    private static List<string> SplitOperands(string text, int line)
    {
        List<string> result = [];
        int start = 0;
        int depth = 0;
        char quote = '\0';
        bool escaped = false;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (quote != '\0')
            {
                if (!escaped && current == quote)
                {
                    quote = '\0';
                }
                escaped = !escaped && current == '\\' && quote == '"';
                continue;
            }
            if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == '(')
            {
                if (++depth > 64)
                {
                    throw Error(line, "Operand nesting exceeds 64 levels.");
                }
            }
            else if (current == ')')
            {
                if (--depth < 0)
                {
                    throw Error(line, "Unexpected closing parenthesis.");
                }
            }
            else if (current == ',' && depth == 0)
            {
                result.Add(text[start..index].Trim());
                start = index + 1;
            }
        }
        result.Add(text[start..].Trim());
        if (depth != 0 || quote != '\0')
        {
            throw Error(line, "Unclosed parenthesis or string.");
        }
        if (result.Any(item => item.Length == 0))
        {
            throw Error(line, "An operand is missing.");
        }
        return result;
    }

    private static int FindCloseParenthesis(string text, int line)
    {
        int depth = 0;
        bool character = false;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\'')
            {
                character = !character;
            }
            else if (!character && text[index] == '(')
            {
                depth++;
            }
            else if (!character && text[index] == ')' && --depth == 0)
            {
                return index;
            }
        }
        throw Error(line, "Unclosed indirect addressing parenthesis.");
    }

    private static byte[] ParseString(string text, int line)
    {
        if (!text.StartsWith('"')) throw Error(line, "Expected a quoted string.");
        List<byte> bytes = [];
        for (int index = 1; index < text.Length; index++)
        {
            char current = text[index];
            if (current == '"')
            {
                if (index != text.Length - 1)
                {
                    throw Error(line, "Unexpected text after string literal.");
                }
                return bytes.ToArray();
            }
            if (current == '\\')
            {
                if (++index == text.Length)
                {
                    throw Error(line, "Incomplete string escape.");
                }
                current = text[index] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    '\\' => '\\',
                    '"' => '"',
                    _ => throw Error(line, "Supported string escapes are \\n, \\r, \\t, \\0, \\\\, and \\\".")
                };
            }
            if (current > 0x7f)
            {
                throw Error(line, "Strings require ASCII characters; use .byte for other byte values.");
            }
            bytes.Add((byte)current);
        }
        throw Error(line, "Unclosed string literal.");
    }

    private sealed class Statement(int line, List<string> labels, string operation, string operand)
    {
        public int Line { get; } = line;
        public List<string> Labels { get; } = labels;
        public string Operation { get; } = operation;
        public string Operand { get; } = operand;
        public AssemblySymbol? Symbol { get; init; }
        public int? Address { get; set; }
        public int Length { get; set; }
        public Instruction? Instruction { get; set; }
        public AssemblyExpression[] Expressions { get; set; } = [];
        public List<DataItem> Items { get; } = [];
        public string? AssertionMessage { get; set; }
    }

    private sealed record DataItem(byte[]? Bytes, AssemblyExpression? Expression);
}
