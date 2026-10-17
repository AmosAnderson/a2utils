// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Graphics;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class StagedProjectBuildTests
{
    [Fact]
    public void Commit_ValidatedImage_PreservesReportAndCleansStaging()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string stagedPath;
        using (StagedProjectBuild staged = ProjectBuilder.Stage(ProjectResolver.Load(project, null, default)))
        {
            stagedPath = staged.ImagePath;
            Assert.True(staged.Preview.Preflight);
            Assert.False(File.Exists(staged.Preview.OutputPath));

            ProjectBuildResult result = staged.Commit();

            Assert.False(result.Preflight);
            Assert.Equal(staged.Preview.Sha256, result.Sha256);
            Assert.Equal(File.ReadAllBytes(staged.ImagePath), File.ReadAllBytes(result.OutputPath));
            Assert.Throws<InvalidOperationException>(() => staged.Commit());
        }
        Assert.False(File.Exists(stagedPath));
    }

    [Theory]
    [InlineData("source", "project.source_changed")]
    [InlineData("manifest", "project.source_changed")]
    [InlineData("staged", "project.build_changed")]
    [InlineData("destination", "write.concurrent_change")]
    [InlineData("external", "project.execution_changed")]
    public void Commit_ChangedEvidence_RefusesAndPreservesDestination(string change, string expectedCode)
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string output = workspace.NewPath("built.po");
        byte[] original = [1, 2, 3];
        File.WriteAllBytes(output, original);
        string stagedPath;
        using (StagedProjectBuild staged = ProjectBuilder.Stage(ProjectResolver.Load(project, null, default), overwrite: true))
        {
            stagedPath = staged.ImagePath;
            if (change == "source") File.AppendAllText(workspace.NewPath("main.asm"), "nop\n");
            if (change == "manifest") File.AppendAllText(project, "\n");
            if (change is "staged" or "destination")
            {
                string changedPath = change == "staged" ? staged.ImagePath : output;
                DateTime modified = File.GetLastWriteTimeUtc(changedPath);
                byte[] changedBytes = File.ReadAllBytes(changedPath);
                changedBytes[0] ^= 0xff;
                File.WriteAllBytes(changedPath, changedBytes);
                File.SetLastWriteTimeUtc(changedPath, modified);
            }

            DiskException exception = Assert.Throws<DiskException>(() => staged.Commit(change == "external"
                ? () => throw new DiskException("project.execution_changed", "Changed test input.", 6) : null));

            Assert.Equal(expectedCode, exception.Code);
            Assert.Equal(change == "destination" ? new byte[] { 0xfe, 2, 3 } : original, File.ReadAllBytes(output));
            Assert.DoesNotContain(Directory.GetFiles(workspace.DirectoryPath), path => Path.GetFileName(path).Contains(".a2-", StringComparison.Ordinal));
        }
        Assert.False(File.Exists(stagedPath));
    }

    [Theory]
    [InlineData("main.asm")]
    [InlineData("part.inc")]
    [InlineData("project.json")]
    [InlineData("disk.do")]
    public void Stage_OutputAliasesInput_RefusesAndPreservesInput(string outputName)
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        string project = Setup(workspace);
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\n.include \"part.inc\"\n");
        File.WriteAllText(workspace.NewPath("part.inc"), "rts\n");
        ProjectManifest manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(project), ProjectJson.Options)!;
        File.WriteAllText(project, JsonSerializer.Serialize(manifest with
        {
            Disk = new() { FileSystem = "dos33", Template = template }
        }, ProjectJson.Options));
        string output = workspace.NewPath(outputName);
        byte[] original = File.ReadAllBytes(output);

        DiskException exception = Assert.Throws<DiskException>(() =>
            ProjectBuilder.Stage(ProjectResolver.Load(project, output, default), overwrite: true));

        Assert.Equal("write.source_alias", exception.Code);
        Assert.Equal(original, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    public void Commit_PathBecomesSymbolicLink_RefusesAndPreservesTarget(string change)
    {
        if (OperatingSystem.IsWindows()) return;
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string output = workspace.NewPath("built.po");
        File.WriteAllBytes(output, [1, 2, 3]);
        using StagedProjectBuild staged = ProjectBuilder.Stage(ProjectResolver.Load(project, null, default), overwrite: true);
        string changedPath = change == "source" ? workspace.NewPath("main.asm") : output;
        string target = workspace.NewPath("link-target.bin");
        byte[] original = File.ReadAllBytes(changedPath);
        File.Move(changedPath, target);
        File.CreateSymbolicLink(changedPath, target);

        DiskException exception = Assert.Throws<DiskException>(() => staged.Commit());

        Assert.Equal("write.link_refused", exception.Code);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void Commit_DestinationAppearsAfterStaging_RefusesEvenWithOverwrite()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        using StagedProjectBuild staged = ProjectBuilder.Stage(ProjectResolver.Load(project, null, default), overwrite: true);
        File.WriteAllBytes(staged.Preview.OutputPath, [7, 8, 9]);

        DiskException exception = Assert.Throws<DiskException>(() => staged.Commit());

        Assert.Equal("write.concurrent_change", exception.Code);
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(staged.Preview.OutputPath));
    }

    [Fact]
    public void Commit_TemplateChangesAfterStaging_PreservesExistingOutput()
    {
        using FixtureWorkspace workspace = new();
        string template = workspace.CopyDisk();
        string project = Setup(workspace);
        ProjectManifest manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(project), ProjectJson.Options)!;
        File.WriteAllText(project, JsonSerializer.Serialize(manifest with
        {
            Disk = new() { FileSystem = "dos33", Template = template }
        }, ProjectJson.Options));
        string output = workspace.NewPath("built.po");
        File.WriteAllBytes(output, [1, 2, 3]);
        using StagedProjectBuild staged = ProjectBuilder.Stage(ProjectResolver.Load(project, null, default), overwrite: true);
        File.WriteAllBytes(template, [4, 5, 6]);

        DiskException exception = Assert.Throws<DiskException>(() => staged.Commit());

        Assert.Equal("project.source_changed", exception.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(template));
    }

    [Fact]
    public void Commit_GeneratedAssetPathAppears_RefusesWithoutOverwritingIt()
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        File.WriteAllBytes(workspace.NewPath("atlas.png"), PngCodec.Encode(new(7, 1, new byte[21])));
        ProjectManifest manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(project), ProjectJson.Options)!;
        File.WriteAllText(project, JsonSerializer.Serialize(manifest with
        {
            Assets = [new() { Name = "ATLAS", Source = "atlas.png", Output = "atlas.bin", CellHeight = 1 }]
        }, ProjectJson.Options));
        using StagedProjectBuild staged = ProjectBuilder.Stage(ProjectResolver.Load(project, null, default));
        File.WriteAllBytes(workspace.NewPath("atlas.bin"), [1, 2, 3]);

        DiskException exception = Assert.Throws<DiskException>(() => staged.Commit());

        Assert.Equal("project.asset_collision", exception.Code);
        Assert.False(File.Exists(staged.Preview.OutputPath));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(workspace.NewPath("atlas.bin")));
    }

    [Fact]
    public void Commit_NewCompilerInputAfterStaging_RefusesWithoutOutput()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("main.c"), "int main(void) { return 0; }\n");
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Output = "built.po",
            Cc65 = new() { Compiler = TestPaths.ExecutionHost() },
            Files = [new() { Source = "main.c", Path = "MAIN", Kind = "cc65" }]
        }, ProjectJson.Options));
        using StagedProjectBuild staged = ProjectBuilder.Stage(ProjectResolver.Load(project, null, default));
        File.WriteAllText(workspace.NewPath("new-header.h"), "#define VALUE 42\n");

        DiskException exception = Assert.Throws<DiskException>(() => staged.Commit());

        Assert.Equal("project.source_changed", exception.Code);
        Assert.False(File.Exists(staged.Preview.OutputPath));
    }

    [Theory]
    [InlineData("source", "project.source_changed")]
    [InlineData("destination", "write.concurrent_change")]
    [InlineData("external", "project.execution_changed")]
    public void Commit_EvidenceChangesDuringFinalValidation_RefusesTransaction(string change, string expectedCode)
    {
        using FixtureWorkspace workspace = new();
        string project = Setup(workspace);
        string output = workspace.NewPath("built.po");
        File.WriteAllBytes(output, [1, 2, 3]);
        using StagedProjectBuild staged = ProjectBuilder.Stage(ProjectResolver.Load(project, null, default), overwrite: true);
        int validations = 0;

        DiskException exception = Assert.Throws<DiskException>(() => staged.Commit(() =>
        {
            validations++;
            if (validations == 1)
            {
                if (change == "source") File.AppendAllText(workspace.NewPath("main.asm"), "nop\n");
                if (change == "destination") File.WriteAllBytes(output, [4, 5, 6]);
            }
            else if (change == "external")
            {
                throw new DiskException("project.execution_changed", "Changed during commit.", 6);
            }
        }));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(change == "destination" ? new byte[] { 4, 5, 6 } : new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
        Assert.DoesNotContain(Directory.GetFiles(workspace.DirectoryPath), path => Path.GetFileName(path).Contains(".a2-", StringComparison.Ordinal));
    }

    private static string Setup(FixtureWorkspace workspace)
    {
        File.WriteAllText(workspace.NewPath("main.asm"), ".org $2000\nrts\n");
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, JsonSerializer.Serialize(new ProjectManifest
        {
            Output = "built.po",
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }]
        }, ProjectJson.Options));
        return project;
    }
}
