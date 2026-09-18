using System.Text.Json;
using A2Utils.Core.Execution;

namespace A2Utils.Core.Tests;

public sealed class ExecutionSuiteRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot,
        "a2-suite-runner-" + Guid.NewGuid().ToString("N"));

    public ExecutionSuiteRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "nested"));
        File.WriteAllText(At("disk.dsk"), "pass");
        WriteSpec("alpha.execution.json", "alpha", "HELLO APPLE II");
        WriteSpec("nested/beta.execution.json", "beta", "MISSING");
        Write(At("suite.json"), new ExecutionSuite
        {
            Tests = ["alpha.execution.json", "nested/beta.execution.json"]
        });
    }

    [Fact]
    public void Plan_NameAndPathFilters_KeepStableSuiteIdentitiesWithoutArtifacts()
    {
        ExecutionSuitePlan byName = ExecutionSuiteRunner.Plan(At("suite.json"), new()
        {
            Filters = ["name:alp*"]
        });
        ExecutionSuitePlan byPath = ExecutionSuiteRunner.Plan(At("suite.json"), new()
        {
            Filters = ["path:nested/*"]
        });

        Assert.Equal(2, byName.Discovered);
        Assert.Equal(1, byName.Planned);
        Assert.Equal("case-001", Assert.Single(byName.Cases, item => item.Selected).Id);
        ExecutionSuiteCaseInfo selected = Assert.Single(byPath.Cases, item => item.Selected);
        Assert.Equal("case-002", selected.Id);
        Assert.Equal("nested/beta.execution.json", selected.Path);
        Assert.Throws<DiskException>(() => ExecutionSuiteRunner.Plan(At("suite.json"), new()
        {
            Filters = ["name:no-match"]
        }));
        Assert.False(Directory.Exists(At("artifacts")));
    }

    [Fact]
    public async Task Run_ParallelCases_OrdersResultsAndWritesProgressAndCounts()
    {
        ExecutionSuiteResult result = await ExecutionSuiteRunner.RunAsync(At("suite.json"), At("artifacts"), new()
        {
            Jobs = 2,
            WriteProgress = true
        });

        Assert.False(result.Passed);
        Assert.Equal(new ExecutionSuiteCounts(2, 2, 1, 1, 0, 0), result.Counts);
        Assert.Equal(["alpha", "beta"], result.Tests.Select(item => item.Name));
        Assert.Equal(["passed", "failed"], result.Cases.Select(item => item.Status));
        Assert.True(Directory.Exists(At("artifacts/case-001")));
        Assert.True(Directory.Exists(At("artifacts/case-002")));
        string[] events = File.ReadAllLines(At("artifacts/events.jsonl"));
        Assert.Equal(6, events.Length);
        for (int index = 0; index < events.Length; index++)
        {
            using JsonDocument json = JsonDocument.Parse(events[index]);
            Assert.Equal(index + 1, json.RootElement.GetProperty("sequence").GetInt64());
        }
        using JsonDocument finalEvent = JsonDocument.Parse(events[^1]);
        Assert.Equal("suite-completed", finalEvent.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public void Run_RerunFailedAndAutomaticDirectory_SelectOnlyPriorFailure()
    {
        ExecutionSuiteResult first = ExecutionSuiteRunner.Run(At("suite.json"), At("first"));
        Directory.CreateDirectory(At("runs"));

        ExecutionSuiteResult rerun = ExecutionSuiteRunner.Run(At("suite.json"), At("runs"), new()
        {
            RerunFailedPath = first.ArtifactDirectory,
            CreateRunSubdirectory = true
        });

        Assert.Equal(new ExecutionSuiteCounts(1, 1, 0, 1, 0, 0), rerun.Counts);
        Assert.Equal("beta", Assert.Single(rerun.Tests).Name);
        Assert.Equal("excluded", rerun.Cases[0].Status);
        Assert.Equal("failed", rerun.Cases[1].Status);
        Assert.Equal(At("runs"), Path.GetDirectoryName(rerun.ArtifactDirectory));
        Assert.StartsWith("run-", Path.GetFileName(rerun.ArtifactDirectory));
        Assert.False(Directory.Exists(Path.Combine(rerun.ArtifactDirectory, "case-001")));
        Assert.True(Directory.Exists(Path.Combine(rerun.ArtifactDirectory, "case-002")));
    }

    [Fact]
    public void Plan_RerunFailedWithDuplicateSpecEntries_UsesStableCaseIdentity()
    {
        Write(At("duplicate-suite.json"), new ExecutionSuite
        {
            Tests = ["alpha.execution.json", "alpha.execution.json"]
        });
        WritePreviousResult(At("duplicate-previous.json"),
            new(1, "case-001", "alpha.execution.json", "alpha", true, "passed", null),
            new(2, "case-002", "alpha.execution.json", "alpha", true, "failed", null));

        ExecutionSuitePlan plan = ExecutionSuiteRunner.Plan(At("duplicate-suite.json"), new()
        {
            RerunFailedPath = At("duplicate-previous.json")
        });

        Assert.Equal(1, plan.Planned);
        Assert.False(plan.Cases[0].Selected);
        Assert.True(plan.Cases[1].Selected);
    }

    [Fact]
    public void Plan_RerunFailedWithCaseDistinctPaths_UsesHostPathSemantics()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;
        WriteSpec("case.execution.json", "same-name", "HELLO APPLE II");
        WriteSpec("CASE.execution.json", "same-name", "HELLO APPLE II");
        Write(At("case-suite.json"), new ExecutionSuite
        {
            Tests = ["case.execution.json", "CASE.execution.json"]
        });
        WritePreviousResult(At("case-previous.json"),
            new(1, "case-001", "case.execution.json", "same-name", true, "failed", null),
            new(2, "case-002", "CASE.execution.json", "same-name", true, "passed", null));

        ExecutionSuitePlan plan = ExecutionSuiteRunner.Plan(At("case-suite.json"), new()
        {
            RerunFailedPath = At("case-previous.json")
        });

        Assert.Equal(1, plan.Planned);
        Assert.True(plan.Cases[0].Selected);
        Assert.False(plan.Cases[1].Selected);
    }

    [Fact]
    public void Run_CpuRoutineCase_DispatchesWithoutMameOrRomInputs()
    {
        File.WriteAllText(At("routine.asm"), "lda #$2a\nsta $0300\nrts\n");
        Write(At("cpu.execution.json"), new ExecutionSpec
        {
            Name = "cpu-routine",
            Engine = "cpu",
            Machine = "apple2e",
            Routine = new() { Source = "routine.asm" },
            Memory = [new(0x0300, "2A")]
        });
        Write(At("cpu-suite.json"), new ExecutionSuite { Tests = ["cpu.execution.json"] });

        ExecutionSuiteResult result = ExecutionSuiteRunner.Run(At("cpu-suite.json"), At("cpu-artifacts"));

        Assert.True(result.Passed);
        Assert.Equal(new ExecutionSuiteCounts(1, 1, 1, 0, 0, 0), result.Counts);
        Assert.Equal(CpuExecutionEngine.Version, Assert.Single(result.Tests).EmulatorVersion);
        Assert.False(File.Exists(At("cpu-artifacts/case-001/emulator.stdout.txt")));
    }

    private void WriteSpec(string path, string name, string expectedText)
        => Write(At(path), new ExecutionSpec
        {
            Name = name,
            EmulatorPath = TestPaths.ExecutionHost(),
            RomDirectory = _directory,
            Machine = "apple2ee",
            DiskImage = At("disk.dsk"),
            TextContains = [expectedText],
            EmulatedSeconds = 3,
            HostTimeoutSeconds = 30
        });

    private static void WritePreviousResult(string path, params ExecutionSuiteCaseResult[] cases)
        => Write(path, new ExecutionSuiteResult(1, false, []) { Cases = cases });

    private static void Write<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, ExecutionSpec.JsonOptions));
    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, true);
}
