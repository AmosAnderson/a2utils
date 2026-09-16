using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using A2Utils.Core.Graphics;

namespace A2Utils.Core.Projects;

public sealed record ProjectAsset
{
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string Output { get; init; } = "";
    public string Kind { get; init; } = "sprite";
    public int CellWidth { get; init; } = 7;
    public int CellHeight { get; init; } = 8;
    public int BitsPerByte { get; init; } = 7;
    public string BitOrder { get; init; } = "lsb";
    public int Threshold { get; init; } = 128;
    public bool Invert { get; init; }
    public int? FirstCodePoint { get; init; }
    public string Mode { get; init; } = "mono";
    public string BankOrder { get; init; } = "aux-main";
    public string? AssemblyInclude { get; init; }
    public string? CHeader { get; init; }
    public string? MetadataOutput { get; init; }
}

public sealed record ProjectAssetCompilation(byte[] Payload, IReadOnlyDictionary<string, byte[]> Outputs,
    IReadOnlyDictionary<string, int> Constants, object Metadata);
public sealed record ProjectAssetReport(string Name, string Kind, string Source,
    IReadOnlyList<BuildInput> Outputs, object Metadata);

/// <summary>Deterministic bounded asset conversion to virtual binary, assembly, C, and metadata inputs.</summary>
public static partial class ProjectAssets
{
    public static ProjectAssetCompilation Compile(ProjectAsset asset, ReadOnlySpan<byte> input)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Name is null || !SymbolName().IsMatch(asset.Name))
            throw Error("name", "Asset name must be an uppercase identifier of 1..48 characters.");
        ValidatePath(asset.Source);
        ValidateOutput(asset.Output, ".bin");
        if (asset.AssemblyInclude is { } assembly) ValidateOutput(assembly, ".inc");
        if (asset.CHeader is { } header) ValidateOutput(header, ".h");
        if (asset.MetadataOutput is { } metadataPath) ValidateOutput(metadataPath, ".json");
        if (input.Length > 32 * 1024 * 1024) throw Error("size", "An asset input may contain at most 32 MiB.");
        Dictionary<string, int> constants = new(StringComparer.Ordinal);
        byte[] payload;
        object metadata;
        switch (asset.Kind)
        {
            case "sprite":
            case "tile":
            case "font":
                BitmapAsset packed = GraphicsAssets.Pack(PngCodec.Decode(input), new(asset.CellWidth, asset.CellHeight,
                    asset.BitsPerByte, asset.BitOrder, asset.Threshold, asset.Invert, asset.Kind, asset.FirstCodePoint));
                payload = packed.Bytes;
                metadata = packed.Metadata;
                Add("CELL_WIDTH", packed.Metadata.CellWidth);
                Add("CELL_HEIGHT", packed.Metadata.CellHeight);
                Add("CELL_COUNT", packed.Metadata.CellCount);
                Add("BYTES_PER_ROW", packed.Metadata.BytesPerRow);
                Add("BYTES_PER_CELL", packed.Metadata.BytesPerCell);
                foreach (BitmapAssetCell cell in packed.Metadata.Cells)
                {
                    Add("CELL_" + cell.Index.ToString(CultureInfo.InvariantCulture) + "_OFFSET", cell.Offset);
                    if (cell.CodePoint is { } codePoint) Add("CELL_" + cell.Index.ToString(CultureInfo.InvariantCulture) + "_CODE", codePoint);
                }
                break;
            case "shapes":
                ShapeDocument document;
                try
                {
                    document = JsonSerializer.Deserialize<ShapeDocument>(input, ProjectJson.Options)
                        ?? throw Error("shapes", "Shape source must be a versioned shape document.");
                }
                catch (JsonException exception) { throw Error("shapes", exception.Message); }
                ShapeTable shapes = ApplesoftShapes.Encode(document);
                payload = shapes.Bytes;
                metadata = shapes.Shapes;
                Add("SHAPE_COUNT", shapes.Shapes.Count);
                foreach (ShapeMetadata shape in shapes.Shapes)
                {
                    string prefix = "SHAPE_" + shape.Number.ToString(CultureInfo.InvariantCulture);
                    Add(prefix + "_OFFSET", shape.Offset);
                    Add(prefix + "_LENGTH", shape.Length);
                }
                break;
            case "dhires":
                payload = DoubleHiresGraphics.Encode(PngCodec.Decode(input), asset.Mode, asset.BankOrder);
                int aux = asset.BankOrder == "aux-main" ? 0 : 8192;
                metadata = new { asset.Mode, asset.BankOrder, AuxiliaryOffset = aux, MainOffset = 8192 - aux, BankLength = 8192 };
                Add("AUX_OFFSET", aux);
                Add("MAIN_OFFSET", 8192 - aux);
                Add("BANK_LENGTH", 8192);
                break;
            case "lores":
            case "hires":
            case "hires-color":
                payload = AppleGraphics.EncodePng(input.ToArray(), asset.Kind);
                metadata = new { asset.Kind, PayloadLength = payload.Length };
                break;
            default:
                throw Error("kind", "Asset kind must be sprite, tile, font, shapes, dhires, lores, hires, or hires-color.");
        }
        Add("LENGTH", payload.Length);
        Dictionary<string, byte[]> outputs = new(StringComparer.OrdinalIgnoreCase);
        Output(asset.Output, payload);
        if (asset.AssemblyInclude is { } include)
            Output(include, Encoding.UTF8.GetBytes("; Generated asset constants. Offsets are relative to the payload.\n" +
                string.Concat(constants.Select(pair => pair.Key + " = " + pair.Value.ToString(CultureInfo.InvariantCulture) + "\n"))));
        if (asset.CHeader is { } cHeader)
        {
            StringBuilder text = new();
            text.Append("/* Generated asset data and offsets. Include in one translation unit. */\n#ifndef A2_ASSET_")
                .Append(asset.Name).Append("_H\n#define A2_ASSET_").Append(asset.Name).Append("_H\n");
            foreach (var pair in constants)
                text.Append("#define ").Append(pair.Key).Append(' ').Append(pair.Value.ToString(CultureInfo.InvariantCulture)).Append("UL\n");
            text.Append("static const unsigned char ").Append(asset.Name).Append("_DATA[] = {\n");
            for (int offset = 0; offset < payload.Length; offset += 16)
                text.Append("    ").Append(string.Join(",", payload.Skip(offset).Take(16).Select(value => "0x" + value.ToString("X2")))).Append(",\n");
            text.Append("};\n#endif\n");
            Output(cHeader, Encoding.UTF8.GetBytes(text.ToString()));
        }
        if (asset.MetadataOutput is { } json)
            Output(json, JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, asset.Name, asset.Kind, Constants = constants, Metadata = metadata }, ProjectJson.Options));
        if (outputs.Values.Sum(bytes => (long)bytes.Length) > 8 * 1024 * 1024)
            throw Error("size", "Generated outputs for one asset exceed 8 MiB.");
        return new(payload, outputs, constants, metadata);

        void Add(string suffix, int value) => constants.Add(asset.Name + "_" + suffix, value);
        void Output(string path, byte[] bytes)
        {
            if (!outputs.TryAdd(path, bytes)) throw Error("collision", "Asset output paths must be distinct.");
        }
    }

    public static void ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\\') || path.Contains(':')
            || path.StartsWith('/') || path.Any(char.IsControl) || path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw Error("path", "Asset paths must be relative project paths using '/' without empty, '.', or '..' segments.");
    }

    private static void ValidateOutput(string path, string extension)
    {
        ValidatePath(path);
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw Error("extension", $"Generated output must use the {extension} extension.");
    }

    [GeneratedRegex("^[A-Z_][A-Z0-9_]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolName();
    private static DiskException Error(string code, string message) => new("project.asset_" + code, message, 2);
}
