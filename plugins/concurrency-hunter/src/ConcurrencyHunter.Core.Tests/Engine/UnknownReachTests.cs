using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The reach of an unknown effect (R1) walked once per start and kept (#462): every call reaches what one walk of its starts
/// in turn, sharing what it visits, reaches, and a start several calls share is walked once.</summary>
public sealed class UnknownReachTests
{
    [Fact]
    public void Each_start_is_walked_once_and_a_call_reaches_what_one_walk_of_its_starts_in_turn_reaches()
    {
        var run = Execute("""
            public sealed class Inner { public int Value; }
            public sealed class State { public int Count; public Inner Inner = new(); public object Box = new Inner(); }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    System.Console.WriteLine(_state);
                    System.Console.Write(_state);
                    System.Console.WriteLine(_state.Inner);
                    System.Console.WriteLine(_state.ToString());
                    System.Threading.CancellationToken.None.Register(() => _state.Count++);
                    _state.Count = 1;
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));
        var program = run.Heap.Program;
        var scope = new ScopeProgram(program.ScopeId, program.Input.Roots, program.Result, run.Heap.Summaries, program.Input.Program,
                                     program.Input.DiIndex, program.Input.InjectionBindings) { MetadataSupertypes = program.MetadataSupertypes };
        var heap = run.Heap.Heap;
        var reach = new UnknownCalls.Reach(scope, heap);
        var calls = UnknownCalls.Of(scope, heap).ToArray();
        var starts = calls.Sum(call => reach.Starts(call).Sum(start => start.Values.SelectMany(value => heap.Resolve(call.Instance.Id, value))
                                                                                   .Distinct(StringComparer.Ordinal).Count()));

        Assert.All(calls, call => Assert.Equal(OneWalk(scope, heap, reach, call).Order(StringComparer.Ordinal),
                                               reach.Of(call).Regions.Order(StringComparer.Ordinal)));
        Assert.Contains(calls, call => reach.Of(call).Regions.Count != 0);
        var walks = int.Parse(reach.Counts().Split(' ').Single(item => item.StartsWith("startWalks=", StringComparison.Ordinal))["startWalks=".Length..]);
        Assert.InRange(walks, 1, starts - 1);
    }

    /// <summary>The reach of a call as one walk of its starts in turn, sharing what it visits: how it was walked before #462.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="heap">The solved heap.</param>
    /// <param name="reach">The reach whose starts, fields and library fields the walk reads.</param>
    /// <param name="call">The unknown call.</param>
    private static HashSet<string> OneWalk(ScopeProgram scope, HeapSolution heap, UnknownCalls.Reach reach, UnknownCall call)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in reach.Starts(call))
        {
            foreach (var first in start.Values.SelectMany(value => heap.Resolve(call.Instance.Id, value)).Distinct(StringComparer.Ordinal))
            {
                var pending = new Stack<string>([first]);
                while (pending.TryPop(out var regionId))
                {
                    var isRestrictedStart = regionId == first && start.DeclaringTypeKey is not null;
                    if (!visited.Add(isRestrictedStart ? $"{regionId}|{start.DeclaringTypeKey}" : regionId))
                        continue;
                    var region = heap.Regions[regionId];
                    var isCollection = InterproceduralAccesses.CollectionKindOf(heap, scope.Program, regionId).IsCollection;
                    if (!isCollection && region.TypeKey is { } key && scope.Program.ImmutableTypeKeys.Contains(key))
                        continue;
                    if (region.Kind == HeapRegionKind.Delegate)
                    {
                        foreach (var captured in heap.DelegateCaptures(regionId))
                            pending.Push(captured);
                        continue;
                    }

                    var restricted = isRestrictedStart
                        ? reach.Seen(reach.FieldsOf(regionId, start.DeclaringTypeKey), start.DeclaringTypeKey).Select(FieldSlot.Key).ToHashSet(StringComparer.Ordinal)
                        : null;
                    var hasState = region.Kind is not (HeapRegionKind.StructCopy or HeapRegionKind.StructStorage) &&
                                   (reach.IsSourceObject(regionId) || reach.LibraryFieldsOf(regionId).Count != 0);
                    if (isCollection || restricted?.Count > 0 || restricted is null && hasState)
                        reached.Add(regionId);
                    foreach (var slot in heap.FieldsOf(regionId))
                    {
                        var root = FieldSlot.Root(slot);
                        if (restricted is null || restricted.Contains(root) || root == PathValue.KEPT || isCollection && PathValue.IsStorage(root))
                        {
                            foreach (var target in heap.PointsTo(regionId, slot))
                                pending.Push(target);
                        }
                    }
                }
            }
        }

        return reached;
    }
}
