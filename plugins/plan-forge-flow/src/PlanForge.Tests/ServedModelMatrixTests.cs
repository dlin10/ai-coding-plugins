using PlanForge.Advisors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Checks stored fallback and first-fresh precedence over the complete served-model cross product.</summary>
public sealed class ServedModelMatrixTests
{
    /// <summary>One independently specified model cell, with an expected outcome for each of three keys.</summary>
    /// <param name="Stored">The stored relevance model, or none.</param>
    /// <param name="First">The first fresh response model, or a transient failure.</param>
    /// <param name="Later">The later fresh response model, or a transient failure.</param>
    /// <param name="Served">The expected call model.</param>
    /// <param name="Outcomes">Answer, unavailable or not_yet_checked for each key in item order.</param>
    private sealed record Cell(string? Stored, string? First, string? Later, string? Served, string[] Outcomes);

    /// <summary>The eighteen expected cells, written independently of the mapper's model selection.</summary>
    private static readonly Cell[] Cells =
    [
        new(null, null, null, null, ["unavailable", "unavailable", "unavailable"]),
        new(null, null, "A", "A", ["unavailable", "unavailable", "answer"]),
        new(null, null, "B", "B", ["unavailable", "unavailable", "answer"]),
        new(null, "A", null, "A", ["unavailable", "answer", "unavailable"]),
        new(null, "A", "A", "A", ["unavailable", "answer", "answer"]),
        new(null, "A", "B", "A", ["unavailable", "answer", "not_yet_checked"]),
        new(null, "B", null, "B", ["unavailable", "answer", "unavailable"]),
        new(null, "B", "A", "B", ["unavailable", "answer", "not_yet_checked"]),
        new(null, "B", "B", "B", ["unavailable", "answer", "answer"]),
        new("A", null, null, "A", ["answer", "unavailable", "unavailable"]),
        new("A", null, "A", "A", ["answer", "unavailable", "answer"]),
        new("A", null, "B", "B", ["not_yet_checked", "unavailable", "answer"]),
        new("A", "A", null, "A", ["answer", "answer", "unavailable"]),
        new("A", "A", "A", "A", ["answer", "answer", "answer"]),
        new("A", "A", "B", "A", ["answer", "answer", "not_yet_checked"]),
        new("A", "B", null, "B", ["not_yet_checked", "answer", "unavailable"]),
        new("A", "B", "A", "B", ["not_yet_checked", "answer", "not_yet_checked"]),
        new("A", "B", "B", "B", ["not_yet_checked", "answer", "answer"])
    ];

    /// <summary>Every model combination keeps, drops and discards exactly its independently labelled outcomes.</summary>
    [Fact]
    public async Task Every_cell_gets_its_expected_answer()
    {
        foreach (var cell in Cells)
        {
            using var run = MapperRun.Plain(3);
            if (cell.Stored is not null) run.SeedRelevance(cell.Stored);
            run.Advisor.Handler = (request, _) =>
            {
                var text = request.RequestState["item"]!["text"]!.GetValue<string>();
                var model = text switch { "fact number 2" => cell.First, "fact number 3" => cell.Later, _ => null };
                if (model is null) throw new AdvisorException(AdvisorFailureKind.Unavailable);
                return Task.FromResult(FakeAdvisor.Answer(request, model));
            };
            var map = await run.Call();
            Assert.Equal(cell.Served, map.Result.ServedModel);
            Assert.Equal(cell.Outcomes, map.Observation.Select(item =>
            {
                var outcome = item.Judgments[0].Questions["relevance"];
                return outcome.Answer is not null ? "answer" : outcome.Reason;
            }));
            var expectedKept = cell.Outcomes.Count(outcome => outcome == "answer");
            Assert.Equal(expectedKept, run.Store().Judgments.Count);
            Assert.All(run.Store().Judgments.Values, judgment => Assert.Equal(cell.Served, judgment.ServedModel));
            Assert.Equal(3 - expectedKept, map.Result.Remaining);
            Assert.Equal(expectedKept == 3, map.Result.Complete);
            Assert.Equal(cell.Stored is null ? 0 : 1, map.Result.CacheHits);
            Assert.Equal(cell.Outcomes.Skip(1).Count(outcome => outcome == "answer"), map.Result.CheckedThisCall);
            Assert.Equal(cell.Stored is not null && cell.Outcomes[0] == "answer", map.Observation[0].Judgments[0].FromStore);
            Assert.All(map.Observation.Skip(1), item => Assert.False(item.Judgments[0].FromStore));
            for (var index = 0; index < 3; index++)
                Assert.Equal(cell.Outcomes[index] == "answer" ? "no_candidates" : "unchecked", map.Observation[index].Status);
        }
    }

    /// <summary>The table contains each axis value and every cross-product cell exactly once.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        Assert.Equal(18, Cells.Length);
        Assert.Equal(18, Cells.Select(cell => (cell.Stored, cell.First, cell.Later)).Distinct().Count());
        Assert.Equal([null, "A"], Cells.Select(cell => cell.Stored).Distinct().Order());
        Assert.Equal([null, "A", "B"], Cells.Select(cell => cell.First).Distinct().Order());
        Assert.Equal([null, "A", "B"], Cells.Select(cell => cell.Later).Distinct().Order());
        foreach (var stored in new string?[] { null, "A" })
            foreach (var first in new string?[] { null, "A", "B" })
                foreach (var later in new string?[] { null, "A", "B" })
                    Assert.Single(Cells, cell => (cell.Stored, cell.First, cell.Later) == (stored, first, later));
    }
}
