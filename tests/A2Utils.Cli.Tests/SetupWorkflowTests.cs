using System.Text.Json;

namespace A2Utils.Cli.Tests;

public sealed class SetupWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-setup-cli-" + Guid.NewGuid().ToString("N"));
    public SetupWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Check_MissingTools_ReturnsStructuredFailedChecks()
    {
        File.WriteAllText(Path.Combine(_directory, "environment.json"), """{"schemaVersion":1,"mamePath":"missing","romDirectory":"missing-roms"}""");
        var result = Run("env", "check", Path.Combine(_directory, "environment.json"), "--json");
        Assert.Equal(1, result.Code);
        Assert.Empty(result.Error);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("env.check", json.RootElement.GetProperty("command").GetString());
        Assert.False(json.RootElement.GetProperty("data").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public void Init_NewProject_ReportsConfigurationNeededAndPreservesExistingDirectory()
    {
        string project = Path.Combine(_directory, "project");
        var created = Run("init", project, "--language", "basic", "--json");
        Assert.Equal(0, created.Code);
        using JsonDocument json = JsonDocument.Parse(created.Output);
        Assert.True(json.RootElement.GetProperty("data").GetProperty("needsEnvironmentConfiguration").GetBoolean());
        string source = File.ReadAllText(Path.Combine(project, "main.bas"));
        var refused = Run("init", project, "--language", "asm", "--json");
        Assert.Equal(2, refused.Code);
        Assert.Equal(source, File.ReadAllText(Path.Combine(project, "main.bas")));
        Assert.False(File.Exists(Path.Combine(project, "main.asm")));
    }

    [Fact]
    public void Init_UnsupportedLanguage_DoesNotCreateDirectory()
    {
        string project = Path.Combine(_directory, "project");
        Assert.Equal(2, Run("init", project, "--language", "pascal", "--json").Code);
        Assert.False(Directory.Exists(project));
    }

    private static (int Code, string Output, string Error) Run(params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(arguments, output, error);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
