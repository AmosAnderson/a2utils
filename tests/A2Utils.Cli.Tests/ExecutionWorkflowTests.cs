using System.Text.Json;
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
        Assert.Contains("--list", output.ToString());
        Assert.Contains("--filter", output.ToString());
        Assert.Contains("--jobs", output.ToString());
        Assert.Contains("--rerun-failed", output.ToString());
        Assert.Contains("--progress", output.ToString());
        Assert.Contains("--run-subdirectory", output.ToString());
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

    [Fact]
    public void Test_ListWithFilter_ReportsStableSelectionWithoutArtifacts()
    {
        PrepareSuite();
        StringWriter output = new(), error = new();

        int code = CliApplication.Run(["test", At("suite.json"), "--list", "--filter", "name:beta", "--json"],
            output, error);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("discovered").GetInt32());
        Assert.Equal(1, data.GetProperty("planned").GetInt32());
        Assert.Equal("case-002", data.GetProperty("cases").EnumerateArray().Single(item =>
            item.GetProperty("selected").GetBoolean()).GetProperty("id").GetString());
        Assert.False(Directory.Exists(At("result")));
    }

    [Fact]
    public void Test_ParallelProgressAndAutomaticRunDirectory_ReportCounts()
    {
        PrepareSuite();
        StringWriter output = new(), error = new();

        int code = CliApplication.Run(["test", At("suite.json"), "--artifacts", At("runs"),
            "--run-subdirectory", "--jobs", "2", "--progress", "--json"], output, error);

        Assert.Equal(1, code);
        Assert.Equal("", error.ToString());
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("counts").GetProperty("completed").GetInt32());
        Assert.Equal(1, data.GetProperty("counts").GetProperty("passed").GetInt32());
        Assert.Equal(1, data.GetProperty("counts").GetProperty("failed").GetInt32());
        string artifacts = data.GetProperty("artifactDirectory").GetString()!;
        Assert.Equal(At("runs"), Path.GetDirectoryName(artifacts));
        Assert.True(File.Exists(Path.Combine(artifacts, "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(artifacts, "suite-result.json")));
    }

    private void PrepareSuite()
    {
        File.WriteAllText(At("disk.dsk"), "pass");
        Write(At("alpha.json"), Spec("alpha", "HELLO APPLE II"));
        Write(At("beta.json"), Spec("beta", "MISSING"));
        Write(At("suite.json"), new ExecutionSuite { Tests = ["alpha.json", "beta.json"] });
    }

    private ExecutionSpec Spec(string name, string expectedText) => new()
    {
        Name = name,
        EmulatorPath = TestPaths.ExecutionHost(),
        RomDirectory = _directory,
        Machine = "apple2ee",
        DiskImage = At("disk.dsk"),
        TextContains = [expectedText],
        EmulatedSeconds = 3,
        HostTimeoutSeconds = 30
    };

    private static void Write<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, ExecutionSpec.JsonOptions));

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
