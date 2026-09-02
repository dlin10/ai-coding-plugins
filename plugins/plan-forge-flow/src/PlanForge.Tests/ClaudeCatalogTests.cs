using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The probe's parsing and merging, on the lines the CLI actually emits. Everything here is a pure
/// function for the same reason cursor's parser is: the process side is one integration test, and
/// the decisions worth pinning down are all in the text.
/// </summary>
public sealed class ClaudeCatalogTests
{
    private const string InitLine =
        """
        {"type":"system","subtype":"init","cwd":"C:\\work","session_id":"abc","tools":[],"model":"claude-fable-5","permissionMode":"auto"}
        """;

    [Fact]
    public void Reads_the_resolved_model_out_of_the_init_line()
    {
        Assert.Equal("claude-fable-5", ClaudeCliVendor.ReadInitModel(InitLine));
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"hook_started","hook_name":"SessionStart:startup"}""")]
    [InlineData("""{"type":"assistant","message":{"model":"claude-opus-5","content":[]}}""")]
    [InlineData("not json at all")]
    [InlineData("\"a bare string that parses\"")]
    public void Ignores_every_line_that_is_not_the_init_event(string line)
    {
        Assert.Null(ClaudeCliVendor.ReadInitModel(line));
    }

    /// <summary>
    /// An alias the CLI does not know is echoed back rather than rejected, and the failure that
    /// follows costs ~40s. Treating the echo as a resolution would put an unusable model in front
    /// of the user, which is the false confidence this whole probe exists to remove.
    /// </summary>
    [Fact]
    public void An_alias_echoed_back_unchanged_did_not_resolve()
    {
        Assert.Null(ClaudeCliVendor.ResolvedId("nosuchmodel", "nosuchmodel"));
        Assert.Null(ClaudeCliVendor.ResolvedId("fable", null));
        Assert.Equal("claude-fable-5", ClaudeCliVendor.ResolvedId("fable", "claude-fable-5"));
    }

    [Fact]
    public void Reads_the_families_out_of_the_structured_answer()
    {
        var line =
            """
            {"type":"assistant","message":{"model":"claude-opus-5","content":[{"type":"tool_use","id":"toolu_1","name":"StructuredOutput","input":{"families":["fable","opus","sonnet","haiku"]}}]}}
            """;

        Assert.Equal(["fable", "opus", "sonnet", "haiku"], ClaudeCliVendor.ReadFamilies(line));
    }

    [Fact]
    public void Ignores_tool_calls_that_are_not_the_structured_answer()
    {
        var line =
            """
            {"type":"assistant","message":{"model":"claude-opus-5","content":[{"type":"text","text":"fable, opus"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}}
            """;

        Assert.Null(ClaudeCliVendor.ReadFamilies(line));
    }

    /// <summary>
    /// The answer is text from a model on its way to a `--model` argument. Only the shape is
    /// trusted, and only what the repo does not already remember is added.
    /// </summary>
    [Fact]
    public void Adds_only_well_formed_families_the_repo_does_not_remember()
    {
        var extra = ClaudeCliVendor.MergeFamilies(
            ["nova", "Opus", " sonnet ", "claude-fable-5", "rm -rf /", "--dangerously-skip-permissions", string.Empty, "nova"],
            ClaudeCliVendor.RememberedAliases);

        // "claude-fable-5" is well formed but is an id, not a family — it survives the shape check
        // and is caught instead by the resolve wave, which sees it echoed back.
        Assert.Equal(["nova", "claude-fable-5"], extra);
    }

    [Fact]
    public void Adds_nothing_when_discovery_reported_nothing()
    {
        Assert.Empty(ClaudeCliVendor.MergeFamilies(null, ClaudeCliVendor.RememberedAliases));
    }

    /// <summary>
    /// Newest first by the resolved id, which is the first time claude's catalogue can honour the
    /// order the skill promises. A tie keeps the remembered order, so `fable` stays ahead of `opus`.
    /// </summary>
    [Fact]
    public void Orders_models_by_the_version_of_the_resolved_id()
    {
        var models = ClaudeCliVendor.BuildModels(
            [
                ("fable", "claude-fable-5"),
                ("opus", "claude-opus-5"),
                ("sonnet", "claude-sonnet-5"),
                ("haiku", "claude-haiku-4-5-20251001"),
                ("legacy", "claude-legacy")
            ],
            defaultModel: null);

        Assert.Equal(["fable", "opus", "sonnet", "haiku", "legacy"], models.Select(model => model.Id));
        Assert.Equal("claude-haiku-4-5-20251001", models[3].DisplayName);
    }

    /// <summary>
    /// The CLI names its own default with a context-window suffix — measured as `claude-opus-5[1m]`
    /// on 2026-09-02 — which is a variant of the same model, not a different one.
    /// </summary>
    [Fact]
    public void Marks_the_default_through_the_context_window_suffix()
    {
        var models = ClaudeCliVendor.BuildModels(
            [("fable", "claude-fable-5"), ("opus", "claude-opus-5")],
            defaultModel: "claude-opus-5[1m]");

        Assert.Equal(["opus"], models.Where(model => model.IsDefault).Select(model => model.Id));
    }

    [Fact]
    public void Marks_nothing_when_the_default_is_outside_the_catalogue()
    {
        var models = ClaudeCliVendor.BuildModels([("fable", "claude-fable-5")], defaultModel: "claude-unknown-9");

        Assert.All(models, model => Assert.False(model.IsDefault));
    }

    [Fact]
    public void Every_model_carries_the_five_effort_levels()
    {
        var models = ClaudeCliVendor.BuildModels([("fable", "claude-fable-5")], defaultModel: null);

        Assert.Equal(["low", "medium", "high", "xhigh", "max"], models.Single().Efforts);
    }

    [Fact]
    public void Claudes_catalogue_is_resolved_rather_than_live()
    {
        Assert.Equal(CatalogSource.Resolved, new ClaudeCliVendor().Catalog.Source);
    }

    /// <summary>
    /// The one test that runs the real probe: it starts `claude` processes and spends the
    /// discovery call's tokens, so it is traited for filtering with --filter Category!=Integration.
    /// Asserts the property the whole design turns on — an alias comes back as something other
    /// than itself — rather than any particular model, which is exactly what must not be pinned.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_real_probe_resolves_its_aliases_into_model_ids()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var vendor = new ClaudeCliVendor();

        var readiness = await vendor.ProbeAsync(timeout.Token);
        Assert.True(readiness.Available, $"claude CLI unavailable: {readiness.Detail}");

        var models = vendor.Catalog.Models;
        Assert.NotEmpty(models);
        Assert.Equal(CatalogSource.Resolved, vendor.Catalog.Source);
        Assert.All(models, model =>
        {
            Assert.NotNull(model.DisplayName);
            Assert.NotEqual(model.Id, model.DisplayName);
        });

        Assert.Contains(models, model => model.Id == "sonnet");
    }
}
