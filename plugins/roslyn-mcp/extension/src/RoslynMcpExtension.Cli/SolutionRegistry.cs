using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli;

/// <summary>Opens a solution into a workspace of its own.</summary>
internal interface ISolutionLoader
{
	/// <summary>Loads the solution into a workspace of its own.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file.</param>
	Task<LoadedSolution> LoadAsync(string solutionPath);
}

/// <summary>A solution the daemon holds, and the analysis served from it.</summary>
/// <param name="workspace">The workspace the solution was loaded into.</param>
/// <param name="loadedAt">When the load finished.</param>
/// <param name="restore">What was restored before the load, or null when the loader restores nothing.</param>
internal sealed class LoadedSolution(Workspace workspace, DateTimeOffset loadedAt, RestoreReport? restore)
{
	public AnalysisService Analysis { get; } = new(workspace);
	public Solution Solution => workspace.CurrentSolution;
	public DateTimeOffset LoadedAt { get; } = loadedAt;
	public RestoreReport? Restore { get; } = restore;
	public int DocumentCount => Solution.Projects.Sum(project => project.DocumentIds.Count);
}

/// <summary>
/// The solutions this daemon holds. Each path is loaded once, however many sessions ask for it and however many ask
/// while it is still loading; a load that failed is forgotten, so that the next request tries again.
/// </summary>
/// <param name="loader">Loads a solution the registry does not hold yet.</param>
internal sealed class SolutionRegistry(ISolutionLoader loader)
{
	private readonly ConcurrentDictionary<string, Lazy<Task<LoadedSolution>>> _solutions = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The load of a solution: the one under way or finished, or a new one when there is neither.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file, compared ignoring letter case.</param>
	public Task<LoadedSolution> Load(string solutionPath)
		=> _solutions.GetOrAdd(solutionPath, path => new Lazy<Task<LoadedSolution>>(() => LoadAsync(path))).Value;

	/// <summary>Runs one load, forgetting it if it fails.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file.</param>
	private async Task<LoadedSolution> LoadAsync(string solutionPath)
	{
		try
		{
			return await loader.LoadAsync(solutionPath).ConfigureAwait(false);
		}
		catch
		{
			_solutions.TryRemove(solutionPath, out _);
			throw;
		}
	}
}
