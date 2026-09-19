using System.CommandLine;
using System.CommandLine.Parsing;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    internal static bool SelectsMcpCommand(string[] arguments)
    {
        CliApplication application = new(TextWriter.Null, TextWriter.Null, CancellationToken.None);
        RootCommand root = application.BuildCommands();
        Command mcp = root.Subcommands.Single(command => command.Name == "mcp");
        ParseResult parse = root.Parse(arguments, ParserConfiguration);
        for (SymbolResult? result = parse.CommandResult; result is not null; result = result.Parent)
        {
            if (result is CommandResult command && ReferenceEquals(command.Command, mcp))
                return true;
        }
        return false;
    }

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
