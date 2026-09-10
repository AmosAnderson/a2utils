using System.Text.Json;
using A2Utils.Core.Graphics;

namespace A2Utils.Cli.Tests;

public sealed class GraphicsAssetWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, $"a2-assets-{Guid.NewGuid():N}");

    public GraphicsAssetWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Assets_PackUnpackFontAtlas_ExportsKnownBytesAndMetadata()
    {
        byte[] rgb = new byte[8 * 3];
        rgb.AsSpan(0, 3).Fill(255);
        rgb.AsSpan(7 * 3, 3).Fill(255);
        File.WriteAllBytes(At("atlas.png"), PngCodec.Encode(new(8, 1, rgb)));
        var packed = Run("graphics", "assets", "pack", At("atlas.png"), "--to", At("font.bin"),
            "--cell-width", "8", "--cell-height", "1", "--bits-per-byte", "8", "--kind", "font", "--first-codepoint", "65", "--json");
        Assert.True(packed.Code == 0, packed.Error);
        Assert.Equal(new byte[] { 0x81 }, File.ReadAllBytes(At("font.bin")));
        using JsonDocument json = JsonDocument.Parse(packed.Output);
        JsonElement metadata = json.RootElement.GetProperty("data").GetProperty("metadata");
        Assert.Equal(1, metadata.GetProperty("payloadLength").GetInt32());
        Assert.Equal(65, metadata.GetProperty("cells")[0].GetProperty("codePoint").GetInt32());
        var unpacked = Run("graphics", "assets", "unpack", At("font.bin"), "--to", At("preview.png"),
            "--cell-width", "8", "--cell-height", "1", "--bits-per-byte", "8");
        Assert.True(unpacked.Code == 0, unpacked.Error);
        Assert.Equal(rgb, PngCodec.Decode(File.ReadAllBytes(At("preview.png"))).Rgb);
    }

    [Fact]
    public void Shapes_EncodeSquare_WritesKnownTableAndPreservesFailedDestination()
    {
        File.WriteAllText(At("shape.json"), """
            {"schemaVersion":1,"shapes":[{"name":"square","commands":[
              {"direction":"right"},{"direction":"down"},{"direction":"left"},{"direction":"up"}
            ]}]}
            """);
        var encoded = Run("graphics", "shapes", "encode", At("shape.json"), "--to", At("shape.bin"), "--json");
        Assert.True(encoded.Code == 0, encoded.Error);
        byte[] expected = Convert.FromHexString("01000400352700");
        Assert.Equal(expected, File.ReadAllBytes(At("shape.bin")));
        File.WriteAllText(At("shape.json"), """
            {"schemaVersion":1,"shapes":[{"name":"bad","commands":[{"direction":"up","plot":false}]}]}
            """);
        var invalid = Run("graphics", "shapes", "encode", At("shape.json"), "--to", At("shape.bin"), "--overwrite");
        Assert.Equal(2, invalid.Code);
        Assert.Equal(expected, File.ReadAllBytes(At("shape.bin")));
        var alias = Run("graphics", "shapes", "encode", At("shape.json"), "--to", At("shape.json"), "--overwrite");
        Assert.Equal(6, alias.Code);
    }

    [Fact]
    public void DoubleHires_ExplicitBanks_RoundTripsMonochromeAndPreservesFailedOutput()
    {
        byte[] rgb = new byte[560 * 192 * 3];
        rgb.AsSpan(0, 3).Fill(255);
        File.WriteAllBytes(At("screen.png"), PngCodec.Encode(new(560, 192, rgb)));
        var encoded = Run("graphics", "dhires", "encode", At("screen.png"), "--to", At("screen.dhgr"),
            "--mode", "mono", "--bank-order", "main-aux");
        Assert.True(encoded.Code == 0, encoded.Error);
        byte[] binary = File.ReadAllBytes(At("screen.dhgr"));
        Assert.Equal(16384, binary.Length);
        Assert.Equal(1, binary[8192]);
        Assert.Equal(0, binary[0]);
        var decoded = Run("graphics", "dhires", "decode", At("screen.dhgr"), "--to", At("preview.png"),
            "--mode", "mono", "--bank-order", "main-aux");
        Assert.True(decoded.Code == 0, decoded.Error);
        Assert.Equal(rgb, PngCodec.Decode(File.ReadAllBytes(At("preview.png"))).Rgb);
        var invalid = Run("graphics", "dhires", "decode", At("screen.dhgr"), "--to", At("preview.png"),
            "--mode", "unknown", "--bank-order", "main-aux", "--overwrite");
        Assert.Equal(2, invalid.Code);
        Assert.Equal(rgb, PngCodec.Decode(File.ReadAllBytes(At("preview.png"))).Rgb);
        var alias = Run("graphics", "dhires", "encode", At("screen.png"), "--to", At("screen.png"),
            "--mode", "mono", "--bank-order", "aux-main", "--overwrite");
        Assert.Equal(6, alias.Code);
    }

    [Fact]
    public void Assets_InvalidSourceAndOutputAlias_PreservesExistingFiles()
    {
        File.WriteAllBytes(At("bad.bin"), [0x80]);
        File.WriteAllText(At("existing.png"), "preserve");
        var invalid = Run("graphics", "assets", "unpack", At("bad.bin"), "--to", At("existing.png"),
            "--cell-width", "7", "--cell-height", "1", "--overwrite");
        Assert.Equal(2, invalid.Code);
        Assert.Equal("preserve", File.ReadAllText(At("existing.png")));
        var alias = Run("graphics", "assets", "unpack", At("bad.bin"), "--to", At("bad.bin"),
            "--cell-width", "7", "--cell-height", "1", "--overwrite");
        Assert.Equal(6, alias.Code);
        Assert.Equal(new byte[] { 0x80 }, File.ReadAllBytes(At("bad.bin")));
        Assert.Empty(Directory.GetFiles(_directory, ".*.a2-*"));
    }

    private string At(string name) => Path.Combine(_directory, name);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
