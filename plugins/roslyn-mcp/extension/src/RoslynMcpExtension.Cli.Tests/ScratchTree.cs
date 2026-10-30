using System;
using System.IO;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// A repository laid out under the temporary folder for discovery to search, and deleted afterwards. Its ".git" folder
/// bounds every upward search, so nothing outside the tree can answer.
/// </summary>
internal sealed class ScratchTree : IDisposable
{
	public ScratchTree()
	{
		Root = Path.Combine(Path.GetTempPath(), "roslyn-mcp-discovery-tests", Guid.NewGuid().ToString("N"));
		Repo = Path.Combine(Root, "repo");
		Directory.CreateDirectory(Path.Combine(Repo, ".git", "worktrees"));
	}

	/// <summary>The folder holding <see cref="Repo"/> and its linked worktrees, outside any repository.</summary>
	public string Root { get; }

	/// <summary>The repository's main working tree.</summary>
	public string Repo { get; }

	/// <summary>A folder of the main working tree, created.</summary>
	/// <param name="parts">Its path below <see cref="Repo"/>.</param>
	public string In(params string[] parts) => Create(Path.Combine([Repo, .. parts]));

	/// <summary>An empty solution file, with its folder.</summary>
	/// <param name="folder">The folder to put it in.</param>
	/// <param name="name">Its file name.</param>
	public static string Solution(string folder, string name)
	{
		var path = Path.Combine(Create(folder), name);
		File.WriteAllText(path, "<Solution />");
		return path;
	}

	/// <summary>A marker, with its folder.</summary>
	/// <param name="folder">The folder to put it in.</param>
	/// <param name="json">What it holds.</param>
	public static string Mark(string folder, string json)
	{
		var path = Path.Combine(Create(folder), SolutionDiscovery.MarkerFileName);
		File.WriteAllText(path, json);
		return path;
	}

	/// <summary>
	/// A linked worktree of <see cref="Repo"/>, as "git worktree add" writes it: a ".git" pointer file naming the
	/// per-worktree folder, whose "commondir" points back at the shared ".git" folder.
	/// </summary>
	/// <param name="name">The worktree's name.</param>
	public string LinkedWorkTree(string name)
	{
		var gitDir = Create(Path.Combine(Repo, ".git", "worktrees", name));
		File.WriteAllText(Path.Combine(gitDir, "commondir"), "../.." + Environment.NewLine);
		var workTree = Create(Path.Combine(Root, "worktrees", name));
		File.WriteAllText(Path.Combine(workTree, ".git"), "gitdir: " + gitDir + Environment.NewLine);
		return workTree;
	}

	public void Dispose()
	{
		try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	private static string Create(string folder)
	{
		Directory.CreateDirectory(folder);
		return folder;
	}
}
