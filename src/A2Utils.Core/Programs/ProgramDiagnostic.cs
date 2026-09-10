namespace A2Utils.Core.Programs;

/// <summary>A source or runtime diagnostic that can be consumed without parsing prose.</summary>
public sealed record ProgramDiagnostic(
    string Code, string Severity, string Message,
    string? File = null, int? Line = null, int? Column = null,
    int? BasicLine = null, string? Symbol = null,
    string? Expected = null, string? Actual = null);
