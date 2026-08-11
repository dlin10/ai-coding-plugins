using System.Collections.Generic;

namespace RoslynMcpExtension.Shared;

public static class ToolErrorCodes
{
	public const string InvalidArgument = "invalid_argument";
	public const string DocumentNotFound = "document_not_found";
	public const string WorkspaceNotReady = "workspace_not_ready";
	public const string Cancelled = "cancelled";
	public const string InternalError = "internal_error";
}

public interface IToolResult
{
	bool RequestSucceeded { get; set; }
	string? ErrorCode { get; set; }
	string? ErrorMessage { get; set; }
}

public class CompilationInfo
{
    public required string ProjectName { get; init; }
    public required string AssemblyName { get; init; }
    public required string LanguageVersion { get; init; }
    public required IReadOnlyList<string> Defines { get; init; }
    public int DocumentErrorCount { get; init; }
}

public class SymbolLocation
{
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string MemberType { get; set; } = string.Empty;
    public string? ContainingSymbol { get; set; }
    public string? ProjectName { get; set; }
    public string? Accessibility { get; set; }
    public string? ReturnType { get; set; }
    public List<string>? Modifiers { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int? EnclosingStartLine { get; set; }
    public int? EnclosingEndLine { get; set; }
}

public class DiagnosticInfo
{
    public string Id { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
}

public class SymbolListResult : IToolResult
{
	public SymbolLocation? Symbol { get; set; }
	public CompilationInfo? Compilation { get; set; }
	public List<SymbolLocation> Members { get; set; } = [];
	public int TotalCount { get; set; }
	public int ReturnedCount { get; set; }
	public bool Truncated { get; set; }
	public bool RequestSucceeded { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorMessage { get; set; }
}

public class SymbolInfoResult : IToolResult
{
	public SymbolLocation? Symbol { get; set; }
	public CompilationInfo? Compilation { get; set; }
	public string? Detail { get; set; }
	public string? Documentation { get; set; }
	public bool RequestSucceeded { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorMessage { get; set; }
}

public class ValidateFileResult : IToolResult
{
	public bool Success { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
	public List<DiagnosticInfo> Errors { get; set; } = [];
	public List<DiagnosticInfo> Warnings { get; set; } = [];
	public List<DiagnosticInfo> AnalyzerDiagnostics { get; set; } = [];
	public bool RequestSucceeded { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorMessage { get; set; }
}
