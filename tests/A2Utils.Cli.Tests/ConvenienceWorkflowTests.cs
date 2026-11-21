// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Diagnostics;
using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;
using A2Utils.Core.Projects;

namespace A2Utils.Cli.Tests;

public sealed class ConvenienceWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-cli-convenience-" + Guid.NewGuid().ToString("N"));

    public ConvenienceWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Build_PathSelectsPreviouslyFingerprintedCompiler_RebuildsWithSelectedCompiler()
    {
        string host = TestPaths.ExecutionHost();
        string compilerName = Path.GetFileName(host);
        string originalBin = Path.GetDirectoryName(host)!;
        string installedBin = At(".toolchain/bin");
        Directory.CreateDirectory(installedBin);
        foreach (string path in Directory.EnumerateFiles(originalBin))
            File.Copy(path, Path.Combine(installedBin, Path.GetFileName(path)));
        File.WriteAllText(At("main.c"), "int main(void) { return 0; }");
        File.WriteAllText(At("project.json"), JsonSerializer.Serialize(new ProjectManifest
        {
            Cc65 = new() { Compiler = compilerName, ToolchainRoot = ".toolchain" },
            Files = [new() { Source = "main.c", Path = "MAIN", Kind = "cc65" }]
        }, ProjectJson.Options));

        foreach (var (bin, output, hit) in new[] {
            (originalBin, "first.po", false), (installedBin, "second.po", false), (originalBin, "third.po", true) })
        {
            var result = await RunProcessAsync(["build", "project.json", "--cache", "cache", "--to", output, "--json"], bin);
            Assert.True(result.Code == 0, result.Error);
            using JsonDocument json = JsonDocument.Parse(result.Output);
            JsonElement data = json.RootElement.GetProperty("data");
            Assert.Equal(hit, data.GetProperty("cacheHit").GetBoolean());
            Assert.Equal(Path.Combine(bin, compilerName),
                data.GetProperty("files")[0].GetProperty("sourceMap").GetProperty("compilerPath").GetString());
        }
    }

    [Theory]
    [InlineData("project.a2.json", true)]
    [InlineData("only.a2.json", false)]
    public async Task Project_OmittedArgument_UsesConventionalOrOnlyManifest(string name, bool otherManifest)
    {
        WriteProject(name);
        if (otherManifest) File.WriteAllText(At("other.a2.json"), "invalid");

        var built = await RunProcessAsync("build", "--check", "--json");
        var resolved = await RunProcessAsync("project", "resolve", "--json");

        Assert.True(built.Code == 0, built.Error);
        Assert.True(resolved.Code == 0, resolved.Error);
        using JsonDocument json = JsonDocument.Parse(resolved.Output);
        Assert.Equal(At(name), json.RootElement.GetProperty("data").GetProperty("manifestPath").GetString());
        Assert.False(Directory.Exists(At("build")));
    }

    [Theory]
    [InlineData(false, "project.not_found")]
    [InlineData(true, "project.ambiguous")]
    public async Task Project_OmittedArgumentWithoutUniqueManifest_ReportsActionableError(bool ambiguous, string expected)
    {
        if (ambiguous)
        {
            WriteProject("first.a2.json");
            WriteProject("second.a2.json");
        }
        foreach (string[] arguments in new[] { new[] { "build", "--check", "--json" }, new[] { "project", "resolve", "--json" } })
        {
            var result = await RunProcessAsync(arguments);
            Assert.Equal(2, result.Code);
            Assert.Empty(result.Output);
            using JsonDocument error = JsonDocument.Parse(result.Error);
            Assert.Equal(expected, error.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains("Specify PROJECT", error.RootElement.GetProperty("error").GetProperty("message").GetString());
        }
        Assert.False(Directory.Exists(At("build")));
    }

    [Fact]
    public async Task Project_ExplicitArgument_OverridesAmbiguousDiscovery()
    {
        WriteProject("first.a2.json");
        File.WriteAllText(At("second.a2.json"), "invalid");
        var result = await RunProcessAsync("build", "first.a2.json", "--check", "--json");
        Assert.True(result.Code == 0, result.Error);
    }

    [Theory]
    [InlineData("run", "case.json", "result.json")]
    [InlineData("test", "suite.json", "suite-result.json")]
    public void Execution_OmittedArtifacts_CreatesUniqueRunsBesideInput(string command, string input, string evidence)
    {
        WriteCpuSuite();
        var first = Run(command, At(input), "--json");
        var second = Run(command, At(input), "--json");
        Assert.True(first.Code == 0, first.Error);
        Assert.True(second.Code == 0, second.Error);
        string firstPath = ArtifactPath(first.Output);
        string secondPath = ArtifactPath(second.Output);
        Assert.NotEqual(firstPath, secondPath);
        Assert.Equal(At("artifacts/runs"), Path.GetDirectoryName(firstPath));
        Assert.Equal(At("artifacts/runs"), Path.GetDirectoryName(secondPath));
        Assert.StartsWith("run-", Path.GetFileName(firstPath));
        Assert.True(File.Exists(Path.Combine(firstPath, evidence)));
        Assert.True(File.Exists(Path.Combine(secondPath, evidence)));
    }

    [Theory]
    [InlineData("run", "case.json")]
    [InlineData("test", "suite.json")]
    public void Execution_ExplicitArtifacts_PreservesExactDestinationAndRefusesReuse(string command, string input)
    {
        WriteCpuSuite();
        var first = Run(command, At(input), "--artifacts", At("exact"), "--json");
        Assert.True(first.Code == 0, first.Error);
        Assert.Equal(At("exact"), ArtifactPath(first.Output));
        var second = Run(command, At(input), "--artifacts", At("exact"), "--json");
        Assert.NotEqual(0, second.Code);
        Assert.Empty(second.Output);
        Assert.False(Directory.Exists(At("artifacts")));

        var child = Run(command, At(input), "--artifacts", At("exact"), "--run-subdirectory", "--json");
        Assert.True(child.Code == 0, child.Error);
        Assert.Equal(At("exact"), Path.GetDirectoryName(ArtifactPath(child.Output)));
    }

    [Fact]
    public void Test_ListWithDefaultArtifacts_DoesNotCreateDirectories()
    {
        WriteCpuSuite();
        var result = Run("test", At("suite.json"), "--list", "--json");
        Assert.True(result.Code == 0, result.Error);
        Assert.False(Directory.Exists(At("artifacts")));
    }

    [Fact]
    public void Init_CpuStarter_PrintsQuotedCommandsAndBuildsWithDefaultArtifacts()
    {
        string directory = At("starter with ' apostrophe");
        var initialized = Run("init", directory, "--engine", "cpu", "--json");
        Assert.True(initialized.Code == 0, initialized.Error);
        using JsonDocument json = JsonDocument.Parse(initialized.Output);
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("needsEnvironmentConfiguration").GetBoolean());
        Assert.Equal("cpu", data.GetProperty("engine").GetString());
        string[] commands = data.GetProperty("nextCommands").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(2, commands.Length);
        Assert.All(commands, command => Assert.StartsWith("a2 build '", command));
        Assert.Contains(OperatingSystem.IsWindows() ? "''" : "'\\''", commands[0]);
        Assert.EndsWith("' --check", commands[0]);
        Assert.EndsWith("' --test", commands[1]);
        Assert.False(File.Exists(Path.Combine(directory, "environment.json")));

        var first = Run("build", Path.Combine(directory, "project.a2.json"), "--test", "--json");
        var second = Run("build", Path.Combine(directory, "project.a2.json"), "--test", "--overwrite", "--json");
        Assert.True(first.Code == 0, first.Error);
        Assert.True(second.Code == 0, second.Error);
        Assert.NotEqual(ArtifactPath(first.Output), ArtifactPath(second.Output));
        Assert.Equal(Path.Combine(directory, "artifacts", "runs"), Path.GetDirectoryName(ArtifactPath(first.Output)));
        Assert.True(File.Exists(Path.Combine(ArtifactPath(first.Output), "project-result.json")));
    }

    [Fact]
    public void Outputs_AliasesWriteTheSameKindsOfDestination()
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nrts\n");
        var program = Run("asm", "compile", At("main.asm"), "--output", At("main.bin"), "--json");
        Assert.True(program.Code == 0, program.Error);
        Assert.Equal(new byte[] { 0x60 }, File.ReadAllBytes(At("main.bin")));
        DiskSession.Create(At("source.do"), "dos33");
        byte[] source = File.ReadAllBytes(At("source.do"));
        var added = Run("disk", "add", At("source.do"), At("main.bin"), "--name", "MAIN", "--type", "B",
            "--load-address", "0x2000", "--to", At("output.do"), "--json");
        Assert.True(added.Code == 0, added.Error);
        Assert.Equal(source, File.ReadAllBytes(At("source.do")));
        using DiskSession result = DiskSession.Open(At("output.do"));
        Assert.Equal(new byte[] { 0x60 }, result.ReadFile("MAIN"));
    }

    [Fact]
    public void DirectoryImport_IntoAndOutput_KeepImageAndHostDestinationsDistinct()
    {
        DiskSession.Create(At("source.po"), "prodos", order: "prodos");
        using (DiskSession source = DiskSession.Open(At("source.po"), writable: true)) source.Mkdir("DOCS");
        Directory.CreateDirectory(At("input"));
        File.WriteAllText(At("input/README"), "HELLO\n");
        var result = Run("disk", "import", At("source.po"), At("input"), "--into", "DOCS", "--format", "text",
            "--output", At("output.po"), "--json");
        Assert.True(result.Code == 0, result.Error);
        using DiskSession output = DiskSession.Open(At("output.po"));
        Assert.Equal("DOCS/README", output.GetEntry("DOCS/README").Path);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("replace")]
    public void Payload_ExceedsImageCapacity_RefusesAndPreservesFiles(string operation)
    {
        PrepareDisk();
        using (FileStream payload = File.Create(At("payload"))) payload.SetLength(140 * 1024 + 1);
        var result = Run(PayloadArguments(operation, At("payload")));
        Assert.Equal(3, result.Code);
        Assert.Contains("program.too_large", result.Error);
        AssertPreservedDisk();
    }

    [Theory]
    [InlineData("add")]
    [InlineData("replace")]
    public async Task Payload_UnixPipe_RefusesPromptlyAndPreservesFiles(string operation)
    {
        if (OperatingSystem.IsWindows()) return;
        PrepareDisk();
        await CreatePipeAsync(At("pipe"));
        var result = await RunProcessAsync(PayloadArguments(operation, At("pipe")));
        Assert.Equal(6, result.Code);
        Assert.Contains("unsupported_host_entry", result.Error);
        AssertPreservedDisk();
    }

    [Theory]
    [InlineData("image")]
    [InlineData("destination")]
    [InlineData("manifest")]
    [InlineData("manifest-payload")]
    public async Task DiskInputs_UnixPipe_RefusesWithoutHangingOrReplacingOutputs(string input)
    {
        if (OperatingSystem.IsWindows()) return;
        PrepareDisk();
        string pipe = At("pipe");
        string[] arguments;
        if (input == "manifest-payload")
        {
            ExtractionManifest manifest;
            using (DiskSession source = DiskSession.Open(At("source.do")))
                manifest = FileTransfer.Extract(source, At("extracted"));
            pipe = Path.Combine(At("extracted"), manifest.Entries.Single().HostFile!);
            File.Delete(pipe);
            using (DiskSession source = DiskSession.Open(At("source.do"), writable: true)) source.Delete("TEXT");
            File.Copy(At("source.do"), At("original.do"), overwrite: true);
            arguments = ["disk", "add", At("source.do"), "--manifest", At("extracted/a2-manifest.json"),
                "--output", At("output.do"), "--overwrite", "--json"];
        }
        else if (input == "destination")
        {
            File.WriteAllBytes(At("payload.bin"), [0x60]);
            arguments = ["disk", "add", At("source.do"), At("payload.bin"), "--name", "MAIN", "--type", "B",
                "--load-address", "0x2000", "--output", pipe, "--overwrite", "--json"];
        }
        else
        {
            arguments = input == "image" ? ["disk", "info", pipe, "--json"]
                : ["disk", "add", At("source.do"), "--manifest", pipe, "--output", At("output.do"), "--overwrite", "--json"];
        }
        await CreatePipeAsync(pipe);
        var result = await RunProcessAsync(arguments);
        Assert.Equal(6, result.Code);
        Assert.Contains("unsupported_host_entry", result.Error);
        AssertPreservedDisk();
    }

    [Theory]
    [InlineData("renumber", "10 APPLE=1\n20 APRICOT=2\n30 END\n")]
    [InlineData("prepare", "APPLE=1\nAPRICOT=2\nEND\n")]
    public void BasicRewrite_AdvisoryWarnings_AreVisibleInTextMode(string command, string source)
    {
        File.WriteAllText(At("source.bas"), source);
        var result = Run("basic", command, At("source.bas"), "--to", At("output.bas"));
        Assert.Equal(0, result.Code);
        Assert.Contains("warning basic.variable_collision", result.Error);
        Assert.Contains("source line 2", result.Error);
        Assert.True(File.Exists(At("output.bas")));
    }

    private static async Task CreatePipeAsync(string path)
    {
        ProcessStartInfo start = new("/usr/bin/mkfifo") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using Process fifo = Process.Start(start)!;
        await fifo.WaitForExitAsync();
        Assert.Equal(0, fifo.ExitCode);
    }

    private void PrepareDisk()
    {
        DiskSession.Create(At("source.do"), "dos33");
        using (DiskSession session = DiskSession.Open(At("source.do"), writable: true)) session.Add("TEXT", [0xC1], "T");
        File.Copy(At("source.do"), At("original.do"));
        File.WriteAllBytes(At("output.do"), [0xAA, 0x55]);
    }

    private string[] PayloadArguments(string operation, string payload)
        => operation == "add"
            ? ["disk", "add", At("source.do"), payload, "--name", "NEW", "--type", "T", "--output", At("output.do"), "--overwrite", "--json"]
            : ["disk", "replace", At("source.do"), "TEXT", payload, "--output", At("output.do"), "--overwrite", "--json"];

    private void AssertPreservedDisk()
    {
        Assert.Equal(File.ReadAllBytes(At("original.do")), File.ReadAllBytes(At("source.do")));
        Assert.Equal(new byte[] { 0xAA, 0x55 }, File.ReadAllBytes(At("output.do")));
        Assert.Empty(Directory.GetFiles(_directory, ".*.a2-*"));
    }

    private void WriteProject(string name)
    {
        File.WriteAllText(At("main.asm"), ".org $2000\nrts\n");
        File.WriteAllText(At(name), JsonSerializer.Serialize(new ProjectManifest
        {
            Output = "build/program.po",
            Files = [new() { Source = "main.asm", Kind = "asm", Path = "MAIN" }]
        }, ProjectJson.Options));
    }

    private void WriteCpuSuite()
    {
        File.WriteAllText(At("routine.asm"), "lda #$2a\nsta $0300\nrts\n");
        File.WriteAllText(At("case.json"), JsonSerializer.Serialize(new ExecutionSpec
        {
            Engine = "cpu",
            Machine = "apple2e",
            Routine = new() { Source = "routine.asm" },
            Memory = [new(0x0300, "2A")]
        }, ExecutionSpec.JsonOptions));
        File.WriteAllText(At("suite.json"), JsonSerializer.Serialize(new ExecutionSuite { Tests = ["case.json"] }, ExecutionSpec.JsonOptions));
    }

    private static string ArtifactPath(string output)
    {
        using JsonDocument json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("data").GetProperty("artifactDirectory").GetString()!;
    }

    private static (int Code, string Output, string Error) Run(params string[] arguments)
    {
        using StringWriter output = new(), error = new();
        int code = CliApplication.Run(arguments, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private Task<(int Code, string Output, string Error)> RunProcessAsync(params string[] arguments)
        => RunProcessAsync(arguments, null);

    private async Task<(int Code, string Output, string Error)> RunProcessAsync(string[] arguments, string? pathPrefix)
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (pathPrefix is not null)
            start.Environment["PATH"] = pathPrefix + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
