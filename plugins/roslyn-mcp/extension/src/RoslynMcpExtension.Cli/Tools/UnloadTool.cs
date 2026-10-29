using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli.Tools;

/// <summary>roslyn_unload: drops a solution from the daemon when the agent is done with it.</summary>
/// <param name="daemon">The session's client of the daemon.</param>
/// <remarks>
/// Annotated read-only, as roslyn_load is: it changes no file, only what the daemon holds in memory, and any other
/// annotation would make Codex ask for approval on every call, which no unattended run can give. See docs/adr/0009.
/// </remarks>
[McpServerToolType]
internal sealed class UnloadTool(DaemonClient daemon)
{
	[McpServerTool(Name = "roslyn_unload", ReadOnly = true, Destructive = false, OpenWorld = false)]
	[Description("Unloads a solution from the daemon at once and frees its memory. Call it when you are done with the solution: at the end of an unattended run, before archiving the session, before removing the worktree that holds it. Without solution it unloads this session's active solution. The session keeps that solution as its active one, and any session that still uses it, this one included, loads it again on its next call. unloaded false means the daemon did not hold the solution. Left alone, the daemon unloads a solution after 2 hours without calls.")]
	public Task<UnloadResult> Unload(
		[Description("Absolute path of the .sln or .slnx file (default: this session's active solution)")] string? solution = null)
	{
		if (solution != null && !Path.IsPathFullyQualified(solution))
			return Task.FromResult(new UnloadResult { ErrorCode = ToolErrorCodes.InvalidArgument, ErrorMessage = $"The solution must be an absolute path: {solution}" });
		return daemon.UnloadAsync(solution);
	}
}
