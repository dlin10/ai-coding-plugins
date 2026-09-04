using System;
using System.IO;
using Xunit;

namespace RoslynMcpExtension.Tests;

/// <summary>
/// ".roslynmcp.json" is developer-local and therefore untracked, so a worktree added from a
/// configured repository starts out without one. These cover the fallback that resolves the
/// worktree's solution folder against the main working tree instead of dropping to the
/// options-page port, which used to leave the worktree serving a port no client queries.
/// </summary>
public sealed class RoslynMcpConfigTests : IDisposable
{
	private const int FallbackPort = 5050;

	private readonly string _root = Path.Combine(Path.GetTempPath(), "roslyn-mcp-config-" + Guid.NewGuid().ToString("N"));

	[Fact]
	public void AWorktreeSolutionUsesThePortConfiguredInTheMainWorkingTree()
	{
		var main = CreateMainWorkTree();
		WriteConfig(Path.Combine(main, "plugins", "sample", "src"), 5099);
		var worktree = CreateLinkedWorkTree("feature");

		var port = RoslynMcpConfig.ResolvePort(Path.Combine(worktree, "plugins", "sample", "src"), FallbackPort, out var configPath);

		Assert.Equal(5099, port);
		Assert.Equal(Path.Combine(main, "plugins", "sample", "src", RoslynMcpConfig.FileName), configPath);
	}

	[Fact]
	public void AConfigInsideTheWorktreeWinsOverTheMainWorkingTree()
	{
		var main = CreateMainWorkTree();
		WriteConfig(Path.Combine(main, "plugins", "sample", "src"), 5099);
		var worktree = CreateLinkedWorkTree("feature");
		WriteConfig(Path.Combine(worktree, "plugins", "sample", "src"), 5098);

		var port = RoslynMcpConfig.ResolvePort(Path.Combine(worktree, "plugins", "sample", "src"), FallbackPort, out _);

		Assert.Equal(5098, port);
	}

	[Fact]
	public void AWorktreeWhoseMainWorkingTreeIsAlsoUnconfiguredKeepsTheFallbackPort()
	{
		CreateMainWorkTree();
		var worktree = CreateLinkedWorkTree("feature");
		var solutionDir = Path.Combine(worktree, "plugins", "sample", "src");
		Directory.CreateDirectory(solutionDir);

		var port = RoslynMcpConfig.ResolvePort(solutionDir, FallbackPort, out var configPath);

		Assert.Equal(FallbackPort, port);
		Assert.Null(configPath);
	}

	[Fact]
	public void AnOrdinaryCheckoutIsNotMappedOntoAnything()
	{
		var main = CreateMainWorkTree();
		var solutionDir = Path.Combine(main, "plugins", "sample", "src");
		Directory.CreateDirectory(solutionDir);

		// A mapping that ignored ".git" being a directory rather than a pointer file would land
		// on this decoy, which no upward search from the solution folder can reach.
		WriteConfig(Path.Combine(_root, "plugins", "sample", "src"), 5099);

		Assert.Equal(FallbackPort, RoslynMcpConfig.ResolvePort(solutionDir, FallbackPort, out _));
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A leaked temp directory must not fail the run.
		}
	}

	private string CreateMainWorkTree()
	{
		var main = Path.Combine(_root, "repo");
		Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees"));
		return main;
	}

	// Mirrors what "git worktree add" writes: a ".git" pointer file naming the per-worktree
	// administrative directory, which in turn carries a "commondir" file pointing back at the
	// shared ".git" directory.
	private string CreateLinkedWorkTree(string name)
	{
		var gitDir = Path.Combine(_root, "repo", ".git", "worktrees", name);
		Directory.CreateDirectory(gitDir);
		File.WriteAllText(Path.Combine(gitDir, "commondir"), "../.." + Environment.NewLine);

		var workTree = Path.Combine(_root, "worktrees", name);
		Directory.CreateDirectory(workTree);
		File.WriteAllText(Path.Combine(workTree, ".git"), "gitdir: " + gitDir + Environment.NewLine);
		return workTree;
	}

	private static void WriteConfig(string dir, int port)
	{
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, RoslynMcpConfig.FileName), "{ \"port\": " + port + " }");
	}
}
