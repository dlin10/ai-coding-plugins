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
        // Two executions of one root and path, folded in turn: the worker's first, which never overlaps itself, then the request's.
        var folding = new AccessFolding();
        folding.Add([folded.View(worker)], 0);
        folding.Add([folded.View(request with { Variant = worker.Variant })], 1);
        var access = Assert.Single(folding.Accesses());

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
        // Each copy an execution of its own, at the position the fold gives the execution of that number.
        var executions = order.Select((variant, position) => (variant switch { 'W' => worker, 'R' => request, _ => second }) with
                                                             {
                                                                 Position = AccessFolding.PositionOf(position, 0)
                                                             })
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

    /// <summary>Two write sites on one field, each access with its own resource object, run by three children of one root, the first
    /// site's before and after the second's, so that either site's execution may come first. A pair reported on its earlier execution's
    /// resource then gives a witness for each side that runs first, and those are two candidates of one pair of roots, which the solver
    /// asks apart; the finding folds them into one occurrence (ADR 0042).</summary>
    [Fact]
    public void Two_sites_with_resource_objects_of_their_own_give_a_candidate_for_each_side_that_runs_first()
    {
        // Each write one call below the children's callee, so that every child reaches it in one instance.
        var execution = Execute(Types + """
            public static class Writes { public static void One() => State.Value = 1; public static void Other() => State.Value = 2; }
            public class VisitsController : ControllerBase
            {
                public void Post() { Task.Run(One); Task.Run(Other); Task.Run(One); }
                private static void One() => Writes.One();
                private static void Other() => Writes.Other();
            }
            """ + Startup());
        var accesses = Collect(execution).Folded.Where(access => access.Resource.Member.Name == "Value").ToArray();
        Assert.Equal(2, accesses.Length);
        var (one, other) = accesses[0].BodyId.EndsWith("One", StringComparison.Ordinal) ? (accesses[0], accesses[1]) : (accesses[1], accesses[0]);
        Assert.False(ReferenceEquals(one.Resource, other.Resource));
        // Three children, three executions in turn: the first and the third reach the first site.
        var order = one.Executions.Concat(other.Executions).OrderBy(made => made.Position).Select(made => made.ExecutionId).ToArray();
        Assert.Equal(3, order.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([order[0], order[2]], one.Executions.Select(made => made.ExecutionId));

        var pairs = InterproceduralPairing.Pair([one, other], execution.Analysis, execution.Heap.Heap);

        var across = pairs.Pairs.Where(pair => pair.First.BodyId != pair.Second.BodyId).ToArray();
        Assert.Equal(["first One | second Other", "second Other | third One"], across.Select(pair => $"{Side(pair.First)} | {Side(pair.Second)}"));
        Assert.Equal(2, across.Select(pair => pair.Candidate).Distinct().Count());
        Assert.Equal(2, across.Select(pair => pair.Resource).Distinct(ReferenceEqualityComparer.Instance).Count());
        // Each site with itself, and the two sites once for each side that runs first.
        Assert.Equal(4, pairs.Candidates);
        var finding = Assert.Single(ConflictFindings.Create(across, CancellationToken.None).Findings);
        Assert.Equal(1, finding.OccurrenceCount);

        string Side(Access side) =>
            $"{(side.ExecutionId == order[0] ? "first" : side.ExecutionId == order[1] ? "second" : "third")} {(side.BodyId == one.BodyId ? "One" : "Other")}";
    }

    /// <summary>An execution has its accesses where the fold first met them, not in the order it made them, and an access it makes from
    /// two path roots at one position, in the ordinal order of the roots' ids (ADR 0042): the order of its copies decides neither, so no
    /// copy has to be kept for it. Positions rise strictly from execution to execution and from access to access within one; the copies
    /// of one execution and one access alone share one.</summary>
    [Fact]
    public void An_execution_has_its_accesses_where_the_fold_first_met_them_and_its_path_roots_in_the_order_of_their_ids()
    {
        var collection = Collect(Types + """
            public static class Second { public static int Count; public static void Mark() => Count = 1; }
            public class FirstController : ControllerBase { public void Post() { Shared.Bump(); Second.Mark(); } }
            """ + Startup());
        var value = Assert.Single(collection.Accesses, access => access.Resource.Member.Name == "Value");
        var count = Assert.Single(collection.Accesses, access => access.Resource.Member.Name == "Count");
        static Access In(Access access, string execution, string pathRoot = "root") =>
            access with { ExecutionId = execution, PathRoot = access.PathRoot with { RootId = pathRoot } };
        var folding = new AccessFolding();

        // The first execution meets the value first; the second makes the count first; the third makes the value from two path roots
        // whose ordinal order is not their order ignoring case, that order first, then the count.
        folding.Add([In(value, "x0"), In(count, "x0")], 0);
        folding.Add([In(count, "x1"), In(value, "x1")], 1);
        folding.Add([In(value, "x2", "root-a"), In(value, "x2", "root-B"), In(count, "x2")], 2);
        var folded = folding.Accesses();

        Assert.Equal(["x0 Value root", "x0 Count root", "x1 Value root", "x1 Count root", "x2 Value root-B", "x2 Value root-a", "x2 Count root"],
                     new AccessExecutionList([folded]).Select(view => $"{view.ExecutionId} {view.Resource.Member.Name} {view.PathRoot.RootId}"));
        var (values, counts) = (folded[0].Executions, folded[1].Executions);
        Assert.Equal(["x0 root", "x1 root", "x2 root-B", "x2 root-a"], values.Select(made => $"{made.ExecutionId} {made.Variant.PathRoot.RootId}"));
        long[] rising = [values[0].Position, counts[0].Position, values[1].Position, counts[1].Position, values[2].Position, counts[2].Position];
        Assert.All(rising.Zip(rising.Skip(1)), step => Assert.True(step.First < step.Second, $"{step.First} < {step.Second}"));
        Assert.Equal(values[2].Position, values[3].Position);
        Assert.Equal(7, folding.ExecutionCount);
    }

    /// <summary>The executions one execution has of one access from one path root, whose variants differ in what they hold, stand in the
    /// order of that content whichever the execution made first (ADR 0042): two folds of the copies made in either order give the same
    /// list and positions, and pairing gives the same witnesses, findings and representatives.</summary>
    [Fact]
    public void Copies_of_one_access_from_one_path_root_in_one_execution_stand_in_the_order_of_their_variants_whichever_was_made_first()
    {
        var execution = Execute(Types + """
            public class VisitsController : ControllerBase { public void Post() => Shared.Bump(); }
            """ + Startup());
        var write = Assert.Single(Collect(execution).Accesses, access => access.Resource.Member.Name == "Value");
        var number = execution.Analysis.Executions.Select(made => made.Id).ToList().IndexOf(write.ExecutionId);
        CodeFlowStep Step(string text) => write.CodeFlow[^1] with { Text = text };
        // Two copies by code flows whose last steps differ, the later text made first in one fold.
        var (zeta, alpha) = (write with { CodeFlow = [.. write.CodeFlow, Step("zeta")] }, write with { CodeFlow = [.. write.CodeFlow, Step("alpha")] });

        var made = Run([zeta, alpha]);
        var other = Run([alpha, zeta]);

        Assert.Equal(["alpha", "zeta"], made.Copies.Select(copy => copy.Flow));
        Assert.Equal(made.Copies, other.Copies);
        Assert.Single(made.Copies.Select(copy => copy.Position).Distinct());
        Assert.NotEmpty(made.Pairs);
        Assert.Equal(made.Pairs, other.Pairs);
        Assert.Equal(made.Findings, other.Findings);

        (IReadOnlyList<(string Flow, long Position)> Copies, IReadOnlyList<string> Pairs, IReadOnlyList<string> Findings) Run(Access[] copies)
        {
            var folding = new AccessFolding();
            folding.Add(copies, number);
            var folded = folding.Accesses();
            var pairs = InterproceduralPairing.Pair(folded, execution.Analysis, execution.Heap.Heap).Pairs;
            var findings = ConflictFindings.Create(pairs, new AccessExecutionList([folded]), CancellationToken.None).Findings;
            return (folded[0].Executions.Select(copy => (copy.Variant.CodeFlow[^1].Text, copy.Position)).ToArray(),
                    pairs.Select(pair => $"{pair.First.CodeFlow[^1].Text} {pair.Second.CodeFlow[^1].Text} {pair.Candidate}").ToArray(),
                    findings.Select(finding => $"{finding.Fingerprint} {finding.AccessA.CodeFlow[^1].Text} {finding.AccessB.CodeFlow[^1].Text}").ToArray());
        }
    }

    /// <summary>A request makes one access from two path roots. Built for one execution each, the two copies are two accesses that the
    /// fold would keep as one, so only their variants tell them apart; pairing them in either order of the list, and pairing the access
    /// the fold makes of them, gives the same pairs in the same order: the path root whose id comes first is the first side of the pair
    /// between them (ADR 0042).</summary>
    [Fact]
    public void Copies_of_one_access_from_two_path_roots_pair_alike_built_for_one_execution_each_in_either_order_and_folded()
    {
        var execution = Execute(Types + """
            public class VisitsController : ControllerBase { public void Post() => Shared.Bump(); }
            """ + Startup());
        var write = Assert.Single(Collect(execution).Accesses, access => access.Resource.Member.Name == "Value");
        var (early, late) = (write with { PathRoot = write.PathRoot with { RootId = "root-a" } }, write with { PathRoot = write.PathRoot with { RootId = "root-b" } });
        var number = execution.Analysis.Executions.Select(made => made.Id).ToList().IndexOf(write.ExecutionId);
        var folding = new AccessFolding();
        folding.Add([late, early], number);

        var lateFirst = Roots(InterproceduralPairing.Pair([late, early], execution.Analysis, execution.Heap.Heap));
        var earlyFirst = Roots(InterproceduralPairing.Pair([early, late], execution.Analysis, execution.Heap.Heap));
        var folded = Roots(InterproceduralPairing.Pair(folding.Accesses(), execution.Analysis, execution.Heap.Heap));

        Assert.Equal(["root-a root-a", "root-a root-b", "root-b root-b"], lateFirst);
        Assert.Equal(lateFirst, earlyFirst);
        Assert.Equal(lateFirst, folded);

        static string[] Roots(PairAnalysis pairs) => pairs.Pairs.Select(pair => $"{pair.First.PathRoot.RootId} {pair.Second.PathRoot.RootId}").ToArray();
    }

    /// <summary>One list two fields hold is one resource named by two paths. A pair of one execution between them has no earlier side,
    /// and the order the fold first met its two accesses is one an earlier execution decides, so the content of the two puts one first
    /// (ADR 0042): whichever access an earlier execution made, the second request's pairs within itself, paired folded as the analysis
    /// pairs, put the alias first, name it, fall into the same candidates and keep their findings' fingerprints.</summary>
    [Fact]
    public void A_pair_of_one_execution_on_a_list_two_fields_hold_is_reported_on_one_path_whichever_access_was_met_first()
    {
        var listFirst = Run("AddList");
        var aliasFirst = Run("AddAlias");

        // The alias's path comes first by its name, so the alias's access is the first side and names the pair, on the list and on its
        // cell, each pair a candidate of its own.
        Assert.Equal(["_alias", "_alias/[?]"], listFirst.Reported);
        Assert.Equal(["AddAlias _alias | AddList 1", "AddAlias _alias/[?] | AddList 1"], listFirst.Within);
        Assert.Equal(listFirst.Reported, aliasFirst.Reported);
        Assert.Equal(listFirst.Within, aliasFirst.Within);
        Assert.Equal(listFirst.Fingerprints, aliasFirst.Fingerprints);

        static (IReadOnlyList<string> Reported, IReadOnlyList<string> Within, IReadOnlyList<string> Fingerprints) Run(string first)
        {
            var run = AnalyzeFolded($$"""
                public sealed class Store
                {
                    private readonly System.Collections.Generic.List<string> _list = new();
                    private readonly System.Collections.Generic.List<string> _alias;
                    public Store() { _alias = _list; }
                    public void AddList(string text) => _list.Add(text);
                    public void AddAlias(string text) => _alias.Add(text);
                }
                public class AController : ControllerBase { private readonly Store _store; public AController(Store store) => _store = store; public void Post() => _store.{{first}}("a"); }
                public class BController : ControllerBase { private readonly Store _store; public BController(Store store) => _store = store; public void Post() { _store.AddAlias("b"); _store.AddList("c"); } }
                """ + Startup("services.AddSingleton<Store>();"));
            // The second request's pairs of the two sites within itself.
            var within = run.Pairs.Pairs.Where(pair => pair.First.ExecutionId == pair.Second.ExecutionId && pair.First.BodyId != pair.Second.BodyId &&
                                                       pair.First.ExecutionId.Contains("BController", StringComparison.Ordinal))
                            .ToArray();
            var identities = within.Select(ConflictFindings.FindingIdentity).ToHashSet(StringComparer.Ordinal);
            var findings = ConflictFindings.Create(run.Pairs.Pairs, run.Collection.Accesses, CancellationToken.None).Findings;
            // Each pair by its sides as it orients them, with how many of the pairs share its candidate.
            return (within.Select(pair => string.Join("/", pair.Resource.AccessPath)).Order(StringComparer.Ordinal).ToArray(),
                    within.Select(pair => $"{Method(pair.First)} {string.Join("/", pair.First.Resource.AccessPath)} | {Method(pair.Second)} " +
                                          $"{within.Count(other => other.Candidate == pair.Candidate)}")
                          .Order(StringComparer.Ordinal).ToArray(),
                    findings.Where(finding => identities.Contains(ConflictFindings.FindingIdentity(finding))).Select(finding => finding.Fingerprint)
                            .Order(StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>A request reaches one list by two paths and spawns work that overlaps itself: its own execution makes the list's access,
    /// the spawned one the alias's and then the list's. Its witnesses under its one pair of path roots are of two executions and of one;
    /// whichever access another request met first, the same witnesses stand, oriented and reported alike and in the same candidates: a
    /// pair of one execution takes its first side from content before any witness is chosen (ADR 0042).</summary>
    [Fact]
    public void Witnesses_of_two_executions_and_of_one_under_one_pair_of_roots_are_the_same_whichever_access_was_met_first()
    {
        var listFirst = Run("AddList");
        var aliasFirst = Run("AddAlias");

        // Of two executions the request's, the earlier, is the first side and names the pair; within the spawned work the alias's path
        // comes first by its name. The two run in opposite orders of the sites, so each is a candidate of its own.
        Assert.Equal(["one spawned AddAlias | spawned AddList on _alias, 1 in its candidate",
                      "one spawned AddAlias | spawned AddList on _alias/[?], 1 in its candidate",
                      "two request AddList | spawned AddAlias on _zlist, 1 in its candidate",
                      "two request AddList | spawned AddAlias on _zlist/[?], 1 in its candidate"], listFirst);
        Assert.Equal(listFirst, aliasFirst);

        static string[] Run(string first)
        {
            var run = AnalyzeFolded($$"""
                public sealed class Store
                {
                    private readonly System.Collections.Generic.List<string> _zlist = new();
                    private readonly System.Collections.Generic.List<string> _alias;
                    public Store() { _alias = _zlist; }
                    public void AddList(string text) => _zlist.Add(text);
                    public void AddAlias(string text) => _alias.Add(text);
                }
                public class AController : ControllerBase { private readonly Store _store; public AController(Store store) => _store = store; public void Post() => _store.{{first}}("a"); }
                public class RController : ControllerBase
                {
                    private readonly Store _store;
                    public RController(Store store) => _store = store;
                    public void Post() { _store.AddList("r"); Task.Run(() => { _store.AddAlias("b"); _store.AddList("c"); }); }
                }
                """ + Startup("services.AddSingleton<Store>();"));
            var roots = run.Pairs.Pairs.Where(pair => pair.First.PathRoot.RootId.Contains("RController", StringComparison.Ordinal) &&
                                                      pair.Second.PathRoot.RootId.Contains("RController", StringComparison.Ordinal) &&
                                                      pair.First.BodyId != pair.Second.BodyId)
                           .ToArray();
            return roots.Select(pair => $"{(pair.First.ExecutionId == pair.Second.ExecutionId ? "one" : "two")} " +
                                        $"{Made(pair.First)} {Method(pair.First)} | {Made(pair.Second)} {Method(pair.Second)} " +
                                        $"on {string.Join("/", pair.Resource.AccessPath)}, {roots.Count(other => other.Candidate == pair.Candidate)} in its candidate")
                        .Order(StringComparer.Ordinal).ToArray();

            string Made(Access access) => run.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.Spawn ? "spawned" : "request";
        }
    }

    /// <summary>One list reached through two holders of one registration in two contexts is one resource identity and one resource
    /// text, but two regions, which a finding's group reads: a request's access of a singleton holder's list, and the same access as a
    /// holder in another context would make it, on that region. A request that makes both has a pair of them within itself; it is
    /// reported on the region the content of the two puts first, whichever of them another request made first, so the folded pairing
    /// gives the same pair and the same group (ADR 0042).</summary>
    [Fact]
    public void A_pair_of_one_execution_on_one_list_in_two_holder_regions_is_reported_on_one_region_whichever_was_met_first()
    {
        var execution = Execute("""
            public sealed class Registry { public readonly System.Collections.Generic.List<string> Items = new(); }
            public sealed class Holder
            {
                private readonly System.Collections.Generic.List<string> _list;
                public Holder(Registry registry) => _list = registry.Items;
                public void Add(string text) => _list.Add(text);
            }
            public class AController : ControllerBase { private readonly Holder _holder; public AController(Holder holder) => _holder = holder; public void Post() => _holder.Add("a"); }
            public class BController : ControllerBase { private readonly Holder _holder; public BController(Holder holder) => _holder = holder; public void Post() => _holder.Add("b"); }
            """ + Startup("services.AddSingleton<Registry>().AddSingleton<Holder>();"));
        var accesses = Collect(execution).Accesses;
        Access Add(string controller) =>
            Assert.Single(accesses, access => access.BodyId.Contains("Holder.Add", StringComparison.Ordinal) && access.Resource.Selector is null &&
                                              access.ExecutionId.Contains(controller, StringComparison.Ordinal));
        var (earlier, holder) = (Add("AController"), Add("BController"));
        var registry = Assert.Single(accesses.Select(access => access.Resource.RegionId).Distinct(), region => region!.Contains("Registry@Singleton", StringComparison.Ordinal))!;
        // The same access as a holder in another context makes it: another instance, whose id comes first, on another region of one
        // resource text, whose id comes after the holder's.
        var other = holder with { InstanceId = "!" + holder.InstanceId, Resource = holder.Resource with { RegionId = registry } };
        Assert.True(AccessResource.SameIdentity(holder.Resource, other.Resource));
        Assert.Equal(ConflictFindings.ResourceText(holder.Resource), ConflictFindings.ResourceText(other.Resource));
        Assert.True(string.CompareOrdinal(holder.Resource.RegionId, other.Resource.RegionId) < 0 && string.CompareOrdinal(other.InstanceId, holder.InstanceId) < 0);

        var holderFirst = Run(holder);
        var otherFirst = Run(other);

        // The region a finding's group reads comes first, before the instance.
        Assert.Equal([$"{holder.InstanceId} | {other.InstanceId} on {holder.Resource.RegionId}"], holderFirst.Within);
        Assert.Equal(holderFirst.Within, otherFirst.Within);
        // The group reads the region the pair is reported on.
        Assert.StartsWith($"{holder.Resource.RegionId} ", Assert.Single(holderFirst.Groups), StringComparison.Ordinal);
        Assert.Equal(holderFirst.Groups, otherFirst.Groups);

        (string[] Within, string[] Groups) Run(Access first)
        {
            var folding = new AccessFolding();
            // The other request makes the access first, by a variant of its own.
            folding.Add([earlier with { InstanceId = first.InstanceId, Resource = first.Resource }], 0);
            folding.Add([holder, other], 1);
            var folded = folding.Accesses();
            var pairs = InterproceduralPairing.Pair(folded, execution.Analysis, execution.Heap.Heap).Pairs;
            var within = pairs.Where(pair => pair.First.ExecutionId == pair.Second.ExecutionId && pair.First.InstanceId != pair.Second.InstanceId).ToArray();
            var (findings, groups) = ConflictFindings.Create(pairs, new AccessExecutionList([folded]), CancellationToken.None);
            var mine = findings.Where(finding => within.Select(ConflictFindings.FindingIdentity).Contains(ConflictFindings.FindingIdentity(finding))).ToArray();
            return (within.Select(pair => $"{pair.First.InstanceId} | {pair.Second.InstanceId} on {pair.Resource.RegionId}").ToArray(),
                    groups.Where(group => mine.Any(finding => finding.GroupId == group.GroupId))
                          .Select(group => $"{group.Resource.RegionId} {group.Fingerprint}: " +
                                           string.Join(", ", findings.Where(finding => group.FindingIds.Contains(finding.FindingId))
                                                                     .Select(finding => $"{finding.Fingerprint} {finding.Resource.RegionId}")))
                          .ToArray());
        }
    }

    /// <summary>Two variants of one access, U before V by content, in two executions: a hosted worker of one instance, which overlaps no
    /// execution of itself, and a request, which overlaps it. The worker's two copies share a position; the one its access lists first,
    /// U, pairs first, so the witness of the two variants is the worker's U with the request's V (ADR 0042).</summary>
    [Fact]
    public void Two_copies_of_one_position_pair_in_the_order_their_access_lists_them()
    {
        var execution = Execute(REQUEST_AND_WORKER + Startup("services.AddHostedService<Worker>();"));
        var accesses = Collect(execution).Accesses;
        var worker = Assert.Single(accesses, access => access.Resource.Member.Name == "Value" && access.ExecutionId.Contains("Worker", StringComparison.Ordinal));
        var request = Assert.Single(accesses, access => access.Resource.Member.Name == "Value" && access.ExecutionId.Contains("Visits", StringComparison.Ordinal));
        // One variant each in both executions: the worker's copy with the code flow's last step told apart, one list each as the
        // collector pools them, made by either execution.
        IReadOnlyList<CodeFlowStep> Flow(string text) => [.. worker.CodeFlow, worker.CodeFlow[^1] with { Text = text }];
        var (u, v) = (Flow("u"), Flow("v"));
        Access Copy(string executionId, IReadOnlyList<CodeFlowStep> flow) => worker with { ExecutionId = executionId, CodeFlow = flow };
        var folding = new AccessFolding();

        folding.Add([Copy(worker.ExecutionId, v), Copy(worker.ExecutionId, u)], 0);
        folding.Add([Copy(request.ExecutionId, v), Copy(request.ExecutionId, u)], 1);
        var pairs = InterproceduralPairing.Pair(folding.Accesses(), execution.Analysis, execution.Heap.Heap).Pairs;

        var across = Assert.Single(pairs, pair => pair.First.CodeFlow[^1].Text != pair.Second.CodeFlow[^1].Text);
        Assert.Equal(["Worker u", "Visits v"], new[] { across.First, across.Second }.Select(side =>
                         $"{(side.ExecutionId.Contains("Worker", StringComparison.Ordinal) ? "Worker" : "Visits")} {side.CodeFlow[^1].Text}"));
    }

    /// <summary>A hosted worker E, which overlaps no execution of itself, spawns work X and writes one field at A, then awaits X through a
    /// library call the analysis cannot see into and writes it at B. X makes A and B too, each under the variant E gives it, so each site
    /// has one variant of two executions. E's A with E's B does not pass; E's A with X's B passes, and X's A with E's B passes with the gap
    /// deciding whether the wait orders E's B. A witness of the two sites takes, of two next executions of one execution, the one whose site
    /// comes first by content, never the one the fold met first (ADR 0042): whichever site E made first, E has A and X has B, and no gap
    /// decides it.</summary>
    [Fact]
    public void A_witness_of_two_sites_gives_each_execution_the_same_site_whichever_site_the_fold_met_first()
    {
        var program = new CallSiteTestProgram("var task = System.Threading.Tasks.Task.Run(Spawned); state.Value = 1; await L.WrapTask(task, state); state.Value = 2;",
                                              members: "private void Spawned() { state.Value = 3; }", worker: true);
        var run = program.Run();
        // The writes of the field; the library call's unknown effect on the state it is handed is no write.
        var writes = run.Collection!.Accesses.Where(access => access.Resource.Member.Name == "Value" && access.Operation == AccessOperation.Write).ToArray();
        var spawned = Assert.Single(writes, access => run.Executions!.Execution(access.ExecutionId).Kind == ExecutionKind.Spawn);
        var sites = writes.Where(access => access.ExecutionId != spawned.ExecutionId).OrderBy(access => access.OperationId).ToArray();
        Assert.Equal(2, sites.Length);
        // One resource object for both, as one reported resource has it: a pair of them gives one witness for each pair of variants,
        // whichever side runs first.
        var (a, b) = (sites[0], sites[1] with { Resource = sites[0].Resource });
        Assert.True(AccessResource.SameIdentity(a.Resource, sites[1].Resource));

        var aFirst = Run(a, b);
        var bFirst = Run(b, a);

        // The worker has A and the spawned work B, and the wait the gap decides orders neither.
        Assert.Equal(["worker A | spawned B: "], aFirst.Across);
        Assert.Equal(aFirst.Across, bFirst.Across);
        // The worker's B against the spawned work's B is the gap's to decide, in both folds alike.
        Assert.Contains(aFirst.Decided, line => line.Contains("WrapTask", StringComparison.Ordinal));
        Assert.Equal(aFirst.Decided, bFirst.Decided);

        (string[] Across, string[] Decided) Run(Access first, Access second)
        {
            var folding = new AccessFolding();
            folding.Add([first, second], 0);
            folding.Add([a with { ExecutionId = spawned.ExecutionId }, b with { ExecutionId = spawned.ExecutionId }], 1);
            var pairs = Marked(run, InterproceduralPairing.Pair(folding.Accesses(), run.Executions!, run.Heap!));
            return (pairs.Pairs.Where(pair => pair.First.OperationId != pair.Second.OperationId)
                         .Select(pair => $"{Side(pair.First)} | {Side(pair.Second)}: {string.Join(", ", pair.DecidingCalls.Select(call => call.Call?.Target))}")
                         .ToArray(),
                    Decided(pairs));
        }

        string Side(Access access) => $"{(access.ExecutionId == spawned.ExecutionId ? "spawned" : "worker")} {(access.OperationId == a.OperationId ? "A" : "B")}";
    }

    /// <summary>On every demo scope, folding the same copies with every execution making its accesses in the reverse of the order the
    /// fold first met them, which changes where the fold first meets them within an execution and nothing between executions, gives the
    /// same found pairs in the same order and candidates, the same skips and assumptions, the same calls gaps decide, the same findings and
    /// groups, and the same order of the solver's budget and witness it asks for each candidate: within one execution the content of the
    /// two sides decides, never the order of first meeting (ADR 0042). Found pairs of one execution whose two accesses the two folds meet
    /// in opposite orders are among those compared.</summary>
    [Fact]
    public async Task Demo_pairs_findings_and_the_solver_order_are_the_same_whichever_order_the_fold_met_accesses_in()
    {
        var (checkedPairs, flipped) = (0, 0);
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            var executions = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
            var collected = input with { Executions = executions };
            var collection = InterproceduralAccesses.Collect(collected, CancellationToken.None);
            var folded = collection.Folded;
            var refolded = Refold(folded);
            var (pairs, again) = (InterproceduralPairing.Pair(folded, executions, input.Heap), InterproceduralPairing.Pair(refolded, executions, input.Heap));
            checkedPairs += pairs.Pairs.Count;
            // The found pairs of one execution between two accesses the two folds meet in opposite orders.
            var (places, otherPlaces) = (Places(folded), Places(refolded));
            flipped += pairs.Pairs.Count(pair => pair.First.ExecutionId == pair.Second.ExecutionId && !AccessFolding.AccessKey.INSTANCE.Equals(pair.First, pair.Second) &&
                                                 places[pair.First] < places[pair.Second] != otherPlaces[pair.First] < otherPlaces[pair.Second]);

            Assert.Equal(pairs.Pairs.Select(PairText), again.Pairs.Select(PairText));
            Assert.Equal(Candidates(pairs.Pairs), Candidates(again.Pairs));
            Assert.Equal((pairs.Comparisons, pairs.Candidates, pairs.Suppressed), (again.Comparisons, again.Candidates, again.Suppressed));
            Assert.Equal(pairs.Skips, again.Skips);
            Assert.Equal(pairs.Assumed, again.Assumed);
            Assert.Equal(Decided(pairs with { Pairs = GapDecisions.Mark(pairs.Pairs, collected, collection.Coverage.Gaps) }),
                         Decided(again with { Pairs = GapDecisions.Mark(again.Pairs, collected, collection.Coverage.Gaps) }));
            Assert.Equal(Describe(ConflictFindings.Create(pairs.Pairs, new AccessExecutionList([folded]), CancellationToken.None)),
                         Describe(ConflictFindings.Create(again.Pairs, new AccessExecutionList([refolded]), CancellationToken.None)));
            Assert.Equal(SolverRefinement.Ordered(pairs.Pairs).Select(index => PairText(pairs.Pairs[index])),
                         SolverRefinement.Ordered(again.Pairs).Select(index => PairText(again.Pairs[index])));
            Assert.Equal(Asked(pairs.Pairs), Asked(again.Pairs));
        });
        Assert.True(checkedPairs > 0);
        Assert.True(flipped > 0, "a found pair of one execution whose accesses the refold met in the other order");

        // Every execution's copies, each execution making the accesses in the reverse of the order the fold first met them.
        static IReadOnlyList<Access> Refold(IReadOnlyList<Access> folded)
        {
            var folding = new AccessFolding();
            var byExecution = folded.SelectMany((access, place) => access.Executions.Select(made => (Execution: (int)(made.Position >> 32), Place: place,
                                                                                                    View: access.View(made))))
                                    .GroupBy(item => item.Execution).OrderBy(group => group.Key);
            // bounded: the executions of one demo scope.
            foreach (var execution in byExecution)
                folding.Add(execution.OrderByDescending(item => item.Place).Select(item => item.View).ToArray(), execution.Key);
            return folding.Accesses();
        }

        // Each folded access's place, by what the fold keeps it as.
        static Dictionary<Access, int> Places(IReadOnlyList<Access> folded) =>
            folded.Select((access, place) => (access, place)).ToDictionary(item => item.access, item => item.place, AccessFolding.AccessKey.INSTANCE);

        // The witness the solver's budget asks for each candidate: its first in the order the budget is spent in.
        static string[] Asked(IReadOnlyList<AccessPair> pairs) =>
            SolverRefinement.Ordered(pairs).Select(index => pairs[index]).DistinctBy(pair => pair.Candidate).Select(PairText).ToArray();

        static string PairText(AccessPair pair) =>
            $"{Side(pair.First)} | {Side(pair.Second)} on {pair.Resource.RegionId} {string.Join("/", pair.Resource.AccessPath)} {pair.Protection}";

        // The found pairs grouped by candidate, whatever number each candidate has.
        static string[] Candidates(IReadOnlyList<AccessPair> pairs) =>
            pairs.GroupBy(pair => pair.Candidate).Select(group => string.Join(" + ", group.Select(PairText))).Order(StringComparer.Ordinal).ToArray();

        static string[] Describe((IReadOnlyList<Finding> Findings, IReadOnlyList<FindingGroup> Groups) made) =>
        [
            .. made.Findings.Select(finding => $"{finding.FindingId} {finding.Fingerprint} {finding.GroupId} {finding.Resource.RegionId} " +
                                               $"{Side(finding.AccessA)} | {Side(finding.AccessB)} {finding.Confidence.Score} " +
                                               $"[{string.Join("; ", finding.Uncertainty)}] [{string.Join("; ", finding.Evidence.Select(item => item.Text))}] " +
                                               string.Join("; ", finding.Occurrences.Select(occurrence => $"{occurrence.RootA.RootId}/{occurrence.RootB.RootId}"))),
            .. made.Groups.Select(group => $"{group.GroupId} {group.Fingerprint} {group.Resource.RegionId} {group.Ownership} " +
                                           $"[{string.Join("; ", group.OwnershipEvidence)}] {string.Join(",", group.FindingIds)}")
        ];

        static string Side(Access access) =>
            $"{access.ExecutionId} {access.BodyId}#{access.OperationId} {access.InstanceId} [{string.Join(",", access.HeldProtection)}] " +
            $"[{string.Join(" / ", access.CodeFlow.Select(step => step.Text))}]";
    }

    /// <summary>On the demo, two findings of one group whose sides start alike stand in the order their sides end and then their
    /// operations, which the report shows, not in the order their pairs were found in, which within one execution follows content
    /// (ADR 0042): a wildcard read whose name sorts first still stands after a read that ends before it.</summary>
    [Fact]
    public async Task Demo_findings_of_a_group_that_start_alike_stand_in_the_order_their_sides_end()
    {
        var demo = await DemoExpectationTests.SharedDemo.Value;
        var compared = 0;
        // bounded: the demo's groups.
        foreach (var group in demo.Findings.GroupBy(finding => finding.GroupId))
        {
            var ordered = group.OrderBy(finding => int.Parse(finding.FindingId[1..], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            // bounded: the neighbouring findings of one group.
            foreach (var (one, other) in ordered.Zip(ordered.Skip(1)).Where(step => Start(step.First) == Start(step.Second)))
            {
                compared++;
                Assert.True(Ends(one, other) <= 0, $"{one.FindingId} ends after {other.FindingId}");
            }
        }

        Assert.True(compared > 0);

        static string Start(Finding finding) =>
            $"{finding.AccessA.PathRoot.RootId}|{finding.AccessA.Operation}|{finding.AccessB.PathRoot.RootId}|{finding.AccessB.Operation}|" +
            $"{finding.AccessA.Source.Path}:{finding.AccessA.Source.StartLine}:{finding.AccessA.Source.StartColumn}|" +
            $"{finding.AccessB.Source.Path}:{finding.AccessB.Source.StartLine}:{finding.AccessB.Source.StartColumn}";

        static int Ends(Finding one, Finding other)
        {
            int result;
            if ((result = one.AccessA.Source.EndLine.CompareTo(other.AccessA.Source.EndLine)) != 0 ||
                (result = one.AccessA.Source.EndColumn.CompareTo(other.AccessA.Source.EndColumn)) != 0 ||
                (result = one.AccessB.Source.EndLine.CompareTo(other.AccessB.Source.EndLine)) != 0 ||
                (result = one.AccessB.Source.EndColumn.CompareTo(other.AccessB.Source.EndColumn)) != 0 ||
                (result = one.AccessA.OperationId.CompareTo(other.AccessA.OperationId)) != 0)
                return result;
            return one.AccessB.OperationId.CompareTo(other.AccessB.OperationId);
        }
    }

    /// <summary>Two variants that differ only in where the items of a list end compare apart, as do two that differ only in a spawn site
    /// or only in a step of a read source's code flow, each either way round: the order is of the items, not of a text made of them
    /// (ADR 0042).</summary>
    [Fact]
    public void Variants_compare_apart_by_every_item_and_where_each_ends()
    {
        var access = Collect(Types + """
            public class VisitsController : ControllerBase { public void Post() => Shared.Bump(); }
            """ + Startup()).Accesses[0];
        var span = new SourceSpan("Case.cs", 1, 1, 1, 2);
        AccessVariant Of(IReadOnlyList<string> path, IReadOnlyList<SpawnSiteLocation>? spawns = null, IReadOnlyList<ReadSource>? reads = null) =>
            new(access.Root, access.PathRoot, path, spawns ?? [], [], reads ?? []);
        static ReadSource Read(string step) => new("x", new SourceSpan("Case.cs", 1, 1, 1, 2), [new CodeFlowStep("call", step, new SourceSpan("Case.cs", 2, 1, 2, 2))]);

        Apart(Of(["p", "q"]), Of(["p\u001fq"]));
        Apart(Of(["p"], spawns: [new SpawnSiteLocation("spawn:a", span)]), Of(["p"], spawns: [new SpawnSiteLocation("spawn:b", span)]));
        Apart(Of(["p"], reads: [Read("a")]), Of(["p"], reads: [Read("b")]));

        static void Apart(AccessVariant one, AccessVariant other)
        {
            var order = PairContent.Compare(one, other);
            Assert.NotEqual(0, order);
            Assert.Equal(-Math.Sign(order), Math.Sign(PairContent.Compare(other, one)));
        }
    }

    /// <summary>On every demo scope, two folded accesses compare equal by content exactly where the fold keeps them as one, and either
    /// way round they compare opposite: the content order a pair of one execution takes its first side from is a total order of the
    /// accesses pairing meets (ADR 0042).</summary>
    [Fact]
    public async Task Demo_accesses_compare_equal_by_content_exactly_where_the_fold_keeps_them_as_one()
    {
        var (compared, wrong) = (0L, new List<string>());
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            var folded = InterproceduralAccesses.Collect(input, CancellationToken.None).Folded;
            // bounded: every pair of a demo scope's folded accesses, once.
            for (var at = 0; at < folded.Count; at++)
            {
                // bounded: the accesses after it.
                for (var other = at + 1; other < folded.Count; other++)
                {
                    compared++;
                    var (order, reverse) = (PairContent.Compare(folded[at], folded[other]), PairContent.Compare(folded[other], folded[at]));
                    if (order == 0 != AccessFolding.AccessKey.INSTANCE.Equals(folded[at], folded[other]) || Math.Sign(order) != -Math.Sign(reverse))
                        wrong.Add($"{folded[at].BodyId}#{folded[at].OperationId} / {folded[other].BodyId}#{folded[other].OperationId}");
                }
            }
        });

        Assert.True(compared > 0);
        Assert.Empty(wrong);
    }

    /// <summary>A whole struct and a field below it are reported on the field whichever runs first, so their witnesses of either order
    /// are one candidate; the candidates' keys and first witnesses are what pairing each pair of copies gives (ADR 0042). Three children
    /// of one root run in turn: the whole struct's, the field's, and the whole struct's again by another path, a variant of its own.</summary>
    [Fact]
    public void A_whole_struct_and_its_field_run_either_way_round_are_one_candidate()
    {
        // Each write one call below the children's callee, so that every child reaches it in one instance.
        var execution = Execute("""
            public struct Point { public int X; public int Y; }
            public static class State { public static Point P; }
            public static class Writes { public static void Whole() => State.P = default; public static void Field() => State.P.X = 1; }
            public class VisitsController : ControllerBase
            {
                public void Post() { Task.Run(Whole); Task.Run(Field); Task.Run(Again); }
                private static void Whole() => Writes.Whole();
                private static void Field() => Writes.Field();
                private static void Again() => Whole();
            }
            """ + Startup());
        var collection = Collect(execution);
        var whole = Assert.Single(collection.Folded, access => access.BodyId.EndsWith("Whole", StringComparison.Ordinal));
        var field = Assert.Single(collection.Folded, access => access.BodyId.EndsWith("Field", StringComparison.Ordinal));
        // Three executions in turn: the first and the third reach the whole struct, the third by another path.
        var order = whole.Executions.Concat(field.Executions).OrderBy(made => made.Position).Select(made => made.ExecutionId).ToArray();
        Assert.Equal(3, order.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([order[0], order[2]], whole.Executions.Select(made => made.ExecutionId));
        Assert.NotEqual(whole.Executions[0].Variant.CallPath, whole.Executions[1].Variant.CallPath);

        var pairs = InterproceduralPairing.Pair([whole, field], execution.Analysis, execution.Heap.Heap);
        var byCopies = ReferencePair([whole, field], execution.Analysis, execution.Heap.Heap);

        var across = pairs.Pairs.Where(pair => pair.First.BodyId != pair.Second.BodyId).ToArray();
        Assert.Equal(["first Whole | second Field", "second Field | third Whole"], across.Select(pair => $"{Side(pair.First)} | {Side(pair.Second)}"));
        Assert.Single(across.Select(pair => pair.Candidate).Distinct());
        Assert.All(across, pair => Assert.Same(field.Resource, pair.Resource));
        var accesses = new Dictionary<Access, int>(AccessFolding.AccessKey.INSTANCE);
        Assert.Equal(DemoExpectationTests.Candidates([byCopies], accesses), DemoExpectationTests.Candidates([pairs], accesses));

        string Side(Access side) =>
            $"{(side.ExecutionId == order[0] ? "first" : side.ExecutionId == order[1] ? "second" : "third")} {(side.BodyId == whole.BodyId ? "Whole" : "Field")}";
    }

    /// <summary>The name of the method an access's body is, without its type and parameters.</summary>
    /// <param name="access">The access.</param>
    private static string Method(Access access)
    {
        var body = access.BodyId.Split('(')[0];
        return body[(body.LastIndexOf('.') + 1)..];
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
