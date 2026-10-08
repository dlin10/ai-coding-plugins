using PlanForge.Prompts;
using Xunit;

namespace PlanForge.Tests;

public sealed class FixNoteSkillTests
{
    [Fact]
    public void Fix_batches_are_formed_by_the_rule_and_never_split_one()
    {
        Contains("### Fix batches and their notes", "Form batches by the rule the findings break, not by file or severity",
                 "Never split one rule's findings across batches", "A batch may carry several rules",
                 "its selected gate (all executable G entries in full, only Fix gate in targeted)", "is a rule of its own", Skill());
    }

    [Fact]
    public void A_finding_another_batch_covered_is_host_verified_because_duplicate_of_needs_a_live_id()
    {
        Contains("close that finding afterwards with `hostVerified` and the evidence", "`duplicateOf` cannot do it",
                 "a canonical ID still in the ledger", Skill());
    }

    [Fact]
    public void The_first_call_of_every_fix_attempt_frames_each_rule_without_pasting_the_findings_back()
    {
        Contains("The first call of every fix attempt carries a `note`", "do not paste them or the critique back",
                 "**Rule**", "**Owner**", "**Axes**", Skill());
    }

    [Fact]
    public void A_rule_cites_a_requirement_and_quotes_the_task_text_a_fix_prompt_does_not_carry()
    {
        Contains("Cite the requirement", "the Builder holds the requirements in its Brief", "A fix prompt carries no task text",
                 "quote that sentence", "a decision the user made during the run", Skill());
    }

    [Fact]
    public void Axes_come_from_the_plan_first_and_are_found_by_references_rather_than_words()
    {
        Contains("Take them from the plan first", "the rules the matrix check listed with their axes", "twin deciders",
                 "by references and callers of the data that carries the fact", "not by searching for a word", Skill());
    }

    [Fact]
    public void What_must_not_change_and_when_to_stop_are_stated_once_for_the_batch()
    {
        Contains("Once for the whole batch", "**Must not change**", "**Stop condition**", "says so in a few words", Skill());
    }

    [Fact]
    public void A_retry_note_says_what_changed_and_a_vendor_switch_sends_the_whole_note_again()
    {
        Contains("resumes the Builder session that already holds the first note", "says what changed since the last call",
                 "restates in full, with its source, only a rule it corrects",
                 "A retry sent to another vendor starts a fresh session: send the whole note again", Skill());
    }

    [Fact]
    public void The_example_note_is_in_a_neutral_domain()
    {
        Contains("copy the form, not the words", "`TenantResolver.FromToken`", "Stop condition:", Skill());
    }

    [Fact]
    public void Every_code_review_round_logs_each_new_finding_as_own_sibling_same_class_or_older()
    {
        Contains("record the labels with `forge.log.append`", "`own` —", "`sibling` —", "`same-class` —", "`older` —",
                 "`own` and `sibling` are fallout", Skill());
    }

    [Fact]
    public void The_glossary_defines_the_fix_note_by_its_parts()
    {
        Contains("**Fix note**", "framing its batch by the rules its findings break", "its owner and its axes",
                 "what must not change and when to stop", Read("CONTEXT.md"));
    }

    private static void Contains(params string[] values)
    {
        var text = Collapse(values[^1]);
        foreach (var value in values[..^1])
            Assert.Contains(Collapse(value), text, StringComparison.Ordinal);
    }

    private static string Skill() => Read("skills", "forge", "SKILL.md");

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. path]));

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
