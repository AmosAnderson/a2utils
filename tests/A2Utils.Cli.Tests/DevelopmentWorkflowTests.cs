using System.Text.Json;
using A2Utils.Core.Graphics;

namespace A2Utils.Cli.Tests;

public sealed class DevelopmentWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-development-cli-" + Guid.NewGuid().ToString("N"));
    public DevelopmentWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Capabilities_Json_ExposesImplementedCommandsAndSchemas()
    {
        var result = Run("capabilities", "--json");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement data = document.RootElement.GetProperty("data");
        Assert.Contains(data.GetProperty("commands").EnumerateArray(), command => command.GetProperty("name").GetString() == "a2 build");
        Assert.Contains(data.GetProperty("schemas").EnumerateArray(), value => value.GetString() == "project");
    }

    [Theory]
    [InlineData("project")]
    [InlineData("diagnostic")]
    [InlineData("execution")]
    [InlineData("execution-suite")]
    [InlineData("environment")]
    public void Schema_PackagedResource_ReturnsActualJsonSchema(string name)
    {
        var result = Run("schema", name, "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        Assert.Equal("object", document.RootElement.GetProperty("data").GetProperty("type").GetString());
        Assert.Contains(name, document.RootElement.GetProperty("data").GetProperty("$id").GetString());
    }

    [Fact]
    public void Build_AssemblyFailure_ReportsStructuredLocation()
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nBNE absent\n");
        File.WriteAllText(At("project.json"), """{"schemaVersion":1,"files":[{"source":"main.asm","path":"MAIN","kind":"asm"}]}""");
        var result = Run("build", At("project.json"), "--json");
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        using JsonDocument json = JsonDocument.Parse(result.Error);
        JsonElement diagnostic = json.RootElement.GetProperty("error").GetProperty("diagnostics")[0];
        Assert.Equal(2, diagnostic.GetProperty("line").GetInt32());
        Assert.Equal(At("main.asm"), diagnostic.GetProperty("file").GetString());
        Assert.False(File.Exists(At("build.po")));
    }

    [Fact]
    public void Build_ManifestOutput_ReportsImageAndInputHashes()
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nRTS\n");
        File.WriteAllText(At("project.json"), """{"schemaVersion":1,"output":"out/demo.po","files":[{"source":"main.asm","path":"MAIN","kind":"asm"}]}""");
        var result = Run("build", At("project.json"), "--json");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal(64, data.GetProperty("sha256").GetString()!.Length);
        Assert.Equal(2, data.GetProperty("inputs").GetArrayLength());
        Assert.True(File.Exists(At("out/demo.po")));
    }

    [Fact]
    public void Graphics_EncodeDecode_EmitsUsablePageAndPng()
    {
        byte[] rgb = Enumerable.Repeat((byte)255, 40 * 48 * 3).ToArray();
        File.WriteAllBytes(At("screen.png"), PngCodec.Encode(new(40, 48, rgb)));
        var encoded = Run("graphics", "encode", At("screen.png"), "--mode", "lores", "--to", At("screen.bin"), "--json");
        Assert.Equal(0, encoded.Code);
        Assert.Equal(1024, File.ReadAllBytes(At("screen.bin")).Length);
        Assert.Equal(0, Run("graphics", "decode", At("screen.bin"), "--mode", "lores", "--to", At("preview.png")).Code);
        Assert.Equal(rgb, PngCodec.Decode(File.ReadAllBytes(At("preview.png"))).Rgb);
    }

    [Fact]
    public void Graphics_InvalidInputWithOverwrite_PreservesPreviousOutput()
    {
        File.WriteAllBytes(At("bad.png"), [0, 1, 2]);
        File.WriteAllBytes(At("previous.bin"), [5, 6, 7]);
        Assert.NotEqual(0, Run("graphics", "encode", At("bad.png"), "--mode", "lores", "--to", At("previous.bin"), "--overwrite").Code);
        Assert.Equal(new byte[] { 5, 6, 7 }, File.ReadAllBytes(At("previous.bin")));
    }

    [Fact]
    public void Graphics_OutputAliasesInput_RefusesAndPreservesSource()
    {
        byte[] png = PngCodec.Encode(new(40, 48, new byte[40 * 48 * 3]));
        File.WriteAllBytes(At("input.png"), png);
        var result = Run("graphics", "encode", At("input.png"), "--mode", "lores", "--to", At("input.png"), "--overwrite");
        Assert.Equal(6, result.Code);
        Assert.Equal(png, File.ReadAllBytes(At("input.png")));
    }

    private string At(string name) => Path.Combine(_directory, name);
    private static (int Code, string Output, string Error) Run(params string[] arguments)
    {
        StringWriter output = new(), error = new();
        int code = CliApplication.Run(arguments, output, error);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        string root = Path.GetFullPath(TestPaths.TemporaryRoot) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_directory).StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test workspace.");
        Directory.Delete(_directory, true);
    }
}
