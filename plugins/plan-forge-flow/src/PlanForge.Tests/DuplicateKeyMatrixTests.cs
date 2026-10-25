using PlanForge.Advisors;
using PlanForge.Evidence;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Exercises shared item and unit keys through both received and stored persistent outcomes.</summary>
public sealed class DuplicateKeyMatrixTests
{
    /// <summary>One independently enumerated duplicate, provenance and outcome cell.</summary>
    /// <param name="Duplicate">Items, units or both.</param>
    /// <param name="Read">Whether this call reads outcomes from the store.</param>
    /// <param name="Reason">Whether the pair outcome is a persistent reason rather than a contradiction.</param>
    /// <param name="Items">The expected item count.</param>
    /// <param name="PairsPerItem">The expected logical pair count per item.</param>
    private sealed record Cell(string Duplicate, bool Read, bool Reason, int Items, int PairsPerItem);

    /// <summary>The complete three-by-two-by-two table.</summary>
    private static readonly Cell[] Cells =
    [
        new("items", false, false, 2, 1), new("items", false, true, 2, 1),
        new("items", true, false, 2, 1), new("items", true, true, 2, 1),
        new("units", false, false, 1, 2), new("units", false, true, 1, 2),
        new("units", true, false, 1, 2), new("units", true, true, 1, 2),
        new("both", false, false, 2, 2), new("both", false, true, 2, 2),
        new("both", true, false, 2, 2), new("both", true, true, 2, 2)
    ];

    /// <summary>Each shared key is asked and stored once, yet its outcome applies to every logical judgment.</summary>
    [Fact]
    public async Task Every_cell_gets_its_expected_answer()
    {
        foreach (var cell in Cells)
        {
            using var run = new MapperRun();
            if (cell.Items == 2)
                run.Entries = [run.Entry(MapperRun.External("WidgetApi evidence"), MapperRun.External("WidgetApi evidence"))];
            if (cell.PairsPerItem == 2)
                run.Plan = "# Plan\n## Work\nUse `WidgetApi`.\n## Work\nUse `WidgetApi`.\n";
            run.Advisor.Choose = question => question.Id == "relevance" ? FakeAdvisor.Valid(question)
                : cell.Reason ? new Refused() : FakeAdvisor.Valid(question,
                    question.Id.StartsWith("relationship:", StringComparison.Ordinal) ? "contradicts" : "explicit_obligation");
            if (cell.Read)
            {
                await run.Call();
                run.ForgetMapping();
                run.Advisor.Requests.Clear();
                run.Advisor.Handler = (_, _) => throw new InvalidOperationException("A stored key must not be requested.");
            }
            var map = await run.Call();
            Assert.Equal(2, map.Result.Required);
            Assert.Equal(cell.Read ? 2 : 0, map.Result.CacheHits);
            Assert.Equal(cell.Read ? 0 : 2, map.Result.CheckedThisCall);
            Assert.True(map.Result.Complete);
            Assert.Equal(0, map.Result.Remaining);
            Assert.Equal(2, run.Store().Judgments.Count);
            Assert.Equal(cell.Read ? 0 : 1, run.Advisor.Requests.Count);
            var sent = run.Advisor.Requests.SelectMany(request => request.Questions).ToArray();
            Assert.Equal(cell.Read ? 0 : 3, sent.Length);
            Assert.Equal(sent.Length, sent.Select(question => question.Id).Distinct().Count());
            Assert.Equal(cell.Items, map.Observation.Count);
            Assert.All(map.Observation, item =>
            {
                Assert.Equal(cell.Reason ? "unchecked" : "contradiction", item.Status);
                Assert.Equal(cell.PairsPerItem, item.CandidateUnitIds.Count);
                Assert.Equal(cell.PairsPerItem + 1, item.Judgments.Count);
                Assert.All(item.Judgments, judgment => Assert.Equal(cell.Read, judgment.FromStore));
            });
            var logical = map.Observation.SelectMany(item => item.Judgments).ToArray();
            Assert.Equal(2, logical.Select(judgment => judgment.Key).Distinct().Count());
            foreach (var group in logical.GroupBy(judgment => judgment.Key))
            {
                var stored = run.Store().Judgments[group.Key];
                foreach (var judgment in group)
                {
                    Assert.Equal(stored.Kind, judgment.Kind);
                    foreach (var (name, outcome) in judgment.Questions)
                    {
                        Assert.Equal(stored.Questions[name].Reason, outcome.Reason);
                        Assert.Equal(stored.Questions[name].Answer?.Choice, outcome.Answer?.Choice);
                        if (judgment.Kind == "pair")
                        {
                            Assert.Equal(cell.Reason ? "refused" : null, outcome.Reason);
                            Assert.Equal(cell.Reason ? null : name == "relationship" ? "contradicts" : "explicit_obligation", outcome.Answer?.Choice);
                        }
                    }
                }
            }
            Assert.Equal(cell.Items, map.Result.Counts[cell.Reason ? "unchecked" : "contradiction"]);
            Assert.Equal(cell.Items, map.Result.Flags.Count);
            Assert.All(map.Result.Flags, flag => Assert.Equal(cell.Read ? "carried" : "new", flag.Change));
        }
    }

    /// <summary>Each duplication, provenance and outcome value occurs in every combination exactly once.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        Assert.Equal(12, Cells.Length);
        Assert.Equal(12, Cells.Select(cell => (cell.Duplicate, cell.Read, cell.Reason)).Distinct().Count());
        Assert.Equal(["both", "items", "units"], Cells.Select(cell => cell.Duplicate).Distinct().Order());
        Assert.Equal([false, true], Cells.Select(cell => cell.Read).Distinct().Order());
        Assert.Equal([false, true], Cells.Select(cell => cell.Reason).Distinct().Order());
        foreach (var duplicate in new[] { "items", "units", "both" })
            foreach (var read in new[] { false, true })
                foreach (var reason in new[] { false, true })
                    Assert.Single(Cells, cell => (cell.Duplicate, cell.Read, cell.Reason) == (duplicate, read, reason));
    }
}
