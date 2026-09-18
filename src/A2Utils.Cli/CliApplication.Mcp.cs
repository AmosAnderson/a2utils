using System.CommandLine;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddMcpCommands(RootCommand root)
    {
        Command mcp = new("mcp", "Expose A2Utils as local Model Context Protocol tools for coding agents.");
        Command serve = new("serve", "Run the MCP 2026-07-28/2025-11-25 compatible server over stdio until input closes.");
        serve.SetAction(_ =>
        {
            A2McpServer.RunAsync(_cancellationToken).GetAwaiter().GetResult();
            return 0;
        });
        mcp.Subcommands.Add(serve);
        root.Subcommands.Add(mcp);
    }
}
