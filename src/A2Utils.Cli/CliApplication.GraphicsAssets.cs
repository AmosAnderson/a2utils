using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2Utils.Core;
using A2Utils.Core.Graphics;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddGraphicsAssetCommands(Command graphics)
    {
        AddShapeCommands(graphics);
        AddDoubleHiresCommands(graphics);
        Command assets = new("assets", "Pack sprite, tile, and bitmap-font atlases or preview their raw bytes.");
        graphics.Subcommands.Add(assets);
        foreach (bool pack in new[] { true, false })
        {
            Command command = new(pack ? "pack" : "unpack", pack
                ? "Pack a PNG grid into contiguous monochrome cells; metadata is available with --json."
                : "Preview packed cells as a PNG grid using the original layout options.");
            Argument<string> input = new("INPUT");
            Option<string> output = new("--to") { Required = true };
            Option<int> width = new("--cell-width") { Required = true, Description = "Cell width in pixels." };
            Option<int> height = new("--cell-height") { Required = true, Description = "Cell height in pixels." };
            Option<int> bits = new("--bits-per-byte") { DefaultValueFactory = _ => 7, Description = "7 (bit 7 clear) or 8." };
            Option<string> order = new("--bit-order") { DefaultValueFactory = _ => "lsb", Description = "lsb or msb within the selected 7/8 bits." };
            Option<string> kind = new("--kind") { DefaultValueFactory = _ => "sprite", Description = "sprite, tile, or font." };
            Option<int?> first = new("--first-codepoint") { Description = "First font glyph's Unicode scalar value; default 32 for fonts." };
            Option<int> threshold = new("--threshold") { DefaultValueFactory = _ => 128, Description = "Integer luma threshold 0..255." };
            Option<bool> invert = new("--invert") { Description = "Invert packed pixels (also pass when unpacking to restore appearance)." };
            Option<int> columns = new("--columns") { DefaultValueFactory = _ => 1, Description = "Preview atlas columns; must divide the cell count." };
            Option<bool> overwrite = new("--overwrite");
            command.Arguments.Add(input);
            foreach (Option option in new Option[] { output, width, height, bits, order, kind, first, threshold, invert, overwrite })
                command.Options.Add(option);
            if (!pack) command.Options.Add(columns);
            command.SetAction(parse =>
            {
                if (parse.GetValue(_inputOrder) is not null || parse.GetValue(_inputFs) is not null)
                    throw new DiskException("invalid_arguments", "Image filesystem/layout overrides do not apply to bitmap assets.", 2);
                string source = Path.GetFullPath(parse.GetValue(input)!);
                string destination = Path.GetFullPath(parse.GetValue(output)!);
                ImageTransactions.EnsureDistinctPaths(source, destination);
                int limit = pack ? 32 * 1024 * 1024 : 65536;
                byte[] inputBytes = ProgramFiles.ReadBytes(source, limit, _cancellationToken);
                string inputHash = ProgramFiles.Hash(inputBytes);
                BitmapAssetOptions options = new(parse.GetValue(width), parse.GetValue(height), parse.GetValue(bits),
                    parse.GetValue(order)!, parse.GetValue(threshold), parse.GetValue(invert), parse.GetValue(kind)!, parse.GetValue(first));
                byte[] bytes;
                BitmapAssetMetadata metadata;
                if (pack)
                {
                    BitmapAsset asset = GraphicsAssets.Pack(PngCodec.Decode(inputBytes), options);
                    bytes = asset.Bytes;
                    metadata = asset.Metadata;
                }
                else
                {
                    metadata = GraphicsAssets.Inspect(inputBytes, options, parse.GetValue(columns));
                    bytes = PngCodec.Encode(GraphicsAssets.Unpack(inputBytes, options, parse.GetValue(columns)));
                }
                string hash = ProgramFiles.Hash(bytes);
                ImageWriteResult written = ImageTransactions.Create(destination, parse.GetValue(overwrite),
                    temporary => File.WriteAllBytes(temporary, bytes), temporary =>
                    {
                        if (ProgramFiles.Hash(ProgramFiles.ReadBytes(source, limit, _cancellationToken)) != inputHash)
                            throw new DiskException("graphics.source_changed", "Bitmap asset input changed before output was committed.", 6);
                        if (ProgramFiles.Hash(ProgramFiles.ReadBytes(temporary, 32 * 1024 * 1024, _cancellationToken)) != hash)
                            throw new DiskException("graphics.validation", "Staged bitmap asset differs from its expected contents.", 4);
                    }, _cancellationToken);
                return Result("graphics.assets." + command.Name, new { written.OutputPath, sha256 = hash, outputLength = bytes.Length, metadata },
                    $"Wrote {written.OutputPath} ({metadata.CellCount} cells; {metadata.PayloadLength} packed bytes).");
            });
            assets.Subcommands.Add(command);
        }
    }

    private void AddShapeCommands(Command graphics)
    {
        Command shapes = new("shapes", "Encode explicit drawing commands as an Applesoft shape table.");
        Command encode = new("encode", "Convert a versioned shape JSON document to raw table bytes.");
        Argument<string> input = new("INPUT");
        Option<string> output = new("--to") { Required = true };
        Option<bool> overwrite = new("--overwrite");
        encode.Arguments.Add(input);
        encode.Options.Add(output); encode.Options.Add(overwrite);
        encode.SetAction(parse =>
        {
            if (parse.GetValue(_inputOrder) is not null || parse.GetValue(_inputFs) is not null)
                throw new DiskException("invalid_arguments", "Image filesystem/layout overrides do not apply to shape tables.", 2);
            string source = Path.GetFullPath(parse.GetValue(input)!);
            string destination = Path.GetFullPath(parse.GetValue(output)!);
            ImageTransactions.EnsureDistinctPaths(source, destination);
            byte[] bytes = ProgramFiles.ReadBytes(source, 1024 * 1024, _cancellationToken);
            string inputHash = ProgramFiles.Hash(bytes);
            ShapeDocument document;
            try
            {
                document = JsonSerializer.Deserialize<ShapeDocument>(bytes, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                    MaxDepth = 16
                }) ?? throw new DiskException("graphics.shape_schema", "A shape document must be an object.", 2);
            }
            catch (JsonException exception) { throw new DiskException("graphics.shape_schema", exception.Message, 2); }
            ShapeTable table = ApplesoftShapes.Encode(document);
            string hash = ProgramFiles.Hash(table.Bytes);
            ImageWriteResult written = ImageTransactions.Create(destination, parse.GetValue(overwrite),
                temporary => File.WriteAllBytes(temporary, table.Bytes), temporary =>
                {
                    if (ProgramFiles.Hash(ProgramFiles.ReadBytes(source, 1024 * 1024, _cancellationToken)) != inputHash)
                        throw new DiskException("graphics.source_changed", "Shape source changed before output was committed.", 6);
                    if (ProgramFiles.Hash(ProgramFiles.ReadBytes(temporary, 65535, _cancellationToken)) != hash)
                        throw new DiskException("graphics.validation", "Staged shape table differs from its expected contents.", 4);
                }, _cancellationToken);
            return Result("graphics.shapes.encode", new { written.OutputPath, sha256 = hash, payloadLength = table.Bytes.Length, table.Shapes },
                $"Wrote {written.OutputPath} ({table.Shapes.Count} shapes, {table.Bytes.Length} bytes).");
        });
        shapes.Subcommands.Add(encode);
        graphics.Subcommands.Add(shapes);
    }

    private void AddDoubleHiresCommands(Command graphics)
    {
        Command group = new("dhires", "Convert double-hires screens with explicit auxiliary/main page order.");
        graphics.Subcommands.Add(group);
        foreach (bool encode in new[] { true, false })
        {
            Command command = new(encode ? "encode" : "decode", encode ? "Encode a PNG into two 8 KiB screen pages." : "Preview two 8 KiB screen pages as PNG.");
            Argument<string> input = new("INPUT");
            Option<string> output = new("--to") { Required = true };
            Option<string> mode = new("--mode") { Required = true, Description = "mono (560 x 192) or color (140 x 192 logical pixels, approximate RGB)." };
            Option<string> banks = new("--bank-order") { Required = true, Description = "aux-main or main-aux; each bank occupies 8192 bytes." };
            Option<bool> overwrite = new("--overwrite");
            command.Arguments.Add(input);
            foreach (Option option in new Option[] { output, mode, banks, overwrite }) command.Options.Add(option);
            command.SetAction(parse =>
            {
                if (parse.GetValue(_inputOrder) is not null || parse.GetValue(_inputFs) is not null)
                    throw new DiskException("invalid_arguments", "Image filesystem/layout overrides do not apply to raw double-hires pages.", 2);
                string source = Path.GetFullPath(parse.GetValue(input)!);
                string destination = Path.GetFullPath(parse.GetValue(output)!);
                ImageTransactions.EnsureDistinctPaths(source, destination);
                byte[] inputBytes = ProgramFiles.ReadBytes(source, 32 * 1024 * 1024, _cancellationToken);
                string inputHash = ProgramFiles.Hash(inputBytes);
                string selected = parse.GetValue(mode)!, order = parse.GetValue(banks)!;
                byte[] result = encode ? DoubleHiresGraphics.Encode(PngCodec.Decode(inputBytes), selected, order)
                    : PngCodec.Encode(DoubleHiresGraphics.Decode(inputBytes, selected, order));
                string hash = ProgramFiles.Hash(result);
                ImageWriteResult written = ImageTransactions.Create(destination, parse.GetValue(overwrite),
                    temporary => File.WriteAllBytes(temporary, result), temporary =>
                    {
                        if (ProgramFiles.Hash(ProgramFiles.ReadBytes(source, 32 * 1024 * 1024, _cancellationToken)) != inputHash)
                            throw new DiskException("graphics.source_changed", "Double-hires input changed before output was committed.", 6);
                        if (ProgramFiles.Hash(ProgramFiles.ReadBytes(temporary, 32 * 1024 * 1024, _cancellationToken)) != hash)
                            throw new DiskException("graphics.validation", "Staged double-hires output differs from its expected contents.", 4);
                    }, _cancellationToken);
                return Result("graphics.dhires." + command.Name, new
                {
                    written.OutputPath,
                    mode = selected,
                    bankOrder = order,
                    width = selected == "mono" ? 560 : 140,
                    height = 192,
                    payloadLength = 16384,
                    bankLength = 8192,
                    sha256 = hash,
                    auxiliaryOffset = order == "aux-main" ? 0 : 8192,
                    mainOffset = order == "aux-main" ? 8192 : 0,
                    rendering = selected == "mono" ? "monochrome" : "logical-16-color-approximation"
                }, $"Wrote {written.OutputPath} (double-hires {selected}, {order}).");
            });
            group.Subcommands.Add(command);
        }
    }
}
