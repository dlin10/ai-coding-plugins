using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Every rule that follows what an array's cells hold follows what each storage of a collection holds (R5, R9c): a list's
/// element storage, a dictionary's value storage and its key storage. Each row runs one rule's fixture over the storage and over an
/// array, and the two must agree on what the rule observes; the array must observe it too, so a fixture that proves nothing cannot
/// pass by agreeing.</summary>
public sealed class CollectionStorageRuleTests
{
    /// <summary>How a fixture declares a box of <c>T</c>, puts a value in, and reads one back as <c>held</c> for a body of statements.
    /// A dictionary's keys are read back through the pairs its enumeration hands out; the others through the cell the value went
    /// into.</summary>
    private sealed record Storage(string Name, string Slot, Func<string, string> Type, Func<string, string> New, Func<string, string, string> Put,
                                  Func<string, string, string> Read);

    private static readonly Storage Array = new("array", "[]", type => $"{type}[]", type => $"new {type}[1]",
                                                (box, value) => $"{box}[0] = {value};", (box, body) => $"{{ var held = {box}[0]; {body} }}");

    private static readonly Storage[] Storages =
    [
        new("list", "[]", type => $"List<{type}>", type => $"new List<{type}>()",
            (box, value) => $"{box}.Add({value});", (box, body) => $"{{ var held = {box}[0]; {body} }}"),
        new("dictionary-value", "[]", type => $"Dictionary<string, {type}>", type => $"new Dictionary<string, {type}>()",
            (box, value) => $"{box}.Add(\"a\", {value});", (box, body) => $"{{ var held = {box}[\"a\"]; {body} }}"),
        new("dictionary-key", "[keys]", type => $"Dictionary<{type}, int>", type => $"new Dictionary<{type}, int>()",
            (box, value) => $"{box}.Add({value}, 0);", (box, body) => $"foreach (var pair in {box}) {{ var held = pair.Key; {body} }}")
    ];

    private static readonly string[] Rules = ["escape", "publication", "quiet-event", "gap-origin", "unknown-effect", "deep-read", "handed-delegate", "join-handle"];

    public static TheoryData<string, string> Rows()
    {
        var data = new TheoryData<string, string>();
        foreach (var rule in Rules)
        foreach (var storage in Storages)
            data.Add(rule, storage.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Rule_follows_every_storage_as_it_follows_array_cells(string rule, string storage)
    {
        var observed = await Observe(rule, Storages.Single(candidate => candidate.Name == storage));
        var reference = await Observe(rule, Array);

        Assert.True(reference, $"the array fixture of {rule} does not observe the rule");
        Assert.Equal(reference, observed);
    }

    private static Task<bool> Observe(string rule, Storage storage) => rule switch
    {
        "escape" => Task.FromResult(Escapes(storage)),
        "publication" => Task.FromResult(Publishes(storage)),
        "quiet-event" => Task.FromResult(BreaksQuietEventProof(storage)),
        "gap-origin" => FollowsGapOrigin(storage),
        "unknown-effect" => Task.FromResult(UnknownEffectWrites(storage)),
        "deep-read" => Task.FromResult(DeepReadReaches(storage)),
        "handed-delegate" => Task.FromResult(HandsOverDelegate(storage)),
        "join-handle" => Task.FromResult(IsNoProvenJoinHandle(storage)),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
    };

    // ---- the rules ----

    /// <summary>An object held by a shared collection has escaped, its chain naming the storage it went into and the insertion site.</summary>
    private static bool Escapes(Storage storage)
    {
        var source = State(storage, "_ = _state.Box;");
        var ownership = Analyze(source).Execution.Ownership("alloc:State..ctor()#Loose");
        return ownership.Kind == OwnershipKind.Escaped &&
               ownership.Evidence.Any(hop => hop.Contains($"is stored into {storage.Slot} of", StringComparison.Ordinal) &&
                                             hop.EndsWith($"at Case.cs:{Line(source, storage.Put("Box", "new Loose()"))}.", StringComparison.Ordinal));
    }

    /// <summary>A constructor that puts <c>this</c> into a shared collection and then writes its own field publishes the object: the
    /// write is not local to the construction and pairs with another execution's read through the collection.</summary>
    private static bool Publishes(Storage storage)
    {
        var run = Analyze($$"""
            using System.Collections.Generic;

            public sealed class Registry { public readonly {{storage.Type("Member")}} Members = {{storage.New("Member")}}; }
            public sealed class Member
            {
                public int Hits;
                public Member(Registry registry) { {{storage.Put("registry.Members", "this")}} Hits = 1; }
            }

            public sealed class Joiner(Registry registry) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _ = new Member(registry); return Task.CompletedTask; }
            }

            public sealed class Counter(Registry registry) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{storage.Read("registry.Members", "_ = held.Hits;")}}
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Registry>(); services.AddHostedService<Joiner>(); services.AddHostedService<Counter>();"));

        var write = run.Accesses("Hits").SingleOrDefault(access => access.Operation == AccessOperation.Write);
        return write is { IsConstructionLocal: false } &&
               run.PairsOn("Hits").Any(pair => ReferenceEquals(pair.First, write) || ReferenceEquals(pair.Second, write));
    }

    /// <summary>A wait handle a timer is disposed with, held by a collection too, proves no quiet event: the pair the proof would
    /// remove stays, as it goes without the collection.</summary>
    private static bool BreaksQuietEventProof(Storage storage)
    {
        const string timer = "var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new ManualResetEvent(false); ";
        const string wait = "timer.Dispose(done); done.WaitOne(); state.P1();";

        return Overlap(Analyze(TimerWorker(timer + $"var box = {storage.New("WaitHandle")}; {storage.Put("box", "done")} " + wait))) &&
               !Overlap(Analyze(TimerWorker(timer + wait)));
    }

    /// <summary>A lock on what a collection holds, when a gap's result went in, is decided by the gap as a lock on its result is.</summary>
    private static async Task<bool> FollowsGapOrigin(Storage storage)
    {
        var result = await PhaseOneAnalyzer.AnalyzeAsync(Solution($$"""
            using System.Collections.Generic;

            public sealed class Work { public int Count; public readonly {{storage.Type("object")}} Gates = {{storage.New("object")}}; }
            public sealed class Locker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{storage.Put("work.Gates", "Opaque.Lib.Gate(work)")}}
                    {{storage.Read("work.Gates", "lock (held) { work.Count = 1; }")}}
                    return Task.CompletedTask;
                }
            }
            public sealed class Writer(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { work.Count = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<Work>(); services.AddHostedService<Locker>(); services.AddHostedService<Writer>();")),
                                                         ROOT_DIRECTORY, CancellationToken.None);

        return result.Findings.Any(finding => string.Join(".", finding.Resource.AccessPath) == "Count" &&
                                              !finding.AccessA.Operation.IsUnknownEffect() && !finding.AccessB.Operation.IsUnknownEffect() &&
                                              finding.Confidence.Components.Protection == 10);
    }

    /// <summary>An unknown effect on an object a shared collection holds is a read and a write of it.</summary>
    private static bool UnknownEffectWrites(Storage storage) =>
        Analyze(State(storage, storage.Read("_state.Box", "Opaque.Lib.Touch(held);")))
            .Of("Hits")
            .Any(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Resource.Region == "alloc:State..ctor()#Loose" &&
                           access.Operation == AccessOperation.UnknownEffect);

    /// <summary>A deep read of a collection reads the objects it holds.</summary>
    private static bool DeepReadReaches(Storage storage) =>
        Analyze(State(storage, "System.Text.Json.JsonSerializer.Serialize(_state.Box);"))
            .Of("Hits")
            .Any(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Resource.Region == "alloc:State..ctor()#Loose" &&
                           access.Operation == AccessOperation.Read);

    /// <summary>A collection handed to an unresolved call hands over the delegates it holds, each run in an unknown execution.</summary>
    private static bool HandsOverDelegate(Storage storage)
    {
        var run = Analyze(State(storage, $"var box = {storage.New("Action")}; {storage.Put("box", "() => _state.Target.Hits = 1")} Opaque.Lib.Touch(box);"));
        return run.Of("Hits").Any(access => access.Operation == AccessOperation.Write &&
                                            run.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.UnknownDelegateCall);
    }

    /// <summary>A task read back out of a collection is no handle whose store is proven to reach the join, as one read straight
    /// back is.</summary>
    private static bool IsNoProvenJoinHandle(Storage storage) =>
        Overlap(Analyze(TimerWorker($"var box = {storage.New("Task")}; {storage.Put("box", "Task.Run(state.F)")} " +
                                    storage.Read("box", "await held; state.P1();")))) &&
        !Overlap(Analyze(TimerWorker("var task = Task.Run(state.F); await task; state.P1();")));

    // ---- fixtures ----

    /// <summary>A singleton <c>State</c> whose box holds a <c>Loose</c> object nothing else holds, put in at construction, and a
    /// <c>Target</c> of its own; a worker does <paramref name="work"/> while another reads the box's objects' <c>Hits</c>.</summary>
    private static string State(Storage storage, string work) => $$"""
        using System.Collections.Generic;

        public class Loose { public int Hits; }
        public sealed class Aim { public int Hits; }

        public sealed class State
        {
            public readonly Aim Target = new Aim();
            public readonly {{storage.Type("Loose")}} Box = {{storage.New("Loose")}};

            public State()
            {
                {{storage.Put("Box", "new Loose()")}}
            }
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }

        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{storage.Read("_state.Box", "_ = held.Hits;")}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();");

    /// <summary>A worker over a singleton whose helpers each write <c>Value</c>, as the ordering tests of timers and joins name them.</summary>
    private static string TimerWorker(string body) => $$"""
        using System.Collections.Generic;

        public sealed class State
        {
            public int Value;
            public void P1() => Value = 1;
            public void F() => Value = 3;
        }

        public sealed class Worker(State state) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();");

    private static bool Overlap(EngineRun run) =>
        run.PairsOn("Value").Any(pair => Is(pair.First, "P1") && Is(pair.Second, "F") || Is(pair.First, "F") && Is(pair.Second, "P1"));

    private static bool Is(Access access, string helper) => access.Symbol.EndsWith($".{helper}()", StringComparison.Ordinal);

    private static int Line(string source, string text) =>
        System.Array.FindIndex((Usings + source).Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1;

    private static EngineRun Analyze(string source) => AnalyzeScope(Solution(source), "scope:Fixture");

    private static Solution Solution(string source) =>
        FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", Usings + source));

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Touch(object value) { }
                public static object Gate(object owner) => new();
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
