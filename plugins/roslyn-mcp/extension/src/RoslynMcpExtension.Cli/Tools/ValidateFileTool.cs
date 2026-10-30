using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli.Tools;

[McpServerToolType]
public sealed class ValidateFileTool(IRoslynAnalysisRpc rpc)
{
	[McpServerTool(Name = "roslyn_validate_file", ReadOnly = true, Destructive = false, OpenWorld = false)]
	[Description("Validates a C# file and returns compiler errors, warnings, and optional analyzer diagnostics from the live IDE compilation. success means no compiler errors; requestSucceeded means the request executed. Also returns sourceGeneratedDocumentCount: zero means a completed observation found no source-generated documents in the file's project, while null or absent means the observation failed or exceeded its 10-second budget. That count reports what was observed, not whether generator output is up to date.")]
	public Task<ValidateFileResult> ValidateFile(
		[Description("Absolute path to the C# file to validate")] string filePath,
		[Description("Include warnings in output (default: true)")] bool includeWarnings = true,
		[Description("Run code analyzers in addition to compiler diagnostics (default: false)")] bool runAnalyzers = false)
		=> rpc.ValidateFileAsync(filePath, includeWarnings, runAnalyzers);
}
