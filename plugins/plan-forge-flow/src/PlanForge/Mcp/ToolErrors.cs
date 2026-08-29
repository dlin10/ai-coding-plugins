using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PlanForge.Mcp;

/// <summary>
/// Puts the reason a tool failed in front of the orchestrator instead of only in the run log.
/// </summary>
/// <remarks>
/// <para>
/// The SDK replaces the message of any exception that is not an <c>McpException</c> with a generic
/// one, deliberately, so that a server does not leak whatever a stray exception happens to carry.
/// The cost was that a run died with "An error occurred invoking 'forge.plan.review'." and the
/// reason — a prompt file that was never deployed — reached nobody who could act on it. Every
/// exception declared in this assembly is written for exactly that reader, so those pass through
/// with their message; everything else keeps the SDK's generic answer.
/// </para>
/// <para>
/// A filter rather than a wrapper inside the tools, because <see cref="Run.RunDirectory.Open"/>
/// runs before the run's log exists and so before anything a tool could wrap: a bad
/// <c>workspaceRoot</c> or a lost <c>runId</c> fails earlier than every other failure and would
/// otherwise be the one class of error left mute.
/// </para>
/// </remarks>
internal static class ToolErrors
{
    private static readonly Assembly Own = typeof(ToolErrors).Assembly;

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Surfaced { get; } =
        next => async (request, ct) =>
        {
            try
            {
                return await next(request, ct);
            }
            // Cancellation is the host taking the call away rather than an answer to give it, and
            // it belongs to no assembly of ours, so it flows on untouched.
            catch (Exception error) when (IsOwn(error))
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = error.Message }]
                };
            }
        };

    internal static bool IsOwn(Exception error) => error.GetType().Assembly == Own;
}
