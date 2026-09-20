using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Reporting;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>
/// What the verdicts and the cells of the last phases look like where a reader and a model meet them: which of a collection's
/// two resources a finding is about (ADR 0010), which of the five protection verdicts it carries (TD-083), what the solver said
/// about it (TD-093), and a remediation chosen by what the two sides do — because a remediation chosen by the resource cannot
/// fix the cases this phase added.
/// </summary>
public sealed class ReportingSelectorTests
{
    [Fact]
    public void A_proven_index_names_its_cell_in_the_resource_text() =>
        Assert.Contains("- Resource: Fixture · di:Ns.Board@Singleton · _slots, cell [0] · scope",
                        Markdown(Cell(ElementSelector.Exact(0))));

    [Fact]
    public void A_proven_key_names_its_cell_in_the_resource_text() =>
        Assert.Contains("- Resource: Fixture · di:Ns.Board@Singleton · _slots, cell [\"a\"] · scope",
                        Markdown(Cell(ElementSelector.Key("a"))));

    [Fact]
    public void A_cell_nothing_proves_still_names_itself_in_the_resource_text() =>
        Assert.Contains("- Resource: Fixture · di:Ns.Board@Singleton · _slots, cell [?] · scope",
                        Markdown(Cell(ElementSelector.Unknown)));

    /// <summary>A collection has two resources, and a reader must be able to tell which one a finding is about.</summary>
    [Fact]
    public void The_structure_and_a_cell_of_one_collection_read_differently()
    {
        var structure = Markdown(Structure());
        var cell = Markdown(Cell(ElementSelector.Key("a")));

        Assert.Contains("- Resource: Fixture · di:Ns.Board@Singleton · _slots · scope", structure);
        Assert.DoesNotContain("- Resource: Fixture · di:Ns.Board@Singleton · _slots, cell", structure, StringComparison.Ordinal);
        Assert.Contains("- Resource: Fixture · di:Ns.Board@Singleton · _slots, cell [\"a\"] · scope", cell, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_resource_says_which_of_the_two_resources_it_is()
    {
        var structure = Resource(Structure());
        var cell = Resource(Cell(ElementSelector.Exact(3)));

        Assert.Equal(("storage", null), (structure.GetProperty("kind").GetString(), structure.GetProperty("selector").GetString()));
        Assert.Equal(("element", "[3]"), (cell.GetProperty("kind").GetString(), cell.GetProperty("selector").GetString()));
    }

    /// <summary>All five verdicts of TD-083 reach the report as they are: only <c>sufficient</c> ever takes a candidate away,
    /// and the three between say someone already treated the resource as shared.</summary>
    [Fact]
    public void Every_protection_verdict_reaches_the_report()
    {
        var verdicts = new[]
        {
            PairProtection.UNPROTECTED, PairProtection.PARTIAL, PairProtection.DIFFERENT_IDENTITY,
            PairProtection.INCOMPATIBLE_MODE, PairProtection.SUFFICIENT
        };

        Assert.Equal(verdicts, verdicts.Select(verdict => Json(Structure() with { ProtectionResult = verdict })
                                                          .GetProperty("protectionAnalysis").GetProperty("result").GetString()));
        Assert.All(verdicts, verdict => Assert.Contains($"- Protection: {verdict};", Markdown(Structure() with { ProtectionResult = verdict })));
    }

    [Fact]
    public void The_solver_answer_reaches_the_report()
    {
        var satisfiable = Structure() with { PathFeasibility = SolverAnswer.Sat };
        var undecided = Structure() with { PathFeasibility = SolverAnswer.Unknown };

        Assert.Equal(("sat", "z3"), Feasibility(satisfiable));
        Assert.Equal(("unknown", "z3"), Feasibility(undecided));
        Assert.Equal(("not-analyzed", null), Feasibility(Structure()));
        Assert.Contains("- Path feasibility: satisfiable: the solver found values on which both paths run", Markdown(satisfiable));
        Assert.Contains("- Path feasibility: unknown: the solver did not decide it", Markdown(undecided));
    }

    /// <summary>A solver that never started is an uncertainty on the finding, not a silence (ADR 0004, TD-095).</summary>
    [Fact]
    public void A_solver_that_could_not_answer_is_an_uncertainty_of_the_finding()
    {
        var reason = SolverRefinement.UnknownUncertainty(UnavailableSolver.NOT_LOADED);
        var finding = Structure() with { PathFeasibility = SolverAnswer.Unknown, Uncertainty = [reason] };

        Assert.Contains($"- Uncertainty: {reason}", Markdown(finding));
        Assert.Contains(reason, Json(finding).GetProperty("uncertainty").EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>And it costs the finding confidence: a pair the solver proved reachable is worth more than one it could not
    /// decide, which is worth less than one it was never asked about (TD-103).</summary>
    [Fact]
    public void An_undecided_solver_costs_the_finding_confidence()
    {
        var scores = new SolverAnswer?[] { SolverAnswer.Sat, null, SolverAnswer.Unknown }
                     .Select(answer => Score(Candidate(AccessOperation.Write, AccessOperation.Write, answer)))
                     .ToArray();

        Assert.Equal([95, 90, 85], scores);
        Assert.True(scores[2] < scores[1], "An answer that decided nothing must not be worth as much as never asking.");
    }

    /// <summary>A partial verdict lowers confidence too: something already treats the resource as shared, so what the analysis
    /// did not see matters more (TD-103).</summary>
    [Fact]
    public void A_verdict_between_the_two_costs_the_finding_confidence()
    {
        var unprotected = Score(Candidate(AccessOperation.Write, AccessOperation.Write, null, PairProtection.UNPROTECTED));
        var partial = Score(Candidate(AccessOperation.Write, AccessOperation.Write, null, PairProtection.PARTIAL));

        Assert.Equal(90, unprotected);
        Assert.Equal(82, partial);
    }

    /// <summary>A pair of compound operations is not answered by a thread-safe collection, and the remediation says so: its
    /// members are atomic one by one, and the gap the sequence leaves is between them (ADR 0010).</summary>
    [Fact]
    public void The_remediation_for_two_sequences_asks_for_one_critical_section()
    {
        var remediation = ReportRenderer.Remediation(
            Cell(ElementSelector.Key("a"), AccessOperation.CompoundOperation, AccessOperation.CompoundOperation));

        Assert.Contains("one critical section", remediation, StringComparison.Ordinal);
        Assert.Contains("thread-safe collection does not help", remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("atomic member", remediation, StringComparison.Ordinal);
    }

    /// <summary>A read against a write needs both sides under one primitive: a lock around the insertion alone leaves a
    /// concurrent read of a plain dictionary unguarded.</summary>
    [Fact]
    public void The_remediation_for_a_read_against_a_write_asks_for_one_primitive_on_both_sides()
    {
        var remediation = ReportRenderer.Remediation(Structure(AccessOperation.Read, AccessOperation.Write));

        Assert.Contains("both the read and the write under one synchronization primitive", remediation, StringComparison.Ordinal);
        Assert.Contains("guarding the write alone", remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("atomic member", remediation, StringComparison.Ordinal);
    }

    /// <summary>Only two plain operations on one cell are answered by an atomic member.</summary>
    [Fact]
    public void The_remediation_for_two_plain_operations_on_one_cell_names_an_atomic_member()
    {
        var remediation = ReportRenderer.Remediation(
            Cell(ElementSelector.Exact(0), AccessOperation.ReadModifyWrite, AccessOperation.Write));

        Assert.Contains("one atomic member", remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("critical section", remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_remediation_reaches_both_the_report_and_the_json()
    {
        var finding = Cell(ElementSelector.Key("a"), AccessOperation.CompoundOperation, AccessOperation.CompoundOperation);

        Assert.Contains($"- Remediation: {ReportRenderer.Remediation(finding)}", Markdown(finding));
        Assert.Equal(ReportRenderer.Remediation(finding), Json(finding).GetProperty("remediation").GetString());
        Assert.EndsWith("verify manually.", ReportRenderer.Remediation(finding), StringComparison.Ordinal);
    }

    private static (string? Result, string? Solver) Feasibility(Finding finding)
    {
        var feasibility = Json(finding).GetProperty("pathFeasibility");
        return (feasibility.GetProperty("result").GetString(), feasibility.GetProperty("solver").GetString());
    }

    private static JsonElement Resource(Finding finding) => Json(finding).GetProperty("resource");

    private static JsonElement Json(Finding finding)
    {
        var rendered = ReportRenderer.Render(ReportingTestData.CreateReport(Analysis(finding))).FindingsJson;
        return JsonDocument.Parse(rendered).RootElement.GetProperty("findings").EnumerateArray().First().Clone();
    }

    private static string Markdown(Finding finding) => ReportRenderer.Render(ReportingTestData.CreateReport(Analysis(finding))).ReportMarkdown;

    private static AnalysisResult Analysis(Finding finding) =>
        FindingTestData.Result([finding], [FindingTestData.Group(finding.GroupId, finding.Confidence.Label, finding.Resource, [finding.FindingId])]);

    /// <summary>The score the rules give a pair of these operations with this solver answer and verdict.</summary>
    private static int Score(AccessPair pair) =>
        ConflictFindings.Create([pair], CancellationToken.None).Findings.Single().Confidence.Score;

    private static AccessPair Candidate(AccessOperation first, AccessOperation second, SolverAnswer? feasibility,
                                        string protection = PairProtection.UNPROTECTED)
    {
        var resource = FindingTestData.Resource(REGION, FIELD);
        return new AccessPair(Access(resource, first, "root-a"), Access(resource, second, "root-b"), protection)
        {
            Feasibility = feasibility
        };
    }

    private const string REGION = "di:Ns.Board@Singleton";
    private const string FIELD = "_slots";

    /// <summary>A finding on the collection itself.</summary>
    private static Finding Structure(AccessOperation first = AccessOperation.Write, AccessOperation second = AccessOperation.Write) =>
        Finding(FindingTestData.Resource(REGION, FIELD), first, second);

    /// <summary>A finding on one cell of it, as the path and the selector carry it (TD-043).</summary>
    private static Finding Cell(ElementSelector selector, AccessOperation first = AccessOperation.Write,
                                AccessOperation second = AccessOperation.Write)
    {
        var resource = FindingTestData.Resource(REGION, FIELD) with { Selector = selector };
        return Finding(resource with { AccessPath = [FIELD, selector.Text] }, first, second);
    }

    private static Finding Finding(AccessResource resource, AccessOperation first, AccessOperation second)
    {
        var accessA = Access(resource, first, "root-a");
        var accessB = Access(resource, second, "root-b");
        var evidence = new[] { "A", "B", "R", "O", "P", "S" }
                       .Select(suffix => new EvidenceItem($"F1.{suffix}", suffix, $"Evidence {suffix}"))
                       .ToArray();
        return new Finding("F1", "fingerprint-F1", "G1", "DCA1001", resource, accessA, accessB, PairProtection.UNPROTECTED,
                           new FindingConfidence("High", 85, new ConfidenceComponents(25, 20, 20, 20, 0)),
                           ["Two roots may run concurrently in one process."],
                           ["A writes the cell", "B writes the cell", "One of the two writes is lost"],
                           [],
                           evidence);
    }

    private static Access Access(AccessResource resource, AccessOperation operation, string rootId) =>
        FindingTestData.Access(resource, operation, rootId, $"Ns.Board.{rootId}()", new SourceSpan("src/Board.cs", 10, 5, 10, 12));
}
