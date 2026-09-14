using ConcurrencyHunter.Reporting;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class RunStatusRulesTests
{
    [Fact]
    public void No_findings_and_a_complete_load_is_CompleteClean()
    {
        var decision = Decide(ReportingTestData.CreateAnalysis());

        Assert.Equal(RunStatus.CompleteClean, decision.Status);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void Findings_with_every_High_and_Medium_group_narrated_is_CompleteWithFindings()
    {
        var decision = Decide(
            ReportingTestData.CreateAnalysis("High", "Medium", "Low"),
            new HashSet<string>(["G1", "G2"], StringComparer.Ordinal));

        Assert.Equal(RunStatus.CompleteWithFindings, decision.Status);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void High_group_without_narrative_is_Incomplete_with_NarrativeMissing()
    {
        var decision = Decide(ReportingTestData.CreateAnalysis("High"));

        Assert.Equal(RunStatus.Incomplete, decision.Status);
        Assert.Equal(["NarrativeMissing:G1"], decision.Reasons);
    }

    [Fact]
    public void Low_group_without_narrative_keeps_the_run_complete()
    {
        var decision = Decide(ReportingTestData.CreateAnalysis("Low"));

        Assert.Equal(RunStatus.CompleteWithFindings, decision.Status);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void Deadline_is_Incomplete_with_OverallTimeout()
    {
        var analysis = ReportingTestData.CreateAnalysis();
        var inputs = new StatusInputs(false, false, true, true, analysis, EmptyNarratives());

        var decision = RunStatusRules.Decide(inputs);

        Assert.Equal(RunStatus.Incomplete, decision.Status);
        Assert.Equal(["OverallTimeout"], decision.Reasons);
    }

    [Fact]
    public void Missing_projects_are_Incomplete_with_ProjectsNotLoaded()
    {
        var analysis = ReportingTestData.CreateAnalysis();
        var inputs = new StatusInputs(false, false, false, false, analysis, EmptyNarratives());

        var decision = RunStatusRules.Decide(inputs);

        Assert.Equal(RunStatus.Incomplete, decision.Status);
        Assert.Equal(["ProjectsNotLoaded"], decision.Reasons);
    }

    [Fact]
    public void Load_failure_is_Failed()
    {
        var inputs = new StatusInputs(true, true, false, true, null, EmptyNarratives());

        var decision = RunStatusRules.Decide(inputs);

        Assert.Equal(RunStatus.Failed, decision.Status);
        Assert.Equal(["LoadFailed"], decision.Reasons);
    }

    [Fact]
    public void Analysis_failure_is_Failed_and_takes_precedence_over_the_deadline()
    {
        var inputs = new StatusInputs(false, true, true, true, null, EmptyNarratives());

        var decision = RunStatusRules.Decide(inputs);

        Assert.Equal(RunStatus.Failed, decision.Status);
        Assert.Equal(["AnalysisFailed"], decision.Reasons);
    }

    [Fact]
    public void Missing_analysis_is_Incomplete_with_AnalysisNotFinished()
    {
        var inputs = new StatusInputs(false, false, true, false, null, EmptyNarratives());

        var decision = RunStatusRules.Decide(inputs);

        Assert.Equal(RunStatus.Incomplete, decision.Status);
        Assert.Equal(["AnalysisNotFinished"], decision.Reasons);
    }

    private static StatusDecision Decide(ConcurrencyHunter.Analysis.AnalysisResult analysis,
                                         IReadOnlySet<string>? narratives = null) =>
        RunStatusRules.Decide(new StatusInputs(
            false,
            false,
            true,
            false,
            analysis,
            narratives ?? EmptyNarratives()));

    private static IReadOnlySet<string> EmptyNarratives() => new HashSet<string>(StringComparer.Ordinal);
}
