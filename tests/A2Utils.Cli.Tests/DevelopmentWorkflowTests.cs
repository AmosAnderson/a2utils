using System.Text.Json;
using System.Text.RegularExpressions;
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
        JsonElement build = data.GetProperty("commands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 build");
        JsonElement test = build.GetProperty("options").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "--test");
        Assert.Equal("boolean", test.GetProperty("type").GetString());
        Assert.False(test.GetProperty("defaultValue").GetBoolean());
        Assert.Contains("--preflight", test.GetProperty("conflictsWith").EnumerateArray()
            .Select(value => value.GetString()));
        Assert.Contains("--artifacts", test.GetProperty("requires").EnumerateArray()
            .Select(value => value.GetString()));
        JsonElement cache = build.GetProperty("options").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "--cache");
        Assert.Equal("cache-directory", cache.GetProperty("pathRole").GetString());
        Assert.Equal(new[] { "--check", "--preflight", "--test" },
            cache.GetProperty("conflictsWith").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("urn:a2utils:schema:result:2", build.GetProperty("resultSchemaId").GetString());
        JsonElement help = data.GetProperty("globalOptions").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "--help");
        Assert.Equal("boolean", help.GetProperty("type").GetString());
        Assert.False(help.GetProperty("defaultValue").GetBoolean());
        Assert.Contains(data.GetProperty("schemas").EnumerateArray(), value => value.GetString() == "envelope");
        JsonElement project = data.GetProperty("commands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 project");
        JsonElement import = project.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 project import");
        Assert.Equal("output-directory", import.GetProperty("options").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "--to")
            .GetProperty("pathRole").GetString());
        Assert.Single(import.GetProperty("options").EnumerateArray(), option =>
            option.GetProperty("name").GetString() == "--json");
        JsonElement resolve = project.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 project resolve");
        Assert.Contains(resolve.GetProperty("sideEffects").EnumerateArray(), value =>
            value.GetString() == "uses-temporary-storage");
        Assert.Contains(data.GetProperty("executionEngines").EnumerateArray(), value =>
            value.GetString() == "cpu");
        Assert.True(data.GetProperty("developmentWorkflow").GetProperty("cpuRoutineExecution").GetBoolean());
        Assert.True(data.GetProperty("developmentWorkflow").GetProperty("contentAddressedBuildCache").GetBoolean());
        Assert.Contains(data.GetProperty("developmentWorkflow").GetProperty("cpuProcessors").EnumerateArray(),
            value => value.GetString() == "apple65c02");
        Assert.Equal(16, data.GetProperty("developmentWorkflow").GetProperty("selectiveSuites")
            .GetProperty("maximumParallelJobs").GetInt32());
        Assert.Equal("stdio", data.GetProperty("agentInterfaces").GetProperty("mcp")
            .GetProperty("transport").GetString());
        JsonElement mame = data.GetProperty("externalTools").EnumerateArray()
            .Single(tool => tool.GetProperty("id").GetString() == "mame");
        Assert.Equal("execution specs/cases with engine mame",
            Assert.Single(mame.GetProperty("requiredFor").EnumerateArray()).GetString());
        JsonElement disk = data.GetProperty("commands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 disk");
        JsonElement attr = disk.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 disk attr");
        Assert.Contains(attr.GetProperty("sideEffects").EnumerateArray(), value =>
            value.GetString() == "writes-host-file-or-disk-image");
        JsonElement diff = disk.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 disk diff");
        Assert.Equal("input-file", diff.GetProperty("arguments")[0].GetProperty("pathRole").GetString());
        Assert.Equal("input-file", diff.GetProperty("arguments")[1].GetProperty("pathRole").GetString());
        Assert.Contains(diff.GetProperty("sideEffects").EnumerateArray(), value =>
            value.GetString() == "reads-disk-images");
        Assert.Contains(diff.GetProperty("sideEffects").EnumerateArray(), value =>
            value.GetString() == "uses-temporary-storage");
        Assert.Equal(new[] { "dos", "prodos" }, diff.GetProperty("options").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "--after-input-order")
            .GetProperty("choices").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "dos33", "prodos" }, diff.GetProperty("options").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "--after-input-fs")
            .GetProperty("choices").EnumerateArray().Select(value => value.GetString()));
        JsonElement plan = disk.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 disk plan");
        Assert.Equal("input-file", plan.GetProperty("arguments")[1].GetProperty("pathRole").GetString());
        Assert.Contains(plan.GetProperty("sideEffects").EnumerateArray(), value =>
            value.GetString() == "uses-temporary-storage");
        JsonElement apply = disk.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 disk apply");
        Assert.Contains(apply.GetProperty("sideEffects").EnumerateArray(), value =>
            value.GetString() == "reads-disk-image");
        Assert.Contains(apply.GetProperty("sideEffects").EnumerateArray(), value =>
            value.GetString() == "writes-host-file-or-disk-image");
        Assert.Contains("--in-place", apply.GetProperty("options").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "--output")
            .GetProperty("conflictsWith").EnumerateArray().Select(value => value.GetString()));

        JsonElement convert = disk.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 disk convert");
        Assert.Equal("input-file", convert.GetProperty("arguments")[0].GetProperty("pathRole").GetString());

        JsonElement basic = data.GetProperty("commands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 basic");
        JsonElement basicCheck = basic.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 basic check");
        Assert.Equal(new[] { "reads-host-files" }, basicCheck.GetProperty("sideEffects")
            .EnumerateArray().Select(value => value.GetString()));

        JsonElement asm = data.GetProperty("commands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 asm");
        Assert.Equal("input-file", asm.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 asm compile")
            .GetProperty("arguments")[0].GetProperty("pathRole").GetString());
        Assert.Equal("input-file-or-image-entry", asm.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 asm decompile")
            .GetProperty("arguments")[0].GetProperty("pathRole").GetString());

        JsonElement graphics = data.GetProperty("commands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 graphics");
        Assert.Empty(graphics.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "a2 graphics assets")
            .GetProperty("sideEffects").EnumerateArray());
        Assert.Equal(2, data.GetProperty("automationContract").GetProperty("contractVersion").GetInt32());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("diagnostic")]
    [InlineData("execution")]
    [InlineData("execution-suite")]
    [InlineData("environment")]
    [InlineData("disk-change-set")]
    [InlineData("project-resolution")]
    [InlineData("result")]
    [InlineData("error")]
    [InlineData("envelope")]
    public void Schema_PackagedResource_ReturnsActualJsonSchema(string name)
    {
        var result = Run("schema", name, "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        Assert.Equal("object", document.RootElement.GetProperty("data").GetProperty("type").GetString());
        Assert.Contains(name, document.RootElement.GetProperty("data").GetProperty("$id").GetString());
        if (name == "diagnostic")
            Assert.Equal("object", document.RootElement.GetProperty("data").GetProperty("$defs")
                .GetProperty("edit").GetProperty("type").GetString());
    }

    [Fact]
    public void ExecutionSchema_CpuProjectCase_AllowsSymbolsForBuildBinding()
    {
        var result = Run("schema", "execution", "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement cpu = Assert.Single(document.RootElement.GetProperty("data")
            .GetProperty("allOf").EnumerateArray(), branch =>
                branch.GetProperty("if").GetProperty("properties")
                    .TryGetProperty("engine", out JsonElement engine) &&
                engine.TryGetProperty("const", out JsonElement value) &&
                value.GetString() == "cpu");
        JsonElement properties = cpu.GetProperty("then").GetProperty("properties");

        Assert.False(properties.TryGetProperty("symbolicMemory", out _));
        Assert.False(properties.TryGetProperty("symbolicUntil", out _));
        Assert.Equal(new[] { "routine" }, cpu.GetProperty("then").GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("object", properties.GetProperty("routine").GetProperty("type").GetString());
        Assert.Contains(cpu.GetProperty("then").GetProperty("anyOf").EnumerateArray(), branch =>
            branch.GetProperty("required").EnumerateArray().Any(value => value.GetString() == "environment"));
        Assert.Equal(new[] { "lores", "hires", "hires-color" }, properties.GetProperty("graphicsMemory")
            .GetProperty("items").GetProperty("properties").GetProperty("mode")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("cpu", properties.GetProperty("graphicsMemory").GetProperty("items")
            .GetProperty("properties").GetProperty("bank").GetProperty("const").GetString());
    }

    [Fact]
    public void DiskChangeSetSchema_InlineHex_RepresentsCompleteBytesIncludingEmptyPayload()
    {
        var result = Run("schema", "disk-change-set", "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        string pattern = document.RootElement.GetProperty("data").GetProperty("properties")
            .GetProperty("changes").GetProperty("items").GetProperty("properties")
            .GetProperty("hex").GetProperty("pattern").GetString()!;

        Assert.Matches(new Regex(pattern, RegexOptions.CultureInvariant), "");
        Assert.Matches(new Regex(pattern, RegexOptions.CultureInvariant), "2A A5");
        Assert.DoesNotMatch(new Regex(pattern, RegexOptions.CultureInvariant), "A");
    }

    [Fact]
    public void ProjectSchema_BootProject_RequiresDosFloppyGeometry()
    {
        var result = Run("schema", "project", "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement boot = Assert.Single(document.RootElement.GetProperty("data")
            .GetProperty("allOf").EnumerateArray(), branch =>
                branch.GetProperty("if").GetProperty("properties").TryGetProperty("boot", out _));
        JsonElement then = boot.GetProperty("then");
        Assert.Contains(then.GetProperty("required").EnumerateArray(), value => value.GetString() == "disk");
        JsonElement disk = then.GetProperty("properties").GetProperty("disk");
        Assert.Contains(disk.GetProperty("required").EnumerateArray(), value => value.GetString() == "fileSystem");
        Assert.Equal("dos33", disk.GetProperty("properties").GetProperty("fileSystem")
            .GetProperty("const").GetString());
        Assert.Equal(280, disk.GetProperty("properties").GetProperty("blocks")
            .GetProperty("const").GetInt32());
    }

    [Fact]
    public void Json_Envelopes_PreserveVersionOneFieldsAndDeclareTypedContract()
    {
        var success = Run("targets", "--json");
        using JsonDocument result = JsonDocument.Parse(success.Output);
        Assert.Equal(1, result.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("targets", result.RootElement.GetProperty("command").GetString());
        Assert.True(result.RootElement.TryGetProperty("data", out _));
        Assert.True(result.RootElement.TryGetProperty("diagnostics", out _));
        Assert.Equal(2, result.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal("result", result.RootElement.GetProperty("envelopeType").GetString());
        Assert.Equal("urn:a2utils:schema:result:2", result.RootElement.GetProperty("schemaId").GetString());

        var failure = Run("schema", "missing", "--json");
        using JsonDocument error = JsonDocument.Parse(failure.Error);
        Assert.Equal(1, error.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("schema.unknown", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, error.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal("error", error.RootElement.GetProperty("envelopeType").GetString());
        Assert.Equal(failure.Code, error.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void ProjectResolve_ReportsEffectivePlanWithoutWritingOutput()
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nstart: RTS\n");
        File.WriteAllText(At("project.json"),
            """{"schemaVersion":1,"target":"apple2enh","output":"out/demo.po","files":[{"source":"main.asm","path":"BIN/MAIN","kind":"asm"}]}""");

        var result = Run("project", "inspect", At("project.json"), "--json");

        Assert.Equal(0, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("project.resolve", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("urn:a2utils:schema:project-resolution:1",
            json.RootElement.GetProperty("resultSchemaId").GetString());
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal("65c02", data.GetProperty("settings").GetProperty("cpu").GetString());
        Assert.Equal(2, data.GetProperty("dependencies").GetArrayLength());
        Assert.Equal("BIN/MAIN", data.GetProperty("diskPlan").GetProperty("entries")[0]
            .GetProperty("imagePath").GetString());
        Assert.Contains(data.GetProperty("diskPlan").GetProperty("directories").EnumerateArray(),
            value => value.GetString() == "BIN");
        Assert.Contains(data.GetProperty("toolRequirements").EnumerateArray(),
            tool => tool.GetProperty("id").GetString() == "a2utils");
        Assert.False(File.Exists(At("out/demo.po")));
        Assert.False(Directory.Exists(At("out")));
    }

    [Fact]
    public void ProjectImport_CreatesDiscoverableProjectThroughCli()
    {
        string destination = At("imported");
        var result = Run("project", "import", Fixture(), "--to", destination, "--json");

        Assert.Equal(0, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("project.import", json.RootElement.GetProperty("command").GetString());
        Assert.True(File.Exists(Path.Combine(destination, "project.a2.json")));
        Assert.True(File.Exists(Path.Combine(destination, "import-report.json")));
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
    public void Build_CacheOption_ReportsMissThenRestoresHitToAnotherOutput()
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nRTS\n");
        File.WriteAllText(At("project.json"),
            """{"schemaVersion":1,"files":[{"source":"main.asm","path":"MAIN","kind":"asm"}]}""");
        string cache = At("cache");

        var first = Run("build", At("project.json"), "--to", At("first.po"),
            "--cache", cache, "--json");
        var second = Run("build", At("project.json"), "--to", At("second.po"),
            "--cache", cache, "--json");

        Assert.Equal(0, first.Code);
        Assert.Equal(0, second.Code);
        using JsonDocument firstJson = JsonDocument.Parse(first.Output);
        using JsonDocument secondJson = JsonDocument.Parse(second.Output);
        JsonElement firstData = firstJson.RootElement.GetProperty("data");
        JsonElement secondData = secondJson.RootElement.GetProperty("data");
        Assert.False(firstData.GetProperty("cacheHit").GetBoolean());
        Assert.True(secondData.GetProperty("cacheHit").GetBoolean());
        Assert.Equal(firstData.GetProperty("sha256").GetString(),
            secondData.GetProperty("sha256").GetString());
        Assert.Equal(File.ReadAllBytes(At("first.po")), File.ReadAllBytes(At("second.po")));

        var human = Run("build", At("project.json"), "--to", At("third.po"),
            "--cache", cache);
        Assert.Equal(0, human.Code);
        Assert.Contains("cache hit", human.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--check")]
    [InlineData("--preflight")]
    [InlineData("--test")]
    public void Build_CacheOptionWithNonNormalMode_RefusesBeforeCreatingCache(string mode)
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nRTS\n");
        File.WriteAllText(At("project.json"),
            """{"schemaVersion":1,"files":[{"source":"main.asm","path":"MAIN","kind":"asm"}]}""");
        string cache = At("cache-" + mode[2..]);

        var result = Run("build", At("project.json"), mode, "--cache", cache, "--json");

        Assert.Equal(2, result.Code);
        using JsonDocument error = JsonDocument.Parse(result.Error);
        Assert.Equal("project.cache", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(cache));
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
    private static string Fixture()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "tests", "TestData", "independent-dos33.do");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Test fixture not found.");
    }
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
