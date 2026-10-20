// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Backends;

namespace A2Utils.Cli.Tests;

public sealed class MigrationWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-migration-cli-" + Guid.NewGuid().ToString("N"));

    public MigrationWorkflowTests()
    {
        Directory.CreateDirectory(_directory);
        DiskSession.Create(At("source.do"), "dos33");
        DiskSession.Create(At("destination.po"), "prodos", order: "prodos");
        using DiskSession source = DiskSession.Open(At("source.do"), writable: true);
        source.Add("SOURCE", [1, 2, 3], "BIN", 0x4567);
    }

    [Fact]
    public void Migrate_ReviewedPlanAndOutputAlias_PreservesInputImages()
    {
        byte[] source = File.ReadAllBytes(At("source.do"));
        byte[] destination = File.ReadAllBytes(At("destination.po"));
        var planned = Run("disk", "migrate", At("destination.po"), "SOURCE", "NEW", "--from", At("source.do"), "--preflight", "--json");
        Assert.True(planned.Code == 0, planned.Error);
        using JsonDocument plan = JsonDocument.Parse(planned.Output);
        Assert.Equal(ResultSchemas.Id("migration-plan-result"), plan.RootElement.GetProperty("resultSchemaId").GetString());
        string hash = plan.RootElement.GetProperty("data").GetProperty("planSha256").GetString()!;
        var applied = Run("disk", "migrate", At("destination.po"), "SOURCE", "NEW", "--from", At("source.do"),
            "--expect-plan-sha256", hash, "--to", At("out.po"), "--json");
        Assert.True(applied.Code == 0, applied.Error);
        using DiskSession result = DiskSession.Open(At("out.po"));
        Assert.Equal(new byte[] { 1, 2, 3 }, result.ReadFile("NEW"));
        Assert.Equal(0x4567, result.GetEntry("NEW").AuxType);
        Assert.Equal(source, File.ReadAllBytes(At("source.do")));
        Assert.Equal(destination, File.ReadAllBytes(At("destination.po")));
    }

    [Fact]
    public void Migrate_PreflightWithWriteFlags_RefusesWithoutOutput()
    {
        var result = Run("disk", "migrate", At("destination.po"), "SOURCE", "NEW", "--from", At("source.do"),
            "--preflight", "--output", At("out.po"), "--json");
        Assert.Equal(2, result.Code);
        Assert.False(File.Exists(At("out.po")));
    }

    [Fact]
    public void Info_CapabilityResultSchema_DescribesFieldsAndRefusalReasons()
    {
        var result = Run("disk", "info", At("source.do"), "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal(ResultSchemas.Id("disk-info-result"), json.RootElement.GetProperty("resultSchemaId").GetString());
        JsonElement operations = json.RootElement.GetProperty("data").GetProperty("capabilities").GetProperty("operations");
        Assert.Contains(operations.EnumerateArray(), item => item.GetProperty("operation").GetString() == "edit" && item.GetProperty("supported").GetBoolean());
        Assert.Contains(operations.EnumerateArray(), item => item.GetProperty("operation").GetString() == "createDirectories" && !item.GetProperty("supported").GetBoolean());
    }

    [Theory]
    [InlineData("disk-info-result", "capabilities")]
    [InlineData("disk-plan-result", "candidateSha256")]
    [InlineData("build-result", "files")]
    [InlineData("execution-result", "registers")]
    [InlineData("suite-result", "counts")]
    public void Schema_TypedPayload_RequiresDocumentedFields(string name, string property)
    {
        var result = Run("schema", name, "--json");
        Assert.True(result.Code == 0, result.Error);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        JsonElement schema = json.RootElement.GetProperty("data");
        Assert.Equal(ResultSchemas.Id(name), schema.GetProperty("$id").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty(property, out _));
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == property);
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        return (CliApplication.Run(args, output, error), output.ToString(), error.ToString());
    }

    private string At(string name) => Path.Combine(_directory, name);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
