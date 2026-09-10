using A2Utils.Core.Execution;

namespace A2Utils.Cli.Tests;

public sealed class ExecutionWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-cli-execution-" + Guid.NewGuid().ToString("N"));

    public ExecutionWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Help_ExecutionCommands_AdvertisesSpecificationsAndArtifacts()
    {
        StringWriter output = new();
        Assert.Equal(0, CliApplication.Run(["run", "--help"], output));
        Assert.Contains("SPEC", output.ToString());
        Assert.Contains("--artifacts", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, CliApplication.Run(["test", "--help"], output));
        Assert.Contains("SUITE", output.ToString());
    }

    [Fact]
    public void Run_UnavailableEmulator_ReportsStructuredFailureAndNonzeroExit()
    {
        File.WriteAllBytes(At("disk.dsk"), new byte[143360]);
        ExecutionSpec spec = new()
        {
            EmulatorPath = At("missing.exe"),
            RomDirectory = _directory,
            DiskImage = At("disk.dsk"),
            Machine = "apple2ee"
        };
        File.WriteAllText(At("spec.json"), System.Text.Json.JsonSerializer.Serialize(spec, ExecutionSpec.JsonOptions));
        StringWriter output = new();
        StringWriter error = new();
        int code = CliApplication.Run(["run", At("spec.json"), "--artifacts", At("result"), "--json"], output, error);
        Assert.NotEqual(0, code);
        Assert.Contains("execution.emulator_unavailable", output.ToString());
        Assert.Contains("\"passed\": false", output.ToString());
        Assert.Equal(new byte[143360], File.ReadAllBytes(At("disk.dsk")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"tests\":[\"case.json\"]}")]
    [InlineData("{\"schemaVersion\":1,\"tests\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"tests\":[\"case.json\"]}")]
    public void Test_InvalidSuite_RejectsBeforeCreatingArtifacts(string json)
    {
        File.WriteAllText(At("suite.json"), json);
        StringWriter error = new();
        int code = CliApplication.Run(["test", At("suite.json"), "--artifacts", At("result"), "--json"], new StringWriter(), error);
        Assert.Equal(2, code);
        Assert.Contains("execution.invalid_suite", error.ToString());
        Assert.False(Directory.Exists(At("result")));
    }

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
