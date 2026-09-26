using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The factories of <c>ConcurrentDictionary.GetOrAdd</c> and <c>AddOrUpdate</c> run where the call stands, as a call of the
/// delegate there would (R11): they are handed the key, the value the dictionary holds or the overload's argument, what they return is
/// held and handed out, and their bodies run in the calling execution, not atomically, under the locks the caller holds.</summary>
public sealed class CollectionFactoryTests
{
    private const string FIRST = "alloc:State..ctor()#Item";
    private const string KEY = "alloc:State..ctor()#Keyed";
    private const string GIVEN = "alloc:State..ctor()#Given";

    [Fact]
    public void Value_made_by_a_factory_is_held_and_handed_out()
    {
        var run = Run("_state.Map.GetOrAdd(\"b\", _ => new Made()).Hits = 1; " +
                      "_state.Map.AddOrUpdate(\"c\", _ => new Made(), (_, old) => old).Hits = 2; " +
                      "_state.Map[\"b\"].Hits = 3;");

        foreach (var at in new[] { "Hits = 1", "Hits = 2", "Hits = 3" })
            Assert.Contains(Written(run, at), region => region.Contains("#Made", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_factory_is_given_the_held_value()
    {
        var run = Run("var result = _state.Map.AddOrUpdate(\"a\", new Made(), (k, old) => { old.Hits++; return old; }); result.Hits = 5;");

        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.ReadModifyWrite && access.Resource.Region == FIRST);
        Assert.Contains(FIRST, Written(run, "result.Hits = 5"));
    }

    [Fact]
    public void Factory_returning_its_key_holds_the_key_object()
    {
        var run = Run("_state.ByKey.GetOrAdd(_state.TheKey, key => key).Hits = 1; foreach (var pair in _state.ByKey) pair.Value.Hits = 2;");

        Assert.Contains(KEY, Written(run, "Hits = 1"));
        Assert.Contains(KEY, Written(run, "Hits = 2"));
    }

    [Fact]
    public void Factory_returning_its_argument_holds_that_argument()
    {
        // Beside KnownCallShapeTests.Argument_a_generic_factory_is_handed_is_not_held, whose factory makes a new object: here the
        // factory returns the argument, so the argument is what the dictionary holds.
        var run = Run("_state.Map.GetOrAdd(\"d\", (_, given) => given, _state.Handed).Hits = 1; _state.Map[\"d\"].Hits = 2;");

        Assert.Contains(GIVEN, Written(run, "Hits = 1"));
        Assert.Contains(GIVEN, Written(run, "Hits = 2"));
    }

    [Fact]
    public void Factory_delegate_is_never_held()
    {
        var run = Run("_state.Objects.GetOrAdd(\"e\", _ => new Made()); _state.Objects.AddOrUpdate(\"f\", _ => new Made(), (_, old) => old);");

        var heap = run.Run.Execution.Heap.Heap;
        var dictionary = Assert.Single(heap.PointsTo(run.Run.Execution.Heap.Region("di:State@Singleton").Identity, "Fixture:State.Objects"));
        var held = heap.PointsTo(dictionary, PathValue.ELEMENT).Concat(heap.PointsTo(dictionary, PathValue.KEYS)).ToArray();
        Assert.Contains(held, region => heap.Regions[region].Display.Contains("#Made", StringComparison.Ordinal));
        Assert.DoesNotContain(held, region => heap.Regions[region].Kind == HeapRegionKind.Delegate);
    }

    [Fact]
    public async Task Factory_body_write_is_collected_in_the_calling_execution_and_is_not_atomic()
    {
        const string work = "_state.Map.GetOrAdd(\"g\", _ => { _state.Count = 1; return new Made(); });";
        var run = Run(work, "_state.Count = 2;");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(AccessOperation.Write, write.Operation);
        Assert.Equal(ExecutionKind.Root, run.Run.Execution.Analysis.Execution(write.ExecutionId).Kind);
        Assert.Contains(run.Run.PairsOn("Count"), pair => ReferenceEquals(pair.First, write) || ReferenceEquals(pair.Second, write));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(Solution(Source(work, "_state.Count = 2;")), ROOT_DIRECTORY, CancellationToken.None);
        var finding = Assert.Single(result.Findings, finding => finding.Resource.AccessPath is ["Count"]);
        Assert.Equal("DCA1001", finding.RuleId);
    }

    [Fact]
    public void Lock_held_around_the_call_protects_the_factory_body()
    {
        var run = Run("lock (_state.Gate) { _state.Map.GetOrAdd(\"h\", _ => { _state.Count = 1; return new Made(); }); }",
                      "lock (_state.Gate) { _state.Count = 2; }");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.NotEmpty(write.HeldProtection);
        Assert.Empty(run.Run.PairsOn("Count"));
    }

    [Fact]
    public void Factory_of_a_call_is_no_delegate_handed_to_an_opaque_call()
    {
        var run = Run("_state.Map.GetOrAdd(\"i\", _ => { _state.Count = 1; return new Made(); });");

        Assert.Equal(Run("").Run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE), run.Run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
        Assert.DoesNotContain(run.Run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Factory_read_from_a_field_or_a_parameter_runs_at_the_call()
    {
        // A factory the body did not create is still run where the call stands: what it makes is held and handed out, and what its
        // body writes is collected there. Method groups, whose methods nothing else calls, are reached only as factories are.
        var run = Run("_state.Map.GetOrAdd(\"l\", _state.Maker).Hits = 1; _state.Through(_state.Spare).Hits = 2;");

        Assert.Contains(Written(run, "Maker).Hits = 1"), region => region.Contains("#Made", StringComparison.Ordinal));
        Assert.Contains(Written(run, "Through(_state.Spare).Hits = 2"), region => region.Contains("#Made", StringComparison.Ordinal));
        Assert.Equal(2, run.Run.Of("Total").Where(access => access.Operation == AccessOperation.Write)
                           .Select(access => access.Source.StartLine).Distinct().Count());
    }

    [Fact]
    public void Factory_without_a_body_is_an_unresolved_call_where_it_stands()
    {
        // As a call of the delegate would be: an interface method no object runs a body of is an unresolved dispatch at the call,
        // whose unknown effect reaches the argument the overload hands it.
        var run = Run("_state.Objects.GetOrAdd(\"o\", _state.Factory!.Make, _state.Handed);");

        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region == GIVEN &&
                                                       access.Source.StartLine == Line(run.Text, "_state.Factory!.Make"));
    }

    [Fact]
    public void Unresolved_factory_sees_the_held_value_only_where_it_updates()
    {
        // A value factory is handed the key alone; an update factory is handed the value the dictionary holds as well.
        var added = Run("_state.Map.GetOrAdd(\"p\", _state.Factory!.Create);");
        var updated = Run("_state.Map.AddOrUpdate(\"q\", new Made(), _state.Factory!.Update);");

        Assert.DoesNotContain(Worker(added, "Hits"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region == FIRST);
        Assert.Contains(Worker(updated, "Hits"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region == FIRST);
    }

    [Fact]
    public void Factory_an_opaque_call_handed_back_is_an_unresolved_call_where_it_stands()
    {
        // No delegate object is known for a factory an opaque call returned, which leaves it as unresolved as a call of it would be:
        // an update factory then sees the value the dictionary holds, and a value factory does not.
        var updated = Run("_state.Map.AddOrUpdate(\"r\", new Made(), Opaque.Lib.Updater<Item>());");
        var added = Run("_state.Map.GetOrAdd(\"s\", Opaque.Lib.Maker<Item>());");

        Assert.Contains(Worker(updated, "Hits"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region == FIRST);
        Assert.DoesNotContain(Worker(added, "Hits"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region == FIRST);
    }

    [Fact]
    public void Delegate_of_another_recognized_call_still_does_not_run()
    {
        var run = Run("_state.Items.ForEach(item => _state.Count = 1); _state.Items.RemoveAll(item => { _state.Count = 2; return false; });");

        Assert.Empty(run.Run.Of("Count"));
    }

    // ---- helpers ----

    /// <summary>A run with the text of the case file it analysed.</summary>
    private sealed record Case(EngineRun Run, string Text);

    private static IReadOnlyList<Access> Worker(Case run, string member) =>
        run.Run.Of(member).Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal)).ToArray();

    /// <summary>The objects the worker writes <c>Hits</c> of at the line containing <paramref name="at"/>.</summary>
    private static string[] Written(Case run, string at) =>
        Worker(run, "Hits").Where(access => access.Operation == AccessOperation.Write && access.Source.StartLine == Line(run.Text, at))
                           .Select(access => access.Resource.Region)
                           .Distinct()
                           .Order(StringComparer.Ordinal)
                           .ToArray();

    private static int Line(string file, string text) =>
        Array.FindIndex(file.Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1 is var found and > 0
            ? found
            : throw new InvalidOperationException($"no line holds '{text}'");

    private static Case Run(string work, string other = "")
    {
        var text = Usings + Source(work, other);
        return new Case(AnalyzeScope(Solution(Source(work, other)), "scope:Fixture"), text);
    }

    private static Solution Solution(string source) =>
        FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", Usings + source));

    /// <summary>A singleton <c>State</c> holding thread-safe dictionaries, one filled with <c>First</c> at construction, with a key
    /// object, an argument object and a lock of its own; a worker does <paramref name="work"/>, one statement a line, while another
    /// does <paramref name="other"/>.</summary>
    private static string Source(string work, string other) => $$"""
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public class Item { public int Hits; }
        public sealed class Made : Item { }
        public sealed class Keyed : Item { }
        public sealed class Given : Item { }
        public interface IMaker
        {
            object Make(string key, Given argument);
            Item Create(string key);
            Item Update(string key, Item old);
        }

        public sealed class State
        {
            public readonly Item First = new Item();
            public readonly Keyed TheKey = new Keyed();
            public readonly Given Handed = new Given();
            public readonly object Gate = new();
            public int Count;
            public readonly ConcurrentDictionary<string, Item> Map = new();
            public readonly ConcurrentDictionary<Keyed, Item> ByKey = new();
            public readonly ConcurrentDictionary<string, object> Objects = new();
            public readonly List<Item> Items = new();
            public int Total;
            public readonly Func<string, Item> Maker;
            public IMaker? Factory;

            public State()
            {
                Map.TryAdd("a", First);
                Items.Add(First);
                Maker = Make;
            }

            public Item Make(string key) { Total = 1; return new Made(); }

            public Item Spare(string key) { Total = 2; return new Made(); }

            public Item Through(Func<string, Item> factory) => Map.GetOrAdd("m", factory);
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{string.Join(Environment.NewLine, work.Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(statement => statement.TrimEnd(';') + ";"))}}
                return Task.CompletedTask;
            }
        }

        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{other}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();");

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Touch(object value) { }
                public static System.Func<string, T> Maker<T>() => _ => default!;
                public static System.Func<string, T, T> Updater<T>() => (_, old) => old;
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
