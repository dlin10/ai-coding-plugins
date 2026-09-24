using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

[McpServerToolType]
public sealed class GetDiagnosticsTool(RpcClient rpc)
{
	[McpServerTool(Name = "roslyn_get_diagnostics")]
	[Description("Returns compiler errors and warnings across projects from the live IDE compilation. Use it after an edit: a changed signature breaks callers in other files and projects, which roslyn_validate_file never looks at. With filePaths it checks the projects holding those files plus every project that depends on them; with projectName, that project only; with neither, the whole solution. Projects are checked in dependency order under a budget of about 45 seconds: complete is false when the budget ran out, and uncheckedProjects names every project it did not check, so no errors from them means nothing. Errors always come before warnings, and errorCount and warningCount count everything found even when the list is truncated. Each diagnostic names its projectName. Diagnostics about source-generated members can be stale after a branch switch; roslyn_validate_file explains how to tell.")]
	public Task<DiagnosticsResult> GetDiagnostics(
		[Description("Absolute paths of the C# files you changed")] string[]? filePaths = null,
		[Description("Check only this project instead of filePaths; a multi-targeted project's name without the framework covers every target")] string? projectName = null,
		[Description("Include warnings (default: true)")] bool includeWarnings = true,
		[Description("Also run the analyzers the projects reference, such as NuGet analyzer packages (default: false)")] bool runAnalyzers = false,
		[Description("Maximum number of diagnostics to return (default: 100, maximum: 1000)")] int maxResults = 100)
		=> rpc.GetDiagnosticsAsync(filePaths, projectName, includeWarnings, runAnalyzers, ToolGuards.ClampMaxResults(maxResults, 1000));
}
