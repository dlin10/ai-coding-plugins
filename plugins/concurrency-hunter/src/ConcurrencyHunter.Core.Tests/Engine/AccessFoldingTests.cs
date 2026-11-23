using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>An access is recorded once with the executions that run it so, and pairing answers each pair of executions as it did when
/// every execution kept its own copy (ADR 0038).</summary>
public sealed class AccessFoldingTests
{
    /// <summary>The write stands one call below the roots' common callee, so every execution reaches it in one instance: an access is one
    /// only within one instance.</summary>
    private const string Types = """
        public static class State { public static int Value; }
        public static class Gates { public static readonly object G = new(); public static readonly object H = new(); }
        public static class Shared { public static void Bump() => Inner.Bump(); }
        public static class Inner { public static void Bump() => State.Value = 1; }

        """;

    /// <summary>The last member of the path of the request's second variant.</summary>
    private const string SECOND = "Second.Variant()";

    /// <summary>The request action of a <see cref="CallSiteTestProgram"/>, and the helper its spawn and timer callback call.</summary>
    private const string GET = "body:Fixture:M:RootController.Get";

    private const string HELPER = "body:Fixture:M:RootController.Helper";

    private const string REQUEST_AND_WORKER = Types + """
        public class VisitsController : ControllerBase { public void Post() => Shared.Bump(); }
        public sealed class Worker : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { Shared.Bump(); return Task.CompletedTask; }
        }
        """;

    [Fact]
    public void One_access_of_two_executions_is_one_access_with_both_executions()
    {
        var collection = Collect(Types + """
            public class FirstController : ControllerBase { public void Post() => Shared.Bump(); }
            public class SecondController : ControllerBase { public void Post() => Shared.Bump(); }
            """ + Startup());

        var access = Assert.Single(collection.Folded, access => access.Resource.Member.Name == "Value");
        Assert.Equal(2, access.Executions.Count);
        Assert.Equal(2, access.ExecutionIds.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(access.Executions[0].Variant.Root.RootId, access.Executions[1].Variant.Root.RootId);
        // Each of its executions still gives the access as that execution made it.
        Assert.Equal(access.ExecutionIds, collection.Accesses.Where(item => item.Resource.Member.Name == "Value").Select(item => item.ExecutionId));
    }

    [Fact]
    public void Executions_that_hold_different_locks_at_one_site_keep_an_access_each()
    {
        var collection = Collect(Types + """
            public class FirstController : ControllerBase { public void Post() { lock (Gates.G) Shared.Bump(); } }
            public class SecondController : ControllerBase { public void Post() { lock (Gates.H) Shared.Bump(); } }
            """ + Startup());

        var accesses = collection.Folded.Where(access => access.Resource.Member.Name == "Value").ToArray();
        Assert.Equal(2, accesses.Length);
        Assert.All(accesses, access => Assert.Single(access.Executions));
        Assert.NotEqual(accesses[0].HeldProtectionIds, accesses[1].HeldProtectionIds);
    }

    [Fact]
    public void A_pair_between_two_executions_of_one_access_is_decided_as_their_two_copies_were()
    {
        var execution = Execute(REQUEST_AND_WORKER + Startup("services.AddHostedService<Worker>();"));
        var collection = Collect(execution);
        var access = Assert.Single(collection.Folded, access => access.Resource.Member.Name == "Value");
        Assert.Equal(2, access.Executions.Count);

        var byCopies = InterproceduralPairing.Pair(collection.Accesses, execution.Analysis, execution.Heap.Heap);
        var folded = InterproceduralPairing.Pair(collection.Folded, execution.Analysis, execution.Heap.Heap);

        // The request with itself and with the worker; the hosted worker never overlaps itself.
        Assert.Equal(2, folded.Pairs.Count);
        Assert.Equal(Executions(byCopies), Executions(folded));
        Assert.Equal(1, byCopies.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_NO_OVERLAP));
        // The one access is compared with itself once and stands with two candidates, counted as two comparisons, where its three pairs
        // of executions were three; every comparison is still a skip, a suppression or a candidate.
        Assert.Equal(byCopies.Comparisons - 1, folded.Comparisons);
        Assert.Equal(folded.Comparisons, folded.Skips.Values.Sum() + folded.Suppressed + folded.Candidates);
    }

    [Fact]
    public void An_occurrence_is_represented_by_the_first_pair_of_executions_that_may_overlap()
    {
        var execution = Execute(REQUEST_AND_WORKER + Startup("services.AddHostedService<Worker>();"));
        var folded = Assert.Single(Collect(execution).Folded, access => access.Resource.Member.Name == "Value");
        var worker = Assert.Single(folded.Executions, execution => execution.Variant.Root.Symbol.Contains("Worker", StringComparison.Ordinal));
        var request = Assert.Single(folded.Executions, execution => execution != worker);
        // Two executions of one root and path: the worker's first, which never overlaps itself, then the request's.
        var access = folded with { Executions = [worker with { Position = 0 }, request with { Variant = worker.Variant, Position = 1 }] };

        var pairs = InterproceduralPairing.Pair([access], execution.Analysis, execution.Heap.Heap).Pairs;
        var byCopies = InterproceduralPairing.Pair([access.View(access.Executions[0]), access.View(access.Executions[1])], execution.Analysis, execution.Heap.Heap).Pairs;

        var pair = Assert.Single(pairs);
        Assert.Equal((worker.ExecutionId, request.ExecutionId), (pair.First.ExecutionId, pair.Second.ExecutionId));
        // The two copies give that pair and the request with itself, one occurrence of one finding, which keeps the first of them.
        Assert.Equal(2, byCopies.Count);
        var finding = Assert.Single(ConflictFindings.Create(pairs, CancellationToken.None).Findings);
        var expected = Assert.Single(ConflictFindings.Create(byCopies, CancellationToken.None).Findings);
        Assert.Equal((expected.AccessA.ExecutionId, expected.AccessB.ExecutionId), (finding.AccessA.ExecutionId, finding.AccessB.ExecutionId));
        Assert.Equal(expected.OccurrenceCount, finding.OccurrenceCount);
        Assert.Equal(expected.Fingerprint, finding.Fingerprint);
    }

    /// <summary>Each pair of a side's variants gives a witness, the first pair of their executions in collection order that may overlap,
    /// whether every variant has one execution, a variant's executions stand next to each other, or the variants alternate: a side keeps
    /// the executions of each variant in collection order whichever way they stand. The witnesses of one pair of path roots are one
    /// candidate, whichever of the request's paths they take (ADR 0042).</summary>
    /// <param name="order">The variant of each execution in collection order: the worker's, the request's, or a second variant of the
    /// request's.</param>
    /// <param name="expected">The variants of the two sides of each witness, in the order pairing gives them.</param>
    /// <param name="candidates">The candidate of each witness, lettered in the order the witnesses first name them.</param>
    [Theory]
    [InlineData("WRS", "WR WS RR RS SS", "a a b b b")]
    [InlineData("RRW", "RR RW", "a b")]
    [InlineData("RWR", "RR RW", "a b")]
    [InlineData("RWRS", "RR RW RS WS SS", "a b a b a")]
    public void Each_pair_of_variants_gives_its_first_pair_of_executions_in_collection_order(string order, string expected, string candidates)
    {
        var execution = Execute(REQUEST_AND_WORKER + Startup("services.AddHostedService<Worker>();"));
        var folded = Assert.Single(Collect(execution).Folded, access => access.Resource.Member.Name == "Value");
        var worker = Assert.Single(folded.Executions, execution => execution.Variant.Root.Symbol.Contains("Worker", StringComparison.Ordinal));
        var request = Assert.Single(folded.Executions, execution => execution != worker);
        // Another variant object is another variant, with a path of its own to tell it by.
        var second = request with { Variant = request.Variant with { CallPath = [.. request.Variant.CallPath, SECOND] } };
        var executions = order.Select((variant, position) => (variant switch { 'W' => worker, 'R' => request, _ => second }) with { Position = position })
                              .ToArray();

        var analysis = InterproceduralPairing.Pair([folded with { Executions = executions }], execution.Analysis, execution.Heap.Heap);
        var pairs = analysis.Pairs;

        // The worker never overlaps itself; the request overlaps itself and the worker, in either variant.
        Assert.Equal(expected, string.Join(" ", pairs.Select(pair => $"{Variant(pair.First)}{Variant(pair.Second)}")));
        var letters = pairs.Select(pair => pair.Candidate).Distinct().ToArray();
        Assert.Equal(candidates, string.Join(" ", pairs.Select(pair => (char)('a' + Array.IndexOf(letters, pair.Candidate)))));
        Assert.Equal(letters.Length, analysis.Candidates);
        // A pair that stands counts a comparison for each candidate, not each witness.
        Assert.Equal(analysis.Comparisons, analysis.Skips.Values.Sum() + analysis.Suppressed + analysis.Candidates);

        static char Variant(Access view) =>
            view.Root.Symbol.Contains("Worker", StringComparison.Ordinal) ? 'W' : view.CallPath.LastOrDefault() == SECOND ? 'S' : 'R';
    }

    /// <summary>A spawn and a timer callback of one root reach one helper. Against the root's own write the two give a witness each, which
    /// are one candidate, and the timer's, whose timer a semantic gap returns, keeps the call the gap decides (ADR 0042). The calls gaps
    /// decide, the library bodies they admit, and the finding's score, label and gap callees are what pairing each pair of copies gives;
    /// the score is the spawn's, the gap callee the timer's.</summary>
    [Fact]
    public void A_spawn_and_a_timer_callback_of_one_root_are_one_candidate_that_keeps_what_a_gap_decides_for_each()
    {
        // The timer is the gap's result, and the gap is handed a holder of its own, so the gap decides only the pairs the callback runs in.
        var program = new CallSiteTestProgram(
            "var timer = L.Timer(holder); timer.Elapsed += (_, _) => First(); timer.Start(); System.Threading.Tasks.Task.Run(Second); state.Value = 0;",
            members: "private void First() => Helper(); private void Second() => Helper(); private void Helper() { state.Value++; }",
            extra: "public sealed class Holder { public int Count; }", injectHolder: true);
        var run = program.Run();
        var indexed = Marked(run, InterproceduralPairing.Pair(run.Collection!.Folded, run.Executions!, run.Heap!));
        var byCopies = Marked(run, ReferencePair(run.Collection.Accesses, run.Executions!, run.Heap!));

        var against = indexed.Pairs.Where(pair => Sites(pair) == (GET, HELPER)).ToArray();
        Assert.Equal([ExecutionKind.Spawn, ExecutionKind.TimerCallback],
                     against.Select(pair => run.Executions!.Execution(pair.Second.ExecutionId).Kind));
        Assert.Single(against.Select(pair => pair.Candidate).Distinct());
        Assert.Equal([false, true], against.Select(pair => pair.DecidingCalls.Count > 0));
        Assert.Equal(Decided(byCopies), Decided(indexed));

        using var compilations = new CallSiteCompilations();
        using var referenceCompilations = new CallSiteCompilations();
        var (rounds, reference) = (program.Rounds(compilations), program.Rounds(referenceCompilations));
        Assert.True(rounds.Grow(run, indexed, 1));
        Assert.True(reference.Grow(run, byCopies, 1));
        Assert.Equal(reference.Admission.BodyIds.Order(StringComparer.Ordinal), rounds.Admission.BodyIds.Order(StringComparer.Ordinal));
        Assert.Contains(rounds.Admission.BodyIds, body => body.Contains("L.Timer", StringComparison.Ordinal));

        var finding = Assert.Single(ConflictFindings.Create(indexed.Pairs, CancellationToken.None).Findings, finding => Sites(finding) == (GET, HELPER));
        var expected = Assert.Single(ConflictFindings.Create(byCopies.Pairs, CancellationToken.None).Findings, finding => Sites(finding) == (GET, HELPER));
        Assert.Equal(expected.Confidence, finding.Confidence);
        Assert.Equal(expected.GapCallees, finding.GapCallees);
        Assert.Equal(expected.Uncertainty, finding.Uncertainty);
        Assert.Equal("High", finding.Confidence.Label);
        Assert.Equal(["L.Timer(object)"], finding.GapCallees);
        Assert.Contains(finding.Uncertainty, line => line.StartsWith("The unresolved call L.Timer(object)", StringComparison.Ordinal));
        Assert.Equal(1, finding.OccurrenceCount);
    }

    /// <summary>One root reaches a write by two paths, one under a lock and one not, against an unlocked write of another root. The two
    /// pairs of accesses are one occurrence, which shows the unprotected one, and the finding is scored over both, as when each path was
    /// an occurrence of its own (ADR 0042, TD-039).</summary>
    [Fact]
    public void A_root_that_reaches_a_write_locked_and_unlocked_is_one_occurrence_scored_over_both_paths()
    {
        var execution = Execute(Types + """
            public class VisitsController : ControllerBase
            {
                public void Post() { Task.Run(Locked); Task.Run(Plain); }
                private static void Locked() { lock (Gates.G) Inner.Bump(); }
                private static void Plain() => Inner.Bump();
            }
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { State.Value = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<Worker>();"));
        var collection = Collect(execution);
        var pairs = InterproceduralPairing.Pair(collection.Folded, execution.Analysis, execution.Heap.Heap).Pairs;
        var byCopies = ReferencePair(collection.Accesses, execution.Analysis, execution.Heap.Heap).Pairs;

        var against = pairs.Where(IsAgainstWorker).ToArray();
        Assert.Equal([PairProtection.PARTIAL, PairProtection.UNPROTECTED], against.Select(pair => pair.Protection));
        var scores = against.Select(pair => Assert.Single(ConflictFindings.Create([pair], CancellationToken.None).Findings).Confidence.Score).ToArray();
        Assert.NotEqual(scores[0], scores[1]);

        var finding = Assert.Single(ConflictFindings.Create(pairs, CancellationToken.None).Findings, FindsWorker);
        var expected = Assert.Single(ConflictFindings.Create(byCopies, CancellationToken.None).Findings, FindsWorker);
        Assert.Equal(1, finding.OccurrenceCount);
        Assert.Equal(scores.Max(), finding.Confidence.Score);
        Assert.Equal(expected.Confidence, finding.Confidence);
        Assert.Equal((PairProtection.UNPROTECTED, PairProtection.UNPROTECTED), (finding.ProtectionResult, expected.ProtectionResult));
        Assert.Contains("VisitsController.Plain()", finding.AccessA.CallPath.Concat(finding.AccessB.CallPath));
        Assert.Equal(expected.Fingerprint, finding.Fingerprint);

        static bool IsAgainstWorker(AccessPair pair) => pair.First.BodyId.Contains("Worker", StringComparison.Ordinal) ||
                                                        pair.Second.BodyId.Contains("Worker", StringComparison.Ordinal);
        static bool FindsWorker(Finding finding) => finding.AccessA.BodyId.Contains("Worker", StringComparison.Ordinal) ||
                                                    finding.AccessB.BodyId.Contains("Worker", StringComparison.Ordinal);
    }

    /// <summary>Two write sites on one field, each access with its own resource object, run by two children of one root, each child twice,
    /// so that either site's execution may come first. A pair reported on its earlier execution's resource then gives a witness for each
    /// side that runs first, and those are two candidates of one pair of roots, which the solver asks apart; the finding folds them into
    /// one occurrence (ADR 0042).</summary>
    [Fact]
    public void Two_sites_with_resource_objects_of_their_own_give_a_candidate_for_each_side_that_runs_first()
    {
        var execution = Execute(Types + """
            public class VisitsController : ControllerBase
            {
                public void Post() { Task.Run(One); Task.Run(Other); }
                private static void One() => State.Value = 1;
                private static void Other() => State.Value = 2;
            }
            """ + Startup());
        var accesses = Collect(execution).Folded.Where(access => access.Resource.Member.Name == "Value").ToArray();
        Assert.Equal(2, accesses.Length);
        Assert.False(ReferenceEquals(accesses[0].Resource, accesses[1].Resource));
        // The first site's child runs before and after the second's.
        var (one, other) = (accesses[0], accesses[1]);
        one = one with { Executions = [one.Executions[0] with { Position = 0 }, one.Executions[0] with { Position = 2 }] };
        other = other with { Executions = [other.Executions[0] with { Position = 1 }] };

        var pairs = InterproceduralPairing.Pair([one, other], execution.Analysis, execution.Heap.Heap);

        var across = pairs.Pairs.Where(pair => pair.First.BodyId != pair.Second.BodyId).ToArray();
        Assert.Equal([one.BodyId, other.BodyId], across.Select(pair => pair.First.BodyId));
        Assert.Equal(2, across.Select(pair => pair.Candidate).Distinct().Count());
        Assert.Equal(2, across.Select(pair => pair.Resource).Distinct(ReferenceEqualityComparer.Instance).Count());
        // Each site with itself, and the two sites once for each side that runs first.
        Assert.Equal(4, pairs.Candidates);
        var finding = Assert.Single(ConflictFindings.Create(across, CancellationToken.None).Findings);
        Assert.Equal(1, finding.OccurrenceCount);
    }

    /// <summary>A whole struct and a field below it are reported on the field whichever runs first, so their witnesses of either order
    /// are one candidate; the candidates' keys and first witnesses are what pairing each pair of copies gives (ADR 0042).</summary>
    [Fact]
    public void A_whole_struct_and_its_field_run_either_way_round_are_one_candidate()
    {
        var execution = Execute("""
            public struct Point { public int X; public int Y; }
            public static class State { public static Point P; }
            public class VisitsController : ControllerBase
            {
                public void Post() { Task.Run(Whole); Task.Run(Field); }
                private static void Whole() => State.P = default;
                private static void Field() => State.P.X = 1;
            }
            """ + Startup());
        var collection = Collect(execution);
        var whole = Assert.Single(collection.Folded, access => access.BodyId.EndsWith("Whole", StringComparison.Ordinal));
        var field = Assert.Single(collection.Folded, access => access.BodyId.EndsWith("Field", StringComparison.Ordinal));
        // The whole struct's child runs before the field's, and again after it by another path, a variant of its own.
        var made = whole.Executions[0];
        var later = made with { Position = 2, Variant = made.Variant with { CallPath = [.. made.Variant.CallPath, SECOND] } };
        whole = whole with { Executions = [made with { Position = 0 }, later] };
        field = field with { Executions = [field.Executions[0] with { Position = 1 }] };

        var pairs = InterproceduralPairing.Pair([whole, field], execution.Analysis, execution.Heap.Heap);
        var byCopies = ReferencePair([whole, field], execution.Analysis, execution.Heap.Heap);

        var across = pairs.Pairs.Where(pair => pair.First.BodyId != pair.Second.BodyId).ToArray();
        Assert.Equal([whole.BodyId, field.BodyId], across.Select(pair => pair.First.BodyId));
        Assert.Single(across.Select(pair => pair.Candidate).Distinct());
        Assert.All(across, pair => Assert.Same(field.Resource, pair.Resource));
        var accesses = new Dictionary<Access, int>(AccessFolding.AccessKey.INSTANCE);
        Assert.Equal(DemoExpectationTests.Candidates([byCopies], accesses), DemoExpectationTests.Candidates([pairs], accesses));
    }

    /// <summary>The bodies of a pair's two sides, in text order.</summary>
    /// <param name="pair">The pair.</param>
    private static (string, string) Sites(AccessPair pair) => Sites(pair.First.BodyId, pair.Second.BodyId);

    /// <summary>The bodies of a finding's two sites, in text order.</summary>
    /// <param name="finding">The finding.</param>
    private static (string, string) Sites(Finding finding) => Sites(finding.AccessA.BodyId, finding.AccessB.BodyId);

    private static (string, string) Sites(string one, string other) =>
        string.CompareOrdinal(one, other) <= 0 ? (one, other) : (other, one);

    /// <summary>The pairs with what a semantic gap decides for each, as the analysis marks them.</summary>
    /// <param name="run">The scope's run.</param>
    /// <param name="pairs">Its pairs.</param>
    private static PairAnalysis Marked(ScopeRun run, PairAnalysis pairs) =>
        pairs with { Pairs = GapDecisions.Mark(pairs.Pairs, run.Interprocedural!, run.Collection!.Coverage.Gaps) };

    /// <summary>Each call a gap decides for a pair, with the pair's two sites and roots, once.</summary>
    /// <param name="pairs">The marked pairs.</param>
    private static string[] Decided(PairAnalysis pairs) =>
        pairs.Pairs.SelectMany(pair => pair.DecidingCalls.Select(call => $"{Sites(pair)} {pair.First.PathRoot.RootId} {pair.Second.PathRoot.RootId} " +
                                                                         $"{call.Call?.Target} {call.Check.Reason}"))
             .Distinct(StringComparer.Ordinal)
             .Order(StringComparer.Ordinal)
             .ToArray();

    private static (string, string, string, string)[] Executions(PairAnalysis pairs) =>
        pairs.Pairs.Select(pair => (pair.First.ExecutionId, pair.Second.ExecutionId, pair.Protection, pair.Resource.Identity)).ToArray();
}
