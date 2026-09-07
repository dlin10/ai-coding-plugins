using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

// ISSUE70: temporary probe MCP tool — remove in task 4.
[McpServerToolType]
public sealed class Issue70ProbeTool(RpcClient rpc)
{
	[McpServerTool(Name = "issue70_probe")]
	[Description("Temporary Issue70 experimental probe for the Issue70Exp hive. Operations: identity, generator_state, ensure_balanced, build, generated_documents, workspace_text.")]
	public Task<Issue70ProbeResult> Probe(
		[Description("Probe operation name")] string operation,
		[Description("Optional expected generated member full name for generated_documents")] string? expectedMemberFullName = null)
	{
		if (string.IsNullOrWhiteSpace(operation))
		{
			return Task.FromResult(new Issue70ProbeResult
			{
				ErrorCode = ToolErrorCodes.InvalidArgument,
				ErrorMessage = "operation must not be empty."
			});
		}

		return rpc.Issue70ProbeAsync(operation, expectedMemberFullName);
	}
}
