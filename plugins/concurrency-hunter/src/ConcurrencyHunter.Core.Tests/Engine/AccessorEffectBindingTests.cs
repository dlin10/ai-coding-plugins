using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A call of an accessor without a body in the run that a library model describes binds the model's effects to its own
/// arguments (R4): the indexer arguments, the value a setter is given and the handler an event accessor is given, by parameter
/// ordinal, whatever form calls the accessor. An accessor without a model stays the opaque call it was.</summary>
public sealed class AccessorEffectBindingTests
{
    [Fact]
    public void Setter_value_reads_deep_reads_the_assigned_object_in_the_callers_execution()
    {
        var run = Run("var box = new Acc.Box(); box.Value = _state.Token;");
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.Read &&
                                                       run.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.Root);

        // The same assignment to a setter without a model reads nothing: its call stays opaque.
        Assert.DoesNotContain(Worker(Run("var box = new Acc.Box(); box.Plain = _state.Token;"), "Hits"),
                              access => access.Operation == AccessOperation.Read);
    }

    [Fact]
    public void Init_setter_in_an_object_initializer_reads_deep_the_assigned_object()
    {
        var run = Run("var box = new Acc.Box { Seed = _state.Token };");
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.Read);
    }

    [Fact]
    public void Setter_keeping_its_value_in_this_hands_it_back_through_the_getter_of_kept_this()
    {
        var run = Run("var box = new Acc.Box(); box.Kept = new Item(); ((Item)box.Kept).Hits = 1;");
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.Write);

        // A getter without the result clause hands back nothing the setter kept.
        Assert.DoesNotContain(Worker(Run("var box = new Acc.Box(); box.Kept = new Item(); ((Item)box.Plain).Hits = 1;"), "Hits"),
                              access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void Indexer_setter_key_reads_deep_reads_the_key_object()
    {
        var run = Run("var table = new Acc.Table(); table[_state.Token] = null;");
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.Read);
        AssertBound(run, "set_Item", 0);
    }

    [Fact]
    public void Indexer_increment_binds_the_key_of_both_its_getter_and_its_setter()
    {
        var run = Run("var counter = new Acc.Counter(); counter[_state.Token]++;");
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.Read);
        var key = AssertBound(run, "get_Item", 0);
        Assert.Equal(key, AssertBound(run, "set_Item", 0));
    }

    [Fact]
    public void Coalescing_assignment_binds_the_setters_value()
    {
        var run = Run("var box = new Acc.Box(); box.Value ??= _state.Token;");
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.Read);
        AssertBound(run, "set_Value", 0);
    }

    [Fact]
    public void Event_add_with_an_invoke_now_fate_runs_the_handler_at_the_subscription()
    {
        var run = Run("var box = new Acc.Box(); box.Changed += () => _state.Count = 1;");
        var write = Assert.Single(Worker(run, "Count"), access => access.Operation == AccessOperation.Write);
        Assert.Equal(ExecutionKind.Root, run.Execution.Analysis.Execution(write.ExecutionId).Kind);

        // An event of the same library without a model hands the handler to an unknown execution instead.
        var opaque = Run("var box = new Acc.Box(); box.Quiet += () => _state.Count = 1;");
        Assert.DoesNotContain(Worker(opaque, "Count"), access => access.Operation == AccessOperation.Write &&
                                                                  opaque.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.Root);
    }

    [Fact]
    public void Ref_of_a_library_indexer_returning_by_reference_binds_the_getters_key()
    {
        var run = Run("var cells = new Acc.Cells(); ref int cell = ref cells[_state.Token]; cell = 1;");
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.Read);
        AssertBound(run, "get_Item", 0);
    }

    [Fact]
    public void Static_setter_value_reads_deep_the_assigned_object()
    {
        var run = Run("Acc.Box.Shared = _state.Token;");
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.Read);
        AssertBound(run, "set_Shared", 0);
    }

    [Fact]
    public void Setter_value_of_an_immutable_type_binds_nothing()
    {
        var run = Run("var box = new Acc.Box(); box.Name = _state.Token.ToString();");
        AssertBound(run, "set_Name", 0, immutable: true);
    }

    // ---- helpers ----

    private static IReadOnlyList<Access> Worker(EngineRun run, string member) =>
        run.Of(member).Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal)).ToArray();

    /// <summary>The value the worker's one call of the library accessor named <paramref name="accessor"/> binds to the effect of its
    /// parameter <paramref name="ordinal"/>, asserting that the effect is bound to exactly the argument the call passes for it.</summary>
    /// <param name="run">The engine run.</param>
    /// <param name="accessor">The accessor's metadata name.</param>
    /// <param name="ordinal">The parameter ordinal.</param>
    /// <param name="immutable">Whether the argument is of an immutable type, which binds nothing.</param>
    private static int? AssertBound(EngineRun run, string accessor, int ordinal, bool immutable = false)
    {
        var call = Assert.Single(run.Execution.Heap.Program.Result.Bodies
                                    .Where(body => body.Value.OwnerSymbol.StartsWith("Worker.", StringComparison.Ordinal))
                                    .SelectMany(body => body.Value.Blocks.SelectMany(block => block.Operations))
                                    .OfType<IrCallOperation>(),
                                 call => call.Library?.MemberId.Contains("." + accessor + "(", StringComparison.Ordinal) == true);
        var effect = Assert.Single(call.Library!.Effects, effect => effect.ParameterOrdinal == ordinal);
        var argument = call.ArgumentAt(ordinal);
        Assert.NotNull(argument);
        if (immutable)
        {
            Assert.Empty(effect.Arguments);
            return null;
        }

        Assert.Equal(argument, Assert.Single(effect.Arguments).Value);
        return argument;
    }

    /// <summary>The models and rejections the resolver makes of a file holding <paramref name="entries"/>.</summary>
    /// <param name="entries">The entry texts.</param>
    private static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections) Resolve(params string[] entries)
    {
        var root = Directory.CreateTempSubdirectory("ch-accessor-binding-").FullName;
        try
        {
            var folder = Path.Combine(root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(Path.Combine(folder, "accessors.json"), File(entries), new UTF8Encoding(false));
            var compilation = Solution("").Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
            var files = ProjectModelFiles.Read(root);
            return ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(root, files));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Solution Solution(string work) =>
        FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", Usings + Source(work)));

    private static EngineRun Run(string work)
    {
        var solution = Solution(work);
        var (models, rejections) = Resolve(ModelEntries());
        Assert.Empty(rejections);
        return AnalyzeScope(solution, "scope:Fixture", models);
    }

    private static string[] ModelEntries() =>
    [
        Entry("Box", ".ctor", ""),
        Entry("Box", "set_Value", """ "effects":{"value":["reads-deep"]}"""),
        Entry("Box", "get_Value", ""),
        Entry("Box", "set_Seed", """ "effects":{"value":["reads-deep"]}"""),
        Entry("Box", "set_Kept", """ "effects":{},"keeps":{"this":["arg:value"]}"""),
        Entry("Box", "get_Kept", """ "effects":{},"result":"[kept:this]" """),
        Entry("Box", "get_Shared", ""),
        Entry("Box", "set_Shared", """ "effects":{"value":["reads-deep"]}"""),
        Entry("Box", "set_Name", """ "effects":{"value":["reads-deep"]}"""),
        Entry("Box", "add_Changed", """ "effects":{},"fates":{"value":{"fate":"invoke-now"}}"""),
        Entry("Table", ".ctor", ""),
        Entry("Table", "set_Item", """ "effects":{"key":["reads-deep"]}"""),
        Entry("Counter", ".ctor", ""),
        Entry("Counter", "get_Item", """ "effects":{"key":["reads-deep"]}"""),
        Entry("Counter", "set_Item", """ "effects":{"key":["reads-deep"]}"""),
        Entry("Cells", ".ctor", ""),
        Entry("Cells", "get_Item", """ "effects":{"key":["reads-deep"]}""")
    ];

    /// <summary>A model entry of the member of <c>Acc.<paramref name="type"/></c> named <paramref name="member"/>; an empty
    /// <paramref name="decision"/> is an entry with no effect.</summary>
    /// <param name="type">The library type's name.</param>
    /// <param name="member">The member's metadata name.</param>
    /// <param name="decision">The entry's clauses, <c>effects</c> included; empty for none.</param>
    private static string Entry(string type, string member, string decision) =>
        "{\"member\":\"" + Id(type, member) + "\"," + (decision.Length == 0 ? "\"effects\":{}" : decision.Trim()) + "}";

    private static string File(params string[] entries) =>
        "{\"schemaVersion\":1,\"assemblies\":[\"Acc\"],\"models\":[" + string.Join(",", entries) + "]}";

    /// <summary>The declaration id of the one member of <c>Acc.<paramref name="type"/></c> with that metadata name.</summary>
    /// <param name="type">The library type's name.</param>
    /// <param name="member">The member's metadata name.</param>
    private static string Id(string type, string member) =>
        DocumentationCommentId.CreateDeclarationId(LibrarySource.Value.GetTypeByMetadataName("Acc." + type)!.GetMembers(member).Single())!;

    /// <summary>A singleton <c>State</c> a hosted worker does <paramref name="work"/> on.</summary>
    /// <param name="work">The worker's statements.</param>
    private static string Source(string work) => $$"""
        public class Item { public int Hits; }

        public sealed class State
        {
            public readonly Item Token = new Item();
            public int Count;
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
        """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Marker>(_ => new Marker()); services.AddHostedService<Worker>();") + """

        public sealed class Marker { }
        """;

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Acc", [CSharpSyntaxTree.ParseText("""
        using System;

        namespace Acc
        {
            public sealed class Box
            {
                public object Value { get; set; }
                public object Seed { get; init; }
                public object Kept { get; set; }
                public object Plain { get; set; }
                public static object Shared { get; set; }
                public string Name { get; set; }
                public event Action Changed;
                public event Action Quiet;
            }

            public sealed class Table
            {
                public object this[object key] { get => null; set { } }
            }

            public sealed class Counter
            {
                public int this[object key] { get => 0; set { } }
            }

            public sealed class Cells
            {
                private int _cell;
                public ref int this[object key] => ref _cell;
            }
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    /// <summary>The library whose accessors have no bodies in the run.</summary>
    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        using var stream = new MemoryStream();
        var emitted = LibrarySource.Value.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
