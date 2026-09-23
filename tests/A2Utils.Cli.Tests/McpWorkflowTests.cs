// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace A2Utils.Cli.Tests;

public sealed class McpWorkflowTests
{
    [Fact]
    public void Invoke_CliResult_PreservesStructuredEnvelopeAndExitCode()
    {
        CallToolResult result = A2McpServer.Invoke(["targets"]);

        Assert.False(result.IsError);
        JsonElement structured = result.StructuredContent!.Value;
        Assert.Equal(0, structured.GetProperty("exitCode").GetInt32());
        Assert.Equal("targets", structured.GetProperty("envelope").GetProperty("command").GetString());
        Assert.False(structured.TryGetProperty("stdout", out _));
        Assert.False(structured.TryGetProperty("stderr", out _));
        Assert.Contains("targets completed with exit code 0",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Invoke_OutputWithinResponseLimit_SucceedsAcrossSplitSurrogatePair()
    {
        const string json = "{\"envelopeType\":\"result\",\"command\":\"cpu-😀\"}";
        int emoji = json.IndexOf("😀", StringComparison.Ordinal);
        const int byteLimit = 1024;

        CallToolResult result = A2McpServer.InvokeForTesting(["targets"], byteLimit,
            (_, output, _, _) =>
            {
                output.Write(json[..(emoji + 1)]);
                output.Write(json[(emoji + 1)..]);
                return 0;
            });

        Assert.False(result.IsError);
        JsonElement structured = result.StructuredContent!.Value;
        Assert.Equal("cpu-😀", structured.GetProperty("envelope").GetProperty("command").GetString());
        Assert.False(structured.TryGetProperty("stdout", out _));
        Assert.False(structured.TryGetProperty("stderr", out _));
    }

    [Fact]
    public void Invoke_ParsedEnvelope_ResponseLimitIncludesStructuredAndTextContent()
    {
        const string json = "{\"command\":\"large\",\"value\":\"123456789012345678901234567890\"}";

        CallToolResult result = A2McpServer.InvokeForTesting(["targets"], 80,
            (_, output, _, _) =>
            {
                output.Write(json);
                return 0;
            });

        Assert.True(result.IsError);
        Assert.Contains("response exceeded", Assert.IsType<TextContentBlock>(
            Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Invoke_UnparsedEscapedText_UsesSerializedWireSizeForLimit()
    {
        string outputText = new('"', 100);

        CallToolResult result = A2McpServer.InvokeForTesting(["targets"], 700,
            (_, output, _, _) =>
            {
                output.Write(outputText);
                return 0;
            });

        Assert.True(result.IsError);
        Assert.Contains("response exceeded", Assert.IsType<TextContentBlock>(
            Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Invoke_OutputBeyondSharedUtf8Limit_ReturnsToolError()
    {
        const int byteLimit = 64;

        CallToolResult result = A2McpServer.InvokeForTesting(["targets"], byteLimit,
            (_, output, error, _) =>
            {
                for (int index = 0; index < 8; index++) output.Write("12345678");
                error.Write("é");
                for (int index = 0; index < 1000; index++) error.Write("discarded");
                return 0;
            });

        Assert.True(result.IsError);
        Assert.Contains("64 bytes", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Invoke_MultibyteArgumentBeyondUtf8Limit_IsRejectedBeforeDispatch()
    {
        bool invoked = false;

        CallToolResult result = A2McpServer.InvokeForTesting([new string('\u00e9', 16385)], 1024,
            (_, _, _, _) =>
            {
                invoked = true;
                return 0;
            });

        Assert.True(result.IsError);
        Assert.False(invoked);
        Assert.Contains("32 KiB", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Invoke_RecursiveServerOrQuietOutput_ReturnsToolError()
    {
        Assert.True(A2McpServer.Invoke(["mcp", "serve"]).IsError);
        Assert.True(A2McpServer.Invoke(["--json", "mcp", "serve"]).IsError);
        Assert.True(A2McpServer.Invoke(["--json=true", "mcp", "serve"]).IsError);
        Assert.True(A2McpServer.Invoke(["--input-fs", "prodos", "mcp", "serve"]).IsError);
        Assert.True(A2McpServer.Invoke(["targets", "--quiet"]).IsError);
        Assert.True(A2McpServer.Invoke(["--quiet=true", "targets"]).IsError);
        Assert.True(A2McpServer.Invoke(["--quiet:true", "targets"]).IsError);
        Assert.True(A2McpServer.Invoke(["--json=false", "targets"]).IsError);
        Assert.True(A2McpServer.Invoke(["--json:false", "targets"]).IsError);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Invoke_RecursiveServerAfterBooleanValue_RefusesBeforeInvokingCli(string value)
    {
        bool invoked = false;

        CallToolResult result = A2McpServer.InvokeForTesting(
            ["--verbose", value, "mcp", "serve"], 1024,
            (_, _, _, _) =>
            {
                invoked = true;
                return 0;
            });

        Assert.True(result.IsError);
        Assert.False(invoked);
        Assert.Contains("recursively invoke", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void Invoke_JsonWithSeparateFalseValue_RefusesNonJsonOutput(string value)
    {
        CallToolResult result = A2McpServer.Invoke(["--json", value, "targets"]);

        Assert.True(result.IsError);
        Assert.Contains("requires JSON output", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Invoke_BooleanValueBeforeOrdinaryCommand_PreservesStructuredEnvelope()
    {
        CallToolResult result = A2McpServer.Invoke(["--verbose", "false", "schema", "project"]);

        Assert.False(result.IsError);
        Assert.Equal("schema", result.StructuredContent!.Value.GetProperty("envelope")
            .GetProperty("command").GetString());
    }

    [Fact]
    public void Invoke_ResponseFileCannotExpandHiddenRecursiveCommand()
    {
        string response = Path.GetTempFileName();
        try
        {
            File.WriteAllText(response, "mcp serve");

            CallToolResult result = A2McpServer.Invoke(["@" + response]);

            Assert.True(result.IsError);
            Assert.Equal(2, result.StructuredContent!.Value.GetProperty("exitCode").GetInt32());
        }
        finally
        {
            File.Delete(response);
        }
    }

    [Fact]
    public async Task Protocol_InMemoryClient_ListsAndCallsA2Tools()
    {
        Pipe clientToServer = new();
        Pipe serverToClient = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        await using McpServer server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            A2McpServer.CreateOptions());
        Task serving = server.RunAsync(deadline.Token);

        await using McpClient client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: deadline.Token);
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Contains(tools, tool => tool.Name == "a2_cli");
        Assert.Contains(tools, tool => tool.Name == "a2_capabilities");
        Assert.Contains(tools, tool => tool.Name == "a2_schema");

        CallToolResult result = await client.CallToolAsync("a2_schema",
            new Dictionary<string, object?> { ["name"] = "execution" },
            cancellationToken: deadline.Token);
        Assert.False(result.IsError);
        Assert.Equal(0, result.StructuredContent!.Value.GetProperty("exitCode").GetInt32());

        deadline.Cancel();
        await serving;
    }
}
