using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli;

/// <summary>What discovery found: a solution and how, or the candidates the user must choose among.</summary>
/// <param name="Solution">The full path of the solution found; null when the user must choose.</param>
/// <param name="FoundBy">How it was found, one of <see cref="SolutionSource"/>; null with no solution.</param>
/// <param name="Marker">The marker that named the solution, when one did.</param>
/// <param name="Candidates">With no solution, the ones to choose from; empty when there is none at all.</param>
internal sealed record Discovery(string? Solution, string? FoundBy, string? Marker, IReadOnlyList<string> Candidates);

/// <summary>
/// Finds the solution an agent means from its working directory, and remembers the user's choice where a later search
/// finds it; see docs/adr/0005. Never chooses among candidates itself: a wrong guess answers about another tree, and
/// nothing in the answers shows it. Searches stay inside the repository holding the directory.
/// </summary>
internal static class SolutionDiscovery
{
	/// <summary>The marker. One that names no solution, as 2.x's port-only ones do, is not a marker to discovery.</summary>
	public const string MarkerFileName = ".roslynmcp.json";

	private const string SolutionKey = "solution";

	/// <summary>Build output and package folders, which a downward search skips besides hidden and dot-named ones.</summary>
	private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

	private static readonly EnumerationOptions Entries = new() { IgnoreInaccessible = true };
	private static readonly EnumerationOptions Folders = new()
	{
		IgnoreInaccessible = true,
		AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
	};
	private static readonly JsonDocumentOptions LenientJson = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	/// <summary>
	/// The solution a directory belongs to: the nearest marker at or above it that names one; else the only solution
	/// in the nearest folder at or above it holding any; else the only marker below it. Anything else is a choice.
	/// </summary>
	/// <param name="directory">The agent's working directory, fully qualified.</param>
	public static Discovery Discover(string directory)
	{
		var start = Path.GetFullPath(directory);
		if (MarkerUpward(start) is { } marked)
			return new Discovery(marked.Solution, SolutionSource.Marker, marked.Marker, []);

		var upward = SolutionsInNearestFolderUpward(start);
		if (upward.Count == 1)
			return new Discovery(upward[0], SolutionSource.Upward, null, []);
		if (upward.Count > 1)
			return new Discovery(null, null, null, upward);

		var below = MarkersBelow(start);
		if (below.Count == 1)
			return new Discovery(below[0].Solution, SolutionSource.Downward, below[0].Marker, []);
		return new Discovery(null, null, null, below.Count > 1 ? [.. below.Select(m => m.Solution)] : SolutionsBelow(start));
	}

	/// <summary>
	/// The solution a file most likely belongs to, found as for a directory but upward only; null when that is not one
	/// solution. A guess: the caller confirms it by asking that solution for the file.
	/// </summary>
	/// <param name="filePath">The file's absolute path.</param>
	public static string? FindOwner(string filePath)
	{
		var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
		if (folder == null || !Directory.Exists(folder))
			return null;
		if (MarkerUpward(folder) is { } marked)
			return marked.Solution;
		return SolutionsInNearestFolderUpward(folder) is [var only] ? only : null;
	}

	/// <summary>
	/// Records the user's choice in a marker next to the solution, keeping whatever else the marker holds. In a linked
	/// worktree it goes to the same folder of the main working tree when that folder exists, so that the main working
	/// tree and every worktree inherit the choice.
	/// </summary>
	/// <param name="solutionPath">The full path of the chosen solution.</param>
	/// <returns>The marker written.</returns>
	public static string Remember(string solutionPath)
	{
		var solution = Path.GetFullPath(solutionPath);
		var folder = Path.GetDirectoryName(solution)!;
		if (WorkTrees.TryMapToMainWorkTree(folder, out var mainFolder) && Directory.Exists(mainFolder))
			folder = mainFolder;

		var marker = Path.Combine(folder, MarkerFileName);
		var json = ReadObject(marker) ?? new JsonObject();
		json[SolutionKey] = Path.GetFileName(solution);
		File.WriteAllText(marker, json.ToJsonString(Indented));
		return marker;
	}

	/// <summary>
	/// The nearest marker at or above a directory that names a solution: the tree's own first, then, in a linked
	/// worktree, the main working tree's, whose solution is taken from the same place in the worktree.
	/// </summary>
	/// <param name="start">The full path of the directory.</param>
	private static FoundMarker? MarkerUpward(string start)
	{
		foreach (var folder in Upward(start))
			if (ReadMarker(folder, folder) is { } own)
				return own;

		if (!WorkTrees.TryMapToMainWorkTree(start, out var mainStart))
			return null;
		foreach (var mainFolder in Upward(mainStart))
			if (ReadMarker(mainFolder, InWorkTree(mainFolder, mainStart, start)) is { } main)
				return main;
		return null;
	}

	/// <summary>The solutions of the nearest folder at or above a directory that holds any.</summary>
	/// <param name="start">The full path of the directory.</param>
	private static List<string> SolutionsInNearestFolderUpward(string start)
	{
		foreach (var folder in Upward(start))
			if (SolutionsIn(folder) is { Count: > 0 } solutions)
				return solutions;
		return [];
	}

	/// <summary>
	/// The markers below a directory that name a solution, one per solution: the tree's own, or when it has none and
	/// is a linked worktree, the main working tree's, each naming the solution at the same place in the worktree.
	/// </summary>
	/// <param name="start">The full path of the directory.</param>
	private static List<FoundMarker> MarkersBelow(string start)
	{
		var own = Below(start).Select(folder => ReadMarker(folder, folder)).OfType<FoundMarker>().ToList();
		if (own.Count == 0 && WorkTrees.TryMapToMainWorkTree(start, out var mainStart) && Directory.Exists(mainStart))
			own = [.. Below(mainStart).Select(folder => ReadMarker(folder, InWorkTree(folder, mainStart, start))).OfType<FoundMarker>()];
		return [.. own.DistinctBy(marker => marker.Solution, StringComparer.OrdinalIgnoreCase)
		              .OrderBy(marker => marker.Solution, StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>Every solution below a directory.</summary>
	/// <param name="start">The full path of the directory.</param>
	private static List<string> SolutionsBelow(string start)
		=> [.. Below(start).SelectMany(SolutionsIn).Order(StringComparer.OrdinalIgnoreCase)];

	/// <summary>The solution a marker names, when it names one that exists.</summary>
	/// <param name="markerFolder">The folder that may hold the marker.</param>
	/// <param name="solutionFolder">
	/// The folder the marker's solution is resolved in: the marker's own, or for a main working tree's marker read
	/// from a linked worktree, the same folder in the worktree.
	/// </param>
	private static FoundMarker? ReadMarker(string markerFolder, string solutionFolder)
	{
		var marker = Path.Combine(markerFolder, MarkerFileName);
		// A rooted name would lead a worktree back to the main working tree's copy of the solution.
		if (ReadObject(marker)?[SolutionKey] is not JsonValue value || !value.TryGetValue<string>(out var name)
		    || string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name))
			return null;

		var solution = Path.GetFullPath(Path.Combine(solutionFolder, name));
		return IsSolution(solution) && File.Exists(solution) ? new FoundMarker(marker, solution) : null;
	}

	/// <summary>A marker's JSON object; null when there is none to read, which a write replaces.</summary>
	/// <param name="marker">The marker's path.</param>
	private static JsonObject? ReadObject(string marker)
	{
		if (!File.Exists(marker))
			return null;
		try
		{
			return JsonNode.Parse(File.ReadAllText(marker), documentOptions: LenientJson) as JsonObject;
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>A directory and its parents up to the root of the repository holding it, or of the drive outside one.</summary>
	/// <param name="start">The full path of the directory.</param>
	private static IEnumerable<string> Upward(string start)
	{
		for (var folder = new DirectoryInfo(start); folder != null; folder = folder.Parent)
		{
			yield return folder.FullName;
			if (IsRepositoryRoot(folder.FullName))
				yield break;
		}
	}

	/// <summary>
	/// Every folder below a directory, except hidden and dot-named ones such as .git, .vs and the worktrees under
	/// .claude, build output, junctions, and nested repositories, which are other trees.
	/// </summary>
	/// <param name="start">The full path of the directory.</param>
	private static IEnumerable<string> Below(string start)
	{
		var pending = new Stack<string>([start]);
		while (pending.TryPop(out var folder))
		{
			foreach (var child in Enumerate(() => Directory.EnumerateDirectories(folder, "*", Folders)))
			{
				var name = Path.GetFileName(child);
				if (name.StartsWith('.') || SkippedFolders.Contains(name) || IsRepositoryRoot(child))
					continue;
				yield return child;
				pending.Push(child);
			}
		}
	}

	private static List<string> SolutionsIn(string folder)
		=> [.. Enumerate(() => Directory.EnumerateFiles(folder, "*.sln*", Entries)).Where(IsSolution).Order(StringComparer.OrdinalIgnoreCase)];

	/// <summary>An enumeration of a folder, empty when the folder cannot be read.</summary>
	/// <param name="enumerate">Starts the enumeration.</param>
	private static List<string> Enumerate(Func<IEnumerable<string>> enumerate)
	{
		try
		{
			return [.. enumerate()];
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private static bool IsSolution(string path)
		=> Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase)
		   || Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase);

	private static bool IsRepositoryRoot(string folder) => Path.Exists(Path.Combine(folder, ".git"));

	/// <summary>A main working tree folder's counterpart in a linked worktree.</summary>
	/// <param name="mainFolder">The folder in the main working tree.</param>
	/// <param name="mainStart">Where the search started in the main working tree.</param>
	/// <param name="start">Where it started in the worktree.</param>
	private static string InWorkTree(string mainFolder, string mainStart, string start)
		=> Path.GetFullPath(Path.Combine(start, Path.GetRelativePath(mainStart, mainFolder)));

	/// <summary>A marker that names an existing solution.</summary>
	/// <param name="Marker">The marker's path.</param>
	/// <param name="Solution">The full path of the solution it names.</param>
	private sealed record FoundMarker(string Marker, string Solution);
}
