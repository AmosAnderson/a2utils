// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Programs;

namespace A2Utils.Cli;

/// <summary>Stable schema identifiers used by CLI discovery and envelopes.</summary>
public static class CliContractSchemas
{
    public const string Result = "urn:a2utils:schema:result:2";
    public const string Error = "urn:a2utils:schema:error:2";
    public const string Envelope = "urn:a2utils:schema:envelope:2";
    public const string ProjectResolution = "urn:a2utils:schema:project-resolution:1";
}

/// <summary>Extended result envelope. The original version-1 fields remain unchanged.</summary>
public sealed record CliResultEnvelope(int SchemaVersion, string Command, object Data,
    IReadOnlyList<ProgramDiagnostic> Diagnostics)
{
    public int ContractVersion { get; init; } = 2;
    public string EnvelopeType { get; init; } = "result";
    public string SchemaId { get; init; } = CliContractSchemas.Result;
    public string ResultSchemaId { get; init; } = CliContractSchemas.Result;
}

/// <summary>Original error payload, retained as the nested error object.</summary>
public sealed record CliError(string Code, string Message, int ExitCode,
    IReadOnlyList<ProgramDiagnostic> Diagnostics);

/// <summary>Extended error envelope. The original schemaVersion/error shape remains valid.</summary>
public sealed record CliErrorEnvelope(int SchemaVersion, CliError Error)
{
    public int ContractVersion { get; init; } = 2;
    public string EnvelopeType { get; init; } = "error";
    public string SchemaId { get; init; } = CliContractSchemas.Error;
    public int ExitCode { get; init; } = Error.ExitCode;
}

public sealed record CliArgumentCapability(string Name, string? Description, string Type,
    int Minimum, int Maximum, string? PathRole);

public sealed record CliOptionCapability(string Name, string? Description, string Type,
    bool Required, IReadOnlyList<string> Aliases, bool HasDefault, object? DefaultValue,
    IReadOnlyList<object> Choices, IReadOnlyList<string> ConflictsWith,
    IReadOnlyList<string> Requires, string? PathRole);

public sealed record CliCommandCapability(string Name, string? Description,
    IReadOnlyList<string> Aliases, IReadOnlyList<CliArgumentCapability> Arguments,
    IReadOnlyList<CliOptionCapability> Options, IReadOnlyList<CliCommandCapability> Subcommands,
    IReadOnlyList<string> SideEffects, string ResultSchemaId, string ErrorSchemaId);
