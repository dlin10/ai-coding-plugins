using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

public sealed class DecisionLedgerSkillTests
{
    [Fact]
    public void The_user_may_be_the_decision_maker_for_every_action()
    {
        Contains("`user` is valid for every action", "Approving the plan as a whole", "settles no finding", Skill());
    }

    [Fact]
    public void Plan_review_and_confirm_share_the_plan_decision_matrix()
    {
        Contains("Apply plan decisions through the next `forge.plan.review`", "through `forge.plan.confirm(approved: true)`", Skill());
        Contains("Both accept the same complete decision shape", "duplicate closures", "reopening answers", Skill());
    }

    [Fact]
    public void Plan_closures_use_next_review_or_final_confirm()
    {
        Contains("`addressedByRevision`", "next `forge.plan.review`", "final revised plan", Skill());
    }

    [Fact]
    public void Refused_confirmation_carries_no_decisions_and_mutates_no_ledger()
    {
        Contains("`approved: false` with no batch or decisions", "leaves the ledger unchanged", Skill());
    }

    [Fact]
    public void Code_decisions_and_host_closure_use_review_fix()
    {
        Contains("Apply code decisions", "host-verified closures", "through `forge.review.fix`", "`forge.review.code` never accepts decisions", Skill());
    }

    [Fact]
    public void Decision_batch_id_is_one_per_logical_set_and_exact_retry()
    {
        Contains("Every non-empty logical decision set gets one new `decisionBatchId`", "exact byte-for-byte decisions", Skill());
        Contains("identical `decisionBatchId` retry is a no-op", "saved result", Skill());
    }

    [Fact]
    public void Decision_conflict_uses_saved_result_and_new_key_only_for_delta()
    {
        Contains("conflicting payload", "also returns the saved result", "new key only for a legal delta", Skill());
    }

    [Fact]
    public void Fix_attempt_is_separate_and_bound_to_exact_ids()
    {
        Contains("Fix execution is separate from decisions", "one `fixAttemptId`", "exact sorted `fixFindingIds`", Skill());
    }

    [Fact]
    public void Fix_retry_reuses_retained_attempt_and_terminal_result()
    {
        Contains("cut-short turn", "failed gate", "same attempt ID and exact ID set", Skill());
        Contains("completed attempt returns its saved terminal result", Skill());
    }

    [Fact]
    public void Phase_projection_is_explicit()
    {
        Contains("Plan review receives entries whose `activePhase` is `plan_review`", Skill());
        Contains("Code review receives settled plan decisions", "`activePhase` is `code_review`", Skill());
    }

    [Fact]
    public void Cross_phase_reopening_preserves_origin_and_moves_active_phase()
    {
        Contains("preserves `origin=plan_review`", "changes `activePhase` to `code_review`", Skill());
    }

    [Fact]
    public void Critic_only_proposes_reopening()
    {
        var prompts = Prompts();
        foreach (var prompt in new[] { prompts.LoadPlanReviewCritic("codex"), prompts.LoadCodeReviewCritic("codex") })
            Contains("may only propose reopening", "never accept a reopening", prompt);
    }

    [Fact]
    public void Declined_reopening_is_audited_without_ledger_mutation()
    {
        Contains("declining changes no ledger state", "Flow audit", Skill());
    }

    [Fact]
    public void Semantic_duplicates_are_closed_by_the_orchestrator()
    {
        Contains("compare every new Critic finding semantically", "Close a redundant new ID with `duplicateOf`", Skill());
    }

    [Fact]
    public void Invalid_critic_response_retries_the_same_applied_batch()
    {
        Contains("Critic response is structurally or semantically invalid", "round does not count", "retry the round with the same batch", Skill());
    }

    [Fact]
    public void Builder_receives_verbatim_findings_for_exact_fix_ids()
    {
        Contains("Builder receives the ledger's verbatim findings for those IDs only", Skill());
    }

    [Fact]
    public void Cut_short_retry_and_gate_retry_are_linked_to_the_attempt()
    {
        Contains("same `fixAttemptId` and exact `fixFindingIds`", "links the retry to the eventual automatic gate closure", Skill());
    }

    [Fact]
    public void Flow_documents_and_chat_surface_decisions_and_retries()
    {
        Contains("Surface each critique, decision, reopening proposal", "cut-short retry", "concise chat narration", Skill());
        Contains("documents.flowLog", "documents.plan", Skill());
    }

    [Fact]
    public void Plan_prompt_names_plan_phase_projection_and_assessment_coverage()
    {
        var prompt = Prompts().LoadPlanReviewCritic("codex");
        Contains("`activePhase` is `plan_review`", "Assess every displayed unresolved ID exactly once", prompt);
    }

    [Fact]
    public void Code_prompt_names_settled_plan_and_active_code_projection()
    {
        var prompt = Prompts().LoadCodeReviewCritic("codex");
        Contains("settled plan-review decisions", "active code-review entries", "`activePhase=code_review`", prompt);
    }

    [Fact]
    public void Critic_prompts_reject_semantic_duplicate_findings()
    {
        var prompts = Prompts();
        foreach (var prompt in new[] { prompts.LoadPlanReviewCritic("codex"), prompts.LoadCodeReviewCritic("codex") })
            Contains("semantically duplicate new finding", prompt);
    }

    [Fact]
    public void Critic_contract_keeps_exhaustive_passes_and_structural_boundaries()
    {
        var critic = Read("prompts", "critic-contract.md").Replace("\r\n", "\n", StringComparison.Ordinal);
        var scope = Read("prompts", "scope-contract.md").Replace("\r\n", "\n", StringComparison.Ordinal);
        var requirements = Read("prompts", "requirements-contract.md").Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("One pass, one kind of gap, every place it occurs.", critic, StringComparison.Ordinal);
        Assert.Contains("never judge on the strength of a command that is still running.\n\nThe projection's IDs", critic,
                        StringComparison.Ordinal);
        Assert.Contains("Use these passes:\n\n- File by file", scope, StringComparison.Ordinal);
        Assert.Contains("- Task by task over the approved plan", scope, StringComparison.Ordinal);
        Assert.Contains("- Gate by gate", scope, StringComparison.Ordinal);
        Assert.Contains("Judge the complete brief:\n\n- The requirements are yours to judge", requirements,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void Agents_names_decision_ledger_as_source_of_truth_and_not_review_log_input()
    {
        var agents = Read("AGENTS.md");
        Contains("`decision-ledger.json` is the sole authoritative finding state", "immutable finding `origin`", "mutable `activePhase`", agents);
        DoesNotContain("review-log.md", agents);
        DoesNotContain("fed the review log", agents);
    }

    [Fact]
    public void Readme_documents_decision_and_fix_retry_split()
    {
        var readme = Read("README.md");
        Contains("One `decisionBatchId` names one logical decision set", "one `fixAttemptId` names one exact sorted ID set", readme);
        DoesNotContain("review-log.md", readme);
    }

    [Fact]
    public void Generated_html_contains_decision_ledger_and_no_review_log_input()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "plan-forge-flow-cli.html");
        if (!File.Exists(path)) return;

        var guide = File.ReadAllText(path);
        Contains("decision-ledger.json", "decisionBatchId", "fixAttemptId", "origin=plan_review", "Flow", "Worker input", guide);
        DoesNotContain("review-log.md", guide);
        DoesNotContain("журнал ревью", guide);
    }

    [Fact]
    public void Context_uses_phase_projection_origin_active_phase_and_idempotent_batch()
    {
        var context = Read("CONTEXT.md");
        Contains("immutable `origin`", "mutable `activePhase`", "same digest is a no-op", "returns the saved result", context);
        DoesNotContain("every unresolved finding and every deferred or rejected finding", context);
    }

    [Fact]
    public void Adr_0022_uses_phase_projection_and_split_retry_keys()
    {
        var adr = Read("docs", "adr", "0022-feed-critics-a-canonical-decision-ledger.md");
        Contains("immutable `origin`", "mutable `activePhase`", "`decisionBatchId`", "`fixAttemptId`", adr);
        DoesNotContain("projection containing all current ledger entries", adr);
    }

    [Fact]
    public void Current_contracts_have_no_transcript_as_critic_input_instruction()
    {
        var contracts = new List<string> { Skill(), Read("AGENTS.md"), Read("prompts", "critic-contract.md") };
        var guidePath = Path.Combine(RepositoryRoot(), "docs", "plan-forge-flow-cli.html");
        if (File.Exists(guidePath)) contracts.Add(File.ReadAllText(guidePath));

        foreach (var text in contracts)
        {
            DoesNotContain("fed the review log", text);
            DoesNotContain("given the log of earlier rounds", text);
            DoesNotContain("review log as input", text);
        }
    }

    [Fact]
    public void Tool_descriptions_publish_the_decision_and_fix_contract()
    {
        Contains("same typed plan decisions", Description(nameof(ForgeTools.ConfirmPlan)));
        Contains("same attempt and exact ID set", Description(nameof(ForgeTools.ReviewFix)));
        Contains("compact source-of-truth summary", Description(nameof(ForgeTools.Status)));
    }

    [Fact]
    public void Tool_schemas_publish_typed_decisions_on_symmetric_paths()
    {
        foreach (var method in new[] { nameof(ForgeTools.ReviewPlan), nameof(ForgeTools.ConfirmPlan), nameof(ForgeTools.ReviewFix), nameof(ForgeTools.StartWork) })
            Assert.True(SchemaFor(method).GetProperty("properties").TryGetProperty("decisions", out _), $"{method} is missing decisions");

        var decisionSchema = SchemaFor(nameof(ForgeTools.ReviewPlan)).GetRawText();
        foreach (var property in new[] { "decisionBatchId", "decisions", "findingId", "action", "by", "reason", "evidence", "duplicateOf" })
            Assert.Contains(property, decisionSchema, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_fix_schema_has_fix_attempt_fields_and_no_transcript_arguments()
    {
        var properties = SchemaFor(nameof(ForgeTools.ReviewFix)).GetProperty("properties");
        Assert.True(properties.TryGetProperty("fixAttemptId", out _));
        Assert.True(properties.TryGetProperty("fixFindingIds", out _));
        Assert.False(properties.TryGetProperty("findings", out _));
        Assert.False(properties.TryGetProperty("deferred", out _));

        var background = SchemaFor(nameof(ForgeTools.StartWork)).GetProperty("properties");
        Assert.False(background.TryGetProperty("findings", out _));
    }

    [Fact]
    public void Review_code_schema_accepts_no_decisions()
    {
        Assert.False(SchemaFor(nameof(ForgeTools.ReviewCode)).GetProperty("properties").TryGetProperty("decisions", out _));
    }

    [Fact]
    public void Package_contract_still_requires_eighteen_tools_and_new_schema_fields()
    {
        var package = Read("build", "package.ps1");
        Contains("if ($tools.Count -ne 18)", "forge.plan.review schema is missing decisions", "forge.review.fix schema is missing", "fixFindingIds", package);
    }

    private static string Description(string methodName)
    {
        var method = typeof(ForgeTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name == methodName
                && candidate.GetCustomAttribute<McpServerToolAttribute>() is not null);
        return method.GetCustomAttribute<DescriptionAttribute>()!.Description;
    }

    private static System.Text.Json.JsonElement SchemaFor(string methodName)
    {
        var services = new ServiceCollection()
            .AddSingleton(SessionRoots.None)
            .AddSingleton(new JobRegistry())
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

    private static PromptLibrary Prompts() => new(Path.Combine(RepositoryRoot(), "prompts"));

    private static string Skill() => Read("skills", "forge", "SKILL.md");

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. path]));

    private static void Contains(params string[] values)
    {
        var text = Collapse(values[^1]);
        foreach (var value in values[..^1])
            Assert.Contains(Collapse(value), text, StringComparison.Ordinal);
    }

    private static void DoesNotContain(string value, string text) =>
        Assert.DoesNotContain(Collapse(value), Collapse(text), StringComparison.OrdinalIgnoreCase);

    private static string Collapse(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "skills", "forge", "SKILL.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return PromptLibrary.Locate(null);
    }
}
