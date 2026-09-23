using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

/// <summary>
/// Compiler and analyzer diagnostics across several projects, checked in dependency order under a time
/// budget; see docs/adr/0002.
/// </summary>
internal class DiagnosticsService(DocumentFinder documentFinder, TimeSpan budget)
{
	public const int BudgetSeconds = 45;

	public async Task<DiagnosticsResult> GetDiagnosticsAsync(string[]? filePaths, string? projectName, bool includeWarnings,
	                                                         bool runAnalyzers, int maxResults)
	{
		var result = new DiagnosticsResult();

		try
		{
			if (filePaths is { Length: > 0 } && projectName != null)
			{
				throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
				                               "Pass filePaths or projectName, not both.");
			}

			var solution = documentFinder.Workspace.CurrentSolution;
			if (!solution.Projects.Any())
			{
				throw new ToolRequestException(ToolErrorCodes.WorkspaceNotReady,
				                               "No solution is loaded or the workspace is still initializing.");
			}

			var scope = Scope(solution, filePaths, projectName);
			var ordered = solution.GetProjectDependencyGraph().GetTopologicallySortedProjects()
				.Where(scope.Contains)
				.Select(solution.GetProject)
				.OfType<Project>()
				.ToList();

			var found = new List<DiagnosticInfo>();
			var clock = Stopwatch.StartNew();
			using var deadline = new CancellationTokenSource(budget);
			foreach (var project in ordered)
			{
				if (clock.Elapsed >= budget)
				{
					result.UncheckedProjects.Add(project.Name);
					continue;
				}

				try
				{
					found.AddRange(await CollectAsync(project, includeWarnings, runAnalyzers, deadline.Token));
					result.CheckedProjects.Add(project.Name);
				}
				catch (OperationCanceledException) when (deadline.IsCancellationRequested)
				{
					result.UncheckedProjects.Add(project.Name);
				}
			}

			// A multi-targeted project compiles each file once per framework; the same diagnostic is reported once.
			var distinct = found
				.GroupBy(d => (d.Id, d.FilePath, d.StartLine, d.StartColumn, d.Message))
				.Select(group => group.First())
				.OrderBy(d => d.Severity == nameof(DiagnosticSeverity.Error) ? 0 : 1)
				.ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(d => d.StartLine)
				.ThenBy(d => d.StartColumn)
				.ToList();

			result.Complete = result.UncheckedProjects.Count == 0;
			result.ErrorCount = distinct.Count(d => d.Severity == nameof(DiagnosticSeverity.Error));
			result.WarningCount = distinct.Count - result.ErrorCount;
			result.Diagnostics.AddRange(distinct.Take(maxResults));
			result.ReturnedCount = result.Diagnostics.Count;
			result.Truncated = distinct.Count > maxResults;
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}

	private HashSet<ProjectId> Scope(Solution solution, string[]? filePaths, string? projectName)
	{
		if (projectName != null)
			return DocumentFinder.ProjectsNamed(solution, projectName).Select(project => project.Id).ToHashSet();

		if (filePaths is not { Length: > 0 })
			return solution.ProjectIds.ToHashSet();

		var graph = solution.GetProjectDependencyGraph();
		var scope = new HashSet<ProjectId>();
		foreach (var filePath in filePaths)
		{
			// Every project compiling the file, not just the first: a linked or multi-targeted file has several.
			var ids = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath));
			var projects = ids.IsEmpty ? [documentFinder.FindDocument(filePath).Project.Id] : ids.Select(id => id.ProjectId);
			foreach (var project in projects)
			{
				scope.Add(project);
				scope.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(project));
			}
		}

		return scope;
	}

	private static async Task<IEnumerable<DiagnosticInfo>> CollectAsync(Project project, bool includeWarnings, bool runAnalyzers,
	                                                                    CancellationToken cancellation)
	{
		var compilation = await project.GetCompilationAsync(cancellation);
		if (compilation == null)
			return [];

		var diagnostics = compilation.GetDiagnostics(cancellation).AsEnumerable();
		var withAnalyzers = runAnalyzers ? ProjectAnalyzers.Attach(project, compilation) : null;
		if (withAnalyzers != null)
			diagnostics = diagnostics.Concat(await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellation));

		return diagnostics
			.Where(d => d.Severity == DiagnosticSeverity.Error || (includeWarnings && d.Severity == DiagnosticSeverity.Warning))
			.Select(d =>
			{
				var info = ValidateFileService.ToDiagnosticInfo(d);
				info.ProjectName = project.Name;
				return info;
			})
			.ToList();
	}
}
