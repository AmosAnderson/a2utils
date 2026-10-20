// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Programs;
using A2Utils.Core.Setup;

namespace A2Utils.Core.Tests;

/// <summary>Process-contract checks; the helper does not boot a machine or compile C.</summary>
public sealed class EnvironmentSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Smoke_ContractTools_RetainsLockedInputsAndSuccessfulWorkflow(bool compiler)
    {
        using FixtureWorkspace workspace = new();
        string profile = CreateProfile(workspace, compiler);
        byte[] original = File.ReadAllBytes(workspace.NewPath("template.po"));
        EnvironmentCheckResult result = await DevelopmentEnvironment.SmokeAsync(profile, workspace.NewPath("evidence"));
        Assert.True(result.Ready, string.Join("\n", result.Checks));
        EnvironmentSmokeEvidence evidence = Assert.IsType<EnvironmentSmokeEvidence>(result.Smoke);
        Assert.True(evidence.Passed);
        Assert.Equal(compiler ? "c" : "asm", evidence.Language);
        Assert.True(File.Exists(evidence.WorkflowResultPath));
        Assert.Equal(original, File.ReadAllBytes(workspace.NewPath("template.po")));
        Assert.Equal(File.ReadAllBytes(profile), File.ReadAllBytes(evidence.ProfileArtifact));
        DevelopmentEnvironment.ValidateLock(profile, evidence.LockPath!);
        Assert.All(evidence.GeneratedInputs, input => Assert.Equal(input.Sha256, ProgramFiles.Hash(File.ReadAllBytes(input.Path))));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(evidence.ResultPath));
        Assert.True(json.RootElement.GetProperty("ready").GetBoolean());
        string standardOutput = File.ReadAllText(workspace.NewPath("evidence/execution/case-001/emulator.stdout.txt"));
        Assert.Contains("not emulator boot", standardOutput);
    }

    [Theory]
    [InlineData("fail", "smoke.workflow")]
    [InlineData("change", "setup.lock_changed")]
    public async Task Smoke_AssertionOrInputFailure_RetainsFailedEvidenceAndTemplate(string outcome, string expectedCode)
    {
        using FixtureWorkspace workspace = new();
        string profile = CreateProfile(workspace);
        File.WriteAllText(workspace.NewPath("roms/contract-smoke-outcome"), outcome);
        byte[] template = File.ReadAllBytes(workspace.NewPath("template.po"));
        EnvironmentCheckResult result = await DevelopmentEnvironment.SmokeAsync(profile, workspace.NewPath("evidence"));
        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check => check.Code == expectedCode && !check.Passed);
        Assert.True(File.Exists(result.Smoke!.ResultPath));
        Assert.Equal(template, File.ReadAllBytes(workspace.NewPath("template.po")));
    }

    [Fact]
    public async Task Smoke_MissingTemplate_ReturnsActionableFailureEvidence()
    {
        using FixtureWorkspace workspace = new();
        string profile = CreateProfile(workspace);
        DevelopmentEnvironmentProfile settings = DevelopmentEnvironmentProfile.Load(profile) with { TemplateImage = null };
        File.WriteAllText(profile, JsonSerializer.Serialize(settings, DevelopmentEnvironment.JsonOptions));
        EnvironmentCheckResult result = await DevelopmentEnvironment.SmokeAsync(profile, workspace.NewPath("evidence"));
        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check => check.Code == "smoke.template" && check.Message.Contains("Applesoft"));
        Assert.True(File.Exists(result.Smoke!.ResultPath));
        Assert.Null(result.Smoke.ProjectPath);
    }

    [Fact]
    public async Task Smoke_InvalidTemplate_RetainsItsFingerprintWithoutStartingWorkflow()
    {
        using FixtureWorkspace workspace = new();
        string profile = CreateProfile(workspace);
        File.WriteAllBytes(workspace.NewPath("template.po"), [1, 2, 3]);
        EnvironmentCheckResult result = await DevelopmentEnvironment.SmokeAsync(profile, workspace.NewPath("evidence"));
        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check => check.Code == "template.verify" && !check.Passed);
        Assert.True(File.Exists(result.Smoke!.LockPath));
        DevelopmentEnvironment.ValidateLock(profile, result.Smoke.LockPath!);
        Assert.Null(result.Smoke.ProjectPath);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(workspace.NewPath("template.po")));
    }

    [Fact]
    public async Task Smoke_TemplateNameConflict_RecordsBuildFailureWithoutChangingTemplate()
    {
        using FixtureWorkspace workspace = new();
        string profile = CreateProfile(workspace);
        using (DiskSession disk = DiskSession.Open(workspace.NewPath("template.po"), writable: true))
            disk.Add("AI.MAIN", [0x60], "BIN", 0x2000);
        byte[] template = File.ReadAllBytes(workspace.NewPath("template.po"));
        EnvironmentCheckResult result = await DevelopmentEnvironment.SmokeAsync(profile, workspace.NewPath("evidence"));
        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check => !check.Passed && check.Code != "smoke.workflow");
        Assert.True(File.Exists(result.Smoke!.ResultPath));
        Assert.Equal(template, File.ReadAllBytes(workspace.NewPath("template.po")));
    }

    [Fact]
    public async Task Smoke_ExistingEvidenceOrInputDirectory_RefusesWithoutChanges()
    {
        using FixtureWorkspace workspace = new();
        string profile = CreateProfile(workspace);
        string evidence = workspace.NewPath("existing");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "keep.txt"), "keep");
        Assert.Equal("setup.smoke_destination", (await Assert.ThrowsAsync<DiskException>(() =>
            DevelopmentEnvironment.SmokeAsync(profile, evidence))).Code);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(evidence, "keep.txt")));
        Assert.Equal("setup.smoke_location", (await Assert.ThrowsAsync<DiskException>(() =>
            DevelopmentEnvironment.SmokeAsync(profile, workspace.NewPath("roms/new")))).Code);
        Assert.False(Directory.Exists(workspace.NewPath("roms/new")));
    }

    [Fact]
    public async Task Smoke_CancelledAfterWorkflowPreparation_RetainsFailedEvidenceAndTemplate()
    {
        using FixtureWorkspace workspace = new();
        string profile = CreateProfile(workspace);
        byte[] template = File.ReadAllBytes(workspace.NewPath("template.po"));
        using CancellationTokenSource cancellation = new();
        bool prepared = false;
        EnvironmentCheckResult result = await DevelopmentEnvironment.SmokeForTestAsync(profile,
            workspace.NewPath("evidence"), () =>
        {
            prepared = true;
            cancellation.Cancel();
        }, cancellation.Token);
        Assert.True(prepared);
        Assert.False(result.Ready);
        Assert.True(result.Smoke!.Cancelled);
        Assert.Contains(result.Checks, check => check.Code == "smoke.cancelled" && !check.Passed);
        Assert.True(File.Exists(result.Smoke.ProjectPath));
        Assert.True(File.Exists(result.Smoke.LockPath));
        Assert.True(File.Exists(result.Smoke.ResultPath));
        Assert.False(Directory.Exists(workspace.NewPath("evidence/execution")));
        DevelopmentEnvironment.ValidateLock(profile, result.Smoke.LockPath!);
        Assert.All(result.Smoke.GeneratedInputs, input =>
            Assert.Equal(input.Sha256, ProgramFiles.Hash(File.ReadAllBytes(input.Path))));
        Assert.Equal(template, File.ReadAllBytes(workspace.NewPath("template.po")));
    }

    private static string CreateProfile(FixtureWorkspace workspace, bool compiler = false)
    {
        Directory.CreateDirectory(workspace.NewPath("roms"));
        File.WriteAllText(workspace.NewPath("roms/contract.rom"), "not an Apple ROM");
        DiskSession.Create(workspace.NewPath("template.po"), "prodos", order: "prodos");
        if (compiler)
            foreach (string part in new[] { "include", "asminc", "lib", "cfg" })
                Directory.CreateDirectory(workspace.NewPath("cc65/" + part));
        DevelopmentEnvironmentProfile profile = new()
        {
            MamePath = TestPaths.ExecutionHost(),
            RomDirectory = workspace.NewPath("roms"),
            TemplateImage = workspace.NewPath("template.po"),
            BootSeconds = 1,
            Cc65Path = compiler ? TestPaths.ExecutionHost() : null,
            Cc65Root = compiler ? workspace.NewPath("cc65") : null
        };
        string path = workspace.NewPath("environment.json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, DevelopmentEnvironment.JsonOptions));
        return path;
    }
}
