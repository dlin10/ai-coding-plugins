using System;
using System.IO;
using System.Text.Json.Nodes;
using RoslynMcpExtension.Shared;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>Finding the solution from a working directory, and remembering a choice, on folders laid out on disk.</summary>
public sealed class SolutionDiscoveryTests : IDisposable
{
	private readonly ScratchTree _tree = new();

	public void Dispose() => _tree.Dispose();

	[Fact]
	public void TheNearestMarkerAboveNamesTheSolution()
	{
		var app = _tree.In("app");
		ScratchTree.Solution(app, "A.slnx");
		var b = ScratchTree.Solution(app, "B.slnx");
		var marker = ScratchTree.Mark(app, """{ "solution": "B.slnx" }""");

		var found = SolutionDiscovery.Discover(_tree.In("app", "src", "deep"));

		Assert.Equal((b, SolutionSource.Marker, marker), (found.Solution, found.FoundBy, found.Marker));
	}

	[Fact]
	public void AMarkerHoldingOnlyAPortIsSkipped()
	{
		var all = ScratchTree.Solution(_tree.Repo, "All.slnx");
		var marker = ScratchTree.Mark(_tree.Repo, """{ "solution": "All.slnx" }""");
		ScratchTree.Mark(_tree.In("app"), """{ "port": 5051 }""");

		var found = SolutionDiscovery.Discover(_tree.In("app"));

		Assert.Equal((all, SolutionSource.Marker, marker), (found.Solution, found.FoundBy, found.Marker));
	}

	[Fact]
	public void TheOnlySolutionInTheNearestFolderAboveIsFound()
	{
		var lib = ScratchTree.Solution(_tree.In("lib"), "Lib.slnx");
		ScratchTree.Solution(_tree.Repo, "Outer.slnx");

		var found = SolutionDiscovery.Discover(_tree.In("lib", "src", "deep"));

		Assert.Equal((lib, SolutionSource.Upward, (string?)null), (found.Solution, found.FoundBy, found.Marker));
	}

	[Fact]
	public void TwoSolutionsInOneFolderNeedAChoice()
	{
		var a = ScratchTree.Solution(_tree.Repo, "A.sln");
		var b = ScratchTree.Solution(_tree.Repo, "B.slnx");
		ScratchTree.Solution(_tree.In("sub", "inner"), "C.slnx");

		var found = SolutionDiscovery.Discover(_tree.In("sub"));

		Assert.Null(found.Solution);
		Assert.Equal([a, b], found.Candidates);
	}

	[Fact]
	public void TheOnlyMarkerBelowNamesTheSolution()
	{
		var x = ScratchTree.Solution(_tree.In("p", "x"), "X.slnx");
		var marker = ScratchTree.Mark(_tree.In("p", "x"), """{ "solution": "X.slnx" }""");
		ScratchTree.Solution(_tree.In("p", "y"), "Y.slnx");

		var found = SolutionDiscovery.Discover(_tree.Repo);

		Assert.Equal((x, SolutionSource.Downward, marker), (found.Solution, found.FoundBy, found.Marker));
	}

	[Fact]
	public void WithNothingMarkedEverySolutionBelowIsACandidateOutsideBuildOutputAndOtherTrees()
	{
		var x = ScratchTree.Solution(_tree.In("p", "x"), "X.slnx");
		var y = ScratchTree.Solution(_tree.In("p", "y"), "Y.sln");
		ScratchTree.Solution(_tree.In(".vs", "copy"), "Copy.sln");
		ScratchTree.Solution(_tree.In(".claude", "worktrees", "w"), "X.slnx");
		ScratchTree.Solution(_tree.In("p", "x", "bin", "Release"), "Output.sln");
		ScratchTree.Solution(_tree.In("node_modules", "n"), "Package.sln");
		Directory.CreateDirectory(_tree.In("nested", ".git"));
		ScratchTree.Solution(_tree.In("nested"), "Nested.slnx");

		var found = SolutionDiscovery.Discover(_tree.Repo);

		Assert.Null(found.Solution);
		Assert.Equal([x, y], found.Candidates);
	}

	[Fact]
	public void ASingleUnmarkedSolutionBelowIsStillAChoice()
	{
		var x = ScratchTree.Solution(_tree.In("src"), "X.slnx");

		var found = SolutionDiscovery.Discover(_tree.Repo);

		Assert.Null(found.Solution);
		Assert.Equal([x], found.Candidates);
	}

	[Fact]
	public void TheUpwardSearchStopsAtTheRepositoryRoot()
	{
		ScratchTree.Solution(_tree.Root, "Stray.slnx");
		ScratchTree.Mark(_tree.Root, """{ "solution": "Stray.slnx" }""");

		var found = SolutionDiscovery.Discover(_tree.In("empty"));

		Assert.Null(found.Solution);
		Assert.Empty(found.Candidates);
	}

	[Fact]
	public void ALinkedWorktreeUsesTheMainTreesMarkerAboveForItsOwnSolution()
	{
		ScratchTree.Solution(_tree.In("p"), "B.slnx");
		var marker = ScratchTree.Mark(_tree.In("p"), """{ "solution": "B.slnx" }""");
		var worktree = _tree.LinkedWorkTree("feature");
		ScratchTree.Solution(Path.Combine(worktree, "p"), "A.slnx");
		var b = ScratchTree.Solution(Path.Combine(worktree, "p"), "B.slnx");

		var found = SolutionDiscovery.Discover(Directory.CreateDirectory(Path.Combine(worktree, "p", "src")).FullName);

		Assert.Equal((b, SolutionSource.Marker, marker), (found.Solution, found.FoundBy, found.Marker));
	}

	[Fact]
	public void ALinkedWorktreeUsesTheMainTreesMarkerBelowForItsOwnSolution()
	{
		ScratchTree.Solution(_tree.In("p", "x"), "X.slnx");
		var marker = ScratchTree.Mark(_tree.In("p", "x"), """{ "solution": "X.slnx" }""");
		var worktree = _tree.LinkedWorkTree("feature");
		var x = ScratchTree.Solution(Path.Combine(worktree, "p", "x"), "X.slnx");
		ScratchTree.Solution(Path.Combine(worktree, "p", "y"), "Y.slnx");

		var found = SolutionDiscovery.Discover(worktree);

		Assert.Equal((x, SolutionSource.Downward, marker), (found.Solution, found.FoundBy, found.Marker));
	}

	[Fact]
	public void ARememberedChoiceIsWrittenNextToTheSolutionAndFoundFromTheSameDirectory()
	{
		var x = ScratchTree.Solution(_tree.In("p", "x"), "X.slnx");
		var y = ScratchTree.Solution(_tree.In("p", "y"), "Y.slnx");
		Assert.Equal([x, y], SolutionDiscovery.Discover(_tree.Repo).Candidates);

		var marker = SolutionDiscovery.Remember(y);
		var found = SolutionDiscovery.Discover(_tree.Repo);

		Assert.Equal(Path.Combine(_tree.Repo, "p", "y", SolutionDiscovery.MarkerFileName), marker);
		Assert.False(File.Exists(Path.Combine(_tree.Repo, SolutionDiscovery.MarkerFileName)));
		Assert.Equal((y, SolutionSource.Downward, marker), (found.Solution, found.FoundBy, found.Marker));
	}

	[Fact]
	public void RememberingKeepsWhatElseTheMarkerHolds()
	{
		var x = ScratchTree.Solution(_tree.Repo, "X.slnx");
		ScratchTree.Solution(_tree.Repo, "Y.slnx");
		var marker = ScratchTree.Mark(_tree.Repo, """{ "port": 5051 }""");

		SolutionDiscovery.Remember(x);

		var json = JsonNode.Parse(File.ReadAllText(marker))!;
		Assert.Equal((5051, "X.slnx"), (json["port"]!.GetValue<int>(), json["solution"]!.GetValue<string>()));
		Assert.Equal(x, SolutionDiscovery.Discover(_tree.Repo).Solution);
	}

	[Fact]
	public void RememberingInALinkedWorktreeWritesToTheMainTree()
	{
		_tree.In("p", "x");
		var worktree = _tree.LinkedWorkTree("feature");
		var x = ScratchTree.Solution(Path.Combine(worktree, "p", "x"), "X.slnx");
		ScratchTree.Solution(Path.Combine(worktree, "p", "y"), "Y.slnx");

		var marker = SolutionDiscovery.Remember(x);
		var found = SolutionDiscovery.Discover(worktree);

		Assert.Equal(Path.Combine(_tree.Repo, "p", "x", SolutionDiscovery.MarkerFileName), marker);
		Assert.False(File.Exists(Path.Combine(worktree, "p", "x", SolutionDiscovery.MarkerFileName)));
		Assert.Equal((x, SolutionSource.Downward), (found.Solution, found.FoundBy));
	}

	[Fact]
	public void AFilesOwnerIsTheSolutionFoundUpwardFromItsFolder()
	{
		var y = ScratchTree.Solution(_tree.In("p", "y"), "Y.slnx");
		ScratchTree.Solution(_tree.In("p", "z"), "Z1.slnx");
		ScratchTree.Solution(_tree.In("p", "z"), "Z2.slnx");

		Assert.Equal(y, SolutionDiscovery.FindOwner(Path.Combine(_tree.In("p", "y", "src"), "File.cs")));
		Assert.Null(SolutionDiscovery.FindOwner(Path.Combine(_tree.In("p", "z", "src"), "File.cs")));
	}
}
