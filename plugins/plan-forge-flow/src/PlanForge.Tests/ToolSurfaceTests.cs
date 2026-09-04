using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Two properties of the tool surface that a caller can only discover by being broken by them: what
/// the published schema demands, and whether a failure says why.
/// </summary>
public sealed class ToolSurfaceTests
{
    /// <summary>
    /// A nullable parameter without a default is published as <c>required</c> with a nullable type,
    /// which reads as "send it, and null is fine" and is answered by the server refusing the call
    /// when the key is absent. One host then proved the other half uncallable too, dropping the
    /// `null` literal while serializing and sending `"revision": ,` — so the contract had no
    /// encoding left that worked. The rule this pins is the one `forge.work.start` already followed.
    /// </summary>
    [Fact]
    public void Every_nullable_tool_parameter_is_declared_optional()
    {
        var nullability = new NullabilityInfoContext();

        var nullable = (from method in typeof(ForgeTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        where method.GetCustomAttribute<McpServerToolAttribute>() is not null
                        from parameter in method.GetParameters()
                        where nullability.Create(parameter).ReadState is NullabilityState.Nullable
                        select (Name: $"{method.Name}.{parameter.Name}", parameter.HasDefaultValue)).ToList();

        // Without this the test would pass by finding nothing at all, which is what it would do if
        // the nullability metadata ever stopped being emitted.
        Assert.NotEmpty(nullable);
        Assert.Empty(nullable.Where(parameter => !parameter.HasDefaultValue).Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task A_failure_of_ours_reaches_the_caller_with_its_reason()
    {
        var path = Path.Combine("cache", "prompts", "codex", "critic.md");

        var result = await Surfaced(new PromptNotFoundException(path));

        Assert.True(result.IsError);
        Assert.Contains(path, Text(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// The one thrown before the run's log exists, and so before anything inside a tool could have
    /// wrapped it. It is why this is a filter rather than a try/catch around the acts.
    /// </summary>
    [Fact]
    public async Task A_failure_before_the_run_is_open_reaches_the_caller_too()
    {
        var result = await Surfaced(new WorkspaceNotRootedException("relative/path"));

        Assert.True(result.IsError);
        Assert.Contains("relative/path", Text(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// The rejection issue #59 measured blank on the wire: written for the orchestrator, thrown as
    /// a framework type, and so blanked by the SDK. It runs the real validator inside the filter
    /// rather than handing it a ready-made exception, because the in-process tests already pinned
    /// this wording and passed while the wire said nothing.
    /// </summary>
    [Fact]
    public async Task An_argument_rejection_reaches_the_caller_naming_the_argument_and_the_act()
    {
        var result = await ToolErrors.Surfaced((_, _) =>
        {
            WorkAct.ValidateArguments("build.next", null, new Selection("model", null), null, null, null, true);
            throw new InvalidOperationException("the validator accepted an argument the act does not take");
        })(null!, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("userGrantedRound", Text(result), StringComparison.Ordinal);
        Assert.Contains("build.next", Text(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// The SDK blanks foreign exception messages so a server cannot leak what a stray exception
    /// happens to carry, and nothing here was written for a model to read. That default stands.
    /// </summary>
    [Fact]
    public async Task A_failure_that_is_not_ours_keeps_the_generic_answer()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Surfaced(new InvalidOperationException("whatever this happened to hold")));
    }

    /// <summary>
    /// The host taking the call away is not an answer to give it.
    /// </summary>
    [Fact]
    public async Task Cancellation_is_not_answered_as_a_tool_error()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await Surfaced(new OperationCanceledException()));
    }

    private static ValueTask<CallToolResult> Surfaced(Exception error) =>
        ToolErrors.Surfaced((_, _) => throw error)(null!, CancellationToken.None);

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
