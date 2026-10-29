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
	[Description("Loads a C# solution and makes it this session's active solution, which every other roslyn tool answers from. Call it first. The daemon loads each solution once and keeps it loaded across sessions, so a solution another session loaded answers at once. A first load that takes longer than about a minute answers status loading and goes on in the background; call roslyn_load again shortly. daemon.exitsWithJob true means the host runs this server inside a job that does not let the daemon outlive it, as codex exec does: the daemon and the solution end with the host's turn, and the next turn loads again.")]
	public Task<LoadResult> Load(
		[Description("Absolute path of the .sln or .slnx file")] string solution)
	{
		if (!Path.IsPathFullyQualified(solution))
			return Task.FromResult(Invalid($"The solution must be an absolute path: {solution}"));
		if (!Path.GetExtension(solution).Equals(".sln", StringComparison.OrdinalIgnoreCase)
		    && !Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
			return Task.FromResult(Invalid($"The solution must be a .sln or .slnx file: {solution}"));

		return daemon.LoadAsync(solution);
	}

	private static LoadResult Invalid(string message)
		=> new() { ErrorCode = ToolErrorCodes.InvalidArgument, ErrorMessage = message };
}
