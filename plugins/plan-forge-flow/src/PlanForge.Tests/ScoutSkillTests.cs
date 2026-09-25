using PlanForge.Prompts;
using Xunit;

namespace PlanForge.Tests;

public sealed class ScoutSkillTests
{
    [Fact]
    public void Skill_names_critic_builder_and_scout()
    {
        Contains("a critic, a builder, and a scout", Skill());
    }

    [Fact]
    public void Broad_reconnaissance_triggers_are_explicit()
    {
        Contains("multi-module exploration", "caller/writer discovery", "dependency tracing", "greenfield orientation", Skill());
    }

    [Fact]
    public void Local_lookup_boundary_stays_with_the_orchestrator()
    {
        Contains("one or two targeted semantic lookups", "cheaper and clearer", "local Roslyn or repository tools", "Do not turn every local lookup into a Scout call", Skill());
    }

    [Fact]
    public void Scout_selection_is_lazy_and_just_in_time()
    {
        Contains("Scout is lazy and independent", "first broad need", "just-in-time Scout selection round", Skill());
    }

    [Fact]
    public void Early_scout_catalogue_call_precedes_its_selection()
    {
        Contains("call `forge.models`, offer up to three valid", "Scout selection round", Skill());
    }

    [Fact]
    public void Early_selection_offers_at_most_three_valid_combinations()
    {
        Contains("up to three valid live/resolved Vendor/model/effort combinations", Skill());
    }

    [Fact]
    public void Early_selection_marks_exactly_one_recommendation()
    {
        Contains("clearly mark exactly one **recommended** combination", Skill());
    }

    [Fact]
    public void Early_selection_includes_continue_without_scout()
    {
        Contains("choice **continue without Scout**", Skill());
    }

    [Fact]
    public void Final_catalogue_is_refreshed_before_critic_builder_selection()
    {
        Contains("again immediately before the Critic/Builder Vendor question", "Do not claim that the catalogue is called only once", Skill());
    }

    [Fact]
    public void Final_catalogue_reuses_successful_cache_entries()
    {
        Contains("successful probes return from `CatalogCache` immediately", Skill());
    }

    [Fact]
    public void Final_catalogue_reprobes_unavailable_entries()
    {
        Contains("previously unavailable probes rerun", "repaired", "sign-in", Skill());
    }

    [Fact]
    public void Scout_vendor_question_is_omitted_for_one_available_vendor()
    {
        ContainsScout("When exactly one valid Vendor is available, omit the Scout Vendor question", ScoutSection());
    }

    [Fact]
    public void Scout_vendor_question_is_omitted_for_cursor_with_cursor_available()
    {
        ContainsScout("On a Cursor host, when `cursor` is available, omit the Scout Vendor question", ScoutSection());
    }

    [Fact]
    public void Continue_is_only_a_same_investigation_follow_up()
    {
        Contains("Use `continue` only for a direct follow-up in the same investigation", Skill());
    }

    [Fact]
    public void Fresh_covers_independent_new_stale_and_reset_questions()
    {
        Contains("Use `fresh` for an independent question, a new subsystem, stale evidence, or a deliberate reset", Skill());
    }

    [Fact]
    public void Every_scout_call_passes_an_explicit_session_mode()
    {
        Contains("For every direct or background Scout call, pass `sessionMode` explicitly", Skill());
    }

    [Fact]
    public void Decline_and_later_enabling_are_explicit()
    {
        Contains("A persisted decline suppresses later automatic Scout questions", "enabling it later requires an explicit `forge.scout.select` call", Skill());
    }

    [Fact]
    public void Retry_reselection_and_continue_without_handling_are_not_silent()
    {
        Contains("After a failure, retry with the saved selection", "reselect explicitly", "explicitly choose continue without", "Never silently choose session continuity", Skill());
    }

    [Fact]
    public void Internet_and_typed_citation_policy_is_explicit()
    {
        Contains("available internet without per-call authorization", "typed repository citations", "external citations", "absolute HTTP(S) URL", Skill());
    }

    [Fact]
    public void Scout_is_independent_of_critic_and_builder()
    {
        Contains("Scout is lazy and independent of the separate Critic and Builder selections", "Critic/Builder selection remains", Skill());
    }

    [Fact]
    public void Non_cursor_scout_routing_is_direct()
    {
        Contains("On non-Cursor hosts, call `forge.scout.run` directly", Skill());
    }

    [Fact]
    public void Cursor_scout_routing_is_background()
    {
        Contains("Cursor runs the `scout` act only through `forge.work.start` → `forge.work.poll` → `forge.work.fetch`", Skill());
    }

    [Fact]
    public void Both_routing_authorities_name_scout()
    {
        var skill = Skill();
        Assert.True(skill.IndexOf("The tools, in order", StringComparison.Ordinal) >= 0);
        Assert.True(skill.IndexOf("non-Cursor hosts, call `forge.scout.run` directly", StringComparison.Ordinal) >= 0);
        Assert.True(skill.IndexOf("The code-review loop", StringComparison.Ordinal) >= 0);
        Assert.True(skill.IndexOf("The same routing applies to Scout", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void Flow_log_appears_after_first_scout_or_critique()
    {
        Contains("first Scout outcome or first critique, whichever happens first", "surface its live path in that turn", Skill());
    }

    [Fact]
    public void Status_table_names_run_scout_state()
    {
        Contains("`run.scout` with enabled/selection, current session, and last failure", Skill());
    }

    [Fact]
    public void Documents_have_scout_only_conditional_metadata()
    {
        Contains("Only a successful direct Scout call or a successful completed Scout fetch", "documents.scout", "Non-Scout results omit it", Skill());
    }

    [Fact]
    public void Documents_repeat_scout_report_only_after_successful_replacement()
    {
        Contains("show the latest Scout report to the user now, and show it again only after a later successful Scout call replaces it", Skill());
    }

    [Fact]
    public void Both_document_sections_describe_flowlog_plan_and_conditional_scout_metadata()
    {
        var skill = Skill();
        Assert.True(Occurrences(skill, "documents.scout") >= 2);
        Assert.True(Occurrences(skill, "documents.flowLog") >= 1);
        Assert.True(Occurrences(skill, "documents.plan") >= 1);
    }

    [Fact]
    public void Worker_tools_reach_all_three_worker_roles()
    {
        Contains("every critic, builder, and scout may call", "all three roles get the same grant", Skill());
    }

    [Fact]
    public void Result_key_names_the_direct_scout_digest()
    {
        Contains("bounded Scout digest under `scout`", Skill());
    }

    [Fact]
    public void Budget_has_six_plus_two_and_one_scout_round_without_domain_cap()
    {
        Contains("at most six questions total: two Vendor questions, two model/effort questions, and one Fast question for each role whose choice offers it", "two instruction questions of step 4 as one round", "A single just-in-time Scout selection round is additional", "no numeric cap on the domain interview", Skill());
    }

    [Fact]
    public void Readme_lists_both_scout_tools_and_eighteen_tools()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));
        Contains("exposes eighteen tools", "`forge.scout.select`", "`forge.scout.run`", readme);
    }

    [Fact]
    public void Agents_names_four_participants_eighteen_tools_and_scout_boundary()
    {
        var agents = File.ReadAllText(Path.Combine(RepositoryRoot(), "AGENTS.md"));
        Contains("all eighteen `forge.*` tools", "## The four participants", "**Scout**", "Read-only and", "non-writable", agents);
    }

    [Fact]
    public void Agents_layout_and_contract_statements_include_scout()
    {
        var agents = File.ReadAllText(Path.Combine(RepositoryRoot(), "AGENTS.md"));
        Contains("PlanReview, Build, CodeReview, ReviewFix, Scout", "scout-contract.md", "roslyn-contract.md` is appended to Critic and Scout", "All Worker roles receive the run's", agents);
    }

    private static void Contains(params string[] values)
    {
        var text = Collapse(values[^1]);
        foreach (var value in values[..^1])
            Assert.Contains(Collapse(value), text, StringComparison.Ordinal);
    }

    private static void ContainsScout(string value, string scoutSection) =>
        Assert.Contains(Collapse(value), Collapse(scoutSection), StringComparison.Ordinal);

    private static string ScoutSection()
    {
        var skill = Skill();
        var start = skill.IndexOf("## Scout reconnaissance", StringComparison.Ordinal);
        var end = skill.IndexOf("## Act 1: the interview", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return skill[start..end];
    }

    private static string Skill() => Collapse(File.ReadAllText(Path.Combine(RepositoryRoot(), "skills", "forge", "SKILL.md")));

    private static string Collapse(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

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
