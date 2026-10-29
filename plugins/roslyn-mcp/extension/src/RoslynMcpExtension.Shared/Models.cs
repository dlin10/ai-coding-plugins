using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace RoslynMcpExtension.Shared;

public static class ToolErrorCodes
{
	public const string InvalidArgument = "invalid_argument";
	public const string DocumentNotFound = "document_not_found";
	public const string WorkspaceNotReady = "workspace_not_ready";
	public const string Cancelled = "cancelled";
	public const string InternalError = "internal_error";
	public const string NoSolution = "no_solution";
	public const string LoadFailed = "load_failed";
}

public static class LoadStatus
{
	public const string Loaded = "loaded";
	/// <summary>The load outlasted the wait and goes on; a later call finds the solution loaded.</summary>
	public const string Loading = "loading";
}

public class LoadResult : IToolResult
{
	public string Status { get; set; } = string.Empty;
	public string Solution { get; set; } = string.Empty;
	/// <summary>How the solution was chosen: "explicit" when the client named it.</summary>
	public string? FoundBy { get; set; }
	public int? ProjectCount { get; set; }
	public int? DocumentCount { get; set; }
	/// <summary>When the daemon finished loading the solution, for whichever session asked first.</summary>
	public DateTimeOffset? LoadedAt { get; set; }
	public DaemonInfo? Daemon { get; set; }
	public bool RequestSucceeded { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorMessage { get; set; }
}

public class DaemonInfo
{
	public int ProcessId { get; set; }
	public string Version { get; set; } = string.Empty;
	/// <summary>
	/// The daemon runs inside a job that kills it when closed, because the host refused it breakaway, as
	/// codex exec does: it and every solution it holds end with the host's turn.
	/// </summary>
	public bool ExitsWithJob { get; set; }
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
    /// <summary>Errors in the document the request was about; null when it named a symbol from metadata.</summary>
    public int? DocumentErrorCount { get; init; }
}

public class SymbolLocation
{
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string MemberType { get; set; } = string.Empty;
    /// <summary>The documentation comment ID a request can name this symbol by; null for a local or a parameter.</summary>
    public string? SymbolId { get; set; }
    public string? ContainingSymbol { get; set; }
    public string? ContainingSymbolId { get; set; }
    /// <summary>For a symbol from metadata, the referenced assembly and its version.</summary>
    public string? Assembly { get; set; }
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
    /// <summary>
    /// The short form the extension's Output pane names the symbol by, such as <c>Type.Method(string)</c>. Never
    /// sent to a client: [IgnoreDataMember] drops it on the hop from Visual Studio to the server, and the MCP
    /// response leaves out a property that is null.
    /// </summary>
    [IgnoreDataMember]
    public string? LogName { get; set; }
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
    public string? ProjectName { get; set; }
}

public class DiagnosticsResult : IToolResult
{
	public bool Complete { get; set; }
	public List<string> CheckedProjects { get; set; } = [];
	public List<string> UncheckedProjects { get; set; } = [];
	public int ErrorCount { get; set; }
	public int WarningCount { get; set; }
	public List<DiagnosticInfo> Diagnostics { get; set; } = [];
	public int ReturnedCount { get; set; }
	public bool Truncated { get; set; }
	public bool RequestSucceeded { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorMessage { get; set; }
}

public class TypeMember
{
	public string Name { get; set; } = string.Empty;
	public string Kind { get; set; } = string.Empty;
	public string Signature { get; set; } = string.Empty;
	public string? SymbolId { get; set; }
	public string? Accessibility { get; set; }
	public string? Summary { get; set; }
	public bool? Obsolete { get; set; }
	/// <summary>For an extension method, the namespace a caller must import and the assembly declaring it.</summary>
	public string? Namespace { get; set; }
	public string? Assembly { get; set; }
}

public class TypeDescriptionResult : IToolResult
{
	public SymbolLocation? Type { get; set; }
	public CompilationInfo? Compilation { get; set; }
	public List<SymbolLocation> BaseTypes { get; set; } = [];
	public List<SymbolLocation> Interfaces { get; set; } = [];
	public List<TypeMember> Members { get; set; } = [];
	public List<TypeMember> Extensions { get; set; } = [];
	public int MemberCount { get; set; }
	public int ExtensionCount { get; set; }
	public bool Truncated { get; set; }
	public bool ExtensionsComplete { get; set; }
	public List<string> UnscannedAssemblies { get; set; } = [];
	public bool RequestSucceeded { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorMessage { get; set; }
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
	public int? SourceGeneratedDocumentCount { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
	public List<DiagnosticInfo> Errors { get; set; } = [];
	public List<DiagnosticInfo> Warnings { get; set; } = [];
	public List<DiagnosticInfo> AnalyzerDiagnostics { get; set; } = [];
	public bool RequestSucceeded { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorMessage { get; set; }
}
