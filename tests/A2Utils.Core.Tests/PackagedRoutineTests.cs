// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class PackagedRoutineTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-packaged-" + Guid.NewGuid().ToString("N"));

    public PackagedRoutineTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("dos33", "MAIN")]
    [InlineData("prodos", "LIB/MAIN")]
    public void Run_PackagedRoutine_UsesImageBytesBuildSymbolAndSourceMap(string fs, string program)
    {
        ExecutionSpec spec = Setup(fs, program);
        ProjectResolutionResult plan = ProjectResolver.Inspect(At("project.json"));
        Assert.DoesNotContain(plan.Dependencies, dependency => dependency.Role == "execution-routine-source");
        Assert.False(File.Exists(At("build.img")));

        ProjectWorkflowResult result = ProjectWorkflow.Run(At("project.json"), At("evidence"));
        ExecutionResult run = Assert.Single(result.Tests.Tests);
        Assert.True(run.Passed, string.Join("\n", run.Diagnostics.Select(d => d.Message)));
        Assert.Empty(run.Disks);
        Assert.Equal(result.Build.Sha256, run.InputSha256);
        BuiltFile file = Assert.Single(result.Build.Files);
        using JsonDocument metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run.ArtifactDirectory, "routine.json")));
        JsonElement root = metadata.RootElement;
        Assert.Equal(file.Sha256, root.GetProperty("sha256").GetString());
        Assert.Equal(run.InputSha256, root.GetProperty("packaged").GetProperty("imageSha256").GetString());
        Assert.Equal(program, root.GetProperty("packaged").GetProperty("program").GetString());
        Assert.Equal(0x2001, root.GetProperty("entryPoint").GetInt32());
        using JsonDocument locations = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run.ArtifactDirectory, "source-locations.json")));
        Assert.Contains(locations.RootElement.EnumerateArray(), location => location.GetProperty("program").GetString() == program
            && location.GetProperty("address").GetInt32() == 0x2001 && location.GetProperty("line").GetInt32() == 3);

        File.Delete(At("main.asm"));
        File.Delete(At("build.img"));
        ExecutionSpec archived = ExecutionSpec.Load(Path.Combine(run.ArtifactDirectory, "spec.json"));
        PreparedRoutine replay = RoutineHarness.Assemble(archived);
        Assert.Equal(file.Sha256, ProgramFiles.Hash(replay.Bytes));
        Assert.Equal(0x2001, replay.EntryPoint);
        Assert.Equal(program, ExecutionSpec.Load(At("case.json")).Routine!.Source);
        Assert.Throws<DiskException>(() => ExecutionRunner.Validate(spec));
    }

    [Fact]
    public void Run_Cc65Export_UsesLinkerAddressAndCompilerSourceMap()
    {
        File.WriteAllText(At("main.c"), "int main(void) { return 0; }\n");
        Write("project.json", new ProjectManifest
        {
            Output = "build.img",
            Cc65 = new() { Compiler = TestPaths.ExecutionHost() },
            Files = [new() { Source = "main.c", Path = "MAIN", Kind = "cc65" }],
            Execution = new("suite.json")
        });
        Write("suite.json", new ExecutionSuite { Tests = ["case.json"] });
        Write("case.json", new ExecutionSpec
        {
            Machine = "apple2e",
            Engine = "cpu",
            Trace = true,
            Routine = new() { Kind = "packaged", Source = "MAIN", EntrySymbol = "_main" }
        });
        ProjectWorkflowResult workflow = ProjectWorkflow.Run(At("project.json"), At("evidence"));
        ExecutionResult run = Assert.Single(workflow.Tests.Tests);
        Assert.True(run.Passed);
        Assert.Equal(0x0803, run.Cycles!.StartAddress);
        using JsonDocument locations = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run.ArtifactDirectory, "source-locations.json")));
        JsonElement location = Assert.Single(locations.RootElement.EnumerateArray());
        Assert.Equal("MAIN", location.GetProperty("program").GetString());
        Assert.Equal(At("main.c"), location.GetProperty("file").GetString());
    }

    [Fact]
    public void Assemble_CachedBuild_UsesDeclaredOrExplicitEntryAndRejectsChangedInputs()
    {
        ExecutionSpec spec = Setup();
        _ = ProjectBuildCache.Build(At("project.json"), At("cache"));
        ProjectBuildResult cached = ProjectBuildCache.Build(At("project.json"), At("cache"), overwrite: true);
        Assert.True(cached.CacheHit);
        File.Delete(At("main.asm"));
        ExecutionSpec explicitEntry = spec with { Routine = spec.Routine! with { EntrySymbol = null, EntryPoint = 0x2003 } };
        PreparedRoutine prepared = RoutineHarness.Assemble(BuildExecution.Bind(explicitEntry, cached));
        Assert.Equal(0x2003, prepared.EntryPoint);
        Assert.NotEmpty(prepared.SourceMap);
        ExecutionSpec defaultEntry = spec with { Routine = spec.Routine! with { EntrySymbol = null } };
        Assert.Equal(0x2001, RoutineHarness.Assemble(BuildExecution.Bind(defaultEntry, cached)).EntryPoint);
        BuiltFile file = Assert.Single(cached.Files);
        Assert.Equal("execution.packaged_hash", Assert.Throws<DiskException>(() =>
            RoutineHarness.Assemble(BuildExecution.Bind(spec, cached with { Files = [file with { Sha256 = new string('1', 64) }] }))).Code);
        Assert.Equal("execution.packaged_metadata", Assert.Throws<DiskException>(() =>
            RoutineHarness.Assemble(BuildExecution.Bind(spec, cached with { Files = [file with { Length = file.Length + 1 }] }))).Code);
        File.WriteAllText(cached.OutputPath, "changed after build");
        Assert.Equal("execution.disk_hash_mismatch", Assert.Throws<DiskException>(() =>
            RoutineHarness.Assemble(BuildExecution.Bind(spec, cached))).Code);
    }

    [Fact]
    public async Task Run_ImageChangesDuringRoutine_RetainsExecutedBytesAndReportsMismatch()
    {
        ExecutionSpec spec = Setup();
        ProjectBuildResult build = ProjectBuilder.Build(At("project.json"));
        ExecutionResult result = await CpuExecutionEngine.RunAsync(BuildExecution.Bind(spec, build),
            At("evidence"), CancellationToken.None, (_, _) => File.WriteAllText(build.OutputPath, "changed"));
        Assert.False(result.Passed);
        Assert.Equal("disk_hash_mismatch", result.StopReason);
        Assert.Equal(build.Sha256, result.InputSha256);
        Assert.Equal(Assert.Single(build.Files).Sha256, ProgramFiles.Hash(File.ReadAllBytes(At("evidence/routine.bin"))));
    }

    [Theory]
    [InlineData("missing", "execution.packaged_program")]
    [InlineData("unknown-symbol", "execution.symbol_missing")]
    [InlineData("origin", "execution.packaged_origin")]
    [InlineData("entry", "execution.routine_range")]
    [InlineData("overlap", "execution.routine_overlap")]
    [InlineData("bank", "execution.packaged_program")]
    [InlineData("type", "execution.packaged_program")]
    public void Run_InvalidPackagedRoutine_PreservesPriorDestination(string problem, string code)
    {
        ExecutionSpec spec = Setup(bank: problem == "bank" ? "aux" : "main", isText: problem == "type");
        ExecutionRoutine routine = spec.Routine!;
        Write("case.json", spec with
        {
            Routine = problem switch
            {
                "missing" => routine with { Source = "OTHER" },
                "unknown-symbol" => routine with { EntrySymbol = "unknown" },
                "origin" => routine with { Origin = 0x0800 },
                "entry" => routine with { EntrySymbol = null, EntryPoint = 0x3000 },
                "overlap" => routine with { Memory = [new(0x2001, "FF")] },
                _ => routine
            }
        });
        File.WriteAllText(At("build.img"), "prior image");
        Assert.Equal(code, Assert.Throws<DiskException>(() => ProjectResolver.Inspect(At("project.json"))).Code);
        Assert.Equal(code, Assert.Throws<DiskException>(() =>
            ProjectWorkflow.Run(At("project.json"), At("evidence"), overwrite: true)).Code);
        Assert.Equal("prior image", File.ReadAllText(At("build.img")));
        Assert.False(Directory.Exists(At("evidence")));
    }

    private ExecutionSpec Setup(string fs = "prodos", string program = "MAIN", string bank = "main", bool isText = false)
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nrts\nupdate: lda #42\nrts\n");
        Write("project.json", new ProjectManifest
        {
            Target = "apple2enh",
            Output = "build.img",
            Disk = new() { FileSystem = fs },
            Files = [new() { Source = "main.asm", Path = program, Kind = isText ? "text" : "asm",
                MemoryBank = bank, EntryPoint = isText ? null : (ushort)0x2001 }],
            Execution = new("suite.json")
        });
        Write("suite.json", new ExecutionSuite { Tests = ["case.json"] });
        ExecutionSpec spec = new()
        {
            Machine = "apple2ee",
            Engine = "cpu",
            Trace = true,
            Routine = new() { Kind = "packaged", Source = program, EntrySymbol = "UPDATE" },
            Registers = [new("A", 42)]
        };
        Write("case.json", spec);
        return spec;
    }

    private void Write<T>(string name, T value) => File.WriteAllText(At(name), JsonSerializer.Serialize(value, ProjectJson.Options));
    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
