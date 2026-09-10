using System.CommandLine;
using A2Utils.Core.Graphics;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddGraphicsCommands(RootCommand root)
    {
        Command group = new("graphics", "Convert PNG assets and Apple II lo-res/hi-res screen pages.");
        root.Subcommands.Add(group);
        AddGraphicsAssetCommands(group);
        foreach (bool encode in new[] { true, false })
        {
            Command command = new(encode ? "encode" : "decode", encode
                ? "Convert a PNG to a raw Apple II screen page." : "Preview a raw screen page as PNG.");
            Argument<string> input = new("INPUT");
            Option<string> output = new("--to") { Required = true };
            Option<string> mode = new("--mode") { Required = true, Description = "lores, hires (monochrome), or hires-color (approximate artifact color)." };
            Option<bool> overwrite = new("--overwrite");
            command.Arguments.Add(input);
            command.Options.Add(output); command.Options.Add(mode); command.Options.Add(overwrite);
            command.SetAction(parse =>
            {
                string source = Path.GetFullPath(parse.GetValue(input)!);
                string destination = Path.GetFullPath(parse.GetValue(output)!);
                ImageTransactions.EnsureDistinctPaths(source, destination);
                byte[] bytes = ProgramFiles.ReadBytes(source, cancellationToken: _cancellationToken);
                string inputHash = ProgramFiles.Hash(bytes);
                string selectedMode = parse.GetValue(mode)!;
                byte[] result = encode ? AppleGraphics.EncodePng(bytes, selectedMode) : AppleGraphics.DecodePng(bytes, selectedMode);
                string hash = ProgramFiles.Hash(result);
                ImageWriteResult written = ImageTransactions.Create(destination, parse.GetValue(overwrite),
                    temporary => File.WriteAllBytes(temporary, result), temporary =>
                    {
                        if (ProgramFiles.Hash(ProgramFiles.ReadBytes(source, cancellationToken: _cancellationToken)) != inputHash)
                            throw new Core.DiskException("graphics.source_changed", "Input changed during conversion.");
                        if (ProgramFiles.Hash(ProgramFiles.ReadBytes(temporary)) != hash)
                            throw new Core.DiskException("graphics.validation", "Staged graphics output differs.", 4);
                    }, _cancellationToken);
                return Result("graphics." + command.Name, new
                {
                    written.OutputPath,
                    mode = selectedMode,
                    outputLength = result.Length,
                    sha256 = hash,
                    width = selectedMode == "lores" ? 40 : 280,
                    height = selectedMode == "lores" ? 48 : 192,
                    rendering = selectedMode == "hires" ? "monochrome" : "approximate-rgb-palette"
                }, $"Wrote {written.OutputPath} ({result.Length} bytes).");
            });
            group.Subcommands.Add(command);
        }
    }
}
