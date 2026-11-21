// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.CommandLine;
using A2Utils.Core;
using A2Utils.Core.Backends;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddArchiveCommands(RootCommand root)
    {
        Command archive = new("archive", "Read native ShrinkIt (NuFX) file and disk archives.");
        root.Subcommands.Add(archive);
        Command list = new("ls", "List records, stored names, forks, disk images, and compression methods.");
        Argument<string> listPath = new("ARCHIVE");
        list.Arguments.Add(listPath);
        list.SetAction(parse =>
        {
            using var source = ShrinkItArchive.Open(parse.GetValue(listPath)!, _cancellationToken);
            EmitDiagnostics(source.Listing.Diagnostics);
            return Result("archive.ls", source.Listing, string.Join(Environment.NewLine, source.Listing.Entries.Select(e =>
                $"{e.Index,5}  {e.Name}  {string.Join(", ", e.Parts.Select(p => $"{p.Name} {p.Length} bytes ({p.Compression})"))}")));
        });
        archive.Subcommands.Add(list);

        foreach (bool catalog in new[] { false, true })
        {
            Command command = catalog
                ? new("catalog", "List the DOS 3.3 or ProDOS filesystem inside an archived disk image.")
                : new("extract", "Extract a data fork, resource fork, or disk image to a host file.");
            archive.Subcommands.Add(command);
            Argument<string> archivePath = new("ARCHIVE");
            Argument<string?> entry = new("ENTRY") { Arity = ArgumentArity.ZeroOrOne, Description = "Exact stored entry name." };
            Option<int?> index = new("--index") { Description = "One-based record index from archive ls." };
            Option<string?> part = new("--part") { Description = "data, resource, or disk (defaults to data for files and disk for images)." };
            Option<string> destination = new("--to") { Required = true, Description = "Output file for the selected part." };
            Option<bool> overwrite = new("--overwrite") { Description = "Replace an existing output file after validation." };
            Option<bool> recursive = new("--recursive") { Description = "Include subdirectory contents." };
            command.Arguments.Add(archivePath);
            command.Arguments.Add(entry);
            command.Options.Add(index);
            if (catalog)
                command.Options.Add(recursive);
            else
            {
                command.Options.Add(part);
                command.Options.Add(destination);
                command.Options.Add(overwrite);
            }
            command.SetAction(parse =>
            {
                using var source = ShrinkItArchive.Open(parse.GetValue(archivePath)!, _cancellationToken);
                string? selector = parse.GetValue(entry);
                int? record = parse.GetValue(index);
                if (catalog)
                {
                    var contents = source.Catalog(selector, record, parse.GetValue(recursive), parse.GetValue(_inputOrder),
                        parse.GetValue(_inputFs), _cancellationToken);
                    EmitDiagnostics(contents.Diagnostics);
                    return Result("archive.catalog", contents, $"Volume: {contents.VolumeName} ({contents.FileSystem})"
                        + Environment.NewLine + string.Join(Environment.NewLine, contents.Entries.Select(e =>
                            $"{(e.IsDirectory ? "DIR" : e.Type),-4} {e.Length,10}  {e.Path}")));
                }
                EmitDiagnostics(source.Listing.Diagnostics);
                string? partName = parse.GetValue(part);
                ValidateChoice(partName, "--part", "data", "resource", "disk");
                ShrinkItExtraction extracted = source.Extract(parse.GetValue(destination)!, selector, record,
                    partName?.ToLowerInvariant(), parse.GetValue(overwrite), _cancellationToken);
                return Result("archive.extract", extracted, $"Extracted {extracted.Name} ({extracted.Part}) to {extracted.OutputPath}");
            });
        }
    }
}
