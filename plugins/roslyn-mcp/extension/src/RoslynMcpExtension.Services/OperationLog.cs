using System;
using System.IO;
using Microsoft.CodeAnalysis;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

/// <summary>
/// Composes the one Output-pane line a tool call leaves once it has finished: how long it took, which tool ran, what
/// the call was about and what it found. The call is about the symbol its request resolved to; a request that
/// resolved none is named the way the client sent it.
/// </summary>
/// <param name="workspace">The workspace whose solution folder file paths are written relative to.</param>
internal sealed class OperationLog(Workspace workspace)
{
	/// <summary>Lays a line out so that durations and tool names stand in columns.</summary>
	/// <param name="milliseconds">How long the call took.</param>
	/// <param name="tool">The tool that ran.</param>
	/// <param name="text">What the call was about and what it found, or how it failed.</param>
	public static string Line(long milliseconds, string tool, string text) => $"{milliseconds,6} ms  {tool,-24} {text}";

	public static string Failed(string subject, string? code, string message)
		=> $"{subject} FAILED {code}: {string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))}";

	/// <summary>A path relative to the solution folder, or in full when it lies outside it.</summary>
	/// <param name="path">The absolute path to write.</param>
	public string RelativePath(string path)
	{
		path = path.Replace('/', '\\');
		var solution = workspace.CurrentSolution.FilePath;
		if (string.IsNullOrEmpty(solution)) return path;

		var folder = Path.GetDirectoryName(solution)!.TrimEnd('\\') + "\\";
		return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? path.Substring(folder.Length) : path;
	}

	/// <summary>A symbol as the client named it: by position or by symbol ID.</summary>
	/// <param name="filePath">The file of a position, or null when the symbol is named by ID.</param>
	/// <param name="line">The 1-based line of a position.</param>
	/// <param name="column">The 1-based column of a position.</param>
	/// <param name="symbolId">The symbol ID, when the symbol is named by one.</param>
	public string Request(string? filePath, int line, int column, string? symbolId)
		=> filePath != null ? $"{RelativePath(filePath)}:{line}:{column}" : symbolId ?? "(no symbol named)";

	/// <summary>What a diagnostics request covers: one file, several, a project or the whole solution.</summary>
	/// <param name="filePaths">The files the client changed, if it named any.</param>
	/// <param name="projectName">The project the client named, if any.</param>
	public string Scope(string[]? filePaths, string? projectName)
		=> filePaths is { Length: 1 } ? RelativePath(filePaths[0])
		 : filePaths is { Length: > 1 } ? $"{filePaths.Length} files"
		 : projectName != null ? $"project {projectName}"
		 : "solution";

	public static string DeadCodeScope(bool includeInternal, bool includePublic)
		=> "private" + (includeInternal ? "+internal" : "") + (includePublic ? "+public" : "");

	public static string Validation(ValidateFileResult result, string request, bool includeWarnings)
		=> Text(result, result.ProjectName != null ? $"{request} [{result.ProjectName}]" : request,
		        () => Problems(result.Errors.Count, result.Warnings.Count, includeWarnings)
		              + (result.AnalyzerDiagnostics.Count > 0 ? $", {Count(result.AnalyzerDiagnostics.Count, "analyzer diagnostic")}" : ""));

	public static string Diagnostics(DiagnosticsResult result, string request, bool includeWarnings)
		=> Text(result, request,
		        () => $"{Problems(result.ErrorCount, result.WarningCount, includeWarnings)} in {Count(result.CheckedProjects.Count, "project")}"
		              + (result.UncheckedProjects.Count > 0 ? $"; unchecked: {string.Join(", ", result.UncheckedProjects)}" : ""));

	/// <summary>The line for a tool that answers with a list of symbols or locations.</summary>
	/// <param name="result">The tool's result.</param>
	/// <param name="request">What the client asked about, written when no symbol was resolved.</param>
	/// <param name="one">The noun for one entry of the list.</param>
	/// <param name="many">The noun for several, when it is not <paramref name="one"/> with an "s".</param>
	public static string Found(SymbolListResult result, string request, string one, string? many = null)
		=> Text(result, result.Symbol?.LogName ?? request, () =>
		{
			var total = Math.Max(result.TotalCount, result.ReturnedCount);
			return total > result.ReturnedCount
				? $"{result.ReturnedCount} of {Count(total, one, many)} (truncated)"
				: Count(total, one, many) + (result.Truncated ? " (truncated)" : "");
		});

	public string Definition(SymbolListResult result, string request)
		=> Text(result, result.Symbol?.LogName ?? request,
		        () => result.Members.Count == 0 ? "no location"
		              : Location(result.Members[0], result.Symbol) + (result.Members.Count > 1 ? $" +{result.Members.Count - 1}" : ""));

	public static string SymbolInfo(SymbolInfoResult result, string request)
		=> Text(result, result.Symbol?.LogName ?? request, () => result.Symbol?.MemberType ?? "no symbol");

	public static string TypeDescription(TypeDescriptionResult result, string request, string? memberFilter)
		=> Text(result, (result.Type?.LogName ?? request) + (string.IsNullOrEmpty(memberFilter) ? "" : $" filter \"{memberFilter}\""),
		        () => $"{Count(result.MemberCount, "member")}, {Count(result.ExtensionCount, "extension")}"
		              + (result.Truncated ? " (truncated)" : "")
		              + (result.ExtensionsComplete
			              ? ""
			              : $", extensions incomplete ({Count(result.UnscannedAssemblies.Count, "assembly", "assemblies")} unscanned)"));

	private string Location(SymbolLocation location, SymbolLocation? symbol)
		=> location.MemberType == "metadata"
			? $"metadata {symbol?.Assembly ?? location.FilePath}"
			: $"{RelativePath(location.FilePath)}:{location.StartLine}";

	private static string Text(IToolResult result, string subject, Func<string> outcome)
		=> result.ErrorMessage == null ? $"{subject} → {outcome()}" : Failed(subject, result.ErrorCode, result.ErrorMessage);

	private static string Problems(int errors, int warnings, bool includeWarnings)
		=> includeWarnings ? $"{Count(errors, "error")}, {Count(warnings, "warning")}" : Count(errors, "error");

	private static string Count(int count, string one, string? many = null) => $"{count} {(count == 1 ? one : many ?? one + "s")}";
}
