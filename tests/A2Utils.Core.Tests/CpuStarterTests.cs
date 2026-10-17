// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Execution;
using A2Utils.Core.Projects;
using A2Utils.Core.Setup;

namespace A2Utils.Core.Tests;

public sealed class CpuStarterTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-cpu-starter-" + Guid.NewGuid().ToString("N"));

    public CpuStarterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Create_CpuStarter_BuildsAndTestsWithoutExternalToolsAndDetectsChangedProgram()
    {
        string project = Path.Combine(_directory, "project");
        ProjectStarterResult created = ProjectStarter.Create(project, engine: "cpu");
        Assert.False(created.NeedsEnvironmentConfiguration);
        Assert.False(File.Exists(Path.Combine(project, "environment.json")));
        string manifest = Path.Combine(project, "project.a2.json");
        ProjectWorkflowResult first = ProjectWorkflow.Run(manifest, Path.Combine(_directory, "first"));
        Assert.True(first.Passed);
        Assert.Equal(CpuExecutionEngine.Version, Assert.Single(first.Tests.Tests).EmulatorVersion);
        byte[] source = File.ReadAllBytes(Path.Combine(project, "main.asm"));
        Assert.Throws<DiskException>(() => ProjectStarter.Create(project, engine: "cpu"));
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(project, "main.asm")));

        string main = Path.Combine(project, "main.asm");
        File.WriteAllText(main, File.ReadAllText(main).Replace("#$2a", "#$2b", StringComparison.Ordinal));
        ProjectWorkflowResult changed = ProjectWorkflow.Run(manifest, Path.Combine(_directory, "changed"), overwrite: true);
        Assert.False(changed.Passed);
        Assert.NotEqual(first.Build.Sha256, changed.Build.Sha256);
        Assert.Contains(changed.Tests.Tests[0].Diagnostics, diagnostic => diagnostic.Code == "execution.memory_assertion");
    }

    [Theory]
    [InlineData("basic", false, null, "cpu")]
    [InlineData("c", false, null, "cpu")]
    [InlineData("asm", true, null, "cpu")]
    [InlineData("asm", false, "missing-profile.json", "cpu")]
    [InlineData("asm", false, null, "unknown")]
    public void Create_UnsupportedCpuOptions_RefusesBeforeCreatingDirectory(string language, bool bareMetal, string? environment, string engine)
    {
        string output = Path.Combine(_directory, "refused");
        Assert.Throws<DiskException>(() => ProjectStarter.Create(output, language, environment, bareMetal: bareMetal, engine: engine));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Create_CancelledCpuStarter_LeavesNoOutput()
    {
        string output = Path.Combine(_directory, "cancelled");
        Assert.Throws<OperationCanceledException>(() => ProjectStarter.Create(output,
            cancellationToken: new CancellationToken(true), engine: "cpu"));
        Assert.False(Directory.Exists(output));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
