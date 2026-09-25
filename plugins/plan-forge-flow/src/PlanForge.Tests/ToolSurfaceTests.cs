using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlanForge.Acts;
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

    /// <summary>
    /// Including the one cancellation this assembly declares. Belonging to no assembly of ours used
    /// to be what kept cancellation out of the filter, and <see cref="TurnCutShortException"/>
    /// satisfies both halves of the old condition: ours, and a cancellation. Answering it as a tool
    /// error would send a result for a call the host has already stopped waiting for.
    /// </summary>
    [Fact]
    public async Task A_turn_the_host_cut_short_is_not_answered_as_a_tool_error_either()
    {
        await Assert.ThrowsAsync<TurnCutShortException>(
            async () => await Surfaced(new TurnCutShortException(["tracked.txt"], new OperationCanceledException())));
    }

    /// <summary>
    /// The two structured arguments of <c>forge.plan.confirm</c> are the first non-scalar tool
    /// inputs this server takes, and reflection-based serialization is off repo-wide: this proves
    /// the SDK can still describe them in the published schema, and that neither is required. The
    /// service the tool takes from the container is bound the way the server binds it, so it stays
    /// out of the schema here as it does on the wire.
    /// </summary>
    [Fact]
    public void Confirm_publishes_the_gate_environment_and_the_builder_roots_as_optional_arguments()
    {
        var services = new ServiceCollection().AddSingleton(SessionRoots.None).BuildServiceProvider();
        var tool = McpServerTool.Create(typeof(ForgeTools).GetMethod(nameof(ForgeTools.ConfirmPlan))!,
                                        options: new McpServerToolCreateOptions { Services = services, SerializerOptions = ToolArgumentJson.ArgumentOptions });

        var schema = tool.ProtocolTool.InputSchema;
        var properties = schema.GetProperty("properties");
        Assert.Equal(["workspaceRoot", "runId", "plan", "approved", "gateEnvironment", "builderRoots", "decisions"],
                     properties.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["workspaceRoot", "runId", "plan", "approved"],
                     schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.Contains("object", properties.GetProperty("gateEnvironment").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("array", properties.GetProperty("builderRoots").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_published_surface_has_eighteen_tools_including_both_scout_tools()
    {
        var tools = typeof(ForgeTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name)
            .ToList();

        Assert.Equal(18, tools.Count);
        Assert.Contains("forge.scout.select", tools);
        Assert.Contains("forge.scout.run", tools);
    }

    [Fact]
    public void Scout_run_requires_session_mode_and_has_only_its_four_arguments()
    {
        var schema = SchemaFor(nameof(ForgeTools.ScoutRun));
        var properties = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToList();

        Assert.Equal(["workspaceRoot", "runId", "question", "sessionMode"], properties);
        Assert.Equal(["workspaceRoot", "runId", "question", "sessionMode"],
                     schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public void Scout_selection_publishes_enabled_as_required_and_selection_fields_as_optional()
    {
        var schema = SchemaFor(nameof(ForgeTools.SelectScout));
        var properties = schema.GetProperty("properties");
        var required = schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()).ToList();

        foreach (var name in new[] { "workspaceRoot", "runId", "enabled", "vendor", "model", "effort" })
            Assert.True(properties.TryGetProperty(name, out _), $"forge.scout.select is missing {name}");
        foreach (var name in new[] { "workspaceRoot", "runId", "enabled" })
            Assert.Contains(name, required);
        foreach (var name in new[] { "vendor", "model", "effort" })
            Assert.DoesNotContain(name, required);
    }

    [Fact]
    public void Background_start_keeps_model_optional_but_publishes_question_and_session_mode()
    {
        var schema = SchemaFor(nameof(ForgeTools.StartWork));
        var properties = schema.GetProperty("properties");
        var required = schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()).ToList();

        foreach (var name in new[] { "act", "model", "question", "sessionMode" })
            Assert.True(properties.TryGetProperty(name, out _), $"forge.work.start is missing {name}");
        Assert.Contains("act", required);
        Assert.DoesNotContain("model", required);
        Assert.DoesNotContain("question", required);
        Assert.DoesNotContain("sessionMode", required);
    }

    /// <summary>
    /// Speed is a third axis beside model and effort (docs/adr/0023), so every tool that takes an
    /// effort takes an optional `fast` beside it, and none is left without one.
    /// </summary>
    [Fact]
    public void Every_tool_that_takes_an_effort_takes_an_optional_fast_beside_it()
    {
        var withEffort = typeof(ForgeTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null
                             && method.GetParameters().Any(parameter => parameter.Name == "effort"))
            .Select(method => method.Name)
            .ToList();

        Assert.Equal([nameof(ForgeTools.SelectScout), nameof(ForgeTools.ReviewPlan), nameof(ForgeTools.BuildNext),
                      nameof(ForgeTools.ReviewCode), nameof(ForgeTools.ReviewFix), nameof(ForgeTools.StartWork)],
                     withEffort);
        foreach (var name in withEffort)
        {
            var schema = SchemaFor(name);
            Assert.True(schema.GetProperty("properties").TryGetProperty("fast", out var fast), $"{name} is missing fast");
            Assert.Contains("boolean", fast.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("fast", schema.GetProperty("required").EnumerateArray().Select(entry => entry.GetString()));
        }
    }

    [Fact]
    public void Work_start_description_names_all_five_acts_and_status_names_scout_state()
    {
        var start = typeof(ForgeTools).GetMethod(nameof(ForgeTools.StartWork), BindingFlags.Public | BindingFlags.Static)!;
        var status = typeof(ForgeTools).GetMethod(nameof(ForgeTools.Status), BindingFlags.Public | BindingFlags.Static,
                                                  binder: null, types: [typeof(JobRegistry), typeof(SessionRoots), typeof(string), typeof(string), typeof(CancellationToken)], modifiers: null)!;
        var actDescription = start.GetParameters().Single(parameter => parameter.Name == "act")
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.Contains("plan.review", actDescription, StringComparison.Ordinal);
        Assert.Contains("build.next", actDescription, StringComparison.Ordinal);
        Assert.Contains("review.code", actDescription, StringComparison.Ordinal);
        Assert.Contains("review.fix", actDescription, StringComparison.Ordinal);
        Assert.Contains("scout", actDescription, StringComparison.Ordinal);
        Assert.Contains("run.scout", status.GetCustomAttribute<DescriptionAttribute>()!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Scout_prompt_bundle_and_skill_markers_are_shipped()
    {
        var prompts = PromptLibrary.Locate(null);
        foreach (var path in new[]
                 {
                     Path.Combine(prompts, "scout-contract.md"),
                     Path.Combine(prompts, "claude", "scout.md"),
                     Path.Combine(prompts, "cursor", "scout.md")
                 })
            Assert.True(File.Exists(path), $"missing Scout prompt asset: {path}");

        var skill = File.ReadAllText(Path.Combine(RepositoryRoot(), "skills", "forge", "SKILL.md"));
        Assert.Contains("multi-module", skill, StringComparison.Ordinal);
        Assert.Contains("forge.scout.run", skill, StringComparison.Ordinal);
        Assert.Contains("documents.scout", skill, StringComparison.Ordinal);
    }

    private static System.Text.Json.JsonElement SchemaFor(string methodName)
    {
        var services = new ServiceCollection()
            .AddSingleton(SessionRoots.None)
            .AddSingleton(new JobRegistry())
            .AddSingleton(new CatalogCache())
            .BuildServiceProvider();
        var method = typeof(ForgeTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name == methodName
                && candidate.GetCustomAttribute<McpServerToolAttribute>() is not null);
        var tool = McpServerTool.Create(method,
                                        options: new McpServerToolCreateOptions
                                        {
                                            Services = services,
                                            SerializerOptions = ToolArgumentJson.ArgumentOptions
                                        });
        return tool.ProtocolTool.InputSchema.Clone();
    }

    private static ValueTask<CallToolResult> Surfaced(Exception error) =>
        ToolErrors.Surfaced((_, _) => throw error)(null!, CancellationToken.None);

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "skills", "forge", "SKILL.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
