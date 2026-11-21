// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.CommandLine;
using System.Text;
using A2Utils.Core.Operations;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddInspectCommand(Command disk)
    {
        Command command = new("inspect", "Read a sector or block with hex, ASCII and host-file offsets.");
        Argument<string> image = new("IMAGE");
        Option<int?> block = new("--block") { Description = "ProDOS logical block number." };
        Option<int?> track = new("--track") { Description = "DOS track number (0–34)." };
        Option<int?> sector = new("--sector") { Description = "DOS logical sector number (0–15)." };
        Option<long?> offset = new("--offset") { Description = "Host-file byte offset within the payload." };
        command.Arguments.Add(image);
        foreach (Option option in new Option[] { block, track, sector, offset }) command.Options.Add(option);
        command.SetAction(parse =>
        {
            DiskInspectionResult result = DiskInspection.Read(parse.GetValue(image)!, parse.GetValue(block),
                parse.GetValue(track), parse.GetValue(sector), parse.GetValue(offset), parse.GetValue(_inputOrder),
                parse.GetValue(_inputFs), _cancellationToken);
            StringBuilder listing = new();
            foreach (DiskInspectionRange range in result.Ranges)
            {
                listing.Append($"Block {range.Block} +{range.BlockOffset}");
                if (range.Track is not null) listing.Append($", track {range.Track} sector {range.Sector}");
                listing.AppendLine($", file offset {range.FileOffset} (0x{range.FileOffset:X})");
                for (int i = 0; i < range.Ascii.Length; i += 16)
                {
                    string hex = string.Join(' ', Enumerable.Range(i, 16).Select(j => range.Hex.Substring(j * 2, 2)));
                    listing.AppendLine($"{range.FileOffset + i:X8}  {hex}  {range.Ascii.Substring(i, 16)}");
                }
            }
            return Result("disk.inspect", result, listing.ToString().TrimEnd());
        });
        disk.Subcommands.Add(command);
    }
}
