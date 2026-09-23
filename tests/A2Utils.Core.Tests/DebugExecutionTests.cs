// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class DebugExecutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-debug-" + Guid.NewGuid().ToString("N"));

    public DebugExecutionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Run_DebugAndBankContract_PreservesInputAndReturnsDecodedEvidence()
    {
        ExecutionSpec spec = Spec() with
        {
            Debug = new() { Breakpoints = [new(8192)] },
            Memory = [new(768, "2A"), new(768, "55", "aux")],
            ObserveMemory = [new(53248, 1, "lc1")],
            TextColumns = 80,
            TextContains = ["A2 {MT:00}"]
        };
        File.WriteAllText(spec.DiskImage, "debug-iie");
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("result"));
        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("debug-iie", File.ReadAllText(spec.DiskImage));
        Assert.Equal("breakpoint", result.StopReason);
        Assert.Equal(8192, result.Debug!.Trigger.ProgramCounter);
        Assert.Equal(8190, Assert.Single(result.Debug.History).Address);
        Assert.Equal(3, result.BankMemory.Count);
        Assert.Equal("2A", result.Memory[768]);
        Assert.Equal(80, result.TextScreen!.Columns);
        Assert.Equal(1920, result.TextScreen.Cells.Count);
        Assert.Equal(result.ScreenText, File.ReadAllText(At("result/screen.txt")));
        Assert.True(File.Exists(At("result/text-screen.json")));
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(At("result/result.json")));
        Assert.Equal("breakpoint", report.RootElement.GetProperty("debug").GetProperty("trigger").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Run_DebugNotReached_FailsAndRetainsEvidence()
    {
        ExecutionSpec spec = Spec() with { Debug = new() { Breakpoints = [new(8192)] } };
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("timeout"));
        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, d => d.Code == "execution.debug_timeout");
        Assert.True(File.Exists(At("timeout/result.json")));
        Assert.Equal("pass", File.ReadAllText(spec.DiskImage));
    }

    [Theory]
    [InlineData("BANK\tunknown\t768\t42")]
    [InlineData("BANK\tmain\t49152\t42")]
    [InlineData("BANK\taux\t768\t42\nBANK\taux\t768\t43")]
    [InlineData("BANK\tcpu\t768\t42\nMEM\t768\t43")]
    [InlineData("HIST\t8192\tNOP")]
    [InlineData("VIDEO\taltCharset\t2")]
    [InlineData("TEXTMEM\tmain\t00")]
    [InlineData("DEBUG\tbreakpoint\t0\t8192\t-1\t8192\t0")]
    public void ParseObservation_MalformedEvidence_Rejects(string fields)
    {
        Assert.Throws<InvalidDataException>(() => ExecutionRunner.ParseObservation(
            "A2EXEC1\nSTOP\temulated_limit\nTIME\t1\n" + fields + "\nEND\n", ""));
    }

    [Fact]
    public void Evaluate_IdenticalAddressesInDifferentBanks_ReportsCorrectBank()
    {
        ExecutionSpec spec = Spec() with { Memory = [new(768, "42", "main"), new(768, "43", "aux")] };
        ExecutionObservation observation = ExecutionRunner.ParseObservation(
            "A2EXEC1\nSTOP\temulated_limit\nTIME\t1\nBANK\tmain\t768\t42\nBANK\taux\t768\t00\nEND\n", "");
        var diagnostic = Assert.Single(ExecutionRunner.Evaluate(spec, observation));
        Assert.Equal("aux:$0300", diagnostic.Symbol);
        Assert.Equal("43", diagnostic.Expected);
        Assert.Equal("00", diagnostic.Actual);
    }

    [Theory]
    [InlineData("STOP\tbreakpoint", "DEBUG\twatchpoint\t0\t768\t42\t8192\t0")]
    [InlineData("STOP\tbreakpoint", "DEBUG\tbreakpoint\t0\t8192\t42\t8192\t0")]
    [InlineData("STOP\tbreakpoint", "DEBUG\tbreakpoint\t0\t8192\t-1\t8193\t0")]
    [InlineData("STOP\tdebug_steps", "DEBUG\tbreakpoint\t0\t8192\t-1\t8192\t0")]
    public void ParseObservation_InconsistentDebugStop_Rejects(string stop, string debug)
    {
        Assert.Throws<InvalidDataException>(() => ExecutionRunner.ParseObservation(
            "A2EXEC1\n" + stop + "\nTIME\t1\n" + debug + "\nEND\n", ""));
    }

    [Fact]
    public void Evaluate_DebugBeforeArmTime_RejectsFalsePositive()
    {
        ExecutionSpec spec = Spec() with { Debug = new() { Breakpoints = [new(8192, AfterSeconds: 5)] } };
        ExecutionObservation observation = ExecutionRunner.ParseObservation(
            "A2EXEC1\nSTOP\tbreakpoint\nTIME\t1\nREG\tPC\t8192\nDEBUG\tbreakpoint\t0\t8192\t-1\t8192\t0\nEND\n", "");
        Assert.Contains(ExecutionRunner.Evaluate(spec, observation), d => d.Code == "execution.debug_evidence");
    }

    [Theory]
    [InlineData("{\"breakpoints\":[]}")]
    [InlineData("{\"breakpoints\":[{\"address\":65536}]}")]
    [InlineData("{\"breakpoints\":[{\"program\":\"MAIN\",\"symbol\":\"START\"}]}")]
    [InlineData("{\"breakpoints\":[{\"address\":8192,\"afterSeconds\":15}]}")]
    [InlineData("{\"breakpoints\":[{\"address\":8192}],\"stepInstructions\":1025}")]
    [InlineData("{\"breakpoints\":[{\"address\":8192}],\"historyInstructions\":257}")]
    [InlineData("{\"watchpoints\":[{\"address\":65535,\"length\":2}]}")]
    [InlineData("{\"watchpoints\":[{\"address\":768,\"access\":\"execute\"}]}")]
    public void Validate_InvalidDebugConfiguration_Refuses(string json)
    {
        ExecutionDebug debug = JsonSerializer.Deserialize<ExecutionDebug>(json, ExecutionSpec.JsonOptions)!;
        Assert.Throws<DiskException>(() => MameAdapter.Validate(Spec() with { Debug = debug }));
    }

    [Theory]
    [InlineData("apple2", "aux", 40)]
    [InlineData("apple2p", "cpu", 80)]
    [InlineData("apple2ee", "main", 41)]
    public void Validate_UnsupportedMachineOrText_Refuses(string machine, string bank, int columns)
    {
        Assert.Throws<DiskException>(() => MameAdapter.Validate(Spec() with
        {
            Machine = machine,
            TextColumns = columns,
            Memory = [new(768, "42", bank)]
        }));
    }

    [Fact]
    public void Validate_DebugWithCompletion_RefusesAmbiguousStop()
    {
        Assert.Throws<DiskException>(() => MameAdapter.Validate(Spec() with
        {
            Debug = new() { Breakpoints = [new(8192)] },
            Until = new(768, 42)
        }));
    }

    [Fact]
    public void Validate_CombinedObservationLimit_RejectsOversizedOrDuplicateCapture()
    {
        Assert.Throws<DiskException>(() => MameAdapter.Validate(Spec() with
        {
            ObserveMemory = [new(0, 49152, "main"), new(0, 49152, "aux")]
        }));
        Assert.Throws<DiskException>(() => MameAdapter.Validate(Spec() with
        {
            Memory = [new(768, "42", "aux")],
            ObserveMemory = [new(768, 2, "aux")]
        }));
    }

    [Fact]
    public void Bind_SymbolicDebugAndBankedAssertions_ResolvesBuildAddressesAndBanks()
    {
        ProjectBuildResult build = new(At("build.do"), new string('0', 64), "apple2enh", "65c02", "dos33", "data", "test",
            new(2000, 1, 1), [], [new BuiltFile("MAIN", "asm", "BIN", 8192, 8192, 8192, 4, "", true,
                new Dictionary<string, int> { ["START"] = 8192 }, null) { MemoryBank = "aux" }], [], []);
        ExecutionSpec bound = BuildExecution.Bind(Spec() with
        {
            SymbolicMemory = [new("MAIN", "START", "42")],
            Debug = new() { Breakpoints = [new(Program: "MAIN", Symbol: "start", Offset: 1)] }
        }, build);
        Assert.Equal(8193, Assert.Single(bound.Debug!.Breakpoints).Address);
        Assert.Null(bound.Debug.Breakpoints[0].Symbol);
        Assert.Equal("aux", Assert.Single(bound.Memory).Bank);
        Assert.Throws<DiskException>(() => BuildExecution.Bind(Spec() with
        {
            Debug = new() { Breakpoints = [new(8192, "MAIN", "START")] }
        }, build));
    }

    private ExecutionSpec Spec()
    {
        File.WriteAllText(At("input.dsk"), "pass");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        string host = Path.Combine(root.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return new() { EmulatorPath = host, RomDirectory = _directory, Machine = "apple2ee", DiskImage = At("input.dsk") };
    }

    private string At(string path) => Path.Combine(_directory, path);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
