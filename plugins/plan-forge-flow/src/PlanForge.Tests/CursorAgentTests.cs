using PlanForge.Vendors;
using PlanForge.Vendors.Cursor;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

public sealed class CursorAgentTests
{
    private const string CriticPrompt =
        "You review implementation plans. Report every gap you find as a finding.";

    private const string PlanWithAHole =
        """
        # Plan: nightly export

        1. Read every row from the orders table.
        2. Write them to a CSV on the shared drive.
        3. Delete rows older than the retention window.
        """;

    private readonly ITestOutputHelper _output;

    public CursorAgentTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The raw list names every effort and speed variant on its own line; the interview wants
    /// families. Collapsed efforts must stay joinable — every one of them appended to its family
    /// base (except "default", which appends nothing) is an id the list actually contained.
    /// </summary>
    [Fact]
    public void Collapses_the_live_list_into_families_newest_first()
    {
        var models = CursorAgentVendor.ParseModels([
            "Available models",
            string.Empty,
            "auto - Auto (current, default)",
            "gpt-5.3-codex-low - Codex 5.3 Low",
            "gpt-5.3-codex-low-fast - Codex 5.3 Low Fast",
            "gpt-5.3-codex - Codex 5.3",
            "gpt-5.3-codex-fast - Codex 5.3 Fast",
            "gpt-5.3-codex-high - Codex 5.3 High",
            "gpt-5.3-codex-xhigh-fast - Codex 5.3 Extra High Fast",
            "claude-opus-4-8-thinking-high - Claude Opus 4.8 Thinking",
            "claude-opus-5-thinking-high - Claude Opus 5 1M Thinking",
            "gpt-5.6-sol-high - GPT-5.6 Sol 1M High",
            "gpt-5.6-sol-xhigh - GPT-5.6 Sol 1M Extra High",
            "not a model line"
        ]);

        // 5.6 above 5.3, opus-5 above opus-4-8, the versionless "auto" at the tail.
        Assert.Equal(["gpt-5.6-sol", "gpt-5.3-codex", "claude-opus-5-thinking", "claude-opus-4-8-thinking", "auto"],
                     models.Select(m => m.Id));

        var codex = models.Single(m => m.Id == "gpt-5.3-codex");
        Assert.Equal(["low", "low-fast", "default", "fast", "high", "xhigh-fast"], codex.Efforts);
        Assert.Equal("Codex 5.3", codex.DisplayName);
        Assert.Equal("default", codex.DefaultEffort);

        var sol = models.Single(m => m.Id == "gpt-5.6-sol");
        Assert.Equal(["high", "xhigh"], sol.Efforts);
        Assert.Null(sol.DisplayName);
        Assert.Null(sol.DefaultEffort);

        var auto = models.Single(m => m.Id == "auto");
        Assert.True(auto.IsDefault);
        Assert.Equal(["default"], auto.Efforts);
        Assert.False(codex.IsDefault);
    }

    /// <summary>"4-8" is the two-segment version 4.8 written with dashes, never the integer 48.</summary>
    [Fact]
    public void Reads_version_segments_out_of_model_ids()
    {
        Assert.Equal([4, 8], CursorAgentVendor.VersionSegments("claude-opus-4-8-thinking"));
        Assert.Equal([5, 3], CursorAgentVendor.VersionSegments("gpt-5.3-codex"));
        Assert.Equal([5], CursorAgentVendor.VersionSegments("claude-opus-5-thinking"));
        Assert.Equal([2, 5], CursorAgentVendor.VersionSegments("composer-2.5"));
        Assert.Empty(CursorAgentVendor.VersionSegments("auto"));
        Assert.Empty(CursorAgentVendor.VersionSegments("kimi-k3"));
    }

    /// <summary>
    /// The critic must not be able to touch the code it is judging. Codex gets that from its
    /// sandbox; here it rests entirely on plan mode, which was measured against cursor-agent on
    /// 2026-08-15: the same prompt writes the file without the flag and writes nothing with it.
    /// What is worth guarding in the suite is that the flag keeps reaching the right role.
    /// </summary>
    [Fact]
    public void Only_the_critic_is_started_in_plan_mode()
    {
        var selection = new Selection("auto", null);

        var critic = new CursorAgentSession(new RoleSpec(VendorRole.Critic, CriticPrompt), selection, null)
                     .BuildArguments();
        var builder = new CursorAgentSession(new RoleSpec(VendorRole.Builder, "implement"), selection, null)
                      .BuildArguments();

        Assert.Equal(["--mode", "plan"], critic.SkipWhile(a => a != "--mode").Take(2));
        Assert.DoesNotContain("--mode", builder);
        Assert.Contains("--force", builder);
    }

    /// <summary>
    /// Headless cursor-agent drops workspace .cursor/mcp.json servers unless they are approved at
    /// launch — "cursor-agent mcp enable" does not reach print mode (measured against
    /// 2026.08.11-e8db854). A role that loses the flag silently loses solution-local MCP servers
    /// such as roslyn-mcp.
    /// </summary>
    [Fact]
    public void Both_roles_approve_workspace_mcp_servers()
    {
        var selection = new Selection("auto", null);

        var critic = new CursorAgentSession(new RoleSpec(VendorRole.Critic, CriticPrompt), selection, null)
                     .BuildArguments();
        var builder = new CursorAgentSession(new RoleSpec(VendorRole.Builder, "implement"), selection, null)
                      .BuildArguments();

        Assert.Contains("--approve-mcps", critic);
        Assert.Contains("--approve-mcps", builder);
    }

    /// <summary>
    /// cursor-agent has no system-prompt flag, so the role instructions must travel at the head of
    /// the prompt itself; a session that drops them runs a critic that was never told it is one.
    /// </summary>
    [Fact]
    public void Role_instructions_lead_the_prompt()
    {
        var composed = Session(new Selection("auto", null)).WithRoleInstructions("Review this plan.");

        Assert.StartsWith(CriticPrompt, composed);
        Assert.EndsWith("Review this plan.", composed);
    }

    /// <summary>
    /// Cursor's live ids already carry the effort as a suffix, so the join must accept every shape
    /// the orchestrator sends: a bare model plus effort, a full id with its effort repeated, and a
    /// full id with no effort at all — the shape a Cursor-hosted orchestrator produces.
    /// </summary>
    [Fact]
    public void Joins_model_and_effort_the_way_cursor_spells_them()
    {
        Assert.Equal("gpt-5.6-sol-xhigh", Session(new Selection("gpt-5.6-sol", "xhigh")).ModelWithEffort());
        Assert.Equal("gpt-5.3-codex-high", Session(new Selection("gpt-5.3-codex-high", "high")).ModelWithEffort());
        Assert.Equal("gpt-5.6-sol-xhigh", Session(new Selection("gpt-5.6-sol-xhigh", null)).ModelWithEffort());
        // "default" names a family's bare variant in the catalogue, so it joins to nothing.
        Assert.Equal("gpt-5.3-codex", Session(new Selection("gpt-5.3-codex", "default")).ModelWithEffort());
        Assert.Equal("gpt-5.3-codex-high-fast", Session(new Selection("gpt-5.3-codex", "high-fast")).ModelWithEffort());
    }

    /// <summary>
    /// A failed run must read as a bad request to correct, not infrastructure to retry: the reply
    /// names the model, the effort, and — when the two were joined — the id actually sent.
    /// </summary>
    [Fact]
    public void A_run_failure_names_the_selection()
    {
        Assert.Equal("model \"gpt-5.6-sol\" with effort \"xhigh\", sent as \"gpt-5.6-sol-xhigh\"",
                     Session(new Selection("gpt-5.6-sol", "xhigh")).DescribeSelection());
        Assert.Equal("model \"gpt-5.6-sol-xhigh\" with no effort",
                     Session(new Selection("gpt-5.6-sol-xhigh", null)).DescribeSelection());
    }

    /// <summary>
    /// A model cursor-agent rejects must fail the act in seconds with the vendor's own message —
    /// naming the model and the available line-up — never crawl toward a timeout. Measured on
    /// 2026-08-18: exit 1 in about ten seconds, stderr "Cannot use this model: …", even with a
    /// 200 KB prompt, because cursor-agent drains stdin before validating the model.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_rejected_model_fails_fast_naming_the_model()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;

        await using var session = new CursorAgentSession(
            new RoleSpec(VendorRole.Critic, CriticPrompt), new Selection("totally-fake-model-zzz9", null), null);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<VendorException>(
            () => session.RunAsync(PlanWithAHole, Schemas.Critique, ct));
        watch.Stop();

        _output.WriteLine($"rejected in {watch.Elapsed.TotalSeconds:n1}s: {error.Message[..Math.Min(200, error.Message.Length)]}");

        Assert.Contains("totally-fake-model-zzz9", error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(1), $"rejection took {watch.Elapsed}");
    }

    private static CursorAgentSession Session(Selection selection) =>
        new(new RoleSpec(VendorRole.Critic, CriticPrompt), selection, null);

    /// <summary>
    /// The step-10 criterion: a vendor with no native schema still returns a valid object, and it
    /// does so within the retry budget.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Critic_returns_a_valid_critique_within_the_retry_budget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var vendor = new CursorAgentVendor();

        var readiness = await vendor.ProbeAsync(ct);
        Assert.True(readiness.Available, $"cursor-agent unavailable: {readiness.Detail}");
        Assert.NotEmpty(vendor.Catalog.Models);
        _output.WriteLine($"catalogue: {readiness.Detail}");

        await using var session = await vendor.StartAsync(
            new RoleSpec(VendorRole.Critic, CriticPrompt), new Selection("auto", null), null, ct);

        var attempts = 0;
        var counting = Task.Run(async () =>
        {
            await foreach (var e in session.Events.WithCancellation(ct))
                if (e.Kind is VendorEventKind.Started) attempts++;
        }, ct);

        var critique = await session.RunAsync(PlanWithAHole, Schemas.Critique, ct);
        await session.DisposeAsync();
        await counting;

        _output.WriteLine($"verdict={critique.Verdict} findings={critique.Findings.Count} attempts={attempts}");

        Assert.Contains(critique.Verdict, (string[])["approve", "revise"]);
        Assert.NotEmpty(critique.Summary);
        Assert.True(attempts <= 2, $"took {attempts} attempts");
    }
}
