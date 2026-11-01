using System.ComponentModel;
using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlanForge.Acts;
using PlanForge.Diagnostics;
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
            WorkAct.ValidateArguments("build.next", new Selection("model", null), note: "framing");
            throw new InvalidOperationException("the validator accepted an argument the act does not take");
        })(null!, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("note", Text(result), StringComparison.Ordinal);
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
    /// The SDK binds a call's arguments before any code of ours runs, refuses one that lacks a
    /// required argument with an exception of its own, and then blanks it: a run's
    /// <c>forge.plan.confirm</c> without <c>plan</c> came back "An error occurred", and only the
    /// server log said why. This goes through the server's real dispatch because the failure lives
    /// in the SDK's binding, which no direct call to a tool method reaches.
    /// </summary>
    [Theory]
    [InlineData("forge.plan.confirm", """{"workspaceRoot": "C:\\work"}""", "forge.plan.confirm requires runId, approved")]
    [InlineData("forge.plan.write", """{"workspaceRoot": "C:\\work", "runId": "run"}""", "forge.plan.write requires planDraft")]
    [InlineData("forge.plan.confirm", """{"workspaceRoot": "C:\\work", "runId": "run", "approved": null}""", "forge.plan.confirm requires approved")]
    [InlineData("forge.status", """{"workspaceRoot": "C:\\work"}""", "forge.status requires runId")]
    public async Task A_call_without_a_required_argument_is_refused_naming_it(string tool, string arguments, string reason)
    {
        var result = await OnTheWireAsync(tool, arguments);

        Assert.True(result.IsError);
        Assert.Equal(reason, Text(result));
    }

    /// <summary>
    /// The SDK's own exception for these names the .NET type and not the argument, so the answer
    /// comes from the published schema, at whatever depth the value sits.
    /// </summary>
    [Theory]
    [InlineData("""{"approved": "yes"}""", "approved to be boolean, not string")]
    [InlineData("""{"approved": true, "decisions": {"decisionBatchId": 3, "decisions": []}}""", "decisions.decisionBatchId to be string or null, not number")]
    [InlineData("""{"approved": true, "gateEnvironment": {"CONN": 1}}""", "gateEnvironment.CONN to be string or null, not number")]
    [InlineData("""{"approved": true, "builderRoots": [1]}""", "builderRoots[0] to be string or null, not number")]
    public async Task An_argument_of_the_wrong_type_is_refused_naming_where_it_sits(string arguments, string reason)
    {
        var call = JsonNode.Parse(arguments)!.AsObject();
        call["workspaceRoot"] = @"C:\work";
        call["runId"] = "run";

        var result = await OnTheWireAsync("forge.plan.confirm", call.ToJsonString());

        Assert.True(result.IsError);
        Assert.Equal($"forge.plan.confirm requires {reason}", Text(result));
    }

    /// <summary>
    /// The schema publishes the edits as objects, but the binder takes a null among them and the
    /// tool refuses it itself, with the edit's index and the sizes it measures. Explaining only what
    /// the binder has already refused is what leaves that path alone: here the call reaches the tool,
    /// which is what fails on a run that does not exist.
    /// </summary>
    [Fact]
    public async Task A_call_the_binder_accepts_reaches_the_tool_whatever_the_schema_says()
    {
        var result = await OnTheWireAsync("forge.plan.patch",
                                          """{"workspaceRoot": "C:\\work", "runId": "run", "baseHash": "h", "edits": [null]}""");

        Assert.True(result.IsError);
        Assert.Equal("run run was not found", Text(result));
    }

    /// <summary>
    /// The calls run 5d B3c made after a compaction, with no plan: since docs/adr/0038 they are
    /// well-formed, and what answers them is the tool, about the run.
    /// </summary>
    [Theory]
    [InlineData("forge.plan.show", """{"workspaceRoot": "C:\\work", "runId": "run"}""")]
    [InlineData("forge.plan.confirm", """{"workspaceRoot": "C:\\work", "runId": "run", "approved": true}""")]
    public async Task A_confirmation_or_a_canvas_without_a_plan_reaches_the_tool(string tool, string arguments)
    {
        var result = await OnTheWireAsync(tool, arguments);

        Assert.True(result.IsError);
        Assert.Equal("run run was not found", Text(result));
    }

    /// <summary>
    /// The SDK's exception, logged as it escaped, was the only record of such a call, and the answer
    /// now stops it escaping. The run log keeps both: the reason given and the exception behind it.
    /// </summary>
    [Fact]
    public async Task An_unbindable_call_is_recorded_in_the_run_log()
    {
        var directory = Path.Combine(Path.GetTempPath(), "planforge-unbindable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "forge.log");
        try
        {
            using (RunLog.Use(new RunLog(path)))
                await OnTheWireAsync("forge.plan.write", """{"workspaceRoot": "C:\\work", "runId": "run"}""");

            var entry = File.ReadLines(path).Select(line => JsonDocument.Parse(line).RootElement)
                            .Single(line => line.GetProperty("event").GetString() == "tool.unbindable");
            var fields = entry.GetProperty("fields");
            Assert.Equal("forge.plan.write requires planDraft", fields.GetProperty("error").GetString());
            Assert.Contains("'planDraft'", fields.GetProperty("exception").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
        Assert.Equal(["workspaceRoot", "runId", "approved", "gateEnvironment", "builderRoots", "decisions", "fullGate", "plan", "planHash", "rebuildFrom"],
                     properties.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["workspaceRoot", "runId", "approved"],
                     schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.Contains("object", properties.GetProperty("gateEnvironment").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("array", properties.GetProperty("builderRoots").GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>The published surface includes the decision record and the bounded Evidence map beside all existing tools.</summary>
    [Fact]
    public void The_published_surface_has_twenty_two_tools_including_the_decision_record()
    {
        var tools = typeof(ForgeTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name)
            .ToList();

        Assert.Equal(22, tools.Count);
        Assert.Contains("forge.plan.outline", tools);
        Assert.Contains("forge.plan.patch", tools);
        Assert.Contains("forge.evidence.map", tools);
        Assert.Contains("forge.scout.select", tools);
        Assert.Contains("forge.scout.run", tools);
        Assert.Contains("forge.decision.record", tools);
    }

    [Fact]
    public void Decision_record_requires_the_question_the_answer_and_who_and_publishes_the_options_as_objects()
    {
        var schema = SchemaFor(nameof(ForgeTools.RecordDecision));

        Assert.Equal(["workspaceRoot", "runId", "phase", "question", "answer", "by"],
                     schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        var properties = schema.GetProperty("properties");
        Assert.Equal(["workspaceRoot", "runId", "phase", "question", "answer", "by", "options", "bearsOn", "supersedes", "scope"],
                     properties.EnumerateObject().Select(property => property.Name));
        var options = properties.GetProperty("options");
        Assert.Contains("array", options.GetProperty("type").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(["label", "recommended"],
                     options.GetProperty("items").GetProperty("properties").EnumerateObject().Select(property => property.Name));
    }

    /// <summary>
    /// The options are the first array of objects this tool takes and its result a new record, and
    /// reflection is off: a type missing from either source-generated context compiles and fails only
    /// here, on the wire.
    /// </summary>
    [Fact]
    public async Task A_decision_with_options_round_trips_on_the_wire()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var run = RunDirectory.Create(workspace, "wire");
            run.WriteState(new RunState(run.RunId, workspace, "Text", DateTimeOffset.Now, 0, 5));
            var call = new JsonObject
            {
                ["workspaceRoot"] = workspace, ["runId"] = run.RunId, ["phase"] = "interview",
                ["question"] = "Is generation by dependencies in scope?", ["answer"] = "Deferred", ["by"] = "user",
                ["options"] = new JsonArray(new JsonObject { ["label"] = "In scope" },
                                            new JsonObject { ["label"] = "Deferred", ["recommended"] = true })
            };

            var result = await OnTheWireAsync("forge.decision.record", call.ToJsonString());

            Assert.True(result.IsError is null or false, Text(result));
            var answer = JsonNode.Parse(Text(result))!;
            Assert.Equal("D-0001", answer["decisionId"]!.GetValue<string>());
            Assert.Equal(run.DecisionsPath, answer["documents"]!["decisions"]!["path"]!.GetValue<string>());
            Assert.True(Assert.Single(run.ReadDecisions()).Options![1].Recommended);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Patch_requires_the_base_hash_and_edits_and_publishes_the_edit_fields()
    {
        var schema = SchemaFor(nameof(ForgeTools.PatchPlan));
        Assert.Equal(["workspaceRoot", "runId", "baseHash", "edits"],
                     schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("baseHash").GetProperty("type").GetString());
        var edits = properties.GetProperty("edits");
        Assert.Equal("array", edits.GetProperty("type").GetString());
        var items = edits.GetProperty("items");
        Assert.Equal(["op", "target", "targetHash", "content", "find", "replace"],
                     items.GetProperty("properties").EnumerateObject().Select(property => property.Name));
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

    /// <summary>
    /// A job runs a Builder act or a Scout question, so its start publishes the Builder's and Scout's
    /// arguments, all optional, and none of the critic's — docs/adr/0043 and 0044.
    /// </summary>
    [Fact]
    public void Background_start_keeps_model_optional_and_publishes_only_builder_and_scout_arguments()
    {
        var schema = SchemaFor(nameof(ForgeTools.StartWork));
        var properties = schema.GetProperty("properties");
        var required = schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()).ToList();

        foreach (var name in new[] { "act", "model", "decisions", "fixAttemptId", "fixFindingIds", "note", "gate", "question", "sessionMode" })
            Assert.True(properties.TryGetProperty(name, out _), $"forge.work.start is missing {name}");
        foreach (var name in new[] { "planDraft", "revision", "deferred", "userGrantedRound", "excludePaths" })
            Assert.False(properties.TryGetProperty(name, out _), $"forge.work.start still publishes {name}");
        Assert.Equal(["act"], required.Except(["workspaceRoot", "runId"]));
    }

    /// <summary>
    /// The fallout labels are a structured argument of <c>forge.plan.review</c>, so the SDK has to
    /// describe them without reflection, and the tool must not require them: rounds 1 and 2, and a
    /// round after one that raised nothing, carry none. See docs/adr/0049.
    /// </summary>
    [Fact]
    public void Plan_review_publishes_the_fallout_labels_as_an_optional_argument_of_four_ID_lists()
    {
        var schema = SchemaFor(nameof(ForgeTools.ReviewPlan));
        var fallout = schema.GetProperty("properties").GetProperty("fallout").GetRawText();
        var required = schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()).ToList();

        foreach (var label in new[] { "own", "sibling", "sameClass", "older" })
            Assert.Contains($"\"{label}\"", fallout, StringComparison.Ordinal);
        Assert.Contains("array", fallout, StringComparison.Ordinal);
        Assert.DoesNotContain("fallout", required);
    }

    [Fact]
    public void Poll_publishes_an_optional_wait_and_hides_its_injected_services()
    {
        var schema = SchemaFor(nameof(ForgeTools.PollWork));
        var properties = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToList();

        Assert.Equal(["workspaceRoot", "runId", "jobId", "waitSeconds"], properties);
        Assert.Equal(["workspaceRoot", "runId", "jobId"],
                     schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
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
    public void Work_start_description_names_the_builder_acts_and_scout_and_status_names_scout_state()
    {
        var start = typeof(ForgeTools).GetMethod(nameof(ForgeTools.StartWork), BindingFlags.Public | BindingFlags.Static)!;
        var status = typeof(ForgeTools).GetMethod(nameof(ForgeTools.Status), BindingFlags.Public | BindingFlags.Static,
                                                  binder: null, types: [typeof(JobRegistry), typeof(SessionRoots), typeof(string), typeof(string), typeof(CancellationToken)], modifiers: null)!;
        var actDescription = start.GetParameters().Single(parameter => parameter.Name == "act")
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (var act in new[] { "build.next", "review.fix", "scout" })
            Assert.Contains(act, actDescription, StringComparison.Ordinal);
        foreach (var removed in new[] { "plan.review", "review.code" })
            Assert.DoesNotContain(removed, actDescription, StringComparison.Ordinal);
        Assert.Contains("run.scout", status.GetCustomAttribute<DescriptionAttribute>()!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Scout_prompt_bundle_and_skill_markers_are_shipped()
    {
        var prompts = PromptLibrary.Locate(null);
        foreach (var path in new[]
                 {
                     Path.Combine(prompts, "scout-contract.md"),
                     Path.Combine(prompts, "claude", "scout.md")
                 })
            Assert.True(File.Exists(path), $"missing Scout prompt asset: {path}");

        var skill = ForgeSkill.Text(RepositoryRoot());
        Assert.Contains("multi-module", skill, StringComparison.Ordinal);
        Assert.Contains("forge.scout.run", skill, StringComparison.Ordinal);
        Assert.Contains("documents.scout", skill, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one way to narrow a code-review round reaches it and is asked for only by the
    /// orchestrator: docs/adr/0029. How the window travels is the server's choice, so nothing is
    /// published for it: docs/adr/0034.
    /// </summary>
    [Fact]
    public void Code_review_publishes_its_optional_round_scope()
    {
        var schema = SchemaFor(nameof(ForgeTools.ReviewCode));
        var properties = schema.GetProperty("properties");
        var required = schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()).ToList();

        Assert.Contains("array", properties.GetProperty("excludePaths").GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("excludePaths", required);
        Assert.False(properties.TryGetProperty("untrackedByReference", out _));
    }

    [Fact]
    public void Fix_gate_and_full_gate_timing_are_optional_strings_only_on_their_public_routes()
    {
        foreach (var method in typeof(ForgeTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            var schema = SchemaFor(method.Name);
            var properties = schema.GetProperty("properties");
            var required = schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToList();
            var acceptsGate = method.Name is nameof(ForgeTools.ReviewFix) or nameof(ForgeTools.StartWork);
            var acceptsTiming = method.Name == nameof(ForgeTools.ConfirmPlan);
            Assert.Equal(acceptsGate, properties.TryGetProperty("gate", out var gate));
            Assert.Equal(acceptsTiming, properties.TryGetProperty("fullGate", out var timing));
            if (acceptsGate) Assert.Contains("string", gate.GetRawText(), StringComparison.Ordinal);
            if (acceptsTiming) Assert.Contains("string", timing.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("gate", required);
            Assert.DoesNotContain("fullGate", required);
        }
    }

    [Fact]
    public void Model_schemas_exclude_host_verification_fields_and_host_json_preserves_empty_pending_arrays()
    {
        foreach (var schema in new[] { Schemas.BuildResult.Json, Schemas.Critique.Json })
        {
            Assert.DoesNotContain("gateMode", schema, StringComparison.Ordinal);
            Assert.DoesNotContain("pendingFullGateFindingIds", schema, StringComparison.Ordinal);
        }

        var build = new BuildResult("done", [], new Verification("unavailable", "server-owned gate"), "implemented");
        var task = System.Text.Json.JsonSerializer.SerializeToElement(build, ContractJson.Default.BuildResult);
        Assert.False(task.TryGetProperty("gateMode", out _));
        Assert.False(task.TryGetProperty("pendingFullGateFindingIds", out _));
        var full = System.Text.Json.JsonSerializer.SerializeToElement(build with { GateMode = "full" }, ContractJson.Default.BuildResult);
        Assert.Equal("full", full.GetProperty("gateMode").GetString());
        Assert.False(full.TryGetProperty("pendingFullGateFindingIds", out _));
        foreach (var ids in new string[][] { [], ["F-0001"] })
        {
            var targeted = System.Text.Json.JsonSerializer.SerializeToElement(
                build with { GateMode = "targeted", PendingFullGateFindingIds = ids }, ContractJson.Default.BuildResult);
            Assert.Equal("targeted", targeted.GetProperty("gateMode").GetString());
            Assert.Equal(ids, targeted.GetProperty("pendingFullGateFindingIds").EnumerateArray().Select(id => id.GetString()));
            var final = System.Text.Json.JsonSerializer.SerializeToElement(
                new Critique("approve", [], "reviewed", PendingFullGateFindingIds: ids), ContractJson.Default.Critique);
            Assert.Equal(ids, final.GetProperty("pendingFullGateFindingIds").EnumerateArray().Select(id => id.GetString()));
        }
        var plan = System.Text.Json.JsonSerializer.SerializeToElement(new Critique("approve", [], "reviewed"), ContractJson.Default.Critique);
        Assert.False(plan.TryGetProperty("pendingFullGateFindingIds", out _));
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

    /// <summary>
    /// Calls one tool through an in-process server wired the way <c>Program.cs</c> wires the real
    /// one, and returns what reached the client.
    /// </summary>
    /// <param name="tool">The published tool name.</param>
    /// <param name="arguments">The call's arguments as a JSON object.</param>
    private static async Task<CallToolResult> OnTheWireAsync(string tool, string arguments)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(SessionRoots.None)
            .AddSingleton(new JobRegistry())
            .AddSingleton(new CatalogCache());
        services.AddMcpServer(o =>
                {
                    o.Filters.Request.CallToolFilters.Add(ToolErrors.Surfaced);
                    o.Filters.Request.CallToolFilters.Add(ToolResultText.Compact);
                })
                .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
                .WithTools<ForgeTools>(ToolArgumentJson.ArgumentOptions);
        await using var provider = services.BuildServiceProvider();
        using var stop = new CancellationTokenSource();
        var serving = provider.GetRequiredService<McpServer>().RunAsync(stop.Token);

        CallToolResult result;
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream())))
        {
            using var document = JsonDocument.Parse(arguments);
            result = await client.CallToolAsync(tool, document.RootElement.EnumerateObject()
                                                              .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));
        }

        await stop.CancelAsync();
        try { await serving; } catch (OperationCanceledException) { }
        return result;
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
