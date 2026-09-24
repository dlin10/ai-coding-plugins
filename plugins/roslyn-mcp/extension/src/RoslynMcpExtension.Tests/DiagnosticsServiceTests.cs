using System;
using System.Threading.Tasks;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;
using Xunit;
using static RoslynMcpExtension.Tests.TestWorkspace;

namespace RoslynMcpExtension.Tests;

public sealed class DiagnosticsServiceTests : IDisposable
{
	private readonly TestWorkspace _solution = new();

	// Lib's signature changed to take a string; App still passes an int. Other is broken on its own.
	private const string ChangedLib = "public static class Api { public static int Get(string x) => 0; }";
	private const string App = "class Use { int U() => Api.Get(1); }";
	private const string Other = "class Broken { int X => Missing; }";

	private void AddThreeProjects()
	{
		var lib = _solution.AddProject("Lib", ChangedLib);
		_solution.AddProject("App", App, lib);
		_solution.AddProject("Other", Other);
	}

	private Task<DiagnosticsResult> RunAsync(string[]? filePaths = null, string? projectName = null, bool includeWarnings = true,
	                                         bool runAnalyzers = false, int maxResults = 100, TimeSpan? budget = null)
		=> new DiagnosticsService(_solution.Finder, budget ?? TimeSpan.FromMinutes(1))
			.GetDiagnosticsAsync(filePaths, projectName, includeWarnings, runAnalyzers, maxResults);

	[Fact]
	public async Task ChangedFilesCoverTheProjectsThatDependOnThem()
	{
		AddThreeProjects();

		var result = await RunAsync(filePaths: [PathOf("Lib")]);

		Assert.True(result.Complete);
		Assert.Equal(["Lib", "App"], result.CheckedProjects);
		Assert.Contains(result.Diagnostics, d => d.Id == "CS1503" && d.ProjectName == "App" && d.FilePath == PathOf("App"));
		Assert.DoesNotContain(result.Diagnostics, d => d.ProjectName == "Other");
	}

	[Fact]
	public async Task ProjectNameChecksThatProjectOnly()
	{
		AddThreeProjects();

		var result = await RunAsync(projectName: "Other");

		Assert.Equal(["Other"], result.CheckedProjects);
		Assert.Equal("CS0103", Assert.Single(result.Diagnostics).Id);
	}

	[Fact]
	public async Task NoScopeChecksTheWholeSolution()
	{
		AddThreeProjects();

		var result = await RunAsync();

		Assert.Equal(3, result.CheckedProjects.Count);
		Assert.Equal(2, result.ErrorCount);
	}

	[Fact]
	public async Task ErrorsComeBeforeWarningsWhenTheLimitCuts()
	{
		_solution.AddProject("Lib", "#warning first in file\nclass Broken { int X => Missing; }");

		var result = await RunAsync(maxResults: 1);

		Assert.Equal("Error", Assert.Single(result.Diagnostics).Severity);
		Assert.True(result.Truncated);
		Assert.Equal(1, result.WarningCount);
		Assert.DoesNotContain((await RunAsync(includeWarnings: false)).Diagnostics, d => d.Severity == "Warning");
	}

	[Fact]
	public async Task ExhaustedBudgetNamesEveryProjectItDidNotCheck()
	{
		AddThreeProjects();

		var result = await RunAsync(budget: TimeSpan.Zero);

		Assert.Null(result.ErrorMessage);
		Assert.False(result.Complete);
		Assert.Empty(result.CheckedProjects);
		Assert.Equal(3, result.UncheckedProjects.Count);
	}

	[Fact]
	public async Task RunAnalyzersAddsTheProjectAnalyzerDiagnostics()
	{
		var id = _solution.AddProject("Lib", "class Fine {}");
		var workspace = _solution.Workspace;
		workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(id, new TestAnalyzerReference(new TypeNameAnalyzer())));

		Assert.Contains((await RunAsync(runAnalyzers: true)).Diagnostics, d => d.Id == TypeNameAnalyzer.Id);
		Assert.DoesNotContain((await RunAsync()).Diagnostics, d => d.Id == TypeNameAnalyzer.Id);
	}

	[Fact]
	public async Task FilePathsAndProjectNameTogetherAreRejected()
	{
		AddThreeProjects();

		var result = await RunAsync(filePaths: [PathOf("Lib")], projectName: "Lib");

		Assert.Equal(ToolErrorCodes.InvalidArgument, result.ErrorCode);
	}

	[Fact]
	public async Task UnknownProjectNameIsAnInvalidArgument()
	{
		AddThreeProjects();

		Assert.Equal(ToolErrorCodes.InvalidArgument, (await RunAsync(projectName: "Missing")).ErrorCode);
	}

	public void Dispose() => _solution.Dispose();
}
