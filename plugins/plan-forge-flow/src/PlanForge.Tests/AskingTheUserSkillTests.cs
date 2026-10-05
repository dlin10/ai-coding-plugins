using PlanForge.Prompts;
using Xunit;

namespace PlanForge.Tests;

public sealed class AskingTheUserSkillTests
{
    [Fact]
    public void The_protocol_covers_every_question_the_orchestrator_asks()
    {
        Contains("## Asking the user", "the interview, the Scout, critic and builder selections, the instruction questions, " +
                 "decisions on findings, the round cap, approval, and the values a gate needs",
                 "holds whichever skill is running the interview", Skill());
    }

    [Fact]
    public void One_question_per_call_in_dependency_order()
    {
        Contains("Ask one question per call of the host's question tool", "`AskUserQuestion`", "or per message where the host has none",
                 "first the one the others depend on, and the next only once that one is answered", Skill());
    }

    [Fact]
    public void The_protocol_overrides_the_grilling_whole_frontier_round()
    {
        Contains("This overrides the `grilling` rule to ask the whole frontier in one round", "never their batching",
                 "each explained as \"Asking the user\" describes", Skill());
    }

    [Fact]
    public void A_batch_only_when_the_user_asks_for_one()
    {
        Contains("If the user asks for questions in a batch, batch them as they asked", Skill());
    }

    [Fact]
    public void Each_question_is_explained_with_its_problem_an_example_and_its_options()
    {
        Contains("Before each question, explain it in the chat", "**The problem it settles**",
                 "what breaks or stays unclear if nobody decides", "appears only beside what it stands for",
                 "one-sentence definition", Skill());
        Contains("**An example**", "`path:line`", "the path taken from the repository root",
                 "a minimal illustrative one labelled as such", "what happens to it under each answer", Skill());
        Contains("**The options**", "what it changes in the plan or the code", "its advantages, its drawbacks and its cost",
                 "the one you recommend, and why", Skill());
    }

    [Fact]
    public void The_question_repeats_the_options_with_the_recommendation_first()
    {
        Contains("The question offers the same options", "in agreement with the explanation",
                 "put the recommended option first, and end its label with \"(Recommended)\"", Skill());
    }

    [Fact]
    public void A_mechanical_question_needs_one_or_two_sentences()
    {
        Contains("A mechanical question", "a model and effort, the Fast tier", "one or two sentences of explanation and no code", Skill());
    }

    [Fact]
    public void Each_worker_role_is_asked_in_one_call_the_critics_first()
    {
        var skill = Skill();
        Contains("You make two batches unasked, one per worker role",
                 "the critic's vendor, model-and-effort, Fast and instruction questions share one call, and the builder's another",
                 "Explain each question of a batch before the call", skill);
        Contains("Ask them in two calls, one per role, the critic's first",
                 "each call carries that role's vendor, model-and-effort, Fast and instruction questions", "four questions at most",
                 skill);
        DoesNotContain("as one round of two questions", skill);
        DoesNotContain("instruction questions of step 4 as one round", skill);
    }

    [Fact]
    public void Both_calls_are_built_from_the_catalogue_fetched_before_the_first()
    {
        Contains("again immediately before the Critic/Builder Vendor question", "Build both calls from that one answer",
                 "every available vendor's models, efforts and Fast tiers", Skill());
    }

    [Fact]
    public void Options_name_what_they_depend_on_and_a_misfit_gets_one_follow_up()
    {
        Contains("every option says what it depends on", "a model-and-effort pair names its vendor",
                 "the Fast question names the pairs that offer it", "a pair from a vendor the user did not choose",
                 "ask one follow-up question about the part that does not fit", Skill());
        Contains("name each pair's vendor in its label", "Ask the Fast question only when a pair the call offers",
                 "only on a yes for a pair that offers it", "propose no wording of your own", Skill());
    }

    [Fact]
    public void Steps_are_listed_in_the_order_of_the_questions_in_a_call()
    {
        var skill = Collapse(Skill());
        var vendor = skill.IndexOf("1. The vendor question offers only vendors", StringComparison.Ordinal);
        var model = skill.IndexOf("2. The model-and-effort question requests", StringComparison.Ordinal);
        var fast = skill.IndexOf("3. Ask the Fast question only when", StringComparison.Ordinal);
        var instructions = skill.IndexOf("4. The instruction question asks", StringComparison.Ordinal);
        Assert.True(vendor >= 0 && vendor < model && model < fast && fast < instructions,
                    $"steps out of order: {vendor}, {model}, {fast}, {instructions}");
    }

    [Fact]
    public void Findings_the_cap_and_the_gate_environment_are_asked_one_at_a_time()
    {
        Contains("When one critique brings several, ask them one at a time", "Say in plain words what the open findings are",
                 "one variable per question, each saying which gate reads it and what for", Skill());
    }

    private static void Contains(params string[] values)
    {
        var text = Collapse(values[^1]);
        foreach (var value in values[..^1])
            Assert.Contains(Collapse(value), text, StringComparison.Ordinal);
    }

    private static void DoesNotContain(string value, string text) =>
        Assert.DoesNotContain(Collapse(value), Collapse(text), StringComparison.Ordinal);

    private static string Skill() => File.ReadAllText(Path.Combine(RepositoryRoot(), "skills", "forge", "SKILL.md"));

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
