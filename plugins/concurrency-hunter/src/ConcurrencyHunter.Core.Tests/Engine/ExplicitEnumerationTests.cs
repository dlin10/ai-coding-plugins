using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ExplicitEnumerationTests
{
    [Fact]
    public async Task Enumerator_method_runs_its_body_at_MoveNext()
    {
        var result = await Run("using var e = _state.Walk().GetEnumerator(); e.MoveNext();");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.Symbol == "State.Walk()" &&
                                                 access.ExecutionId.StartsWith("root:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_GetEnumerator_on_an_iterator_runs_the_body_at_MoveNext()
    {
        var result = await Run("var sequence = _state.Walk(); using var e = sequence.GetEnumerator(); e.MoveNext();");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.Symbol == "State.Walk()" &&
                                                 access.ExecutionId.StartsWith("root:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_enumeration_of_an_iterator_alone_makes_no_unknown_call_or_gap()
    {
        var result = await Run("using var e = _state.Walk().GetEnumerator(); e.MoveNext();");
        Assert.Empty(result.Coverage[0].Gaps);
    }

    [Fact]
    public async Task Foreach_over_an_enumerable_iterator_runs_its_body_once()
    {
        var result = await Run("foreach (var value in _state.Walk()) { }");
        Assert.Single(WalkAccesses(result));
        Assert.DoesNotContain(result.Accesses, access => access.Symbol.EndsWith("MoveNext()", StringComparison.Ordinal) &&
                                                         access.Resource.AccessPath.SequenceEqual(["Value"]));
    }

    [Fact]
    public async Task Explicit_GetEnumerator_runs_nothing_at_its_own_site()
    {
        var result = await Run("lock (_state.Gate) { var e = _state.Walk().GetEnumerator(); }");
        Assert.Empty(WalkAccesses(result));
    }

    [Fact]
    public async Task Iterator_GetEnumerator_hands_back_the_iterator()
    {
        var result = await Run("var walk = _state.Walk(); using var e = walk.GetEnumerator(); e.MoveNext();");
        Assert.Single(WalkAccesses(result));
        Assert.Empty(result.Coverage[0].Gaps);
    }

    [Fact]
    public async Task Explicit_enumeration_does_not_depend_on_unrelated_enumerable_types()
    {
        const string action = "using var e = _state.Walk().GetEnumerator(); e.MoveNext();";
        var without = await Run(action);
        var with = await Run(action, extraTypes: "public sealed class Other : IEnumerable<int> { public IEnumerator<int> GetEnumerator() { yield return 0; } System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }");
        Assert.Equal(WalkAccesses(without).Select(access => access.Operation), WalkAccesses(with).Select(access => access.Operation));
        Assert.Equal(without.Coverage[0].Gaps.Select(gap => gap.Callee), with.Coverage[0].Gaps.Select(gap => gap.Callee));
    }

    [Fact]
    public async Task MoveNext_under_a_lock_runs_the_body_under_it()
    {
        var result = await Run("var e = _state.Walk().GetEnumerator(); lock (_state.Gate) e.MoveNext();");
        Assert.Contains(WalkAccesses(result), access => access.HeldProtection.Count != 0);
    }

    [Fact]
    public async Task Explicit_enumeration_carries_no_body_lock_out_of_MoveNext()
    {
        var result = await Run("using var e = _state.Walk().GetEnumerator(); e.MoveNext(); _state.Value++;",
                               walkBody: "lock (Gate) { Value++; yield return 1; }");
        Assert.Contains(WalkAccesses(result), access => access.HeldProtection.Count != 0);
        Assert.Contains(result.Accesses, access => access.Symbol == "Worker.ExecuteAsync(CancellationToken)" &&
                                                 access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.HeldProtection.Count == 0);
    }

    [Fact]
    public async Task Explicit_enumeration_carries_no_body_lock_out_of_Dispose()
    {
        var result = await Run("var e = _state.Walk().GetEnumerator(); e.MoveNext(); e.Dispose(); _state.Value++;",
                               walkBody: "lock (Gate) { Value++; yield return 1; }");
        Assert.Contains(result.Accesses, access => access.Symbol == "Worker.ExecuteAsync(CancellationToken)" &&
                                                 access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.HeldProtection.Count == 0);
    }

    [Fact]
    public async Task Finally_runs_with_the_body_at_MoveNext()
    {
        var result = await Run("using var e = _state.Walk().GetEnumerator(); e.MoveNext();",
                               walkBody: "try { yield return 1; } finally { Value++; }");
        Assert.True(WalkAccesses(result).Any(access => access.ExecutionId.StartsWith("root:", StringComparison.Ordinal)),
                    string.Join("; ", result.Accesses.Where(access => access.Resource.AccessPath.SequenceEqual(["Value"]))
                                                     .Select(access => $"{access.Symbol}|{access.ExecutionId}|{access.Operation}")));
    }

    [Fact]
    public async Task Dispose_outside_the_lock_leaves_the_finally_unprotected()
    {
        var result = await Run("var e = _state.Walk().GetEnumerator(); lock (_state.Gate) e.MoveNext(); e.Dispose();",
                               walkBody: "try { yield return 1; } finally { Value++; }");
        Assert.Contains(WalkAccesses(result), access => access.HeldProtection.Count == 0);
        Assert.Contains(WalkAccesses(result), access => access.HeldProtection.Count != 0);
    }

    [Fact]
    public async Task Escaping_explicit_enumerator_keeps_its_unknown_enumeration()
    {
        var result = await Run("_state.Escaped = _state.Walk().GetEnumerator();");
        Assert.Contains(WalkAccesses(result), access => access.ExecutionId.StartsWith("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_enumeration_of_a_library_sequence_alone_makes_no_unknown_enumeration()
    {
        var result = await Run("using var e = _state.Items.Select(value => { _state.Value++; return value; }).GetEnumerator(); e.MoveNext();",
                               extraUsings: "using System.Linq;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.ExecutionId.StartsWith("root:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                        access.ExecutionId.StartsWith("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Library_sequence_enumerated_explicitly_runs_its_delegate_at_MoveNext()
    {
        var result = await Run("using var e = _state.Items.Select(value => { _state.Value++; return value; }).GetEnumerator(); e.MoveNext();",
                               extraUsings: "using System.Linq;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.Operation == AccessOperation.ReadModifyWrite &&
                                                 access.ExecutionId.StartsWith("root:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Library_sequence_disposed_explicitly_runs_its_delegate_at_Dispose()
    {
        var result = await Run("var e = _state.Items.Select(value => { _state.Value++; return value; }).GetEnumerator(); e.Dispose();",
                               extraUsings: "using System.Linq;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.Operation == AccessOperation.ReadModifyWrite &&
                                                 access.ExecutionId.StartsWith("root:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_generic_enumerator_method_runs_its_body_at_MoveNext()
    {
        var result = await EnumeratorMethod("public System.Collections.IEnumerator GetEnumerator() { _state.Value++; yield return 1; }",
                                            "var e = _shelf.GetEnumerator(); e.MoveNext();");
        Assert.Contains(result.Accesses, access => access.Symbol == "Shelf.GetEnumerator()" &&
                                                 access.Resource.AccessPath.SequenceEqual(["Value"]));
    }

    [Fact]
    public async Task Foreach_over_a_type_with_an_enumerator_method_runs_the_body()
    {
        var result = await EnumeratorMethod("public IEnumerator<int> GetEnumerator() { _state.Value++; yield return 1; }",
                                            "foreach (var item in _shelf) { }");
        Assert.Contains(result.Accesses, access => access.Symbol == "Shelf.GetEnumerator()" &&
                                                 access.Resource.AccessPath.SequenceEqual(["Value"]));
    }

    [Fact]
    public async Task Receiver_holding_an_iterator_and_a_source_enumerable_runs_both()
    {
        var result = await Analyze(Usings + "using System.Collections.Generic;" + """
            public sealed class State
            {
                public int Value;
                public int Other;
                public readonly List<int> Items = new() { 1 };
                public IEnumerable<int> Walk() { Value++; yield return 1; }
            }
            public sealed class Source : IEnumerable<int>
            {
                private readonly State _state;
                public Source(State state) => _state = state;
                public IEnumerator<int> GetEnumerator() { _state.Other++; return _state.Items.GetEnumerator(); }
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                private readonly Source _source;
                public Worker(State state, Source source) { _state = state; _source = source; }
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    IEnumerable<int> values = DateTime.UtcNow.Ticks > 0 ? _state.Walk() : _source;
                    var e = values.GetEnumerator(); e.MoveNext();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Source>(); services.AddHostedService<Worker>();"));
        Assert.Contains(result.Accesses, access => access.Symbol == "State.Walk()" && access.Resource.AccessPath.SequenceEqual(["Value"]));
        Assert.Contains(result.Accesses, access => access.Symbol == "Source.GetEnumerator()" && access.Resource.AccessPath.SequenceEqual(["Other"]));
    }

    [Fact]
    public async Task Receiver_holding_an_iterator_and_a_list_keeps_the_lists_table_effects()
    {
        var result = await Run("IEnumerable<int> values = DateTime.UtcNow.Ticks > 0 ? _state.Walk() : _state.Items; var e = values.GetEnumerator(); e.MoveNext();");
        Assert.NotEmpty(WalkAccesses(result));
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Items"]));
    }

    [Fact]
    public async Task Receiver_holding_an_iterator_and_an_opaque_object_keeps_the_unknown_effect()
    {
        var source = Usings + "using System.Collections.Generic;" + """
            public sealed class State
            {
                public int Value;
                public IEnumerable<int> Walk() { try { yield return 1; } finally { Value++; } }
            }
            public sealed class Other { public int Count; }
            public sealed class Opaque : IDisposable
            {
                public readonly Other Other;
                public Opaque(Other other) => Other = other;
                public extern void Dispose();
            }
            [ApiController]
            public sealed class FeedController : ControllerBase
            {
                private readonly State _state;
                private readonly Other _other;
                public FeedController(State state, Other other) { _state = state; _other = other; }
                [HttpPost]
                public void Post()
                {
                    IDisposable value = DateTime.UtcNow.Ticks > 0 ? _state.Walk().GetEnumerator() : new Opaque(_other);
                    value.Dispose();
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Other>();");
        HeapSolution? heap = null;
        var result = await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", source)), ROOT_DIRECTORY,
            ProviderRegistry.BuiltIn, (accesses, executions, solved) =>
            {
                heap = solved;
                return InterproceduralPairing.Pair(accesses, executions, solved);
            }, CancellationToken.None);
        Assert.Contains(result.Accesses, access => access.Symbol == "State.Walk()" && access.Resource.AccessPath.SequenceEqual(["Value"]));
        Assert.NotNull(heap);
        Assert.Contains(heap.Instances.Values, instance => instance.Summary.OpaqueCalls.Any(call =>
            call.Callee.Contains("Dispose", StringComparison.Ordinal) &&
            heap.IteratorMemberReceivers.ContainsKey((instance.Id, call.OperationId))));
        Assert.True(heap.Instances.Values.Any(instance => instance.Summary.OpaqueCalls.Any(call =>
            call.Callee.Contains("Dispose", StringComparison.Ordinal) && !call.IsKnown && !call.IsRecognized && call.Collection is null &&
            call.Receivers.SelectMany(value => heap.Resolve(instance.Id, value))
                .Any(region => heap.IteratorMemberReceivers.GetValueOrDefault((instance.Id, call.OperationId))?.Contains(region) != true))),
            string.Join("; ", heap.Instances.Values.SelectMany(instance => instance.Summary.OpaqueCalls)
                .Where(call => call.Callee.Contains("Dispose", StringComparison.Ordinal))
                .Select(call => $"{call.Callee}/{call.IsKnown}/{call.IsRecognized}/{call.Collection is not null}")));
        Assert.DoesNotContain(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                        access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public async Task Current_gives_what_foreach_gives()
    {
        var explicitRead = await Run("var e = _state.Items.Select(value => value).GetEnumerator(); e.MoveNext(); _state.Value += e.Current;",
                                     extraUsings: "using System.Linq;");
        var loop = await Run("foreach (var value in _state.Items.Select(value => value)) _state.Value += value;",
                             extraUsings: "using System.Linq;");
        Assert.Equal(explicitRead.Accesses.Count(access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                           access.Operation == AccessOperation.ReadModifyWrite),
                     loop.Accesses.Count(access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                   access.Operation == AccessOperation.ReadModifyWrite));
    }

    [Fact]
    public async Task Foreach_over_an_enumerable_iterator_keeps_its_lock_carry()
    {
        var result = await Run("foreach (var item in _state.Walk()) _state.Value++;",
                               walkBody: "System.Threading.Monitor.Enter(Gate); try { yield return 1; } finally { System.Threading.Monitor.Exit(Gate); }");
        Assert.Contains(result.Accesses, access => access.Symbol == "Worker.ExecuteAsync(CancellationToken)" &&
                                                 access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.HeldProtection.Count != 0);
    }

    [Fact]
    public async Task SelectMany_shaped_iterator_class_runs_its_selector()
    {
        var result = await Analyze(Usings + "using System.Collections; using System.Collections.Generic;" + """
            public sealed class State { public int Value; }
            public abstract class Iterator<T> : IEnumerable<T>, IEnumerator<T>
            {
                private bool _used;
                public IEnumerator<T> GetEnumerator()
                {
                    if (_used) return Clone();
                    _used = true;
                    return this;
                }
                protected abstract Iterator<T> Clone();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                public abstract bool MoveNext();
                public T Current => default!;
                object IEnumerator.Current => Current!;
                public void Reset() { }
                public void Dispose() { }
            }
            public sealed class SelectManyIterator : Iterator<int>
            {
                private readonly Func<int, int> _selector;
                public SelectManyIterator(Func<int, int> selector) => _selector = selector;
                protected override Iterator<int> Clone() => new SelectManyIterator(_selector);
                public override bool MoveNext() { _selector(1); return false; }
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    var e = new SelectManyIterator(value => { _state.Value++; return value; }).GetEnumerator();
                    e.MoveNext(); return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.Operation == AccessOperation.ReadModifyWrite);
    }

    [Fact]
    public async Task OrderBy_shaped_enumerator_method_runs_its_key_selector()
    {
        var result = await Analyze(Usings + "using System.Collections.Generic;" + """
            public sealed class State { public int Value; }
            public sealed class Ordered
            {
                private readonly Func<int, int> _keySelector;
                public Ordered(Func<int, int> keySelector) => _keySelector = keySelector;
                public IEnumerator<int> GetEnumerator() { _keySelector(1); yield return 1; }
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    var e = new Ordered(key => { _state.Value++; return key; }).GetEnumerator();
                    e.MoveNext(); return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.Operation == AccessOperation.ReadModifyWrite);
    }

    [Fact]
    public async Task GroupBy_shaped_explicit_enumeration_runs_its_result_selector()
    {
        var result = await Analyze(Usings + "using System.Collections.Generic;" + """
            public sealed class State { public int Value; }
            public static class Grouping
            {
                public static IEnumerable<int> Groups(Func<int, int> resultSelector)
                {
                    yield return resultSelector(1);
                }
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    var e = Grouping.Groups(value => { _state.Value++; return value; }).GetEnumerator();
                    e.MoveNext(); return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.Operation == AccessOperation.ReadModifyWrite);
    }

    [Fact]
    public async Task Foreach_over_a_library_sequence_keeps_its_Current()
    {
        var result = await Analyze(Usings + """
            using System.Collections.Generic;
            using System.Linq;
            public sealed class Bin { public int Count; }
            public sealed class Item
            {
                public readonly Bin Group;
                public int Value;
                public Item(Bin group) => Group = group;
            }
            public sealed class Stock
            {
                public readonly List<Item> Items = new();
                public Stock()
                {
                    for (var i = 0; i < 2; i++) Items.Add(new Item(new Bin()));
                }
            }
            public sealed class StockController : ControllerBase
            {
                private readonly Stock _stock;
                public StockController(Stock stock) => _stock = stock;
                public void Post()
                {
                    foreach (var group in _stock.Items.GroupBy(item => item.Group))
                    {
                        group.Key.Count++;
                        foreach (var item in group) item.Value++;
                    }
                }
            }
            """ + Startup("services.AddSingleton<Stock>();"));

        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.Operation == AccessOperation.ReadModifyWrite);
        Assert.DoesNotContain(result.Accesses, access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Empty(result.Coverage[0].Gaps);
    }

    [Fact]
    public async Task Explicit_Current_of_a_GroupBy_sequence_hands_out_groups()
    {
        HeapSolution? heap = null;
        var result = await Analyze(Usings + """
            using System.Collections.Generic;
            using System.Linq;
            public sealed class Bin { public int Count; }
            public sealed class Item
            {
                public readonly Bin Group;
                public int Value;
                public Item(Bin group) => Group = group;
            }
            public sealed class Stock
            {
                public readonly List<Item> Items = new();
                public Stock()
                {
                    for (var i = 0; i < 2; i++) Items.Add(new Item(new Bin()));
                }
            }
            public sealed class StockController : ControllerBase
            {
                private readonly Stock _stock;
                public StockController(Stock stock) => _stock = stock;
                public void Post()
                {
                    var groups = _stock.Items.GroupBy(item => item.Group);
                    ((Item[])(object)groups)[0] = _stock.Items[0];
                    using var enumerator = groups.GetEnumerator();
                    enumerator.MoveNext();
                    enumerator.Current.Key.Count++;
                    foreach (var item in enumerator.Current) item.Value++;
                }
            }
            """ + Startup("services.AddSingleton<Stock>();"), solved => heap = solved);

        Assert.NotNull(heap);
        var groups = heap.Instances.Values.SelectMany(instance => instance.Summary.OpaqueCalls
            .Where(call => call.Callee.Contains("IGrouping", StringComparison.Ordinal) && call.Callee.EndsWith(".get_Current()", StringComparison.Ordinal))
            .SelectMany(call => heap.Resolve(instance.Id, new CallResultValue(call.OperationId)))).Distinct().ToArray();
        Assert.NotEmpty(groups);
        Assert.All(groups, group => Assert.NotEqual("Fixture:Item", heap.Regions[group].TypeKey));

        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Count"]) &&
                                                 access.Operation == AccessOperation.ReadModifyWrite);
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                 access.Operation == AccessOperation.ReadModifyWrite);
        Assert.DoesNotContain(result.Accesses, access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Empty(result.Coverage[0].Gaps);
    }

    private static IReadOnlyList<Access> WalkAccesses(AnalysisResult result) =>
        result.Accesses.Where(access => access.Symbol == "State.Walk()" && access.Resource.AccessPath.SequenceEqual(["Value"]))
              .ToArray();

    private static Task<AnalysisResult> EnumeratorMethod(string member, string action) =>
        Analyze(Usings + $$"""
            using System.Collections.Generic;
            public sealed class State { public int Value; }
            public sealed class Shelf
            {
                private readonly State _state;
                public Shelf(State state) => _state = state;
                {{member}}
            }
            public sealed class Worker : BackgroundService
            {
                private readonly Shelf _shelf;
                public Worker(Shelf shelf) => _shelf = shelf;
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    {{action}}
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Shelf>(); services.AddHostedService<Worker>();"));

    private static Task<AnalysisResult> Run(string action, string walkBody = "Value++; yield return 1;", string extraTypes = "",
                                            string extraUsings = "") =>
        Analyze(Usings + $$"""
            using System.Collections.Generic;
            {{extraUsings}}
            public sealed class State
            {
                public int Value;
                public readonly object Gate = new();
                public readonly List<int> Items = new() { 1 };
                public IEnumerator<int>? Escaped;
                public IEnumerable<int> Walk() { {{walkBody}} }
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{action}}
                    return Task.CompletedTask;
                }
            }
            {{extraTypes}}
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));

    private static Task<AnalysisResult> Analyze(string source, Action<HeapSolution>? onHeap = null) =>
        onHeap is null
            ? PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", source)), ROOT_DIRECTORY, CancellationToken.None)
            : PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", source)), ROOT_DIRECTORY, ProviderRegistry.BuiltIn,
                                            (accesses, executions, heap) =>
                                            {
                                                onHeap(heap);
                                                return InterproceduralPairing.Pair(accesses, executions, heap);
                                            }, CancellationToken.None);
}
