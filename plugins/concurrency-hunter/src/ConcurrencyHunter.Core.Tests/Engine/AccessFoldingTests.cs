using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
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
        Assert.Equal(folded.Comparisons, folded.Skips.Values.Sum() + folded.Suppressed + folded.Pairs.Count);
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

    /// <summary>Each pair of a side's variants gives the first pair of their executions in collection order that may overlap, whether every
    /// variant has one execution, a variant's executions stand next to each other, or the variants alternate: a side keeps the executions
    /// of each variant in collection order whichever way they stand.</summary>
    /// <param name="order">The variant of each execution in collection order: the worker's, the request's, or a second variant of the
    /// request's.</param>
    /// <param name="expected">The variants of the two sides of each candidate, in the order pairing gives them.</param>
    [Theory]
    [InlineData("WRS", "WR WS RR RS SS")]
    [InlineData("RRW", "RR RW")]
    [InlineData("RWR", "RR RW")]
    [InlineData("RWRS", "RR RW RS WS SS")]
    public void Each_pair_of_variants_gives_its_first_pair_of_executions_in_collection_order(string order, string expected)
    {
        var execution = Execute(REQUEST_AND_WORKER + Startup("services.AddHostedService<Worker>();"));
        var folded = Assert.Single(Collect(execution).Folded, access => access.Resource.Member.Name == "Value");
        var worker = Assert.Single(folded.Executions, execution => execution.Variant.Root.Symbol.Contains("Worker", StringComparison.Ordinal));
        var request = Assert.Single(folded.Executions, execution => execution != worker);
        // Another variant object is another variant, with a path of its own to tell it by.
        var second = request with { Variant = request.Variant with { CallPath = [.. request.Variant.CallPath, SECOND] } };
        var executions = order.Select((variant, position) => (variant switch { 'W' => worker, 'R' => request, _ => second }) with { Position = position })
                              .ToArray();

        var pairs = InterproceduralPairing.Pair([folded with { Executions = executions }], execution.Analysis, execution.Heap.Heap).Pairs;

        // The worker never overlaps itself; the request overlaps itself and the worker, in either variant.
        Assert.Equal(expected, string.Join(" ", pairs.Select(pair => $"{Variant(pair.First)}{Variant(pair.Second)}")));

        static char Variant(Access view) =>
            view.Root.Symbol.Contains("Worker", StringComparison.Ordinal) ? 'W' : view.CallPath.LastOrDefault() == SECOND ? 'S' : 'R';
    }

    private static (string, string, string, string)[] Executions(PairAnalysis pairs) =>
        pairs.Pairs.Select(pair => (pair.First.ExecutionId, pair.Second.ExecutionId, pair.Protection, pair.Resource.Identity)).ToArray();
}
