using PlanForge.Prompts;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class ScoutContractTests
{
    [Fact]
    public void Claude_scout_prompt_contains_the_shared_and_vendor_contracts()
    {
        var prompt = Prompts().Load("claude", VendorRole.Scout);

        Assert.Contains("You are Scout, a read-only evidence gatherer.", prompt, StringComparison.Ordinal);
        Assert.Contains("Return the Scout report through the structured output tool.", prompt, StringComparison.Ordinal);
        Assert.Contains("Roslyn-first review", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Cursor_scout_structured_return_wording_is_appended_after_the_shared_contract()
    {
        var prompt = Prompts().Load("cursor", VendorRole.Scout);

        Assert.Contains("Return the Scout report as the JSON object only", prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf("Finish all foreground work before returning", StringComparison.Ordinal)
                    < prompt.IndexOf("Return the Scout report as the JSON object only", StringComparison.Ordinal));
    }

    [Fact]
    public void Codex_scout_loads_without_a_vendor_prompt_file()
    {
        var prompt = Prompts().Load("codex", VendorRole.Scout);

        Assert.Contains("Return exactly the six categories", prompt, StringComparison.Ordinal);
        Assert.Contains("Roslyn-first review", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("structured output tool", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Scout_contract_requires_bounded_sourced_evidence_without_worker_instructions()
    {
        var prompt = Prompts().Load("claude", VendorRole.Scout);

        Assert.Contains("Answer only the current bounded question.", prompt, StringComparison.Ordinal);
        Assert.Contains("Gather evidence rather than requirements, plans, judgments, or edits.", prompt,
                         StringComparison.Ordinal);
        Assert.Contains("without asking for per-call authorization", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not return raw", prompt, StringComparison.Ordinal);
        Assert.Contains("command output or secrets.", prompt, StringComparison.Ordinal);
        Assert.Contains("Make no instructions to the Critic or Builder.", prompt, StringComparison.Ordinal);
        Assert.Contains("Finish all foreground work before returning", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Scout_contract_requires_exactly_the_six_categories()
    {
        var prompt = Prompts().Load("cursor", VendorRole.Scout);

        Assert.Contains("Return exactly the six categories defined by the Scout output schema.", prompt,
                         StringComparison.Ordinal);
        Assert.Contains("Do not add, merge, or omit", prompt, StringComparison.Ordinal);
        Assert.Contains("Every item in every category must carry a non-empty `source`.", prompt,
                         StringComparison.Ordinal);
    }

    [Fact]
    public void Scout_contract_defines_dependents_and_pinned_behaviour()
    {
        var prompt = Prompts().Load("codex", VendorRole.Scout);

        Assert.Contains("dependentsAndPinnedBehaviour", prompt, StringComparison.Ordinal);
        Assert.Contains("naming the test method", prompt, StringComparison.Ordinal);
        Assert.Contains("\"none found\"", prompt, StringComparison.Ordinal);
        Assert.Contains("names what was searched", prompt, StringComparison.Ordinal);
        Assert.Contains("cite that location", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Scout_contract_requires_verifiable_names_and_complete_reporting()
    {
        var prompt = Prompts().Load("codex", VendorRole.Scout);

        Assert.Contains("must appear at the line or symbol you cite", prompt, StringComparison.Ordinal);
        Assert.Contains("do not drop a", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Roslyn_contract_takes_dependents_from_reference_and_caller_queries()
    {
        var prompts = Prompts();
        var scout = prompts.Load("codex", VendorRole.Scout);
        var critic = prompts.Load("codex", VendorRole.Critic);

        Assert.Contains("who uses it comes from the reference and caller queries", scout,
                        StringComparison.Ordinal);
        Assert.Contains("who uses it comes from the reference and caller queries", critic,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void Scout_contract_locator_grammar_matches_the_report_schema_description()
    {
        var prompt = Prompts().Load("claude", VendorRole.Scout);

        Assert.Contains("<path>:<positive-line>", prompt, StringComparison.Ordinal);
        Assert.Contains("<path>#<non-empty-symbol>", prompt, StringComparison.Ordinal);
        Assert.Contains("absolute URL beginning with `http://` or `https://`", prompt,
                         StringComparison.Ordinal);
        Assert.Contains("Cite repository evidence by path plus line or symbol, and external evidence by URL.",
                         prompt, StringComparison.Ordinal);
    }

    private static PromptLibrary Prompts() =>
        new(Path.Combine(AppContext.BaseDirectory, "prompts"));
}
