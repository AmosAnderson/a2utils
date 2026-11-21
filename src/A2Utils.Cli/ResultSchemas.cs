// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using A2Utils.Core;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;
using A2Utils.Core.Projects;

namespace A2Utils.Cli;

/// <summary>Versioned payload contracts derived from the same public types serialized by commands.</summary>
public static class ResultSchemas
{
    private static readonly IReadOnlyDictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["replay-result"] = typeof(ExecutionReplayResult),
        ["disk-inspect-result"] = typeof(DiskInspectionResult),
        ["archive-list-result"] = typeof(ShrinkItListing),
        ["archive-extract-result"] = typeof(ShrinkItExtraction),
        ["archive-catalog-result"] = typeof(ShrinkItDiskCatalog),
        ["batch-migration-plan-result"] = typeof(BatchMigrationPlan),
        ["batch-migration-result"] = typeof(BatchMigrationResult),
        ["disk-info-result"] = typeof(DiskInfo),
        ["disk-diff-result"] = typeof(DiskDifference),
        ["disk-plan-result"] = typeof(DiskChangePlan),
        ["disk-apply-result"] = typeof(DiskApplyResult),
        ["migration-plan-result"] = typeof(DiskMigrationPlan),
        ["migration-result"] = typeof(DiskMigrationResult),
        ["build-result"] = typeof(ProjectBuildResult),
        ["build-test-result"] = typeof(ProjectWorkflowResult),
        ["execution-result"] = typeof(ExecutionResult),
        ["suite-result"] = typeof(ExecutionSuiteResult),
        ["suite-plan-result"] = typeof(ExecutionSuitePlan)
    };

    public static IReadOnlyList<string> Names { get; } = [.. Types.Keys, "build-command-result", "test-command-result", "migration-command-result", "batch-migration-command-result"];

    public static string Id(string name) => $"urn:a2utils:schema:{name}:1";

    public static string ForCommand(string command) => command switch
    {
        "execution.replay" or "a2 replay" => Id("replay-result"),
        "disk.inspect" or "a2 disk inspect" => Id("disk-inspect-result"),
        "archive.ls" or "a2 archive ls" => Id("archive-list-result"),
        "archive.extract" or "a2 archive extract" => Id("archive-extract-result"),
        "archive.catalog" or "a2 archive catalog" => Id("archive-catalog-result"),
        "disk.migrate-batch.plan" => Id("batch-migration-plan-result"),
        "disk.migrate-batch" => Id("batch-migration-result"),
        "a2 disk migrate-batch" => Id("batch-migration-command-result"),
        "info" or "a2 disk info" => Id("disk-info-result"),
        "diff" or "a2 disk diff" => Id("disk-diff-result"),
        "disk.plan" or "a2 disk plan" => Id("disk-plan-result"),
        "disk.apply" or "a2 disk apply" => Id("disk-apply-result"),
        "disk.migrate.plan" => Id("migration-plan-result"),
        "disk.migrate" => Id("migration-result"),
        "a2 disk migrate" => Id("migration-command-result"),
        "build" => Id("build-result"),
        "build.test" => Id("build-test-result"),
        "a2 build" => Id("build-command-result"),
        "run" or "a2 run" => Id("execution-result"),
        "test" => Id("suite-result"),
        "test.list" => Id("suite-plan-result"),
        "a2 test" => Id("test-command-result"),
        "project.resolve" or "a2 project resolve" => CliContractSchemas.ProjectResolution,
        _ => CliContractSchemas.Result
    };

    public static JsonObject Create(string name)
    {
        JsonObject schema;
        if (Types.TryGetValue(name, out Type? type))
        {
            JsonSerializerOptions options = new(FileTransfer.JsonOptions)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
                RespectNullableAnnotations = true,
                RespectRequiredConstructorParameters = true
            };
            schema = options.GetJsonSchemaAsNode(type, new()
            {
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode = (context, node) =>
                {
                    if (node is JsonObject date &&
                        (Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type) == typeof(DateTime))
                    {
                        // Filesystem dates and reproducible manifest timestamps can be offset-free.
                        date.Remove("format");
                        date["pattern"] = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})?$";
                    }
                    if (node is JsonObject item && item["properties"] is JsonObject properties)
                    {
                        item["required"] = new JsonArray(properties.Where(property =>
                            context.TypeInfo.Properties.FirstOrDefault(info => info.Name == property.Key)?.ShouldSerialize is null)
                            .Select(property => (JsonNode?)JsonValue.Create(property.Key)).ToArray());
                        item["additionalProperties"] = false;
                    }
                    return node;
                }
            }).AsObject();
        }
        else
        {
            string[] alternatives = name switch
            {
                "build-command-result" => ["build-result", "build-test-result"],
                "test-command-result" => ["suite-result", "suite-plan-result"],
                "batch-migration-command-result" => ["batch-migration-result", "batch-migration-plan-result"],
                "migration-command-result" => ["migration-result", "migration-plan-result"],
                _ => throw new ArgumentException("Unknown result schema.", nameof(name))
            };
            schema = new() { ["oneOf"] = new JsonArray(alternatives.Select(item => (JsonNode?)new JsonObject { ["$ref"] = Id(item) }).ToArray()) };
        }
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = Id(name);
        schema["title"] = $"A2Utils {name} payload v1";
        return schema;
    }
}
