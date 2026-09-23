// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[assembly: InternalsVisibleTo("A2Utils.Cli.Tests")]

namespace A2Utils.Cli;

/// <summary>Official MCP stdio surface for invoking the stable JSON CLI contract.</summary>
public static class A2McpServer
{
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private const int ResponseEnvelopeAllowanceBytes = 512;

    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using McpServer server = McpServer.Create(new StdioServerTransport("A2Utils"), CreateOptions());
        await server.RunAsync(cancellationToken);
    }

    public static McpServerOptions CreateOptions() => new()
    {
        ServerInfo = new()
        {
            Name = "a2utils",
            Version = typeof(CliApplication).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"
        },
        ServerInstructions = "Use a2_capabilities to discover commands and schemas, then call a2_cli with ordinary A2Utils arguments. Paths are resolved from the server process working directory.",
        ToolCollection =
        [
            McpServerTool.Create(Invoke,
                new() { Name = "a2_cli", Description = "Run one A2Utils command through its stable JSON contract. Pass arguments without the executable name or --json." }),
            McpServerTool.Create(Capabilities,
                new() { Name = "a2_capabilities", Description = "Return the complete typed A2Utils command, option, schema, format, and side-effect catalog." }),
            McpServerTool.Create(Schema,
                new() { Name = "a2_schema", Description = "Return one bundled A2Utils JSON Schema by name." })
        ]
    };

    public static CallToolResult Invoke(string[] arguments, CancellationToken cancellationToken = default)
        => InvokeCore(arguments, MaxResponseBytes,
            static (args, output, error, token) => CliApplication.Run(args, output, error, token),
            cancellationToken);

    internal static CallToolResult InvokeForTesting(string[] arguments, int outputLimitBytes,
        Func<string[], TextWriter, TextWriter, CancellationToken, int> runner,
        CancellationToken cancellationToken = default)
        => InvokeCore(arguments, outputLimitBytes, runner, cancellationToken);

    private static CallToolResult InvokeCore(string[] arguments, int outputLimitBytes,
        Func<string[], TextWriter, TextWriter, CancellationToken, int> runner,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputLimitBytes);
        ArgumentNullException.ThrowIfNull(runner);
        if (arguments is null || arguments.Length == 0 || arguments.Length > 4096
            || arguments.Any(argument => argument is null || argument.Length > 32768 || argument.IndexOf('\0') >= 0)
            || arguments.Any(argument => Encoding.UTF8.GetByteCount(argument) > 32 * 1024)
            || arguments.Sum(argument => (long)Encoding.UTF8.GetByteCount(argument)) > 1024 * 1024)
        {
            return ToolError("arguments must contain 1..4096 strings, at most 32 KiB each and 1 MiB total.");
        }
        if (CliApplication.SelectsMcpCommand(arguments))
            return ToolError("The MCP server cannot recursively invoke the mcp command.");
        if (arguments.Any(argument => argument.Equals("--quiet", StringComparison.Ordinal)
            || IsInlineOption(argument, "--quiet")))
            return ToolError("Do not combine MCP invocation with --quiet; the tool returns the JSON result directly.");
        if (arguments.Where((argument, index) => IsInlineOption(argument, "--json") ||
            argument == "--json" && index + 1 < arguments.Length &&
            bool.TryParse(arguments[index + 1], out _)).Any())
            return ToolError("Do not set --json with a value; the MCP server requires JSON output.");

        string[] cliArguments = arguments.Contains("--json", StringComparer.Ordinal)
            ? arguments.ToArray() : [.. arguments, "--json"];
        OutputBudget budget = new(outputLimitBytes);
        using BoundedUtf8TextWriter output = new(budget);
        using BoundedUtf8TextWriter error = new(budget);
        int exitCode = runner(cliArguments, output, error, cancellationToken);
        string stdout = output.GetText().Trim();
        string stderr = error.GetText().Trim();
        if (budget.Exceeded)
            return ToolError($"Command output exceeded the {FormatByteLimit(outputLimitBytes)} MCP response limit.");

        JsonElement? envelope = Parse(stdout.Length != 0 ? stdout : stderr);
        JsonElement structured = JsonSerializer.SerializeToElement(new { exitCode, envelope });
        string text = envelope.HasValue
            ? EnvelopeSummary(envelope.Value, exitCode)
            : stdout.Length != 0 && stderr.Length != 0 ? stdout + Environment.NewLine + stderr
            : stdout.Length != 0 ? stdout : stderr.Length != 0 ? stderr
            : JsonSerializer.Serialize(new { exitCode });
        long responseBytes = JsonSerializer.SerializeToUtf8Bytes(text).LongLength +
            Encoding.UTF8.GetByteCount(structured.GetRawText()) + ResponseEnvelopeAllowanceBytes;
        if (responseBytes > outputLimitBytes)
            return ToolError($"Command response exceeded the {FormatByteLimit(outputLimitBytes)} MCP response limit.");
        return new()
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = structured,
            IsError = exitCode != 0
        };
    }

    public static CallToolResult Capabilities(CancellationToken cancellationToken = default)
        => Invoke(["capabilities"], cancellationToken);

    public static CallToolResult Schema(string name, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(name) ? ToolError("name is required.")
            : Invoke(["schema", name], cancellationToken);

    private static JsonElement? Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string EnvelopeSummary(JsonElement envelope, int exitCode)
    {
        string command = envelope.ValueKind == JsonValueKind.Object &&
            envelope.TryGetProperty("command", out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()! : "A2Utils command";
        return $"{command} completed with exit code {exitCode}; see structuredContent.envelope.";
    }

    private static bool IsInlineOption(string argument, string name)
        => argument.Length > name.Length && argument.StartsWith(name, StringComparison.Ordinal) &&
            argument[name.Length] is '=' or ':';

    private static string FormatByteLimit(int byteLimit)
        => byteLimit % (1024 * 1024) == 0
            ? $"{byteLimit / (1024 * 1024)} MiB"
            : $"{byteLimit} byte" + (byteLimit == 1 ? string.Empty : "s");

    private sealed class OutputBudget(int byteLimit)
    {
        public object SyncRoot { get; } = new();
        public int RemainingBytes { get; private set; } = byteLimit;
        public bool Exceeded { get; private set; }

        public bool TryConsume(int byteCount)
        {
            if (Exceeded) return false;
            if (byteCount <= RemainingBytes)
            {
                RemainingBytes -= byteCount;
                return true;
            }
            Exceeded = true;
            return false;
        }
    }

    private sealed class BoundedUtf8TextWriter(OutputBudget budget) : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private char? _pendingHighSurrogate;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            Span<char> character = stackalloc char[1];
            character[0] = value;
            Write(character);
        }

        public override void Write(char[] buffer, int index, int count)
            => Write(buffer.AsSpan(index, count));

        public override void Write(string? value)
        {
            if (value is not null) Write(value.AsSpan());
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            lock (budget.SyncRoot)
            {
                if (budget.Exceeded)
                {
                    _pendingHighSurrogate = null;
                    return;
                }

                int index = 0;
                if (_pendingHighSurrogate is char pending)
                {
                    if (buffer.Length != 0 && char.IsLowSurrogate(buffer[0]))
                    {
                        Span<char> pair = stackalloc char[2];
                        pair[0] = pending;
                        pair[1] = buffer[0];
                        _pendingHighSurrogate = null;
                        if (!Append(pair, 4)) return;
                        index++;
                    }
                    else if (buffer.Length != 0)
                    {
                        Span<char> invalid = stackalloc char[1];
                        invalid[0] = pending;
                        _pendingHighSurrogate = null;
                        if (!Append(invalid, 3)) return;
                    }
                    else
                    {
                        return;
                    }
                }

                while (index < buffer.Length)
                {
                    char character = buffer[index];
                    if (char.IsHighSurrogate(character))
                    {
                        if (index + 1 == buffer.Length)
                        {
                            _pendingHighSurrogate = character;
                            return;
                        }
                        if (char.IsLowSurrogate(buffer[index + 1]))
                        {
                            if (!Append(buffer.Slice(index, 2), 4)) return;
                            index += 2;
                            continue;
                        }
                    }

                    int byteCount = character switch
                    {
                        <= '\u007f' => 1,
                        <= '\u07ff' => 2,
                        _ => 3
                    };
                    if (!Append(buffer.Slice(index, 1), byteCount)) return;
                    index++;
                }
            }
        }

        public override void WriteLine() => Write(CoreNewLine);

        public string GetText()
        {
            lock (budget.SyncRoot)
            {
                if (_pendingHighSurrogate is char pending)
                {
                    Span<char> invalid = stackalloc char[1];
                    invalid[0] = pending;
                    _pendingHighSurrogate = null;
                    Append(invalid, 3);
                }
                return _buffer.ToString();
            }
        }

        private bool Append(ReadOnlySpan<char> value, int byteCount)
        {
            if (!budget.TryConsume(byteCount)) return false;
            _buffer.Append(value);
            return true;
        }
    }

    private static CallToolResult ToolError(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        StructuredContent = JsonSerializer.SerializeToElement(new { error = message }),
        IsError = true
    };
}
