using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What a semantic gap does to a finding's confidence (R6): the checks it decides cost their component, cap the occurrence
/// below High and are named in the uncertainty; a gap that decides nothing changes nothing.</summary>
public sealed class GapConfidenceTests
{
    private const int DECIDED = 10;

    // ---- the checks a gap decides ----

    [Fact]
    public async Task Join_on_a_handle_that_may_be_a_gap_result_decides_the_overlap()
    {
        var finding = Assert.Single(await Findings(JoinCase("Opaque.Factory.Start(work)")));

        Assert.Equal(DECIDED, finding.Confidence.Components.ExecutionOverlap);
        Assert.Equal(("Medium", 79), (finding.Confidence.Label, finding.Confidence.Score));
        Assert.Contains(finding.Uncertainty, item => item.Contains("Opaque.Factory.Start(object)", StringComparison.Ordinal) &&
                                                     item.Contains("overlap check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Join_on_a_handle_from_an_opaque_call_that_is_no_gap_scores_as_before()
    {
        var finding = Assert.Single(await Findings(JoinCase("Opaque.Factory.Start()")));

        Assert.Equal(20, finding.Confidence.Components.ExecutionOverlap);
        Assert.Equal(("High", 90), (finding.Confidence.Label, finding.Confidence.Score));
        Assert.DoesNotContain(finding.Uncertainty, item => item.Contains("unresolved call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Continuation_of_a_task_that_may_be_a_gap_result_decides_the_overlap()
    {
        var finding = Assert.Single(await Findings("""
            public sealed class Work { public int Count; public void Run() => Count = 1; }
            public sealed class Worker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var antecedent = stoppingToken.IsCancellationRequested ? Task.Run(work.Run) : Opaque.Factory.Start(work);
                    antecedent.ContinueWith(_ => work.Count = 2);
                    return Task.CompletedTask;
                }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Worker>();"));

        Assert.Equal(DECIDED, finding.Confidence.Components.ExecutionOverlap);
        Assert.Equal("Medium", finding.Confidence.Label);
        Assert.Contains(finding.Uncertainty, item => item.Contains("continuation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callback_of_a_timer_that_may_be_a_gap_result_decides_the_overlap_of_its_pair_and_its_self_pair()
    {
        var findings = Written(await Findings("""
            public sealed class Work { public int Count; }
            public sealed class Ticker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var known = new System.Timers.Timer();
                    var made = Opaque.Factory.Timer(work);
                    (stoppingToken.IsCancellationRequested ? known : made).Elapsed += (_, _) => work.Count = 1;
                    return Task.CompletedTask;
                }
            }
            public sealed class Writer(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { work.Count = 2; return Task.CompletedTask; }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Ticker>(); services.AddHostedService<Writer>();"));

        var self = Assert.Single(findings, finding => finding.AccessA.Symbol == finding.AccessB.Symbol);
        var other = Assert.Single(findings, finding => finding.AccessA.Symbol != finding.AccessB.Symbol);
        Assert.All(new[] { self, other }, finding =>
        {
            Assert.Equal(DECIDED, finding.Confidence.Components.ExecutionOverlap);
            Assert.Contains(finding.Uncertainty, item => item.Contains("Opaque.Factory.Timer(object)", StringComparison.Ordinal) &&
                                                         item.Contains("periodic", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Lock_whose_identity_is_a_gap_result_decides_the_protection()
    {
        var finding = Assert.Single(Written(await Findings("""
            public sealed class Work { public int Count; }
            public sealed class Locker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var gate = Opaque.Factory.Gate(work);
                    lock (gate) { work.Count = 1; }
                    return Task.CompletedTask;
                }
            }
            public sealed class Writer(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { work.Count = 2; return Task.CompletedTask; }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Locker>(); services.AddHostedService<Writer>();")));

        Assert.Equal(DECIDED, finding.Confidence.Components.Protection);
        Assert.Equal(20, finding.Confidence.Components.ExecutionOverlap);
        Assert.Equal("Medium", finding.Confidence.Label);
        Assert.Contains(finding.Uncertainty, item => item.Contains("Opaque.Factory.Gate(object)", StringComparison.Ordinal) &&
                                                     item.Contains("protection check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lock_whose_identity_is_a_gap_result_handed_on_as_an_argument_decides_the_protection()
    {
        var finding = Assert.Single(Written(await Findings("""
            public sealed class Work { public int Count; }
            public sealed class Locker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Write(work, Opaque.Factory.Gate(work));
                    return Task.CompletedTask;
                }

                private static void Write(Work work, object gate) { lock (gate) { work.Count = 1; } }
            }
            public sealed class Writer(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { work.Count = 2; return Task.CompletedTask; }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Locker>(); services.AddHostedService<Writer>();")));

        Assert.Equal(DECIDED, finding.Confidence.Components.Protection);
        Assert.Equal("Medium", finding.Confidence.Label);
        Assert.Contains(finding.Uncertainty, item => item.Contains("Opaque.Factory.Gate(object)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lock_whose_identity_is_a_gap_result_kept_in_a_field_decides_the_protection()
    {
        var finding = Assert.Single(Written(await Findings("""
            public sealed class Work { public int Count; public object? Gate; }
            public sealed class Locker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    work.Gate = Opaque.Factory.Gate(work);
                    lock (work.Gate) { work.Count = 1; }
                    return Task.CompletedTask;
                }
            }
            public sealed class Writer(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { work.Count = 2; return Task.CompletedTask; }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Locker>(); services.AddHostedService<Writer>();")),
                                    finding => string.Join(".", finding.Resource.AccessPath) == "Count");

        Assert.Equal(DECIDED, finding.Confidence.Components.Protection);
        Assert.Equal("Medium", finding.Confidence.Label);
    }

    [Fact]
    public async Task Join_on_a_handle_kept_in_a_field_that_may_be_a_gap_result_decides_the_overlap()
    {
        var finding = Assert.Single(Written(await Findings("""
            public sealed class Work { public int Count; public Task? Handle; public void Run() => Count = 1; }
            public sealed class Worker(Work work) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    work.Handle = stoppingToken.IsCancellationRequested ? Task.Run(work.Run) : Opaque.Factory.Start(work);
                    await work.Handle;
                    work.Count = 2;
                }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Worker>();")),
                                    finding => string.Join(".", finding.Resource.AccessPath) == "Count");

        Assert.Equal(DECIDED, finding.Confidence.Components.ExecutionOverlap);
        Assert.Equal("Medium", finding.Confidence.Label);
    }

    [Theory]
    [InlineData("var gate = Opaque.Factory.Gate(work); _ = Task.Run(() => { lock (gate) { work.Count = 1; } });")]
    [InlineData("work.Gates[0] = Opaque.Factory.Gate(work); lock (work.Gates[0]) { work.Count = 1; }")]
    [InlineData("work.Gate = Opaque.Factory.Gate(work); ref var gate = ref work.Gate; lock (gate!) { work.Count = 1; }")]
    [InlineData("ref var gate = ref work.Gate; gate = Opaque.Factory.Gate(work); lock (work.Gate!) { work.Count = 1; }")]
    [InlineData("ref var gate = ref work.Gates[0]; gate = Opaque.Factory.Gate(work); lock (work.Gates[0]) { work.Count = 1; }")]
    [InlineData("work.Gates[0] = Opaque.Factory.Gate(work); ref var gate = ref work.Gates[0]; lock (gate) { work.Count = 1; }")]
    public async Task Lock_whose_identity_is_a_gap_result_through_a_capture_a_cell_or_a_reference_decides_the_protection(string locked)
    {
        var finding = Assert.Single(Written(await Findings($$"""
            public sealed class Work { public int Count; public object? Gate; public object[] Gates = new object[1]; }
            public sealed class Locker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{locked}}
                    return Task.CompletedTask;
                }
            }
            public sealed class Writer(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { work.Count = 2; return Task.CompletedTask; }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Locker>(); services.AddHostedService<Writer>();")),
                                    finding => string.Join(".", finding.Resource.AccessPath) == "Count");

        Assert.Equal(DECIDED, finding.Confidence.Components.Protection);
        Assert.Equal("Medium", finding.Confidence.Label);
    }

    [Theory]
    [InlineData("Continue(work);")]
    [InlineData("_ = Task.Run(() => Continue(work));")]
    public async Task Continuation_of_a_gap_result_against_the_rest_of_its_parent_decides_nothing(string start)
    {
        // Whatever the antecedent is, the continuation is never ordered with what its parent, a root or a spawn, does after starting it.
        var finding = Assert.Single(Written(await Findings($$"""
            public sealed class Work { public int Count; }
            public sealed class Worker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{start}}
                    return Task.CompletedTask;
                }

                private static void Continue(Work work)
                {
                    var handle = Opaque.Factory.Start(work);
                    handle.ContinueWith(_ => work.Count = 1);
                    work.Count = 2;
                }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Worker>();")),
                                    finding => string.Join(".", finding.Resource.AccessPath) == "Count");

        Assert.Equal(20, finding.Confidence.Components.ExecutionOverlap);
        Assert.DoesNotContain(finding.Uncertainty, item => item.Contains("continuation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Join_the_waiting_side_runs_ahead_of_decides_nothing()
    {
        // Both writes may meet before the await, however well its handle were known: the gap is only on the path.
        var finding = Assert.Single(Written(await Findings("""
            public sealed class Work { public int Count; public void Run() => Count = 1; }
            public sealed class Worker(Work work) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _ = Task.Run(work.Run);
                    work.Count = 2;
                    await Opaque.Factory.Start(work);
                }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Worker>();")),
                                    finding => string.Join(".", finding.Resource.AccessPath) == "Count");

        Assert.Equal(20, finding.Confidence.Components.ExecutionOverlap);
        Assert.DoesNotContain(finding.Uncertainty, item => item.Contains("overlap check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gap_on_the_call_path_before_an_access_that_decides_no_check_leaves_the_confidence_as_it_was()
    {
        var finding = Assert.Single(Written(await Findings("""
            public sealed class Work { public int Count; }
            public sealed class Starter(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Opaque.Factory.Start(work);
                    work.Count = 1;
                    return Task.CompletedTask;
                }
            }
            public sealed class Writer(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { work.Count = 2; return Task.CompletedTask; }
            }
            """, "services.AddSingleton<Work>(); services.AddHostedService<Starter>(); services.AddHostedService<Writer>();")));

        Assert.Equal(new ConfidenceComponents(25, 20, 20, 20, 5), finding.Confidence.Components);
        Assert.Equal(("High", 90), (finding.Confidence.Label, finding.Confidence.Score));
        Assert.DoesNotContain(finding.Uncertainty, item => item.Contains("unresolved call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Findings_json_shows_the_decided_component_and_the_uncertainty()
    {
        var result = await Analyze(JoinCase("Opaque.Factory.Start(work)"));
        using var json = JsonDocument.Parse(ReportRenderer.Render(ReportingTestData.CreateReport(result)).FindingsJson);

        var finding = Assert.Single(json.RootElement.GetProperty("findings").EnumerateArray());
        var confidence = finding.GetProperty("confidence");
        Assert.Equal(("medium", 79), (confidence.GetProperty("label").GetString(), confidence.GetProperty("score").GetInt32()));
        Assert.Equal(DECIDED, confidence.GetProperty("components").GetProperty("executionOverlap").GetInt32());
        Assert.Contains(finding.GetProperty("uncertainty").EnumerateArray(),
                        item => item.GetString()!.Contains("Opaque.Factory.Start(object)", StringComparison.Ordinal));
    }

    // ---- how occurrences are scored ----

    [Fact]
    public void Finding_takes_its_best_occurrence_after_a_gap_caps_the_other()
    {
        // The occurrence a gap decides would score 85 (a proven path, a decided overlap); capped to 79 it loses to the occurrence no
        // gap decides, which scores 82 on a partial protection.
        var finding = Assert.Single(ConflictFindings.Create(
        [
            Pair("root-a", "root-b", PairProtection.UNPROTECTED, SolverAnswer.Sat, [Overlap]),
            Pair("root-c", "root-d", PairProtection.PARTIAL, null, [])
        ], CancellationToken.None).Findings);

        Assert.Equal(("High", 82), (finding.Confidence.Label, finding.Confidence.Score));
        Assert.Equal(new ConfidenceComponents(25, 20, 20, 12, 5), finding.Confidence.Components);
        Assert.Contains(Overlap.Uncertainty, finding.Uncertainty);
        Assert.Equal(2, finding.OccurrenceCount);
    }

    [Fact]
    public void Finding_whose_every_occurrence_a_gap_decides_is_at_most_Medium()
    {
        var finding = Assert.Single(ConflictFindings.Create(
        [
            Pair("root-a", "root-b", PairProtection.UNPROTECTED, SolverAnswer.Sat, [Overlap]),
            Pair("root-c", "root-d", PairProtection.UNPROTECTED, null, [Protection])
        ], CancellationToken.None).Findings);

        Assert.Equal(("Medium", 79), (finding.Confidence.Label, finding.Confidence.Score));
        Assert.Contains(Overlap.Uncertainty, finding.Uncertainty);
        Assert.Contains(Protection.Uncertainty, finding.Uncertainty);
    }

    [Fact]
    public void Two_decided_components_that_sum_below_Medium_give_Low()
    {
        var finding = Assert.Single(ConflictFindings.Create(
        [
            Pair("root-a", "root-b", PairProtection.PARTIAL, SolverAnswer.Unknown, [Overlap, Protection], wildcard: true)
        ], CancellationToken.None).Findings);

        Assert.Equal(new ConfidenceComponents(10, DECIDED, 20, DECIDED, 0), finding.Confidence.Components);
        Assert.Equal(("Low", 50), (finding.Confidence.Label, finding.Confidence.Score));
    }

    [Fact]
    public void Finding_whose_every_occurrence_has_a_decided_check_is_never_High()
    {
        var checks = new[] { Overlap, Protection, Operation };
        foreach (var protection in new[] { PairProtection.UNPROTECTED, PairProtection.PARTIAL })
        foreach (SolverAnswer? feasibility in new SolverAnswer?[] { SolverAnswer.Sat, SolverAnswer.Unknown, null })
        foreach (var check in checks)
        foreach (var wildcard in new[] { false, true })
        {
            var finding = Assert.Single(ConflictFindings.Create([Pair("root-a", "root-b", protection, feasibility, [check], wildcard)],
                                                                CancellationToken.None).Findings);
            Assert.NotEqual("High", finding.Confidence.Label);
            Assert.True(finding.Confidence.Score <= 79);
        }
    }

    [Fact]
    public void Expected_score_for_the_solver_budget_counts_the_decided_checks()
    {
        Assert.Equal(90, ConflictFindings.ExpectedScore(Pair("root-a", "root-b", PairProtection.UNPROTECTED, null, [])));
        Assert.Equal(79, ConflictFindings.ExpectedScore(Pair("root-a", "root-b", PairProtection.UNPROTECTED, null, [Overlap])));
        Assert.Equal(25 + DECIDED + 20 + DECIDED + 5, ConflictFindings.ExpectedScore(Pair("root-a", "root-b", PairProtection.PARTIAL, null, [Overlap, Protection])));
    }

    // ---- helpers ----

    private static readonly GapCheck Overlap = new("Opaque.Factory.Start(object)", SemanticGapKinds.UNKNOWN_LIBRARY, GapComponents.OVERLAP, "a reason");
    private static readonly GapCheck Protection = new("Opaque.Factory.Gate(object)", SemanticGapKinds.UNKNOWN_LIBRARY, GapComponents.PROTECTION, "a reason");
    private static readonly GapCheck Operation = new("Opaque.Factory.Touch(object)", SemanticGapKinds.UNKNOWN_LIBRARY, GapComponents.OPERATION, "a reason");

    /// <summary>A write pair of one finding: the same two sites under the two roots given.</summary>
    private static AccessPair Pair(string rootA, string rootB, string protection, SolverAnswer? feasibility, IReadOnlyList<GapCheck> checks,
                                   bool wildcard = false)
    {
        var resource = FindingTestData.Resource("static:Counters", "Hits") with { RegionId = "static:Fixture:Counters", IsWildcard = wildcard };
        var first = FindingTestData.Access(resource, AccessOperation.Write, rootA, "OneController.Post()", new SourceSpan("Case.cs", 10, 1, 10, 20));
        var second = FindingTestData.Access(resource, AccessOperation.Write, rootB, "TwoController.Post()", new SourceSpan("Case.cs", 20, 1, 20, 20));
        return new AccessPair(first, second, protection) { Feasibility = feasibility, GapChecks = checks };
    }

    /// <summary>A worker that waits for a task a helper picks from a known spawn of the work and <paramref name="other"/>, then writes
    /// what the work writes: the wait orders the two only when it proves which task it waits for.</summary>
    private static string JoinCase(string other) => $$"""
        public sealed class Work { public int Count; public void Run() => Count = 1; }
        public sealed class Worker(Work work) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                await Pick(stoppingToken.IsCancellationRequested);
                work.Count = 2;
            }

            private Task Pick(bool known) => known ? Task.Run(work.Run) : {{other}};
        }
        """ + Startup("services.AddSingleton<Work>(); services.AddHostedService<Worker>();");

    private static async Task<IReadOnlyList<Finding>> Findings(string source, string registrations) =>
        (await Analyze(source + Startup(registrations))).Findings;

    private static async Task<IReadOnlyList<Finding>> Findings(string source) => (await Analyze(source)).Findings;

    /// <summary>The findings between the two plain writes: the gap call's own unknown effect on the work is a finding of its own
    /// (R1), and not the one whose checks these tests read.</summary>
    private static Finding[] Written(IReadOnlyList<Finding> findings) =>
        findings.Where(finding => !finding.AccessA.Operation.IsUnknownEffect() && !finding.AccessB.Operation.IsUnknownEffect()).ToArray();

    private static Task<AnalysisResult> Analyze(string source) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", Usings + source)),
                                      ROOT_DIRECTORY, CancellationToken.None);

    /// <summary>A library the run has no source of: every member is an opaque call.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            using System.Threading.Tasks;
            namespace Opaque;
            public static class Factory
            {
                public static Task Start(object work) => Task.CompletedTask;
                public static Task Start() => Task.CompletedTask;
                public static System.Timers.Timer Timer(object owner) => new();
                public static object Gate(object owner) => new();
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
