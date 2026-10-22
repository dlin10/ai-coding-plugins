using System.Text.Json.Nodes;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Run;
using PlanForge.Vendors;
using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// A Fast request nothing has confirmed is refused before any Worker starts; see docs/adr/0023.
/// Model and effort stay advisory — only the speed, which fails quietly and costs money either way,
/// is checked against the Catalogue.
/// </summary>
public sealed class FastTierTests : IDisposable
{
    private static readonly VendorModel Astra =
        new("gpt-6-astra", ["low", "high"], "GPT-6 Astra") { FastEfforts = ["low", "high"], FastHint = "2x speed" };

    private static readonly VendorModel Mini = new("gpt-5.4-mini", ["low"]);

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "planforge-fast-" + Guid.NewGuid().ToString("N"));

    public FastTierTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_listed_model_confirms_fast_at_an_effort_it_offers_it_at()
    {
        var catalog = new VendorCatalog([Astra, Mini], CatalogSource.Live);
        IVendor codex = new CodexCliVendor();

        Assert.Null(codex.RefuseFast(new Selection("gpt-6-astra", "high", Fast: true), catalog));
        Assert.Null(codex.RefuseFast(new Selection("gpt-6-astra", null, Fast: true), catalog));
        Assert.Contains("no Fast tier", codex.RefuseFast(new Selection("gpt-5.4-mini", "low", Fast: true), catalog));
        Assert.Contains("\"xhigh\"", codex.RefuseFast(new Selection("gpt-6-astra", "xhigh", Fast: true), catalog));
        Assert.Contains("does not list", codex.RefuseFast(new Selection("gpt-7", null, Fast: true), catalog));
    }

    /// <summary>
    /// Claude's catalogue lists aliases and names the model each resolved to; either spelling is the
    /// same model. A model the account will not serve Fast is refused with the account's reason.
    /// </summary>
    [Fact]
    public void A_claude_model_is_found_by_alias_or_resolved_id_and_the_account_refusal_is_named()
    {
        var opus = new VendorModel("opus", ["low", "high"], "claude-opus-5-5") { FastEfforts = ["low", "high"] };
        var refused = opus with { FastUnavailable = "extra_usage_disabled" };
        IVendor claude = new PlanForge.Vendors.Claude.ClaudeCliVendor();

        Assert.Null(claude.RefuseFast(new Selection("opus", "high", Fast: true), new VendorCatalog([opus], CatalogSource.Resolved)));
        Assert.Null(claude.RefuseFast(new Selection("claude-opus-5-5", null, Fast: true), new VendorCatalog([opus], CatalogSource.Resolved)));
        Assert.Contains("extra_usage_disabled",
                        claude.RefuseFast(new Selection("opus", null, Fast: true), new VendorCatalog([refused], CatalogSource.Resolved)));
    }

    [Fact]
    public async Task A_background_act_asking_for_fast_the_catalogue_does_not_confirm_never_starts()
    {
        var run = NewRun("refused");
        var vendor = new RecordingVendor("codex") { Catalog = new VendorCatalog([Mini], CatalogSource.Live) };
        var registry = new JobRegistry();

        var refusal = await Assert.ThrowsAsync<ArgumentRejectedException>(() => StartBuild(registry, run, vendor, "gpt-5.4-mini"));

        Assert.Contains("gpt-5.4-mini", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(vendor.Sessions);
        Assert.Null(registry.Get(run.Path));
    }

    [Fact]
    public async Task A_confirmed_fast_request_reaches_the_worker()
    {
        var run = NewRun("confirmed");
        var vendor = new RecordingVendor("codex") { Catalog = new VendorCatalog([Astra], CatalogSource.Live) };
        vendor.Enqueue(Built());
        var registry = new JobRegistry();

        var start = JsonNode.Parse(await StartBuild(registry, run, vendor, "gpt-6-astra"))!;
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, start["jobId"]!.GetValue<string>(),
                                  CancellationToken.None);

        Assert.True(vendor.Sessions.Single().Selection.Fast);
    }

    /// <summary>
    /// A Fast turn served at standard speed for part of it still counts. The result the orchestrator
    /// reads and the flow log the user reads both say so.
    /// </summary>
    [Fact]
    public async Task A_fast_turn_served_partly_at_standard_speed_warns_in_the_result_and_the_flow_log()
    {
        var run = NewRun("fell-back");
        var vendor = new RecordingVendor("codex") { Catalog = new VendorCatalog([Astra], CatalogSource.Live) };
        vendor.Enqueue(Built(),
                       speedWarning: "claude fell back to standard speed during the turn (fast_mode_state cooldown)");
        var registry = new JobRegistry();

        var jobId = JsonNode.Parse(await StartBuild(registry, run, vendor, "gpt-6-astra"))!["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);
        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
                                                              CancellationToken.None))!;

        var result = fetch["result"]!;
        Assert.Equal("done", result["result"]!["status"]!.GetValue<string>());
        Assert.Contains("cooldown", result["speedWarning"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("fast_mode_state cooldown", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    /// <summary>The same refusal on a direct act call, before the vendor process could start.</summary>
    [Fact]
    public async Task A_direct_build_asking_for_fast_on_a_model_without_it_is_refused()
    {
        var run = NewRun("direct");
        var cache = new CatalogCache((_, _) => new RecordingVendor("codex") { Catalog = new VendorCatalog([Mini], CatalogSource.Live) });

        var refusal = await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.BuildNext(
            cache, SessionRoots.None, _workspace, run.RunId, "gpt-5.4-mini", CancellationToken.None, vendor: "codex", fast: true));

        Assert.Contains("no Fast tier", refusal.Message, StringComparison.Ordinal);
    }

    private Task<string> StartBuild(JobRegistry registry, RunDirectory run, RecordingVendor vendor, string model) =>
        ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId, "build.next", model, null, "codex",
                             CancellationToken.None, _ => vendor, fast: true);

    private static BuildResult Built() => new("done", [], new Verification("passed", "condition checked"), "built");

    /// <summary>An approved one-task plan whose gate is a condition, so a build runs no command.</summary>
    /// <param name="runId">The Run to create.</param>
    private RunDirectory NewRun(string runId)
    {
        var run = RunDirectory.Create(_workspace, runId);
        run.WritePlan("## Approach\n1. Task.\n   **Gate:** condition checked\n");
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5, Approved: true));
        return run;
    }
}
