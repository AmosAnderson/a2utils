// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

namespace A2Utils.Core.Programs;

/// <summary>A source or runtime diagnostic that can be consumed without parsing prose.</summary>
public sealed record ProgramDiagnostic(
    string Code, string Severity, string Message,
    string? File = null, int? Line = null, int? Column = null,
    int? BasicLine = null, string? Symbol = null,
    string? Expected = null, string? Actual = null)
{
    /// <summary>Optional inclusive end of the source range.</summary>
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }

    /// <summary>RFC 6901 pointer when the diagnostic refers to a JSON document.</summary>
    public string? JsonPointer { get; init; }

    /// <summary>Build or execution phase and tool that produced the diagnostic.</summary>
    public string? Phase { get; init; }
    public string? Tool { get; init; }

    /// <summary>Optional documentation and machine-applicable repair information.</summary>
    public string? HelpUri { get; init; }
    public IReadOnlyList<DiagnosticRelatedLocation> Related { get; init; } = [];
    public IReadOnlyList<DiagnosticFix> Fixes { get; init; } = [];
}

public sealed record DiagnosticRelatedLocation(string Message, string? File = null,
    int? Line = null, int? Column = null, int? EndLine = null, int? EndColumn = null,
    string? JsonPointer = null);

public sealed record DiagnosticEdit(string File, string Replacement,
    int? Line = null, int? Column = null, int? EndLine = null, int? EndColumn = null,
    string? JsonPointer = null);

public sealed record DiagnosticFix(string Description, IReadOnlyList<DiagnosticEdit> Edits);
