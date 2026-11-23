using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The reach of an unknown effect (R1) worked out once per strongly connected component (#468): every start reaches what its
/// own walk reached, a call what its starts' walks or its one walk reached, the regions are numbered in the order those walks numbered
/// them, as many distinct answers are kept, a component passes over the sets a set it took holds already, a reach that let go of its
/// walks answers as before, and the search for gaps after access collection walks nothing.</summary>
public sealed class UnknownReachComponentTests
{
    [Fact]
    public async Task Each_start_of_the_demo_reaches_what_its_own_walk_reached_and_regions_are_numbered_as_it_numbered_them() =>
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            var calls = UnknownCalls.Of(input.Scope, input.Heap);
            Assert.NotEmpty(calls);
            CompareInCallOrder(input.Scope, input.Heap, calls);
        });

    [Fact]
    public void Each_start_on_a_heap_with_cycles_reaches_what_its_own_walk_reached_whatever_the_order_of_the_starts()
    {
        var input = Input(Execute(Cycles()));
        var (scope, heap) = (input.Scope, input.Heap);
        var calls = UnknownCalls.Of(scope, heap);
        var reference = new WalkPerStart(scope, heap);
        var starts = calls.SelectMany(reference.StartsOf).Distinct().ToArray();
        var walks = starts.Select(start => (Start: start, Walk: reference.WalkOf(start.Region, start.DeclaringTypeKey))).ToArray();

        // The fixture has what the walk of components must get right: a start in a cycle, a receiver seen through its declaring type
        // that its own walk meets again and that is a start seen whole too, one that is not met again, and a call walked whole.
        Assert.Contains(starts, start => reference.OnCycle(start.Region));
        Assert.Contains(walks, walk => walk.Walk.MetItself && starts.Contains((walk.Start.Region, null)));
        Assert.Contains(walks, walk => walk.Start.DeclaringTypeKey is not null && !walk.Walk.MetItself && walk.Walk.Reached.Count != 0);
        Assert.Contains(calls, call => reference.IsWalkedWhole(call));

        CompareInCallOrder(scope, heap, calls);
        // A receiver asked before any walk of components met it, and after one did.
        CompareInOrder(scope, heap, starts.OrderBy(start => start.DeclaringTypeKey is null).ToArray());
        CompareInOrder(scope, heap, starts.OrderBy(start => start.DeclaringTypeKey is not null).ToArray());
    }

    [Fact]
    public void A_reach_that_let_go_of_its_walks_answers_every_start_and_call_as_their_own_walks_did()
    {
        var input = Input(Execute(Cycles()));
        var (scope, heap) = (input.Scope, input.Heap);
        var calls = UnknownCalls.Of(scope, heap);
        var starts = calls.SelectMany(new WalkPerStart(scope, heap).StartsOf).Distinct().ToArray();

        // Half the calls or starts asked, the walks let go of, then everything asked: the first half again and the rest anew.
        CompareInCallOrder(scope, heap, [.. calls.Take(calls.Count / 2), .. calls], releaseAfter: calls.Count / 2);
        CompareInOrder(scope, heap, [.. starts.Take(starts.Length / 2), .. starts], releaseAfter: starts.Length / 2);
    }

    [Fact]
    public void A_component_passes_over_the_sets_a_set_it_took_holds_already_and_reaches_the_same()
    {
        var input = Input(Execute(Dense()));
        var calls = UnknownCalls.Of(input.Scope, input.Heap);

        var counts = CompareInCallOrder(input.Scope, input.Heap, calls);
        Assert.NotEqual(0, counts["unions"]);
        Assert.NotEqual(0, counts["skippedUnions"]);
    }

    [Fact]
    public async Task The_search_after_collection_walks_no_start_the_search_before_walked_and_finds_the_gaps_a_reach_of_its_own_finds()
    {
        var input = Input(Execute(Cycles()));
        var (walked, _) = CompareSharedSearches(input);
        // Some components of the fixture reach sets no answer is: a reach that asked the search's calls and kept its walks holds more
        // than the one the search let go of them in.
        var kept = new UnknownCalls.Reach(input.Scope, input.Heap);
        foreach (var call in UnknownCalls.Of(input.Scope, input.Heap).Where(call => call.Kind != SemanticGapKinds.RECOGNIZED))
            kept.BitsOf(call);
        Assert.InRange(walked["reachWords"], 0, Counted(kept.Counts())["reachWords"] - 1);
        await ParallelCollectionTests.ForEachDemoScope(scope => CompareSharedSearches(scope));
    }

    /// <summary>Asks a reach and the walks per start about each call in turn, then about each of its starts, and compares the answers,
    /// the numbering and how many distinct answers there are.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="heap">The solved heap.</param>
    /// <param name="calls">The unknown calls, in the order they are asked about.</param>
    /// <param name="releaseAfter">How many calls are asked before the reach lets go of its walks, or -1 for never.</param>
    /// <returns>The reach's counters at the end.</returns>
    private static Dictionary<string, long> CompareInCallOrder(ScopeProgram scope, HeapSolution heap, IReadOnlyList<UnknownCall> calls,
                                                               int releaseAfter = -1)
    {
        var reach = new UnknownCalls.Reach(scope, heap);
        var reference = new WalkPerStart(scope, heap);
        for (var at = 0; at < calls.Count; at++)
        {
            if (at == releaseAfter)
                reach.ReleaseWalks();
            Assert.Equal(reference.Of(calls[at]), reach.Of(calls[at]).Regions.Order(StringComparer.Ordinal));
            foreach (var (region, declaringTypeKey) in reference.StartsOf(calls[at]))
                Assert.Equal(reference.WalkOf(region, declaringTypeKey).Reached.Order(StringComparer.Ordinal),
                             reach.OfStart(region, declaringTypeKey).Order(StringComparer.Ordinal));
        }
        Assert.Equal(reference.Numbered, Enumerable.Range(0, reach.Numbered).Select(reach.RegionId));
        var counts = Counted(reach.Counts());
        Assert.Equal(reference.DistinctAnswers, counts["distinctReaches"]);
        return counts;
    }

    /// <summary>Asks a reach and the walks per start about starts in a given order, and compares the answers, the numbering and how
    /// many distinct answers there are.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="heap">The solved heap.</param>
    /// <param name="starts">The starts, in the order they are asked about.</param>
    /// <param name="releaseAfter">How many starts are asked before the reach lets go of its walks, or -1 for never.</param>
    private static void CompareInOrder(ScopeProgram scope, HeapSolution heap, IReadOnlyList<(string Region, string? DeclaringTypeKey)> starts,
                                       int releaseAfter = -1)
    {
        var reach = new UnknownCalls.Reach(scope, heap);
        var reference = new WalkPerStart(scope, heap);
        for (var at = 0; at < starts.Count; at++)
        {
            if (at == releaseAfter)
                reach.ReleaseWalks();
            var (region, declaringTypeKey) = starts[at];
            Assert.Equal(reference.WalkOf(region, declaringTypeKey).Reached.Order(StringComparer.Ordinal),
                         reach.OfStart(region, declaringTypeKey).Order(StringComparer.Ordinal));
        }
        Assert.Equal(reference.Numbered, Enumerable.Range(0, reach.Numbered).Select(reach.RegionId));
        Assert.Equal(reference.DistinctAnswers, Counted(reach.Counts())["distinctReaches"]);
    }

    /// <summary>Searches a scope for gaps before and after collection over one reach, as the collection does, and compares each search
    /// with one over a reach of its own; the second search walks nothing, a call walked whole included, and the reach keeps no more
    /// than after the first.</summary>
    /// <param name="input">The scope's input.</param>
    /// <returns>The reach's counters after the first search and after the second.</returns>
    private static (Dictionary<string, long> Walked, Dictionary<string, long> Again) CompareSharedSearches(InterproceduralInput input)
    {
        var accesses = InterproceduralAccesses.Collect(input, CancellationToken.None).Folded;
        var reach = new UnknownCalls.Reach(input.Scope, input.Heap);
        var before = SemanticGaps.Find(input, reach: reach);
        var walked = Counted(reach.Counts());
        var after = SemanticGaps.Find(input, accesses, reach);
        var again = Counted(reach.Counts());

        Assert.Equal(Describe(SemanticGaps.Find(input)), Describe(before));
        Assert.Equal(Describe(SemanticGaps.Find(input, accesses)), Describe(after));
        Assert.NotEqual(0, walked["calls"]);
        foreach (var unchanged in (string[])["startWalks", "components", "distinctReaches", "visits", "edges", "restrictedVisits", "unions",
                                              "skippedUnions", "unionWords"])
            Assert.Equal(walked[unchanged], again[unchanged]);
        Assert.Equal(2 * walked["calls"], again["calls"]);
        Assert.Equal(2 * walked["wholeCallWalks"], again["wholeCallWalks"]);
        Assert.InRange(again["reachWords"], 0, walked["reachWords"]);
        return (walked, again);
    }

    /// <summary>The counters of a reach's <c>Counts</c> line by name.</summary>
    /// <param name="counts">The line.</param>
    private static Dictionary<string, long> Counted(string counts) =>
        counts.Split(' ').Select(item => item.Split('=')).ToDictionary(pair => pair[0], pair => long.Parse(pair[1]), StringComparer.Ordinal);

    /// <summary>Each gap with its kind, materiality and sites.</summary>
    /// <param name="gaps">The gaps.</param>
    private static string[] Describe(IReadOnlyList<SemanticGap> gaps) =>
        gaps.Select(gap => $"{gap.Callee} {gap.Kind} roots={gap.Roots} regions={gap.Regions} sites={gap.CallSites} " +
                           string.Join(",", gap.Sites.Select(site => $"{site.BodyId}#{site.OperationId}")))
            .ToArray();

    /// <summary>A singleton <c>Ring</c> of three nodes, the first of which points back to the ring and holds a tag, a list holding the
    /// second, and a delegate capturing the ring; a worker hands the ring, its first node, that node's leaf and the delegate to unknown
    /// code, and calls members without a body that see the first node through its base type, which declares neither its leaf nor its
    /// tag, and through its own type, the ring and the leaf through theirs. Between the two calls on the first node it hands unknown
    /// code a singleton <c>Side</c> the ring does not reach, so a region numbered out of turn shows in the order of numbering, and the
    /// nodes' leaf, so a walk of components meets a component an earlier walk completed.</summary>
    private static string Cycles() => """
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;

        public sealed class Leaf
        {
            public int Count;
            public string Name = "leaf";

            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Poke();
        }

        public sealed class Tag
        {
            public int Mark;
            public Leaf Detail = new();
        }

        public class Linked
        {
            public Node? Next;
            public Ring? Owner;

            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Touch();
        }

        public sealed class Node : Linked
        {
            public int Value;
            public Leaf Leaf = new();
            public Tag? Tag;

            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Link(Node other);
        }

        public sealed class Ring
        {
            public int Total;
            public readonly List<Node> Nodes = new();
            public readonly Node First;
            public readonly Action Callback;

            public Ring()
            {
                var second = new Node();
                var third = new Node();
                First = new Node { Next = second, Owner = this, Tag = new Tag() };
                second.Next = third;
                third.Next = First;
                Nodes.Add(second);
                Callback = () => Total++;
            }

            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Visit();
        }

        public sealed class Side
        {
            public int Count;
            public Leaf Mine = new();
        }

        public sealed class Worker(Ring ring, Side side) : BackgroundService
        {
            private readonly Ring _ring = ring;
            private readonly Side _side = side;

            protected override Task ExecuteAsync(CancellationToken token)
            {
                _ring.First.Touch();
                System.Console.WriteLine(_side);
                System.Console.WriteLine(_ring.First.Leaf);
                _ring.First.Link(_ring.First.Next!);
                _ring.Visit();
                _ring.First.Leaf.Poke();
                System.Console.WriteLine(_ring);
                System.Console.WriteLine(_ring.First);
                CancellationToken.None.Register(_ring.Callback);
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Ring>(); services.AddSingleton<Side>(); services.AddHostedService<Worker>();");

    /// <summary>A singleton <c>Hub</c> holding four leaves, and two singleton holders that each point to the hub and to the same four
    /// leaves straight: a holder's component points to five components, and the hub's set holds the other four. A worker hands both
    /// holders and the hub to unknown code.</summary>
    private static string Dense() => """
        public sealed class Leaf
        {
            public int Count;
        }

        public sealed class Hub
        {
            public int Mark;
            public readonly Leaf A = new();
            public readonly Leaf B = new();
            public readonly Leaf C = new();
            public readonly Leaf D = new();
        }

        public sealed class First(Hub hub)
        {
            public int Mark;
            public readonly Hub Hub = hub;
            public readonly Leaf A = hub.A;
            public readonly Leaf B = hub.B;
            public readonly Leaf C = hub.C;
            public readonly Leaf D = hub.D;
        }

        public sealed class Second(Hub hub)
        {
            public int Mark;
            public readonly Leaf D = hub.D;
            public readonly Leaf C = hub.C;
            public readonly Hub Hub = hub;
            public readonly Leaf B = hub.B;
            public readonly Leaf A = hub.A;
        }

        public sealed class Worker(First first, Second second, Hub hub) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken token)
            {
                System.Console.WriteLine(first);
                System.Console.WriteLine(second);
                System.Console.WriteLine(hub);
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Hub>(); services.AddSingleton<First>(); services.AddSingleton<Second>(); " +
                      "services.AddHostedService<Worker>();");

    /// <summary>The reach as it was walked before #468: each start on its own with a stack, a region's targets numbered the first time
    /// the region is stepped, a start's walk kept, and a call whose receiver seen through its declaring type meets itself walked whole,
    /// its starts in turn.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="heap">The solved heap.</param>
    private sealed class WalkPerStart(ScopeProgram scope, HeapSolution heap)
    {
        private readonly UnknownCalls.Reach _fields = new(scope, heap);
        private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Step> _steps = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, string?), (HashSet<string> Reached, bool MetItself)> _walks = [];
        private readonly HashSet<string> _answers = new(StringComparer.Ordinal);

        /// <summary>The regions in the order they were numbered.</summary>
        public List<string> Numbered { get; } = [];

        /// <summary>How many distinct sets the walks of starts and of calls walked whole reached.</summary>
        public int DistinctAnswers => _answers.Count;

        /// <summary>The starts of a call, as the reach takes them: each value of each start resolved, in order.</summary>
        /// <param name="call">The call.</param>
        public IEnumerable<(string Region, string? DeclaringTypeKey)> StartsOf(UnknownCall call) =>
            _fields.Starts(call).SelectMany(start => start.Values.SelectMany(value => heap.Resolve(call.Instance.Id, value)).Distinct(StringComparer.Ordinal)
                                                         .Select(region => (region, start.DeclaringTypeKey)));

        /// <summary>What a call reaches, its starts numbered first, as the reach did.</summary>
        /// <param name="call">The call.</param>
        public IReadOnlyList<string> Of(UnknownCall call)
        {
            var starts = StartsOf(call).ToArray();
            foreach (var (region, _) in starts)
                Number(region);
            var walks = starts.Select(start => Walk([start])).ToArray();
            var reached = walks.Any(walk => walk.MetItself) && starts.Length > 1
                ? Walk(starts).Reached
                : walks.SelectMany(walk => walk.Reached).ToHashSet(StringComparer.Ordinal);
            return reached.Order(StringComparer.Ordinal).ToArray();
        }

        /// <summary>Whether a call is walked whole: a start of it meets itself and it has more than one.</summary>
        /// <param name="call">The call.</param>
        public bool IsWalkedWhole(UnknownCall call)
        {
            var starts = StartsOf(call).ToArray();
            return starts.Length > 1 && starts.Any(start => WalkOf(start.Region, start.DeclaringTypeKey).MetItself);
        }

        /// <summary>The walk of one start, its region numbered first.</summary>
        /// <param name="region">The start's region.</param>
        /// <param name="declaringTypeKey">The type it is seen through, or null.</param>
        public (HashSet<string> Reached, bool MetItself) WalkOf(string region, string? declaringTypeKey)
        {
            Number(region);
            return Walk([(region, declaringTypeKey)]);
        }

        /// <summary>Whether a region reaches itself through the regions a walk steps to.</summary>
        /// <param name="region">The region.</param>
        public bool OnCycle(string region)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(Successors(region));
            while (pending.TryPop(out var next))
            {
                if (next == region)
                    return true;
                if (visited.Add(next))
                {
                    foreach (var successor in Successors(next))
                        pending.Push(successor);
                }
            }
            return false;
        }

        private IEnumerable<string> Successors(string region) =>
            StepOf(region) is var step && step.IsImmutable ? [] : step.Captures ?? step.Slots.SelectMany(slot => slot.Targets);

        private int Number(string region)
        {
            if (!_numbers.TryGetValue(region, out var number))
            {
                _numbers.Add(region, number = Numbered.Count);
                Numbered.Add(region);
            }
            return number;
        }

        private string Numbering(string region)
        {
            Number(region);
            return region;
        }

        private Step StepOf(string regionId)
        {
            if (_steps.TryGetValue(regionId, out var step))
                return step;
            var region = heap.Regions[regionId];
            var isCollection = InterproceduralAccesses.CollectionKindOf(heap, scope.Program, regionId).IsCollection;
            step = !isCollection && region.TypeKey is { } key && scope.Program.ImmutableTypeKeys.Contains(key)
                ? new Step(isCollection, true, null, false, [])
                : region.Kind == HeapRegionKind.Delegate
                    ? new Step(isCollection, false, heap.DelegateCaptures(regionId).Select(Numbering).ToArray(), false, [])
                    : new Step(isCollection, false, null,
                               region.Kind is not (HeapRegionKind.StructCopy or HeapRegionKind.StructStorage) &&
                               (_fields.IsSourceObject(regionId) || _fields.LibraryFieldsOf(regionId).Count != 0),
                               heap.FieldsOf(regionId).Select(slot => (FieldSlot.Root(slot), heap.PointsTo(regionId, slot).Select(Numbering).ToArray()))
                                   .ToArray());
            _steps.Add(regionId, step);
            return step;
        }

        /// <summary>Walks starts in turn, sharing what is visited; a walk of one start is kept.</summary>
        /// <param name="starts">The starts.</param>
        private (HashSet<string> Reached, bool MetItself) Walk(IReadOnlyList<(string Region, string? DeclaringTypeKey)> starts)
        {
            if (starts.Count == 1 && _walks.TryGetValue(starts[0], out var kept))
                return kept;
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var restrictedVisited = new HashSet<(string, string)>();
            var metItself = false;
            foreach (var (start, declaringTypeKey) in starts)
            {
                var pending = new Stack<string>([start]);
                while (pending.TryPop(out var region))
                {
                    var isRestrictedStart = region == start && declaringTypeKey is not null;
                    if (isRestrictedStart ? !restrictedVisited.Add((region, declaringTypeKey!)) : !visited.Add(region))
                    {
                        metItself |= isRestrictedStart;
                        continue;
                    }
                    var step = StepOf(region);
                    if (step.IsImmutable)
                        continue;
                    if (step.Captures is { } captures)
                    {
                        foreach (var captured in captures)
                            pending.Push(captured);
                        continue;
                    }
                    var restricted = isRestrictedStart
                        ? _fields.Seen(_fields.FieldsOf(region, declaringTypeKey), declaringTypeKey).Select(FieldSlot.Key).ToHashSet(StringComparer.Ordinal)
                        : null;
                    if (step.IsCollection || restricted?.Count > 0 || restricted is null && step.HasState)
                        reached.Add(region);
                    foreach (var (root, targets) in step.Slots)
                    {
                        if (restricted is null || restricted.Contains(root) || root == PathValue.KEPT || step.IsCollection && PathValue.IsStorage(root))
                        {
                            foreach (var target in targets)
                                pending.Push(target);
                        }
                    }
                }
            }

            if (starts.Count == 1)
                _walks.Add(starts[0], (reached, metItself));
            _answers.Add(string.Join(",", reached.Order(StringComparer.Ordinal)));
            return (reached, metItself);
        }

        /// <summary>What a walk learns at a region.</summary>
        /// <param name="IsCollection">Whether it is a collection.</param>
        /// <param name="IsImmutable">Whether it is an immutable object that is no collection.</param>
        /// <param name="Captures">What a delegate captures, or null.</param>
        /// <param name="HasState">Whether it has state an effect reaches.</param>
        /// <param name="Slots">Its slots by root with their targets.</param>
        private sealed record Step(bool IsCollection, bool IsImmutable, string[]? Captures, bool HasState, (string Root, string[] Targets)[] Slots);
    }
}
