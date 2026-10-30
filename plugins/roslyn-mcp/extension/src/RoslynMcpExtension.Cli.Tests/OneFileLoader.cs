using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// Loads every solution as one project holding one C# file, held in memory, and keeps each load's workspace, so that a
/// test can count the loads and see which of them the daemon has released.
/// </summary>
/// <param name="filePath">The path of the one file, which need not exist.</param>
internal sealed class OneFileLoader(string filePath) : ISolutionLoader
{
	private readonly ConcurrentQueue<AdhocWorkspace> _workspaces = new();

	public int Loads => _workspaces.Count;

	/// <summary>Whether the daemon has released a load's solution, which empties its workspace.</summary>
	/// <param name="load">The load, counted from 0.</param>
	public bool Released(int load) => _workspaces.ElementAt(load).CurrentSolution.ProjectIds.Count == 0;

	public Task<LoadedSolution> LoadAsync(string solutionPath)
	{
		var workspace = new AdhocWorkspace();
		workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), solutionPath));
		var project = workspace.AddProject("OneFile", LanguageNames.CSharp);
		workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "OneFile.cs",
		                                          loader: TextLoader.From(TextAndVersion.Create(SourceText.From("class OneFile { }"), VersionStamp.Create())),
		                                          filePath: filePath));
		_workspaces.Enqueue(workspace);
		return Task.FromResult(new LoadedSolution(workspace, DateTimeOffset.UtcNow, restore: null));
	}
}
