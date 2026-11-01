using System.Text.Json.Nodes;
using PlanForge.Advisors;
using PlanForge.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

/// <summary>Opt-in live check that OpenAI writes a request's shared part to its prompt cache and reads it back.</summary>
/// <param name="output">The test runner's measurement sink.</param>
public sealed class OpenAiDecisionsLiveTests(ITestOutputHelper output)
{
    /// <summary>The credential variable the live check needs in the process environment.</summary>
    private const string KEY_VARIABLE = "PLANFORGE_OPENAI_API_KEY";

    /// <summary>The synthetic requirement sentences that make the shared part about 2,700 tokens.</summary>
    private const int REQUIREMENTS = 40;

    /// <summary>
    /// Two requests in sequence share a fresh synthetic shared part and differ in their item: the first writes the shared
    /// part to the cache, the second reads it and so costs less than the same usage priced as all uncached.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_second_request_reads_the_shared_part_from_the_cache()
    {
        Assert.True(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KEY_VARIABLE)),
            $"Set {KEY_VARIABLE} to run the opt-in openai-decisions cache check.");
        var advisor = AdvisorCatalog.Default.Select(Environment.GetEnvironmentVariable, "openai-decisions");
        using var lifetime = advisor as IDisposable;
        output.WriteLine($"{advisor.Id}: requested model {advisor.RequestedModel}");
        var shared = Shared();
        var first = await Ask(advisor, shared, "Item A: the planner rewrites the journal format of the synthetic ledger.");
        var second = await Ask(advisor, shared, "Item B: the planner adds a status column to the synthetic ledger's report.");

        Assert.True(first.InputTokens is not null && second.InputTokens is not null, "Both requests must report input tokens.");
        var path = Path.Combine(Path.GetTempPath(), $"planforge-openai-decisions-cache-{Guid.NewGuid():N}.md");
        AtomicFile.Write(path, string.Join('\n', MeasuredRows.Cache(first, second)) + "\n");
        output.WriteLine($"Measured rows: {path}");
        Assert.True((first.CachedInputTokens ?? 0) + (first.CacheWriteTokens ?? 0) > 0,
            "The first request must read or write the shared part in the cache.");
        Assert.True(second.CachedInputTokens > 0, "The second request must read the shared part from the cache.");
        Assert.True(AdvisorPrice.Usd(advisor, second.InputTokens, second.CachedInputTokens, second.CacheWriteTokens)
                    < AdvisorPrice.Usd(advisor, second.InputTokens, 0, 0),
            "The second request must cost less than the same usage priced as all uncached.");
    }

    /// <summary>Sends one request with the shared part and an item, writing its counts and price to test output.</summary>
    /// <param name="advisor">The selected openai-decisions Advisor.</param>
    /// <param name="shared">The shared part both requests send.</param>
    /// <param name="item">The item this request adds.</param>
    private async Task<AdviceUsage> Ask(IAdvisor advisor, JsonObject shared, string item)
    {
        var result = await advisor.AskAsync(new(shared, new JsonObject { ["item"] = item }, [Question()]), CancellationToken.None);
        Assert.NotNull(result.Usage);
        var usage = result.Usage;
        output.WriteLine($"{item[..6]} served by {result.ServedModel}: input={usage.InputTokens}, output={usage.OutputTokens}, "
                         + $"cached={usage.CachedInputTokens}, written={usage.CacheWriteTokens}, "
                         + $"usd={AdvisorPrice.Usd(advisor, usage.InputTokens, usage.CachedInputTokens, usage.CacheWriteTokens)}, "
                         + $"uncached usd={AdvisorPrice.Usd(advisor, usage.InputTokens, 0, 0)}");
        return usage;
    }

    /// <summary>A shared part opening with a fresh GUID, so no earlier run's cache answers it, then synthetic requirements.</summary>
    private static JsonObject Shared()
    {
        var requirements = new JsonArray();
        for (var number = 1; number <= REQUIREMENTS; number++)
            requirements.Add(JsonValue.Create($"R{number}. The planner must keep record {number} of the synthetic ledger "
                             + "consistent with the approved scope, write its status to the journal before the next step "
                             + "begins, refuse any change that would remove an earlier obligation, report every refused change "
                             + "with its reason and owner, and leave unrelated records exactly as they were found at the start."));
        return new JsonObject { ["run"] = Guid.NewGuid().ToString("N"), ["requirements"] = requirements };
    }

    /// <summary>One Choice question of three options about the request's item.</summary>
    private static ChoiceQuestion Question() =>
        new("relevance", "Does the item touch any of the requirements?",
            [new("yes", "The item changes what a requirement governs."),
             new("partly", "The item only borders on what a requirement governs."),
             new("no", "The item is unrelated to every requirement.")]);
}
