// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.Text.Json;
using A2Utils.Core.Backends;

namespace A2Utils.Cli.Tests;

public sealed class BatchMigrationWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-batch-cli-" + Guid.NewGuid().ToString("N"));

    public BatchMigrationWorkflowTests()
    {
        Directory.CreateDirectory(_directory);
        DiskSession.Create(At("source.do"), "dos33");
        DiskSession.Create(At("destination.po"), "prodos", order: "prodos");
        using DiskSession disk = DiskSession.Open(At("source.do"), writable: true);
        disk.Add("FIRST", [1, 2], "BIN", 0x2000);
        disk.Add("SECOND", [0xc1, 0x8d], "TXT");
    }

    [Fact]
    public void MigrateBatch_ReviewedSelection_ProducesTypedPlanAndMatchingCandidate()
    {
        File.WriteAllText(At("selection.json"), """
            {"schemaVersion":1,"files":[
              {"source":"FIRST","destination":"PROGRAM"},
              {"source":"SECOND","destination":"TEXT","textMode":"convert"}]}
            """);
        var planned = Run("--selection", At("selection.json"), "--preflight");
        Assert.True(planned.Code == 0, planned.Error);
        using JsonDocument plan = JsonDocument.Parse(planned.Output);
        Assert.Equal(ResultSchemas.Id("batch-migration-plan-result"), plan.RootElement.GetProperty("resultSchemaId").GetString());
        JsonElement data = plan.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("canApply").GetBoolean());
        Assert.Equal(2, data.GetProperty("files").GetArrayLength());
        var applied = Run("--selection", At("selection.json"), "--expect-plan-sha256", data.GetProperty("planSha256").GetString()!,
            "--output", At("out.po"));
        Assert.True(applied.Code == 0, applied.Error);
        using JsonDocument result = JsonDocument.Parse(applied.Output);
        Assert.Equal(ResultSchemas.Id("batch-migration-result"), result.RootElement.GetProperty("resultSchemaId").GetString());
        using DiskSession disk = DiskSession.Open(At("out.po"));
        Assert.Equal("A\r"u8.ToArray(), disk.ReadFile("TEXT"));
        Assert.Equal(new byte[] { 1, 2 }, disk.ReadFile("PROGRAM"));
        Assert.Equal(0x2000, disk.GetEntry("PROGRAM").AuxType);
    }

    [Fact]
    public void MigrateBatch_AllWithDestinationCollision_ReportsBothFilesAndRefusesApply()
    {
        using (DiskSession disk = DiskSession.Open(At("destination.po"), writable: true))
            disk.Add("FIRST", [9], "BIN");
        var planned = Run("--all", "--preflight");
        Assert.Equal(3, planned.Code);
        using JsonDocument plan = JsonDocument.Parse(planned.Output);
        JsonElement data = plan.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("canApply").GetBoolean());
        Assert.Equal(2, data.GetProperty("files").GetArrayLength());
        Assert.Contains(data.GetProperty("files").EnumerateArray(), file =>
            file.GetProperty("diagnostics").EnumerateArray().Any(issue => issue.GetProperty("code").GetString() == "destination_exists"));
        File.WriteAllText(At("keep.po"), "keep");
        Assert.Equal(3, Run("--all", "--output", At("keep.po"), "--overwrite").Code);
        Assert.Equal("keep", File.ReadAllText(At("keep.po")));
    }

    [Theory]
    [InlineData("neither")]
    [InlineData("both")]
    [InlineData("preflight-output")]
    [InlineData("selection-as-output")]
    [InlineData("malformed-json")]
    public void MigrateBatch_InvalidOptions_PreserveSelectionAndImages(string scenario)
    {
        string selection = At("selection.json");
        File.WriteAllText(selection, scenario == "malformed-json" ? "{}" :
            """{"schemaVersion":1,"files":[{"source":"FIRST","destination":"A"}]}""");
        byte[] before = File.ReadAllBytes(selection);
        byte[] destination = File.ReadAllBytes(At("destination.po"));
        var result = scenario switch
        {
            "neither" => Run("--preflight"),
            "both" => Run("--all", "--selection", selection, "--preflight"),
            "preflight-output" => Run("--all", "--preflight", "--output", At("new.po")),
            "selection-as-output" => Run("--selection", selection, "--output", selection, "--overwrite"),
            _ => Run("--selection", selection, "--output", At("new.po"))
        };
        Assert.NotEqual(0, result.Code);
        Assert.Equal(before, File.ReadAllBytes(selection));
        Assert.Equal(destination, File.ReadAllBytes(At("destination.po")));
        Assert.False(File.Exists(At("new.po")));
    }

    private (int Code, string Output, string Error) Run(params string[] options)
    {
        using StringWriter output = new();
        using StringWriter errors = new();
        int code = CliApplication.Run(["disk", "migrate-batch", At("destination.po"), "--from", At("source.do"), .. options, "--json"],
            output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    private string At(string path) => Path.Combine(_directory, path);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
