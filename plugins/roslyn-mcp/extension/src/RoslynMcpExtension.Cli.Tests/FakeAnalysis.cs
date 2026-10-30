using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// The analysis contract answered with no workspace, for tests of what crosses a pipe: it records every call it gets and
/// echoes what the call carried. The calls no test makes are not supported.
/// </summary>
internal class FakeAnalysis : IRoslynAnalysisRpc
{
	/// <summary>The one symbol <see cref="GetDocumentSymbolsAsync"/> reports, whatever the file.</summary>
	public const string SymbolName = "FromFake";

	/// <summary>Each call as its method's name and arguments, in the order they came.</summary>
	public ConcurrentQueue<string> Calls { get; } = new();

	/// <summary>What validation reports as the project's count of source-generated documents.</summary>
	public int? GeneratedDocumentCount { get; set; }

	public Task<ValidateFileResult> ValidateFileAsync(string filePath, bool includeWarnings, bool runAnalyzers)
	{
		Calls.Enqueue($"ValidateFile({filePath})");
		return Task.FromResult(new ValidateFileResult
		{
			FilePath = filePath,
			Success = true,
			RequestSucceeded = true,
			SourceGeneratedDocumentCount = GeneratedDocumentCount
		});
	}

	public Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings, bool runAnalyzers,
	                                                   int maxResults)
	{
		Calls.Enqueue($"GetDiagnostics({string.Join(", ", filePaths ?? [])})");
		return Task.FromResult(new DiagnosticsResult { CheckedProjects = [.. filePaths ?? []], Complete = true, RequestSucceeded = true });
	}

	// Succeeds only when no file path arrived, so that a test sees an omitted path cross as null rather than as an empty one.
	public Task<SymbolListResult> FindReferencesAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                  int maxResults)
	{
		Calls.Enqueue($"FindReferences({filePath ?? "null"}, {symbolId})");
		return Task.FromResult(new SymbolListResult { Symbol = new SymbolLocation { SymbolId = symbolId }, RequestSucceeded = filePath == null });
	}

	public Task<SymbolListResult> GetDocumentSymbolsAsync(string filePath)
	{
		Calls.Enqueue($"GetDocumentSymbols({filePath})");
		return Task.FromResult(new SymbolListResult
		{
			Members = [new SymbolLocation { Name = SymbolName, FilePath = filePath }],
			RequestSucceeded = true
		});
	}

	public Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                       int maxResults)
		=> throw new NotSupportedException();

	public Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                               int maxResults)
		=> throw new NotSupportedException();

	public Task<SymbolListResult> GoToDefinitionAsync(string filePath, int line, int column) => throw new NotSupportedException();

	public Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults) => throw new NotSupportedException();

	public Task<SymbolListResult> FindDeadCodeAsync(int maxResults, bool includeInternal, bool includePublic) => throw new NotSupportedException();

	public Task<SymbolInfoResult> GetSymbolInfoAsync(string? filePath, int line, int column, string? symbolId, string? projectName)
		=> throw new NotSupportedException();

	public Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId, string? projectName,
	                                                     string? memberFilter, int maxResults)
		=> throw new NotSupportedException();
}

/// <summary>A daemon that loads every solution at once into nothing, answering the analysis calls as <see cref="FakeAnalysis"/> does.</summary>
internal sealed class FakeDaemon : FakeAnalysis, IRoslynDaemonRpc
{
	public Task<LoadResult> LoadAsync(string solutionPath)
	{
		Calls.Enqueue($"Load({solutionPath})");
		return Task.FromResult(new LoadResult
		{
			Status = LoadStatus.Loaded,
			Solution = solutionPath,
			ServedBy = SolutionServer.Daemon,
			RequestSucceeded = true
		});
	}

	public Task<UnloadResult> UnloadAsync(string solutionPath) => throw new NotSupportedException();
}
