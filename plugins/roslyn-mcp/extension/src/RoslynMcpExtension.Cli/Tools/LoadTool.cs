using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli.Tools;

[McpServerToolType]
internal sealed class LoadTool(DaemonClient daemon)
{
	[McpServerTool(Name = "roslyn_load", ReadOnly = true, Destructive = false, OpenWorld = false)]
	[Description("Loads a C# solution and makes it this session's active solution, which every other roslyn tool answers from. Call it first, with directory set to your working directory: the solution is found from there, and foundBy says how. marker: the nearest .roslynmcp.json at or above the directory names it. upward: it is the only solution in the nearest folder at or above the directory that holds any. downward: the only marker below the directory names it. Anything else answers status needsChoice with candidates and loads nothing. Then ask the user which candidate to use and call roslyn_load again with solution and remember true, which saves the choice in a marker next to that solution, so that the next session in that directory is not asked. A session no user is watching must not choose: report Roslyn unavailable instead. solution, when given, wins over directory (foundBy explicit). Afterwards, a call naming a file the active solution does not hold switches the session to the solution holding it, and its result says so in solutionSwitch. The daemon loads each solution once and keeps it loaded across sessions, so a solution another session loaded answers at once, until it goes 2 hours without calls or a session calls roslyn_unload. Every call sees the files as they are on disk: an edit needs no reload, and an edit to a project file or a file it imports restores and reloads the projects it affects. A first load that takes longer than about a minute answers status loading and goes on in the background; call roslyn_load again shortly. daemon.exitsWithJob true means the host runs this server inside a job that does not let the daemon outlive it, as codex exec does: the daemon and the solution end with the host's turn, and the next turn loads again. Before a load the daemon restores packages for the projects that need it; restore lists by project file the projects restored, the ones that failed with the reason, and notApplicable ones, which name no assets file and are never restored. partial true means a restore failed: answers about the failed projects may miss the types of their packages and report errors such as CS0246 that a restore would clear.")]
	public async Task<LoadResult> Load(
		[Description("Absolute path of the .sln or .slnx file; wins over directory")] string? solution = null,
		[Description("Your working directory, absolute; the solution is found from it")] string? directory = null,
		[Description("With solution: save it as the user's choice in a .roslynmcp.json marker next to it, so that roslyn_load from the same directory finds it without asking (default: false)")] bool remember = false)
	{
		if (solution != null)
			return await LoadExplicitAsync(solution, remember).ConfigureAwait(false);
		if (remember)
			return Invalid("remember needs solution, the solution the user chose.");
		if (directory == null)
			return Invalid("Pass directory, your working directory, or solution.");
		if (!Path.IsPathFullyQualified(directory))
			return Invalid($"The directory must be an absolute path: {directory}");
		if (!Directory.Exists(directory))
			return Invalid($"There is no directory {directory}");

		var discovery = SolutionDiscovery.Discover(directory);
		if (discovery.Solution is { } found)
			return FoundBy(await daemon.LoadAsync(found).ConfigureAwait(false), discovery.FoundBy, discovery.Marker);
		if (discovery.Candidates.Count == 0)
		{
			return new LoadResult
			{
				ErrorCode = ToolErrorCodes.NoSolution,
				ErrorMessage = $"There is no .sln or .slnx file at, above or below {directory}."
			};
		}

		return new LoadResult
		{
			Status = LoadStatus.NeedsChoice,
			Candidates = [.. discovery.Candidates],
			ErrorCode = ToolErrorCodes.NeedsChoice,
			ErrorMessage = $"Nothing marks which solution {directory} uses, and discovery does not choose. Ask the user which candidate to use, then call roslyn_load with that solution and remember true. A session no user is watching must not choose: report Roslyn unavailable."
		};
	}

	/// <summary>Loads the solution the client named, first saving it as the user's choice when asked to.</summary>
	/// <param name="solution">The solution the client named.</param>
	/// <param name="remember">Whether to save it in a marker next to it.</param>
	private async Task<LoadResult> LoadExplicitAsync(string solution, bool remember)
	{
		if (!Path.IsPathFullyQualified(solution))
			return Invalid($"The solution must be an absolute path: {solution}");
		if (!Path.GetExtension(solution).Equals(".sln", StringComparison.OrdinalIgnoreCase)
		    && !Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
			return Invalid($"The solution must be a .sln or .slnx file: {solution}");
		if (remember && !File.Exists(solution))
			return Invalid($"There is no solution to remember at {solution}");

		var marker = remember ? SolutionDiscovery.Remember(solution) : null;
		return FoundBy(await daemon.LoadAsync(solution).ConfigureAwait(false), SolutionSource.Explicit, marker);
	}

	private static LoadResult FoundBy(LoadResult result, string? foundBy, string? marker)
	{
		result.FoundBy = foundBy;
		result.Marker = marker;
		return result;
	}

	private static LoadResult Invalid(string message)
		=> new() { ErrorCode = ToolErrorCodes.InvalidArgument, ErrorMessage = message };
}
