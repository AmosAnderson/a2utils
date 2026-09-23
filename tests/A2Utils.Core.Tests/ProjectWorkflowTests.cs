// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Diagnostics;
using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class ProjectWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-project-test-" + Guid.NewGuid().ToString("N"));

    public ProjectWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Run_ProjectSuite_BindsExactImageAndSymbolsAndRetainsSourceEvidence()
    {
        Setup();
        ProjectWorkflowResult result = ProjectWorkflow.Run(At("project.json"), At("evidence"));
        Assert.True(result.Passed, string.Join('\n', result.Tests.Tests.SelectMany(run => run.Diagnostics).Select(d => d.Message)));
        Assert.False(result.Build.Preflight);
        Assert.Equal(ProgramFiles.Hash(File.ReadAllBytes(At("build.po"))), result.Build.Sha256);
        ExecutionResult run = Assert.Single(result.Tests.Tests);
        Assert.Equal(result.Build.Sha256, run.Disks.Single(disk => disk.Device == "flop2").InputSha256);
        Assert.Equal("pass", File.ReadAllText(At("os.dsk")));
        ExecutionSpec bound = ExecutionSpec.Load(Path.Combine(run.ArtifactDirectory, "spec.json"));
        Assert.Empty(bound.SymbolicMemory);
        Assert.Equal(768, Assert.Single(bound.Memory).Address);
        Assert.Equal(result.Build.Sha256, bound.Disks.Single(disk => disk.Device == "flop2").ExpectedSha256);
        Assert.All(bound.Disks, disk => Assert.Equal(64, disk.ExpectedSha256!.Length));
        Assert.Contains(Path.Combine(run.ArtifactDirectory, "source-locations.json"), run.Artifacts);
        Assert.Equal(new ExecutionSuiteCounts(1, 1, 1, 0, 0, 0), result.Tests.Counts);
        Assert.Equal("passed", Assert.Single(result.Tests.Cases).Status);
        using JsonDocument locations = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.ArtifactDirectory, "source-locations.json")));
        Assert.Contains(locations.RootElement.EnumerateArray(), location => location.GetProperty("line").GetInt32() == 3
            && location.GetProperty("program").GetString() == "MAIN");
        Assert.True(File.Exists(At("evidence/build.json")));
        Assert.True(File.Exists(At("evidence/project-result.json")));
    }

    [Fact]
    public void Run_CpuProjectCase_BindsBuildSymbolsWithoutMountingImageOrRequiringMame()
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nmailbox = $300\nentry: rts\n");
        File.WriteAllText(At("routine.asm"), "lda #$2a\nsta $0300\nrts\n");
        Write(At("project.json"), new ProjectManifest
        {
            Target = "apple2enh",
            Output = "build.po",
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }],
            Execution = new("suite.json", "flop2")
        });
        Write(At("suite.json"), new ExecutionSuite { Tests = ["case.json"] });
        Write(At("case.json"), new ExecutionSpec
        {
            Engine = "cpu",
            Machine = "apple2ee",
            Routine = new() { Source = "routine.asm" },
            SymbolicMemory = [new("MAIN", "mailbox", "2A")],
            Trace = true
        });

        ProjectWorkflowResult result = ProjectWorkflow.Run(At("project.json"), At("cpu-evidence"));

        Assert.True(result.Passed,
            string.Join('\n', result.Tests.Tests.SelectMany(run => run.Diagnostics).Select(diagnostic => diagnostic.Message)));
        Assert.True(File.Exists(At("build.po")));
        ExecutionResult run = Assert.Single(result.Tests.Tests);
        Assert.Equal(CpuExecutionEngine.Version, run.EmulatorVersion);
        Assert.Null(run.InputSha256);
        Assert.Empty(run.Disks);
        ExecutionSpec bound = ExecutionSpec.Load(Path.Combine(run.ArtifactDirectory, "spec.json"));
        Assert.Empty(bound.Disks);
        Assert.Empty(bound.SymbolicMemory);
        Assert.Equal(new MemoryAssertion(0x0300, "2A"), Assert.Single(bound.Memory));
        using JsonDocument locations = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(run.ArtifactDirectory, "source-locations.json")));
        JsonElement first = locations.RootElement.EnumerateArray().First();
        Assert.Equal(At("routine.asm"), first.GetProperty("program").GetString());
        Assert.Equal(At("routine.asm"), first.GetProperty("file").GetString());
        Assert.Equal(1, first.GetProperty("line").GetInt32());
        Assert.Equal(0x6000, first.GetProperty("address").GetInt32());
        Assert.Equal("executed instruction", first.GetProperty("observation").GetString());
        Assert.Equal("cpu", first.GetProperty("memoryBank").GetString());
        Assert.DoesNotContain(locations.RootElement.EnumerateArray(), location =>
            location.GetProperty("program").GetString() == "MAIN");
    }

    [Fact]
    public void Run_SelectedCaseWithProgress_PreservesBuildBindingAndStableCaseNumber()
    {
        Setup();
        ExecutionSpec first = ExecutionSpec.Load(At("case.json")) with { Name = "first" };
        ExecutionSpec second = first with { Name = "second" };
        Write(At("case.json"), first);
        Write(At("case-two.json"), second);
        Write(At("suite.json"), new ExecutionSuite { Tests = ["case.json", "case-two.json"] });

        ProjectWorkflowResult result = ProjectWorkflow.Run(At("project.json"), At("evidence"), null, false,
            new ExecutionSuiteRunOptions { Filters = ["name:second"], Jobs = 2, WriteProgress = true });

        Assert.True(result.Passed);
        Assert.Equal(new ExecutionSuiteCounts(1, 1, 1, 0, 0, 0), result.Tests.Counts);
        Assert.Equal("excluded", result.Tests.Cases[0].Status);
        Assert.Equal("passed", result.Tests.Cases[1].Status);
        ExecutionResult run = Assert.Single(result.Tests.Tests);
        Assert.EndsWith("case-002", run.ArtifactDirectory);
        Assert.False(Directory.Exists(At("evidence/case-001")));
        Assert.True(File.Exists(At("evidence/events.jsonl")));
        Assert.Equal(result.Build.Sha256, run.Disks.Single(disk => disk.Device == "flop2").InputSha256);
        Assert.Equal("pass", File.ReadAllText(At("os.dsk")));
    }

    [Fact]
    public void Run_FilterExcludesInvalidCase_StillRefusesBeforeReplacingOutput()
    {
        Setup();
        ExecutionSpec first = ExecutionSpec.Load(At("case.json")) with
        {
            Name = "invalid",
            Machine = "apple2p"
        };
        ExecutionSpec second = ExecutionSpec.Load(At("case.json")) with { Name = "selected" };
        Write(At("case.json"), first);
        Write(At("case-two.json"), second);
        Write(At("suite.json"), new ExecutionSuite { Tests = ["case.json", "case-two.json"] });
        File.WriteAllBytes(At("build.po"), [1, 2, 3]);

        DiskException error = Assert.Throws<DiskException>(() => ProjectWorkflow.Run(
            At("project.json"), At("filtered-evidence"), null, true,
            new ExecutionSuiteRunOptions { Filters = ["name:selected"] }));

        Assert.Equal("execution.machine", error.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(At("build.po")));
        Assert.False(Directory.Exists(At("filtered-evidence")));
    }

    [Fact]
    public void Run_SymbolicAssertionFails_AnnotatesSymbolAndKeepsEvidence()
    {
        Setup(hex: "FF");
        ProjectWorkflowResult result = ProjectWorkflow.Run(At("project.json"), At("evidence"));
        Assert.False(result.Passed);
        ProgramDiagnostic diagnostic = Assert.Single(result.Tests.Tests[0].Diagnostics,
            diagnostic => diagnostic.Code == "execution.memory_assertion");
        Assert.Equal("MAIN:mailbox", diagnostic.Symbol);
        Assert.Equal("FF", diagnostic.Expected);
        Assert.Equal("2A", diagnostic.Actual);
        Assert.True(File.Exists(At("build.po")));
        Assert.True(File.Exists(At("evidence/project-result.json")));
    }

    [Fact]
    public void Run_RoutineWithSourceAndBinaryIncludes_RecordsAllInputsBeforeBuild()
    {
        Setup();
        File.WriteAllText(At("routine.asm"), ".include \"routine.inc\"\n.incbin \"table.bin\"\n");
        File.WriteAllText(At("routine.inc"), "RTS\n");
        File.WriteAllBytes(At("table.bin"), [0x2a]);
        Write(At("case.json"), ExecutionSpec.Load(At("case.json")) with
        {
            Routine = new() { Source = At("routine.asm") }
        });

        ProjectWorkflowResult result = ProjectWorkflow.Run(At("project.json"), At("evidence"));

        Assert.True(result.Passed, string.Join('\n', result.Tests.Tests.SelectMany(run => run.Diagnostics).Select(d => d.Message)));
        BuildInput[] inputs = JsonSerializer.Deserialize<BuildInput[]>(File.ReadAllText(At("evidence/execution-inputs.json")), ProjectJson.Options)!;
        foreach (string name in new[] { "routine.asm", "routine.inc", "table.bin" })
            Assert.Contains(inputs, input => input.Path == At(name) && input.Sha256 == ProgramFiles.Hash(File.ReadAllBytes(At(name))));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("include")]
    [InlineData("incbin")]
    public void Run_RoutineInputAliasesBuildOutput_RefusesBeforeReplacingInput(string mode)
    {
        Setup();
        File.WriteAllText(At("build.po"), "RTS\n");
        File.WriteAllText(At("routine.asm"), mode == "include" ? ".include \"build.po\"\n" : ".incbin \"build.po\"\n");
        Write(At("case.json"), ExecutionSpec.Load(At("case.json")) with
        {
            Routine = new() { Source = mode == "source" ? At("build.po") : At("routine.asm") }
        });
        byte[] before = File.ReadAllBytes(At("build.po"));

        Assert.Throws<DiskException>(() => ProjectWorkflow.Run(At("project.json"), At("evidence"), overwrite: true));

        Assert.Equal(before, File.ReadAllBytes(At("build.po")));
        Assert.False(Directory.Exists(At("evidence")));
    }

    [Fact]
    public void Run_NoExplicitExpectations_RefusesBeforeReplacingOutput()
    {
        Setup();
        Write(At("case.json"), ExecutionSpec.Load(At("case.json")) with { SymbolicMemory = [], DiskAssertions = [] });
        File.WriteAllBytes(At("build.po"), [1, 2, 3]);

        DiskException error = Assert.Throws<DiskException>(() => ProjectWorkflow.Run(At("project.json"), At("evidence"),
            overwrite: true));

        Assert.Equal("execution.invalid_suite", error.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(At("build.po")));
        Assert.False(Directory.Exists(At("evidence")));
    }

    [Fact]
    public void Run_InvalidGraphicsExpectation_RefusesBeforeReplacingOutput()
    {
        Setup();
        File.WriteAllBytes(At("invalid.png"), [1, 2, 3]);
        Write(At("case.json"), ExecutionSpec.Load(At("case.json")) with
        {
            SymbolicMemory = [],
            DiskAssertions = [],
            GraphicsMemory = [new(At("invalid.png"), "hires")]
        });
        File.WriteAllBytes(At("build.po"), [4, 5, 6]);

        Assert.Throws<DiskException>(() => ProjectWorkflow.Run(At("project.json"), At("evidence"),
            overwrite: true));

        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(At("build.po")));
        Assert.False(Directory.Exists(At("evidence")));
    }

    [Fact]
    public void Run_InvalidScreenshotExpectation_RefusesBeforeReplacingOutput()
    {
        Setup();
        File.WriteAllBytes(At("invalid.png"), [1, 2, 3]);
        Write(At("case.json"), ExecutionSpec.Load(At("case.json")) with
        {
            SymbolicMemory = [],
            DiskAssertions = [],
            ScreenshotAssertion = new(At("invalid.png"))
        });
        File.WriteAllBytes(At("build.po"), [4, 5, 6]);

        Assert.Throws<DiskException>(() => ProjectWorkflow.Run(At("project.json"), At("evidence"),
            overwrite: true));

        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(At("build.po")));
        Assert.False(Directory.Exists(At("evidence")));
    }

    [Fact]
    public void Run_ExplicitDiskVerificationOnly_VerifiesBuiltImage()
    {
        Setup();
        ExecutionSpec original = ExecutionSpec.Load(At("case.json"));
        Write(At("case.json"), original with
        {
            SymbolicMemory = [],
            DiskAssertions = [],
            Disks = [original.Disks[0], new("flop2", "replaced-by-build.po", Verify: true)]
        });

        ProjectWorkflowResult result = ProjectWorkflow.Run(At("project.json"), At("evidence"));

        Assert.True(result.Passed);
        ExecutionSpec bound = ExecutionSpec.Load(Path.Combine(Assert.Single(result.Tests.Tests).ArtifactDirectory, "spec.json"));
        Assert.True(bound.Disks.Single(disk => disk.Device == "flop2").Verify);
        Assert.Equal(result.Build.OutputPath, bound.Disks.Single(disk => disk.Device == "flop2").Image);
    }

    [Fact]
    public void HashExecutionInput_EmulatorLargerThanDiskLimit_HashesWithoutImageSizeCap()
    {
        string executable = At("large-emulator");
        using (FileStream file = File.Create(executable)) file.SetLength(64 * 1024 * 1024 + 1);

        string hash = ProjectWorkflow.HashExecutionInput(executable, maximumBytes: null);

        // Independently calculated SHA-256 of 64 MiB + 1 zero bytes.
        Assert.Equal("91990977345985aaf03af1358f4f989d7eaf985b58529efb72f613c588f6599a", hash);
    }

    [Theory]
    [InlineData(4 * 1024 * 1024)]
    [InlineData(64 * 1024 * 1024)]
    public void HashExecutionInput_ConfigurationOrDiskExceedsLimit_Refuses(int limit)
    {
        string input = At("oversized-input");
        using (FileStream file = File.Create(input)) file.SetLength((long)limit + 1);

        Assert.Equal("program.too_large", Assert.Throws<DiskException>(() =>
            ProjectWorkflow.HashExecutionInput(input, limit)).Code);
    }

    [Fact]
    public void HashExecutionInput_Cancelled_StopsBeforeReading()
    {
        Assert.Throws<OperationCanceledException>(() => ProjectWorkflow.HashExecutionInput(At("missing-emulator"),
            maximumBytes: null, cancellationToken: new(true)));
    }

    [Theory]
    [InlineData("missing-symbol")]
    [InlineData("wrong-machine")]
    [InlineData("missing-emulator")]
    [InlineData("output-is-suite")]
    [InlineData("output-is-os")]
    [InlineData("artifact-parent-file")]
    [InlineData("existing-artifacts")]
    public void Run_InvalidSetup_ProtectsPreviousOutputAndInputs(string mode)
    {
        Setup();
        ExecutionSpec spec = ExecutionSpec.Load(At("case.json"));
        if (mode == "missing-symbol") spec = spec with { SymbolicMemory = [new("MAIN", "missing", "2A")] };
        if (mode == "wrong-machine") spec = spec with { Machine = "apple2p" };
        if (mode == "missing-emulator") spec = spec with { EmulatorPath = At("missing") };
        Write(At("case.json"), spec);
        string output = mode switch { "output-is-suite" => At("suite.json"), "output-is-os" => At("os.dsk"), _ => At("build.po") };
        if (!File.Exists(output)) File.WriteAllBytes(output, [1, 2, 3]);
        byte[] before = File.ReadAllBytes(output);
        string artifacts = At("evidence");
        if (mode == "artifact-parent-file")
        {
            File.WriteAllText(At("parent"), "keep");
            artifacts = At("parent/evidence");
        }
        if (mode == "existing-artifacts")
        {
            Directory.CreateDirectory(artifacts);
            File.WriteAllText(Path.Combine(artifacts, "keep.txt"), "keep");
        }
        Assert.ThrowsAny<Exception>(() => ProjectWorkflow.Run(At("project.json"), artifacts, output, overwrite: true));
        Assert.Equal(before, File.ReadAllBytes(output));
        Assert.Equal("pass", File.ReadAllText(At("os.dsk")));
        if (mode == "existing-artifacts") Assert.Equal("keep", File.ReadAllText(Path.Combine(artifacts, "keep.txt")));
        else Assert.False(Directory.Exists(artifacts));
    }

    [Theory]
    [InlineData("null-mount")]
    [InlineData("duplicate-device")]
    [InlineData("legacy-and-mounts")]
    [InlineData("null-symbol")]
    [InlineData("offset-overflow")]
    [InlineData("duplicate-address")]
    [InlineData("both-completions")]
    public void Bind_InvalidSpecification_ReportsUsageError(string mode)
    {
        Setup();
        ProjectBuildResult build = ProjectBuilder.Build(At("project.json"), preflight: true);
        ExecutionSpec spec = ExecutionSpec.Load(At("case.json"));
        spec = mode switch
        {
            "null-mount" => spec with { Disks = [null!] },
            "duplicate-device" => spec with { Disks = [new("flop2", "one"), new("flop2", "two")] },
            "legacy-and-mounts" => spec with { DiskImage = "one" },
            "null-symbol" => spec with { SymbolicMemory = [null!] },
            "offset-overflow" => spec with { SymbolicMemory = [new("MAIN", "mailbox", "2A", int.MaxValue)] },
            "duplicate-address" => spec with { Memory = [new(768, "2A")] },
            "both-completions" => spec with { Until = new(768, 42), SymbolicUntil = new("MAIN", "mailbox", 42) },
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(2, Assert.Throws<DiskException>(() => BuildExecution.Bind(spec, build, "flop2")).ExitCode);
        Assert.False(File.Exists(At("build.po")));
    }

    [Fact]
    public void Bind_SymbolicCompletionAndOffset_UsesCompiledAddresses()
    {
        Setup();
        ProjectBuildResult build = ProjectBuilder.Build(At("project.json"), preflight: true);
        ExecutionSpec spec = ExecutionSpec.Load(At("case.json")) with
        {
            SymbolicMemory = [],
            SymbolicUntil = new("main", "MAILBOX", 42, 1, 2)
        };
        ExecutionSpec bound = BuildExecution.Bind(spec, build, "flop2");
        Assert.Equal(new CompletionCondition(769, 42, 2), bound.Until);
        Assert.Null(bound.SymbolicUntil);
    }

    [Theory]
    [InlineData("flop1")]
    [InlineData("flop2")]
    public void Bind_NoSourceMount_InsertsOnlySelectedBuildDevice(string device)
    {
        Setup();
        ProjectBuildResult build = ProjectBuilder.Build(At("project.json"), preflight: true);
        ExecutionSpec spec = ExecutionSpec.Load(At("case.json")) with { Disks = [], DiskAssertions = [] };
        ExecutionSpec bound = BuildExecution.Bind(spec, build, device);
        ExecutionDisk disk = Assert.Single(bound.Disks);
        Assert.Equal(device, disk.Device);
        Assert.Equal(build.OutputPath, disk.Image);
        Assert.Equal(build.Sha256, disk.ExpectedSha256);
        Assert.Equal(768, Assert.Single(bound.Memory).Address);
        Assert.Throws<DiskException>(() => MameAdapter.Validate(spec));
    }

    [Fact]
    public void Locate_TraceSamples_MapsUniqueAddressesToSourceWithoutClaimingInstructionTrace()
    {
        Setup();
        ProjectBuildResult build = ProjectBuilder.Build(At("project.json"), preflight: true);
        Directory.CreateDirectory(At("trace"));
        File.WriteAllText(At("trace/trace.tsv"), "seconds\tPC\n0.1\t8192\n0.2\t8192\n0.3\t8193\n0.4\t65535\n");
        ExecutionResult result = new(1, "sample", true, "emulated_limit", "0.289", 1,
            new Dictionary<string, long>(), new Dictionary<int, string>(), "", build.Sha256, At("trace"), [], []);
        IReadOnlyList<ExecutionSourceLocation> locations = BuildExecution.Locate(build, result);
        Assert.Equal(2, locations.Count);
        Assert.All(locations, location => Assert.Equal("sampled PC", location.Observation));
        Assert.Equal(new[] { 3, 4 }, locations.Select(location => location.Line));
        string sourceTrace = Assert.IsType<string>(BuildExecution.WriteSourceTrace(build, result));
        string annotated = File.ReadAllText(sourceTrace);
        Assert.Contains("program\tmemoryBank\tsourceFile\tsourceLine\tsource", annotated);
        Assert.Contains("MAIN\tmain\t" + At("main.asm") + "\t3\tentry: nop", annotated);
    }

    [Fact]
    public void Locate_CpuTrace_ParsesHexPcAndUsesEmbeddedRoutineSource()
    {
        Setup();
        ProjectBuildResult build = ProjectBuilder.Build(At("project.json"), preflight: true);
        Directory.CreateDirectory(At("cpu-trace"));
        File.WriteAllText(At("cpu-trace/trace.tsv"),
            "cycleStart\tcycleEnd\tpc\topcode\tmnemonic\taBefore\txBefore\tyBefore\tpBefore\tspBefore\t" +
            "aAfter\txAfter\tyAfter\tpAfter\tspAfter\taccesses\tsourceFile\tsourceLine\tsource\n" +
            "0\t2\t2000\tA92A\tLDA\t00\t00\t00\t24\tFD\t2A\t00\t00\t24\tFD\t\t" +
            At("routine.inc") + "\t7\tlda #$2a\n");
        ExecutionResult result = new(1, "cpu", true, "routine_return", CpuExecutionEngine.Version, null,
            new Dictionary<string, long> { ["PC"] = 0x2000 }, new Dictionary<int, string>(), null, null,
            At("cpu-trace"), [], []);
        ExecutionSpec source = new()
        {
            Engine = "cpu",
            Machine = "apple2ee",
            Routine = new() { Source = At("routine.asm") }
        };

        ExecutionSourceLocation location = Assert.Single(BuildExecution.Locate(build, result, source));

        Assert.Equal(At("routine.asm"), location.Program);
        Assert.Equal(0x2000, location.Address);
        Assert.Equal(At("routine.inc"), location.File);
        Assert.Equal(7, location.Line);
        Assert.Equal("lda #$2a", location.Source);
        Assert.Equal("executed instruction", location.Observation);
        Assert.Equal("cpu", location.MemoryBank);
    }

    [Fact]
    public void WriteSourceTrace_UnmappedSamples_LeavesRawTraceWithoutDerivedArtifact()
    {
        Setup();
        ProjectBuildResult build = ProjectBuilder.Build(At("project.json"), preflight: true);
        Directory.CreateDirectory(At("unmapped-trace"));
        File.WriteAllText(At("unmapped-trace/trace.tsv"), "seconds\tPC\n0.1\t65535\n");
        ExecutionResult result = new(1, "sample", true, "emulated_limit", "0.289", 1,
            new Dictionary<string, long>(), new Dictionary<int, string>(), "", build.Sha256, At("unmapped-trace"), [], []);

        Assert.Null(BuildExecution.WriteSourceTrace(build, result));
        Assert.False(File.Exists(At("unmapped-trace/trace-source.tsv")));
        Assert.True(File.Exists(At("unmapped-trace/trace.tsv")));
    }

    [Fact]
    public void WriteSourceTrace_LargeSourceAnnotations_StopsAtEvidenceLimit()
    {
        Setup();
        ProjectBuildResult build = ProjectBuilder.Build(At("project.json"), preflight: true);
        BuiltFile file = build.Files[0] with
        {
            SourceMap = new[]
            {
                new A2Utils.Core.Assembly.AssemblySourceMapEntry(At("main.asm"), 3, 0x2000, 1)
                {
                    Source = new string('x', 4096)
                }
            }
        };
        build = build with { Files = [file] };
        Directory.CreateDirectory(At("bounded-trace"));
        System.Text.StringBuilder trace = new("seconds\tPC\n");
        for (int index = 0; index < 5000; index++) trace.Append(index).Append("\t8192\n");
        File.WriteAllText(At("bounded-trace/trace.tsv"), trace.ToString());
        ExecutionResult result = new(1, "sample", true, "emulated_limit", "0.289", 1,
            new Dictionary<string, long>(), new Dictionary<int, string>(), "", build.Sha256, At("bounded-trace"), [], []);

        string output = Assert.IsType<string>(BuildExecution.WriteSourceTrace(build, result));

        Assert.True(new FileInfo(output).Length <= 16 * 1024 * 1024);
        Assert.Contains("trace-source truncated", File.ReadAllText(output));
    }

    [Fact]
    public void Run_PreCancelled_PreservesOutputAndCreatesNoEvidence()
    {
        Setup();
        File.WriteAllBytes(At("build.po"), [1, 2, 3]);
        Assert.Throws<OperationCanceledException>(() => ProjectWorkflow.Run(At("project.json"), At("evidence"),
            overwrite: true, cancellationToken: new(true)));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(At("build.po")));
        Assert.False(Directory.Exists(At("evidence")));
    }

    [Fact]
    public async Task Run_CancelledMachine_RetainsSuiteSummaryAndSkipsLaterCases()
    {
        Setup();
        File.WriteAllText(At("os.dsk"), "cancel");
        Write(At("suite.json"), new ExecutionSuite { Tests = ["case.json", "case.json"] });
        using CancellationTokenSource cancellation = new();
        Task<ProjectWorkflowResult> task = Task.Run(() => ProjectWorkflow.Run(At("project.json"), At("evidence"),
            cancellationToken: cancellation.Token));
        Stopwatch watch = Stopwatch.StartNew();
        while (!File.Exists(At("evidence/case-001/child.pid")) && !task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(20);
        cancellation.Cancel();
        ProjectWorkflowResult result = await task;
        Assert.True(result.Cancelled);
        Assert.False(result.Passed);
        Assert.Equal("cancelled", Assert.Single(result.Tests.Tests).StopReason);
        Assert.Equal(new ExecutionSuiteCounts(2, 1, 0, 0, 1, 1), result.Tests.Counts);
        Assert.False(Directory.Exists(At("evidence/case-002")));
        Assert.True(File.Exists(At("evidence/project-result.json")));
        Assert.Equal("cancel", File.ReadAllText(At("os.dsk")));
    }

    private void Setup(string hex = "2A")
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nmailbox = $300\nentry: nop\nrts\n");
        File.WriteAllText(At("os.dsk"), "pass");
        Write(At("project.json"), new ProjectManifest
        {
            Target = "apple2enh",
            Output = "build.po",
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }],
            Execution = new("suite.json", "flop2")
        });
        Write(At("suite.json"), new ExecutionSuite { Tests = ["case.json"] });
        Write(At("case.json"), new ExecutionSpec
        {
            EmulatorPath = HostPath(),
            RomDirectory = _directory,
            Machine = "apple2ee",
            Disks = [new("flop1", "os.dsk")],
            SymbolicMemory = [new("MAIN", "mailbox", hex)],
            DiskAssertions = [new("flop2", "MAIN", Hex: "EA60", Type: "BIN", AuxType: 0x2000)],
            EmulatedSeconds = 3,
            HostTimeoutSeconds = 30
        });
    }

    private static string HostPath()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(root!.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }

    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, ProjectJson.Options));
    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, true);
}
