using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The <c>not-run</c> fate (ADR 0015, R5): the call neither runs nor keeps the delegate. It carries no holder and no inputs,
/// no <c>returns:</c> value may name it, and in a run it reaches nothing while the call stays known; a delegate the receiver already
/// holds still runs by the holder rule, and the same delegate passed to another parameter follows that parameter's fate.</summary>
public sealed class NotRunFateTests
{
    [Fact]
    public void Not_run_is_parsed_and_written_back_by_the_entry_writer()
    {
        var files = ProjectModelFiles.Read("model.json", Encoding.UTF8.GetBytes(File(Entry("Drop", """ "fates":{"action":{"fate":"not-run"}}"""))));
        var entry = Assert.Single(files.Entries);
        Assert.Empty(files.Rejections);
        var fate = Assert.Single(entry.Fates);
        Assert.Equal(LibraryFateKind.NotRun, fate.Kind);
        Assert.Null(fate.Holder);
        Assert.Null(fate.Inputs);

        var written = ModelEntryWriter.File(new LibraryModel(entry.Member, [new("Fates", new Version(0, 0, 0, 0), new Version(100, 0, 0, 0))],
                                                             entry.Effects) { Fates = entry.Fates });
        Assert.Contains("\"fate\": \"not-run\"", Encoding.UTF8.GetString(written));
        var reread = Assert.Single(ProjectModelFiles.Read("model.json", written).Entries);
        Assert.True(LibraryVocabulary.Alike(entry.Result, entry.Fates, reread.Result, reread.Fates, entry.Stores, reread.Stores,
                                            entry.Outputs, reread.Outputs, entry.Keeps, reread.Keeps));
    }

    [Fact]
    public void Not_run_with_a_holder_is_refused()
    {
        AssertRefused(Entry("Watch", """ "fates":{"action":{"fate":"not-run","holder":"this"}}"""), "holder goes only with the holder fate");
    }

    [Fact]
    public void Not_run_with_inputs_is_refused()
    {
        AssertRefused(Entry("Drop", """ "fates":{"action":{"fate":"not-run","inputs":[]}}"""), "not-run carries no inputs");
        AssertRefused(Entry("Visit", """ "fates":{"action":{"fate":"not-run","inputs":[["arg:value"]]}}"""), "not-run carries no inputs");
    }

    [Fact]
    public void Not_run_as_the_source_of_returns_is_refused()
    {
        AssertRefused(Entry("Pipe", """ "fates":{"make":{"fate":"not-run"},"use":{"fate":"invoke-now","inputs":[["returns:make"]]}}"""),
                      "returns:make: the runs of a not-run delegate are not there to be had here.");
    }

    [Fact]
    public void Two_entries_differing_only_by_not_run_conflict()
    {
        var (models, rejections) = Resolve(Entry("Drop", """ "fates":{"action":{"fate":"not-run"}}"""),
                                           Entry("Drop", """ "fates":{"action":{"fate":"unknown-execution"}}"""));

        Assert.Equal(2, rejections.Count);
        Assert.All(rejections, rejection => Assert.Contains("disagree", rejection.Reason));
        var model = Assert.Single(models.Members, member => member.Id == Id("Drop"));
        Assert.True(model.DeclaredOpaque);
        Assert.Empty(model.Fates);
    }

    [Fact]
    public void Not_run_delegate_body_is_not_reached_and_makes_no_access()
    {
        // The same lambda handed to an invoke-now parameter writes, so the absence below is the fate's and not the fixture's.
        var ran = Run("Fates.Lib.Run(() => _state.Count = 1);");
        Assert.Contains(Worker(ran, "Count"), access => access.Operation == AccessOperation.Write);

        var run = Run("Fates.Lib.Drop(() => _state.Count = 1);");
        Assert.Empty(run.Of("Count"));
        Assert.Equal(Run("").Counter(CoverageCounters.REACHABLE_BODIES), run.Counter(CoverageCounters.REACHABLE_BODIES));
        Assert.Empty(run.Execution.Heap.Heap.DelegateHandoffs);
        Assert.Empty(run.Execution.Heap.Heap.UnresolvedDispatches);
        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Not_run_call_counts_as_known_and_not_as_delegate_to_opaque()
    {
        var run = Run("Fates.Lib.Drop(() => _state.Count = 1);");
        var none = Run("");

        Assert.Equal(none.Counter(CoverageCounters.KNOWN_CALL_PROJECT) + 1, run.Counter(CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.Equal(none.Counter(CoverageCounters.KNOWN_CALL) + 1, run.Counter(CoverageCounters.KNOWN_CALL));
        Assert.Equal(none.Counter(CoverageCounters.DELEGATE_TO_OPAQUE), run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
        Assert.Equal(none.Counter(CoverageCounters.OPAQUE_CALL), run.Counter(CoverageCounters.OPAQUE_CALL));
        Assert.Equal(none.Counter(CoverageCounters.SEMANTIC_GAP), run.Counter(CoverageCounters.SEMANTIC_GAP));
    }

    [Fact]
    public void Delegate_passed_to_a_not_run_and_an_invoke_now_parameter_runs_by_the_invoke_now_fate()
    {
        var run = Run("Action work = () => _state.Count = 1; Fates.Lib.Both(work, work);");

        var write = Assert.Single(Worker(run, "Count"), access => access.Operation == AccessOperation.Write);
        Assert.Equal(ExecutionKind.Root, run.Execution.Analysis.Execution(write.ExecutionId).Kind);
        Assert.Empty(Worker(Run("Action work = () => _state.Count = 1; Fates.Lib.Both(work, () => { });"), "Count"));
    }

    [Fact]
    public void Not_run_remove_on_a_holder_runs_what_it_already_holds()
    {
        // The bus hands what it keeps the first argument of a call of its member. The add's first argument is the handler itself, so
        // only the remove, a member of the holder without a body whose own fate is not-run, hands it the token it writes.
        const string ADD = "var bus = new Fates.Bus(); Action<object> handler = value => ((Item)value).Hits = 1; bus.Add(handler);";
        Assert.Contains(Worker(Run(ADD), "Hits"), access => access.Operation == AccessOperation.Write);
        Assert.DoesNotContain(Worker(Run(ADD), "Hits"), WritesToken);

        var run = Run(ADD + " bus.Remove(_state.Token, handler);");
        Assert.Contains(Worker(run, "Hits"), WritesToken);
    }

    private static bool WritesToken(Access access) =>
        access.Operation == AccessOperation.Write && access.Resource.Region.EndsWith("#Item", StringComparison.Ordinal);

    // ---- helpers ----

    private static IReadOnlyList<Access> Worker(EngineRun run, string member) =>
        run.Of(member).Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal)).ToArray();

    /// <summary>One entry refused by the project reader with a reason containing <paramref name="reason"/>.</summary>
    /// <param name="entry">The entry text.</param>
    /// <param name="reason">A part of the expected refusal.</param>
    private static void AssertRefused(string entry, string reason)
    {
        var files = ProjectModelFiles.Read("model.json", Encoding.UTF8.GetBytes(File(entry)));

        Assert.Empty(files.Entries);
        Assert.Contains(reason, Assert.Single(files.Rejections).Reason);
    }

    /// <summary>The models and rejections the resolver makes of a file holding <paramref name="entries"/>.</summary>
    /// <param name="entries">The entry texts.</param>
    private static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections) Resolve(params string[] entries)
    {
        var root = Directory.CreateTempSubdirectory("ch-not-run-").FullName;
        try
        {
            var folder = Path.Combine(root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(Path.Combine(folder, "fates.json"), File(entries), new UTF8Encoding(false));
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
        Entry("Run", """ "fates":{"action":{"fate":"invoke-now"}}"""),
        Entry("Drop", """ "fates":{"action":{"fate":"not-run"}}"""),
        Entry("Both", """ "fates":{"kept":{"fate":"not-run"},"now":{"fate":"invoke-now"}}"""),
        Entry("Bus.#ctor", ""),
        Entry("Bus.Add", """ "fates":{"handler":{"fate":"holder","holder":"this","inputs":[["holder-arg:0"]]}}"""),
        Entry("Bus.Remove", """ "fates":{"handler":{"fate":"not-run"}}""")
    ];

    private static string Entry(string member, string decision) =>
        "{\"member\":\"" + Id(member) + "\",\"effects\":{}" + (decision.Length == 0 ? "" : "," + decision.Trim()) + "}";

    private static string File(params string[] entries) =>
        "{\"schemaVersion\":1,\"assemblies\":[\"Fates\"],\"models\":[" + string.Join(",", entries) + "]}";

    /// <summary>The declaration id of the one member of <c>Fates.Lib</c>, or of <c>Fates.Bus</c> when the name starts with
    /// <c>Bus.</c>, with that name.</summary>
    /// <param name="name">The member's name.</param>
    private static string Id(string name)
    {
        var (type, member) = name.StartsWith("Bus.", StringComparison.Ordinal) ? ("Fates.Bus", name[4..]) : ("Fates.Lib", name);
        var symbol = LibrarySource.Value.GetTypeByMetadataName(type)!.GetMembers(member == "#ctor" ? ".ctor" : member).Single();
        return DocumentationCommentId.CreateDeclarationId(symbol)!;
    }

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

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Fates", [CSharpSyntaxTree.ParseText("""
        using System;

        namespace Fates
        {
            public static class Lib
            {
                public static void Run(Action action) { }
                public static void Drop(Action action) { }
                public static void Both(Action kept, Action now) { }
                public static void Watch(Action action) { }
                public static void Visit(object value, Action<object> action) { }
                public static void Pipe(Func<object> make, Action<object> use) { }
            }

            public sealed class Bus
            {
                public Bus() { }
                public void Add(Action<object> handler) { }
                public void Remove(object token, Action<object> handler) { }
            }
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        using var stream = new MemoryStream();
        var emitted = LibrarySource.Value.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
