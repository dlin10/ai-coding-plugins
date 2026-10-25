using System.Text.Json;
using PlanForge.Evidence;
using PlanForge.Mcp;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Exercises the complete map pipeline with sanitized, independently labelled evidence.</summary>
public sealed class EvidenceMapFixtureTests
{
    /// <summary>Every Scout item and implication has exactly one observation under either plan.</summary>
    [Fact]
    public async Task Every_item_is_observed_once_in_both_plans()
    {
        using var fixture = new EvidenceMapFixture();
        Assert.Equal(3, fixture.Entries.Count);
        foreach (var plan in fixture.Labels.Keys)
        {
            var map = await fixture.Map(plan);
            Assert.Equal(32, map.Observation.Count);
            Assert.Equal(fixture.Labels[plan].Select(label => label.Id).Order(), map.Observation.Select(item => item.Id).Order());
            Assert.Equal(map.Observation.Count, map.Observation.Select(item => item.Id).Distinct().Count());
        }
    }

    /// <summary>All eight statuses are settled from scripted question answers, not from expected status fields.</summary>
    [Fact]
    public async Task Every_status_matches_its_hand_written_label()
    {
        using var fixture = new EvidenceMapFixture();
        foreach (var plan in fixture.Labels.Keys)
        {
            var map = await fixture.Map(plan);
            Assert.True(map.Result.Complete);
            foreach (var label in fixture.Labels[plan])
                Assert.Equal(label.Script.Status, Assert.Single(map.Observation, item => item.Id == label.Id).Status);
        }
    }

    /// <summary>Every independently labelled candidate is selected, and candidate ids match the fixture exactly.</summary>
    [Fact]
    public async Task Candidate_recall_is_one_for_both_plans()
    {
        using var fixture = new EvidenceMapFixture();
        var evaluations = new List<EvidenceEvaluation>();
        foreach (var plan in fixture.Labels.Keys)
        {
            var map = await fixture.Map(plan);
            foreach (var label in fixture.Labels[plan])
                Assert.Equal(label.Candidates, Assert.Single(map.Observation, item => item.Id == label.Id).CandidateUnitIds);
            evaluations.Add(new(map, fixture.Labels[plan]));
        }
        Assert.Equal(1d, EvidenceMetrics.Of(evaluations, fixture.Progress).CandidateRecall);
    }

    /// <summary>The original missing-consumer gap and its derived obligation need explicit consumer work.</summary>
    [Fact]
    public async Task F0030_item_and_implication_are_flagged_only_when_consumers_are_missing()
    {
        using var fixture = new EvidenceMapFixture();
        foreach (var plan in fixture.Labels.Keys)
        {
            var map = await fixture.Map(plan);
            foreach (var id in new[] { "A1.confirmedFacts.1", "H1" })
            {
                var item = Assert.Single(map.Observation, item => item.Id == id);
                if (plan == "plan-covered.md")
                {
                    Assert.Equal("covered", item.Status);
                    Assert.DoesNotContain(map.Result.Flags, flag => flag.Id == id);
                    Assert.Equal("explicit_obligation", Assert.Single(item.Judgments, judgment => judgment.UnitId == "T2")
                        .Questions["obligation"].Answer!.Choice);
                }
                else
                {
                    Assert.Equal("no_coverage_established", item.Status);
                    Assert.Contains(map.Result.Flags, flag => flag.Id == id);
                }
            }
        }
        Assert.Contains("A1.confirmedFacts.1", fixture.Implications[0].DerivedFrom);
    }

    /// <summary>Consumed numbers above the three indexed answers remain available for manual checking.</summary>
    [Fact]
    public async Task Missing_answer_numbers_are_not_indexed()
    {
        using var fixture = new EvidenceMapFixture();
        fixture.Run.WriteState(fixture.Run.ReadState() with { ScoutAnswers = 6 });
        var map = await fixture.Map("plan-covered.md", scoutAnswers: fixture.Run.ReadState().ScoutAnswers);
        Assert.Equal([4, 5, 6], map.Result.NotIndexedAnswers);
        Assert.Equal(3, map.Result.Counts["notIndexedAnswers"]);
        Assert.True(map.Result.Complete);
        Assert.Equal(32, map.Observation.Count);
    }

    /// <summary>After a complete map a revision asks nothing: every item keeps the mark the map gave it.</summary>
    [Fact]
    public async Task A_revision_after_a_complete_map_asks_nothing()
    {
        using var fixture = new EvidenceMapFixture();
        var before = await fixture.Map("plan-covered.md");
        var advisor = new LabelledAdvisor(fixture, "plan-missing.md");
        var after = await fixture.Map("plan-missing.md", advisor, keepStore: true);
        Assert.Empty(advisor.Requests);
        Assert.True(after.Result.Complete);
        Assert.Equal(0, after.Result.Required);
        Assert.Equal(before.Observation.Count, after.Result.AlreadyMapped);
        Assert.Empty(after.Observation);
        var marks = EvidenceStore.Load(fixture.Run.EvidenceMapPath).LastComplete;
        Assert.Equal(before.Observation.Count, marks.Count);
        Assert.All(before.Observation.Where(item => item.Id.StartsWith('A')), item => Assert.Equal(item.Status, marks[item.Id].Status));
    }

    /// <summary>
    /// When nothing leaves items out — a map written before mappings existed — a revision asks only the changed task's
    /// pairs; every unchanged key comes from the store.
    /// </summary>
    [Fact]
    public async Task Revision_reuses_all_unchanged_judgments()
    {
        using var fixture = new EvidenceMapFixture();
        var before = await fixture.Map("plan-covered.md");
        var previous = before.Observation.SelectMany(item => item.Judgments).Select(judgment => judgment.Key).ToHashSet();
        MapperRun.ForgetMapping(fixture.Run.EvidenceMapPath);
        var advisor = new LabelledAdvisor(fixture, "plan-missing.md");
        var after = await fixture.Map("plan-missing.md", advisor, keepStore: true);
        var judgments = after.Observation.SelectMany(item => item.Judgments).ToArray();
        Assert.Contains(judgments, judgment => judgment.FromStore);
        Assert.Contains(judgments, judgment => !judgment.FromStore);
        foreach (var judgment in judgments)
        {
            Assert.Equal(previous.Contains(judgment.Key), judgment.FromStore);
            if (!judgment.FromStore) Assert.Equal("T2", judgment.UnitId);
        }
        Assert.All(advisor.Requests.SelectMany(request => request.Questions), question =>
        {
            Assert.DoesNotContain("relevance", question.Id);
            Assert.Contains("**Describe the producer.**", question.Instructions);
        });
        Assert.Equal(judgments.Where(judgment => judgment.FromStore).DistinctBy(judgment => judgment.Key).Count(), after.Result.CacheHits);
        Assert.Equal(judgments.Where(judgment => !judgment.FromStore).DistinctBy(judgment => judgment.Key).Count(), after.Result.CheckedThisCall);
    }

    /// <summary>All R12 statuses occur repeatedly, including unresolved and external evidence and conflicting answers.</summary>
    [Fact]
    public async Task Fixture_covers_every_status_at_least_twice()
    {
        using var fixture = new EvidenceMapFixture();
        var observed = new List<EvidenceItemObservation>();
        foreach (var plan in fixture.Labels.Keys) observed.AddRange((await fixture.Map(plan)).Observation);
        foreach (var status in new[] { "contradiction", "unchecked", "context_only", "covered", "excluded",
                                       "uncertain", "no_candidates", "no_coverage_established" })
            Assert.True(observed.Count(item => item.Status == status) >= 2, status);
        Assert.Contains(observed, item => item.Locator == "external");
        Assert.Contains(observed, item => item.LocatorCheck is { Resolved: false, Reason: "missing_file" });
        Assert.All(observed, item => Assert.False(item.SourceChanged));
        Assert.Contains(observed, item => item.Id == "A1.confirmedFacts.2" && item.Status == "covered");
        Assert.Contains(observed, item => item.Id == "A3.confirmedFacts.1" && item.Status == "contradiction");
    }

    /// <summary>The actual tool returns the same item accounting and flag set as the direct observation on the same Run.</summary>
    [Fact]
    public async Task Tool_counts_and_flags_match_the_observation()
    {
        using var fixture = new EvidenceMapFixture();
        foreach (var plan in fixture.Labels.Keys)
        {
            var map = await fixture.Map(plan);
            // The tool maps the same plan afresh: against the store the direct map left, it would ask nothing.
            File.Delete(fixture.Run.EvidenceMapPath);
            using var result = JsonDocument.Parse(await ForgeTools.EvidenceMap(SessionRoots.None, fixture.Root,
                fixture.Run.RunId, fixture.ChangePoints, CancellationToken.None, _ => null,
                (_, _) => new LabelledAdvisor(fixture, plan), TimeProvider.System, implications: fixture.Implications));
            var counts = result.RootElement.GetProperty("counts");
            foreach (var count in counts.EnumerateObject())
            {
                var expected = count.Name == "sourceChanged" ? map.Observation.Count(item => item.SourceChanged)
                    : count.Name == "notIndexedAnswers" ? map.Result.NotIndexedAnswers.Count
                    : map.Observation.Count(item => item.Status == count.Name);
                Assert.Equal(expected, count.Value.GetInt32());
            }
            var flagged = map.Observation.Where(item => item.SourceChanged || item.Status is "contradiction"
                or "unchecked" or "uncertain" or "no_candidates" or "no_coverage_established").Select(item => item.Id).Order();
            Assert.Equal(flagged, result.RootElement.GetProperty("flags").EnumerateArray()
                .Select(flag => flag.GetProperty("id").GetString()).Order());
            Assert.Equal(map.Observation.Count, counts.EnumerateObject().Where(count => count.Name is not "sourceChanged" and not "notIndexedAnswers")
                .Sum(count => count.Value.GetInt32()));
        }
    }
}
