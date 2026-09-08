using System.CommandLine;
using System.Globalization;
using A2Utils.Core;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddTransferCommands(Command disk)
    {
        AddExportCommand(disk);
        AddCopyCommand(disk);
        AddMoveCommand(disk);
        AddDirectoryImportCommand(disk);
    }

    private void AddExportCommand(Command disk)
    {
        Command command = new("export", "Export a logical file payload or readable UTF-8 text.");
        Argument<string> image = new("IMAGE");
        Argument<string> path = new("PATH");
        Option<string> destination = new("--to") { Required = true, Description = "Host file to create." };
        Option<string> format = new("--format") { DefaultValueFactory = _ => "binary", Description = "binary (payload bytes) or text (UTF-8 with LF newlines)." };
        Option<bool> overwrite = new("--overwrite") { Description = "Allow replacing the host output file." };
        command.Arguments.Add(image);
        command.Arguments.Add(path);
        command.Options.Add(destination);
        command.Options.Add(format);
        command.Options.Add(overwrite);
        command.SetAction(parse =>
        {
            ValidateChoice(parse.GetValue(format), "--format", "binary", "text");
            using DiskSession session = Open(parse.GetValue(image)!);
            ImageWriteResult result = FileExport.Export(session, parse.GetValue(path)!,
                parse.GetValue(destination)!, parse.GetValue(format)!, parse.GetValue(overwrite), _cancellationToken);
            return Result("export", result, $"Exported {parse.GetValue(path)} to {result.OutputPath}");
        });
        disk.Subcommands.Add(command);
    }

    private void AddCopyCommand(Command disk)
    {
        Command command = new("copy", "Copy a file or directory to an exact new image path, preserving stored bytes and metadata.");
        Argument<string> image = new("IMAGE") { Description = "Image receiving the copy." };
        Argument<string> sourcePath = new("SOURCE") { Description = "Entry to copy." };
        Argument<string> destinationPath = new("DESTINATION") { Description = "Exact new path; parent directory must exist." };
        Option<string?> from = new("--from") { Description = "Separate source image. Must use the same filesystem as IMAGE." };
        Option<bool> recursive = new("--recursive") { Description = "Copy the directory and all its contents." };
        Option<string?> sourceOrder = new("--source-order") { Description = "Layout override for --from: dos or prodos." };
        Option<string?> sourceFs = new("--source-fs") { Description = "Filesystem override for --from: dos33 or prodos." };
        command.Arguments.Add(image);
        command.Arguments.Add(sourcePath);
        command.Arguments.Add(destinationPath);
        foreach (Option option in new Option[] { from, recursive, sourceOrder, sourceFs }) command.Options.Add(option);
        WriteOptions write = AddWriteOptions(command);
        command.SetAction(parse =>
        {
            string? sourceImage = parse.GetValue(from);
            ValidateChoice(parse.GetValue(sourceOrder), "--source-order", "dos", "prodos");
            ValidateChoice(parse.GetValue(sourceFs), "--source-fs", "dos33", "prodos");
            if (sourceImage is null && (parse.GetValue(sourceOrder) is not null || parse.GetValue(sourceFs) is not null))
            {
                throw new DiskException("invalid_arguments", "Source overrides require --from.", 2);
            }
            if (sourceImage is not null && parse.GetValue(write.Output) is { } output)
            {
                ImageTransactions.EnsureDistinctPaths(sourceImage, output);
            }
            bool sameImage = sourceImage is not null && string.Equals(Path.GetFullPath(sourceImage),
                Path.GetFullPath(parse.GetValue(image)!), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            return Mutate("copy", parse, image, write, destination =>
            {
                if (sourceImage is null || sameImage)
                {
                    if ((parse.GetValue(sourceOrder) is { } order && order != destination.Info.Order)
                        || (parse.GetValue(sourceFs) is { } fs && fs != destination.Info.FileSystem))
                    {
                        throw new DiskException("conflicting_source_override", "Source overrides conflict with the destination image's detected layout or filesystem.", 3);
                    }
                    destination.Copy(parse.GetValue(sourcePath)!, parse.GetValue(destinationPath)!,
                        parse.GetValue(recursive), _cancellationToken);
                }
                else
                {
                    using DiskSession source = DiskSession.Open(sourceImage,
                        parse.GetValue(sourceOrder), parse.GetValue(sourceFs));
                    destination.CopyFrom(source, parse.GetValue(sourcePath)!, parse.GetValue(destinationPath)!,
                        parse.GetValue(recursive), _cancellationToken);
                }
            });
        });
        disk.Subcommands.Add(command);
    }

    private void AddMoveCommand(Command disk)
    {
        Command command = new("move", "Move or rename an entry within the image without reallocating its file data.");
        Argument<string> image = new("IMAGE");
        Argument<string> sourcePath = new("SOURCE");
        Argument<string> destinationPath = new("DESTINATION") { Description = "Exact new path; parent directory must exist." };
        command.Arguments.Add(image);
        command.Arguments.Add(sourcePath);
        command.Arguments.Add(destinationPath);
        WriteOptions write = AddWriteOptions(command);
        command.SetAction(parse => Mutate("move", parse, image, write,
            session => session.Move(parse.GetValue(sourcePath)!, parse.GetValue(destinationPath)!)));
        disk.Subcommands.Add(command);
    }

    private void AddDirectoryImportCommand(Command disk)
    {
        Command command = new("import", "Import the contents of a host directory in one image transaction.");
        Argument<string> image = new("IMAGE");
        Argument<string> hostDirectory = new("HOSTDIRECTORY");
        Option<string> destination = new("--to") { DefaultValueFactory = _ => "/", Description = "Existing image directory; defaults to volume root." };
        Option<string?> type = new("--type") { Description = "Type for every imported file; defaults to TXT with --format text." };
        Option<string> format = new("--format") { DefaultValueFactory = _ => "binary", Description = "binary payloads or UTF-8 text." };
        Option<bool> recursive = new("--recursive") { Description = "Include host subdirectories (ProDOS only)." };
        Option<string?> address = new("--load-address") { Description = "Required for DOS binary payloads, e.g. 0x2000." };
        Option<string?> auxiliary = new("--aux-type") { Description = "ProDOS auxiliary type for every imported file." };
        command.Arguments.Add(image);
        command.Arguments.Add(hostDirectory);
        foreach (Option option in new Option[] { destination, type, format, recursive, address, auxiliary }) command.Options.Add(option);
        WriteOptions write = AddWriteOptions(command);
        command.SetAction(parse =>
        {
            string contentFormat = parse.GetValue(format)!;
            ValidateChoice(contentFormat, "--format", "binary", "text");
            string? fileType = parse.GetValue(type) ?? (contentFormat == "text" ? "TXT" : null);
            if (fileType is null) throw new DiskException("invalid_arguments", "Binary directory import requires --type.", 2);
            if (contentFormat == "text") RequireTextType(fileType);
            if (parse.GetValue(address) is not null && parse.GetValue(auxiliary) is not null)
                throw new DiskException("invalid_arguments", "Specify --load-address or --aux-type, not both.", 2);
            if (WriteDestination(parse, image, write) is { } target)
            {
                string hostRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parse.GetValue(hostDirectory)!));
                string targetPath = Path.GetFullPath(target);
                StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (targetPath.Equals(hostRoot, comparison)
                    || targetPath.StartsWith(Path.EndsInDirectorySeparator(hostRoot) ? hostRoot : hostRoot + Path.DirectorySeparatorChar, comparison))
                {
                    throw new DiskException("import_output_in_source", "The destination image must be outside the host import directory.");
                }
            }
            return Mutate("import", parse, image, write, session =>
            {
                if (session.Info.FileSystem == "dos33" && IsDosBinaryType(fileType) && parse.GetValue(address) is null)
                    throw new DiskException("invalid_arguments", "DOS binary import requires --load-address.", 2);
                if ((session.Info.FileSystem == "dos33" && parse.GetValue(auxiliary) is not null)
                    || (session.Info.FileSystem == "prodos" && parse.GetValue(address) is not null))
                    throw new DiskException("invalid_arguments", "Use --load-address for DOS and --aux-type for ProDOS.", 2);
                if (parse.GetValue(address) is not null && !IsDosBinaryType(fileType))
                    throw new DiskException("invalid_arguments", "--load-address requires a DOS binary file type.", 2);
                DirectoryImport.Import(session, parse.GetValue(hostDirectory)!, parse.GetValue(destination)!, fileType,
                    ParseUShort(parse.GetValue(address) ?? parse.GetValue(auxiliary) ?? "0"), parse.GetValue(recursive),
                    contentFormat == "text" ? bytes => EncodeText(bytes, session, fileType) : null, _cancellationToken);
            });
        });
        disk.Subcommands.Add(command);
    }

    private static byte[] EncodeText(byte[] bytes, DiskSession session, string fileType)
    {
        RequireTextType(fileType);
        return AppleTextCodec.Encode(bytes, session.Info.FileSystem);
    }

    private static void RequireTextType(string fileType)
    {
        bool textType = fileType.Equals("T", StringComparison.OrdinalIgnoreCase)
            || fileType.Equals("TXT", StringComparison.OrdinalIgnoreCase)
            || (fileType.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && byte.TryParse(fileType.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte type) && type == 4);
        if (!textType)
        {
            throw new DiskException("invalid_arguments", "Text conversion requires the T/TXT (0x04) file type.", 2);
        }
    }

    private static string? WriteDestination(ParseResult parse, Argument<string> image, WriteOptions write)
        => parse.GetValue(write.Output) ?? (parse.GetValue(write.InPlace) ? parse.GetValue(image) : null);
}
