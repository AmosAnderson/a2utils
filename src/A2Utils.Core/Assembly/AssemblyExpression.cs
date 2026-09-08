namespace A2Utils.Core.Assembly;

internal readonly record struct ExpressionValue(bool IsKnown, long Number)
{
    public static ExpressionValue Unknown => default;
    public static ExpressionValue Of(long number) => new(true, number);
}

internal abstract record AssemblyExpression
{
    public ExpressionValue Evaluate(AssemblyResolver resolver, int? address, int line)
    {
        resolver.EnterExpression(line);
        try
        {
            return EvaluateCore(resolver, address, line);
        }
        finally
        {
            resolver.ExitExpression();
        }
    }

    protected abstract ExpressionValue EvaluateCore(AssemblyResolver resolver, int? address, int line);

    public static AssemblyExpression Parse(string text, int line) => new Parser(text, line).Parse();

    private sealed record NumberExpression(long Number) : AssemblyExpression
    {
        protected override ExpressionValue EvaluateCore(AssemblyResolver resolver, int? address, int line) =>
            ExpressionValue.Of(Number);
    }

    private sealed record AddressExpression : AssemblyExpression
    {
        protected override ExpressionValue EvaluateCore(AssemblyResolver resolver, int? address, int line) =>
            address.HasValue ? ExpressionValue.Of(address.Value) : ExpressionValue.Unknown;
    }

    private sealed record SymbolExpression(string Name) : AssemblyExpression
    {
        protected override ExpressionValue EvaluateCore(AssemblyResolver resolver, int? address, int line) =>
            resolver.Resolve(Name, line);
    }

    private sealed record UnaryExpression(char Operator, AssemblyExpression Operand) : AssemblyExpression
    {
        protected override ExpressionValue EvaluateCore(AssemblyResolver resolver, int? address, int line)
        {
            ExpressionValue value = Operand.Evaluate(resolver, address, line);
            if (!value.IsKnown)
            {
                return value;
            }
            try
            {
                return ExpressionValue.Of(Operator switch
                {
                    '+' => value.Number,
                    '-' => checked(-value.Number),
                    '<' => value.Number & 0xff,
                    '>' => (value.Number >> 8) & 0xff,
                    _ => throw new InvalidOperationException()
                });
            }
            catch (OverflowException)
            {
                throw Assembler.Error(line, "Expression exceeds the signed 64-bit range.");
            }
        }
    }

    private sealed record BinaryExpression(char Operator, AssemblyExpression Left, AssemblyExpression Right)
        : AssemblyExpression
    {
        protected override ExpressionValue EvaluateCore(AssemblyResolver resolver, int? address, int line)
        {
            ExpressionValue left = Left.Evaluate(resolver, address, line);
            ExpressionValue right = Right.Evaluate(resolver, address, line);
            if (!left.IsKnown || !right.IsKnown)
            {
                return ExpressionValue.Unknown;
            }
            try
            {
                return ExpressionValue.Of(Operator == '+'
                    ? checked(left.Number + right.Number)
                    : checked(left.Number - right.Number));
            }
            catch (OverflowException)
            {
                throw Assembler.Error(line, "Expression exceeds the signed 64-bit range.");
            }
        }
    }

    private sealed class Parser(string text, int line)
    {
        private int _position;
        private int _depth;
        private int _operations;

        public AssemblyExpression Parse()
        {
            if (text.Length > 4096)
            {
                throw Assembler.Error(line, "An expression may contain at most 4096 characters.");
            }
            AssemblyExpression result = ParseSum();
            SkipWhiteSpace();
            if (_position != text.Length)
            {
                throw Assembler.Error(line, $"Unexpected character '{text[_position]}' in expression.");
            }
            return result;
        }

        private AssemblyExpression ParseSum()
        {
            AssemblyExpression result = ParseUnary();
            while (true)
            {
                SkipWhiteSpace();
                if (_position == text.Length || text[_position] is not ('+' or '-'))
                {
                    return result;
                }
                if (++_operations > 128)
                {
                    throw Assembler.Error(line, "An expression may contain at most 128 binary operations.");
                }
                char operation = text[_position++];
                result = new BinaryExpression(operation, result, ParseUnary());
            }
        }

        private AssemblyExpression ParseUnary()
        {
            if (++_depth > 64)
            {
                throw Assembler.Error(line, "Expression nesting exceeds 64 levels.");
            }
            try
            {
                SkipWhiteSpace();
                if (_position == text.Length)
                {
                    throw Assembler.Error(line, "Expected an expression.");
                }
                char current = text[_position++];
                if (current is '+' or '-' or '<' or '>')
                {
                    return new UnaryExpression(current, ParseUnary());
                }
                if (current == '(')
                {
                    AssemblyExpression result = ParseSum();
                    SkipWhiteSpace();
                    if (_position == text.Length || text[_position++] != ')')
                    {
                        throw Assembler.Error(line, "Unclosed expression parenthesis.");
                    }
                    return result;
                }
                if (current == '*')
                {
                    return new AddressExpression();
                }
                if (current == '\'')
                {
                    if (_position + 1 >= text.Length || text[_position + 1] != '\'' || text[_position] > 0x7f)
                    {
                        throw Assembler.Error(line, "Character literals must contain exactly one ASCII character.");
                    }
                    char value = text[_position];
                    _position += 2;
                    return new NumberExpression(value);
                }
                if (current is '$' or '%' || char.IsAsciiDigit(current))
                {
                    int radix = current switch { '$' => 16, '%' => 2, _ => 10 };
                    if (radix == 10)
                    {
                        _position--;
                        if (text.AsSpan(_position).StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            radix = 16;
                            _position += 2;
                        }
                    }
                    int start = _position;
                    long value = 0;
                    while (_position < text.Length)
                    {
                        char digit = char.ToUpperInvariant(text[_position]);
                        int number = digit is >= '0' and <= '9' ? digit - '0'
                            : digit is >= 'A' and <= 'F' ? digit - 'A' + 10 : -1;
                        if (number < 0 || number >= radix)
                        {
                            break;
                        }
                        try
                        {
                            value = checked(value * radix + number);
                        }
                        catch (OverflowException)
                        {
                            throw Assembler.Error(line, "Numeric literal exceeds the signed 64-bit range.");
                        }
                        _position++;
                    }
                    if (_position == start)
                    {
                        throw Assembler.Error(line, "Expected digits after the numeric prefix.");
                    }
                    return new NumberExpression(value);
                }
                if (IsIdentifierStart(current))
                {
                    int start = _position - 1;
                    while (_position < text.Length && IsIdentifierPart(text[_position]))
                    {
                        _position++;
                    }
                    return new SymbolExpression(text[start.._position]);
                }
                throw Assembler.Error(line, $"Unexpected character '{current}' in expression.");
            }
            finally
            {
                _depth--;
            }
        }

        private void SkipWhiteSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }
    }

    public static bool IsIdentifierStart(char value) => char.IsAsciiLetter(value) || value == '_';
    public static bool IsIdentifierPart(char value) => IsIdentifierStart(value) || char.IsAsciiDigit(value);
}

internal sealed class AssemblySymbol(string name, int line, AssemblyExpression? expression)
{
    public string Name { get; } = name;
    public int Line { get; } = line;
    public AssemblyExpression? Expression { get; } = expression;
    public int? Address { get; set; }
}

internal sealed class AssemblyResolver(Dictionary<string, AssemblySymbol> symbols, bool requireKnown,
    CancellationToken cancellationToken)
{
    private readonly HashSet<string> _resolving = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknown = new(StringComparer.OrdinalIgnoreCase);
    private int _expressionDepth;

    public void EnterExpression(int line)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_expressionDepth == 0)
        {
            // Unknown labels can acquire addresses between statements during layout.
            _unknown.Clear();
        }
        if (++_expressionDepth > 256)
        {
            throw Assembler.Error(line, "Expression evaluation exceeds 256 nested operations and references.");
        }
    }

    public void ExitExpression() => _expressionDepth--;

    public ExpressionValue Resolve(string name, int line)
    {
        if (_resolved.TryGetValue(name, out long cached))
        {
            return ExpressionValue.Of(cached);
        }
        if (_unknown.Contains(name))
        {
            return ExpressionValue.Unknown;
        }
        if (!symbols.TryGetValue(name, out AssemblySymbol? symbol))
        {
            if (requireKnown)
            {
                throw Assembler.Error(line, $"Undefined symbol '{name}'.");
            }
            return ExpressionValue.Unknown;
        }
        if (symbol.Expression is null)
        {
            return symbol.Address.HasValue ? ExpressionValue.Of(symbol.Address.Value) : ExpressionValue.Unknown;
        }
        if (_resolving.Count >= 128)
        {
            throw Assembler.Error(line, "Constant references exceed 128 levels.");
        }
        if (!_resolving.Add(name))
        {
            throw Assembler.Error(line, $"Circular constant reference involving '{name}'.");
        }
        try
        {
            ExpressionValue result = symbol.Expression.Evaluate(this, symbol.Address, symbol.Line);
            if (requireKnown && !result.IsKnown)
            {
                throw Assembler.Error(symbol.Line, $"Constant '{name}' needs a defined origin before using '*'.");
            }
            if (result.IsKnown)
            {
                _resolved[name] = result.Number;
            }
            else
            {
                _unknown.Add(name);
            }
            return result;
        }
        finally
        {
            _resolving.Remove(name);
        }
    }
}
