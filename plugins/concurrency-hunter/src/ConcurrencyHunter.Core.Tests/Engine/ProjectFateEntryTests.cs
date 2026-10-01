using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Project entries carrying a result and fates: the entry step as the file is read, the member step as each entry is
/// resolved, and the rule that makes two entries of one member alike.</summary>
public sealed class ProjectFateEntryTests
{
    private const string MODEL_PATH = ".concurrency-hunter/models/model.json";
    private const string NEIGHBOUR = """{"member":"M:Fates.Lib.Plain(System.Object)","effects":{"value":["reads-deep"]}}""";
    private const string WORK = "Fates.Lib.Plain(_state);";

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Fates", [CSharpSyntaxTree.ParseText("""
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Linq;

        namespace Fates
        {
            public static class Lib
            {
                public static void Plain(object value) { }
                public static void Each(IEnumerable<string> source, Action<string> action) { }
                public static void Numbers(int value, Action<long> action) { }
                public static void Mixed(Version version, Action<string> action) { }
                public static void Produce(Func<int> make, Action<object> use) { }
                public static void After(Action first, Action<object> then) { }
                public static void Take(IEnumerable<string> source, Version other, Action<IEnumerable<string>> action) { }
                public static void Run(Action callback) { }
                public static void Twice(Action first, Action second) { }
                public static void Many(Action[] actions) { }
                public static IEnumerable<string> Filter(IEnumerable<string> source, Func<string, bool> predicate) => null!;
                public static List<string> Collect(IEnumerable<string> source, Version other, Func<string, string> selector) => null!;
                public static Dictionary<string, object> Map(IEnumerable<string> source, Func<string, object> selector) => null!;
                public static IComparer<string> Create(Comparison<string> comparison) => null!;
                public static string First(IEnumerable<string> source, Version other, Func<string, bool> predicate) => null!;
                public static int Size(IEnumerable<string> source, Func<string, bool> predicate) => 0;
                public static IList<string> Listed(IEnumerable<string> source) => null!;
                public static IEnumerable<T> Cast<T>(IEnumerable source) => null!;
                public static IEnumerable<IGrouping<int, string>> Group(IEnumerable<string> source, Func<string, int> key) => null!;
                public static object Aggregate(object seed, Func<object, object, object> func) => null!;
            }

            public sealed class Box
            {
                public Box(Func<object, object> factory) { }
                public void Watch(Func<object, object> callback) { }
                public void Stop(Action callback) { }
            }
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        using var bytes = new MemoryStream();
        var emit = LibrarySource.Value.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    });

    [Fact]
    public async Task Member_with_every_delegate_fated_is_accepted()
    {
        using var repo = new Repository();
        repo.Model(File(
            Entry("Lib.Filter", """ "effects":{},"result":"sequence(elements(arg:source))","fates":{"predicate":{"fate":"iterator","inputs":[["elements(arg:source)"]]}}"""),
            Entry("Lib.Collect", """ "effects":{},"result":"collection(returns:selector)","fates":{"selector":{"fate":"invoke-now","inputs":[["elements(arg:source)"]]}}"""),
            Entry("Lib.Map", """ "effects":{},"result":"dictionary(elements(arg:source),returns:selector)","fates":{"selector":{"fate":"invoke-now","inputs":[["elements(arg:source)"]]}}"""),
            Entry("Lib.Create", """ "effects":{},"fates":{"comparison":{"fate":"holder","holder":"result","inputs":[["holder-arg:0"],["holder-arg:1"]]}}"""),
            Entry("Box.#ctor", """ "effects":{},"fates":{"factory":{"fate":"holder","holder":"result","inputs":[["holder-arg:0"]]}}"""),
            Entry("Box.Watch", """ "effects":{},"fates":{"callback":{"fate":"holder","holder":"this","note":"kept"}}"""),
            Entry("Lib.First", """ "effects":{},"result":"[elements(arg:source)]","fates":{"predicate":{"fate":"invoke-now","inputs":[["elements(arg:source)"]]}}"""),
            Entry("Lib.Cast", """ "effects":{},"result":"sequence(elements(arg:source))" """),
            Entry("Lib.Group", """ "effects":{},"result":"sequence(grouping(returns:key,elements(arg:source)))","fates":{"key":{"fate":"iterator","inputs":[["elements(arg:source)"]]}}"""),
            Entry("Lib.Run", """ "effects":{},"fates":{"callback":{"fate":"startup"}}"""),
            Entry("Lib.Twice", """ "effects":{},"fates":{"first":{"fate":"unknown-execution"},"second":{"fate":"invoke-now","inputs":[]}}"""),
            Entry("Lib.Produce", """ "effects":{},"fates":{"make":{"fate":"invoke-now"},"use":{"fate":"invoke-now","inputs":[["returns:make"]]}}"""),
            Entry("Lib.Aggregate", """ "effects":{},"result":"[arg:seed,returns:func]","fates":{"func":{"fate":"invoke-now","inputs":[["arg:seed","returns:func"],[]]}}"""),
            NEIGHBOUR));
        var result = await Run(repo);

        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.DoesNotContain(Assert.Single(result.Coverage).Diagnostics, diagnostic => diagnostic.StartsWith("library-models:", StringComparison.Ordinal));
        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_PROJECT));
    }

    [Fact]
    public async Task Absent_inputs_are_alike_to_empty_ones()
    {
        using var repo = new Repository();
        repo.Model(File(Entry("Lib.Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now"}}"""),
                        Entry("Lib.Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[[]]}}"""),
                        NEIGHBOUR));
        var result = await Run(repo);

        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        var model = Assert.Single(Resolve(repo).Members, member => member.Id == Id("Lib.Each"));
        Assert.False(model.DeclaredOpaque);
        Assert.Equal(LibraryFateKind.InvokeNow, Assert.Single(model.Fates).Kind);
    }

    [Fact]
    public async Task Fated_entries_alike_in_another_order_count_once()
    {
        using var repo = new Repository();
        repo.Model(File(Entry("Lib.Aggregate", """ "effects":{"seed":["reads-deep"]},"result":"[arg:seed,returns:func]","fates":{"func":{"fate":"invoke-now","inputs":[["arg:seed","returns:func"],[]]}}"""),
                        Entry("Lib.Aggregate", """ "fates":{"func":{"inputs":[["returns:func","arg:seed","arg:seed"],[]],"fate":"invoke-now"}},"result":"[returns:func,arg:seed]","effects":{"seed":["reads-deep"]}"""),
                        NEIGHBOUR));
        var result = await Run(repo);

        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        var model = Assert.Single(Resolve(repo).Members, member => member.Id == Id("Lib.Aggregate"));
        Assert.Equal(ModelLayer.Project, model.Layer);
        Assert.False(model.DeclaredOpaque);
        Assert.Equal("[arg:seed,returns:func]", model.Result?.ToString());
    }

    [Fact]
    public async Task Fated_entries_that_differ_leave_the_member_opaque()
    {
        using var repo = new Repository();
        repo.Model(File(Entry("Lib.Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["elements(arg:source)"]]}}"""),
                        Entry("Lib.Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now"}}"""),
                        NEIGHBOUR));
        var result = await Run(repo, WORK + " Fates.Lib.Each(new[] { \"a\" }, text => _state.Count++);");

        Assert.Equal(2, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Contains(Assert.Single(result.Coverage).Diagnostics, diagnostic => diagnostic.Contains("disagree", StringComparison.Ordinal));
        var model = Assert.Single(Resolve(repo).Members, member => member.Id == Id("Lib.Each"));
        Assert.True(model.DeclaredOpaque);
        Assert.Empty(model.Fates);
    }

    [Fact]
    public async Task Di_factory_is_rejected_until_the_generator()
    {
        using var repo = new Repository();
        repo.Model(File(Entry("Lib.Each", """ "effects":{},"fates":{"action":{"fate":"di-factory"}}"""), NEIGHBOUR));
        var result = await Run(repo);

        AssertRejected(result, Id("Lib.Each"), "di-factory is not supported before phase 5d");
    }

    [Theory]
    [InlineData("repeated result", """ "effects":{},"result":"[arg:source]","result":"[arg:source]" """, "repeated property 'result'")]
    [InlineData("repeated fates", """ "effects":{},"fates":{},"fates":{}""", "repeated property 'fates'")]
    [InlineData("parameter named twice", """ "effects":{},"fates":{"action":{"fate":"invoke-now"},"action":{"fate":"invoke-now"}}""", "repeated property 'action'")]
    [InlineData("property repeated inside a fate", """ "effects":{},"fates":{"action":{"fate":"invoke-now","fate":"invoke-now"}}""", "repeated property 'fate'")]
    [InlineData("fates not an object", """ "effects":{},"fates":[]""", "expected a JSON object")]
    [InlineData("fate not an object", """ "effects":{},"fates":{"action":"invoke-now"}""", "expected a JSON object")]
    [InlineData("no fate", """ "effects":{},"fates":{"action":{}}""", "needs a fate")]
    [InlineData("fate not a string", """ "effects":{},"fates":{"action":{"fate":1}}""", "fate must be a string")]
    [InlineData("unknown fate", """ "effects":{},"fates":{"action":{"fate":"soon"}}""", "unknown fate 'soon'")]
    [InlineData("holder without holder", """ "effects":{},"fates":{"action":{"fate":"holder"}}""", "needs its holder")]
    [InlineData("holder neither result nor this", """ "effects":{},"fates":{"action":{"fate":"holder","holder":"field"}}""", "holder must be result or this")]
    [InlineData("holder on another fate", """ "effects":{},"fates":{"action":{"fate":"startup","holder":"this"}}""", "holder goes only with the holder fate")]
    [InlineData("inputs not an array", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":{}}}""", "inputs must be an array of arrays of strings")]
    [InlineData("value not a string", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[[1]]}}""", "inputs must be an array of arrays of strings")]
    [InlineData("value that does not parse", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["elements(arg:source"]]}}""", "does not parse")]
    [InlineData("holder-arg outside a holder", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["holder-arg:0"]]}}""", "stands only alone")]
    [InlineData("returns naming no fate", """ "effects":{},"result":"[returns:other]","fates":{"action":{"fate":"invoke-now"}}""", "names no fate")]
    [InlineData("iterator without a sequence", """ "effects":{},"fates":{"action":{"fate":"iterator"}}""", "needs a sequence")]
    [InlineData("result not a string", """ "effects":{},"result":["arg:source"]""", "result must be a string")]
    [InlineData("opaque with fates", """ "opaque":true,"fates":{}""", "go only with effects")]
    [InlineData("unknown fate property", """ "effects":{},"fates":{"action":{"fate":"invoke-now","runs":"now"}}""", "unknown property 'runs'")]
    [InlineData("fate key that is no name", """ "effects":{},"fates":{"action!":{"fate":"invoke-now"}}""", "keyed by parameter names")]
    public async Task Rejected_project_fate_entries(string name, string decision, string reason)
    {
        Assert.NotEmpty(name);
        using var repo = new Repository();
        repo.Model(File(Entry("Lib.Each", decision.TrimEnd()), NEIGHBOUR));

        AssertRejected(await Run(repo), Id("Lib.Each"), reason);
    }

    [Theory]
    [InlineData("delegate without a fate", "Lib.Each", """ "effects":{}""", "delegate-typed parameter 'action' has no fate")]
    [InlineData("fate on a parameter that is not a delegate", "Lib.Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now"},"source":{"fate":"invoke-now"}}""", "'source', which is not delegate-typed")]
    [InlineData("fate on a missing parameter", "Lib.Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now"},"missing":{"fate":"invoke-now"}}""", "'missing', which is no parameter")]
    [InlineData("inputs of the wrong length", "Lib.Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[[],[]]}}""", "name 2 parameters; its delegate takes 1")]
    [InlineData("arg naming a missing parameter", "Lib.Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["arg:nothing"]]}}""", "arg:nothing names no parameter")]
    [InlineData("arg naming a delegate parameter", "Lib.Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["arg:action"]]}}""", "names a delegate-typed parameter")]
    [InlineData("elements of a parameter that is not enumerable", "Lib.Numbers", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["elements(arg:value)"]]}}""", "int is not enumerable")]
    [InlineData("elements of a return that is not enumerable", "Lib.Produce", """ "effects":{},"fates":{"make":{"fate":"invoke-now"},"use":{"fate":"invoke-now","inputs":[["elements(returns:make)"]]}}""", "int is not enumerable")]
    [InlineData("returns of a void delegate", "Lib.After", """ "effects":{},"fates":{"first":{"fate":"invoke-now"},"then":{"fate":"invoke-now","inputs":[["returns:first"]]}}""", "returns void")]
    [InlineData("input that does not convert", "Lib.Mixed", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["arg:version"]]}}""", "System.Version, which does not convert to string")]
    [InlineData("numeric input to another numeric type", "Lib.Numbers", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["arg:value"]]}}""", "int, which does not convert to long")]
    [InlineData("sequence whose later value does not convert", "Lib.Take", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["sequence(elements(arg:source),arg:other)"]]}}""", "System.Version, which does not convert to string")]
    [InlineData("collection value that does not convert", "Lib.Collect", """ "effects":{},"result":"collection(arg:other)","fates":{"selector":{"fate":"invoke-now"}}""", "System.Version, which does not convert to string")]
    [InlineData("one-of value that does not convert", "Lib.First", """ "effects":{},"result":"[arg:other]","fates":{"predicate":{"fate":"invoke-now"}}""", "System.Version, which does not convert to string")]
    [InlineData("holder this on a static method", "Lib.Create", """ "effects":{},"fates":{"comparison":{"fate":"holder","holder":"this"}}""", "holder this needs an instance method")]
    [InlineData("holder this on a constructor", "Box.#ctor", """ "effects":{},"fates":{"factory":{"fate":"holder","holder":"this"}}""", "holder this needs an instance method")]
    [InlineData("holder result on a void method", "Box.Stop", """ "effects":{},"fates":{"callback":{"fate":"holder","holder":"result"}}""", "holder result needs")]
    [InlineData("holder result on a method returning int", "Lib.Size", """ "effects":{},"fates":{"predicate":{"fate":"holder","holder":"result"}}""", "holder result needs")]
    [InlineData("collection outside ADR 0010", "Lib.Listed", """ "effects":{},"result":"collection(elements(arg:source))" """, "collection(…) needs")]
    [InlineData("dictionary on another type", "Lib.Collect", """ "effects":{},"result":"dictionary(elements(arg:source),returns:selector)","fates":{"selector":{"fate":"invoke-now"}}""", "dictionary(…) needs")]
    [InlineData("sequence on a type that is no IEnumerable interface", "Lib.Collect", """ "effects":{},"result":"sequence(returns:selector)","fates":{"selector":{"fate":"iterator"}}""", "sequence(…) needs")]
    [InlineData("array of delegates", "Lib.Many", """ "effects":{}""", "array of delegates")]
    [InlineData("one-of on a void member", "Lib.Each", """ "effects":{},"result":"[arg:source]","fates":{"action":{"fate":"invoke-now"}}""", "needs a member that returns something")]
    public async Task Rejected_project_fate_members(string name, string member, string decision, string reason)
    {
        Assert.NotEmpty(name);
        using var repo = new Repository();
        repo.Model(File(Entry(member, decision.TrimEnd()), NEIGHBOUR));

        AssertRejected(await Run(repo), Id(member), reason);
    }

    /// <summary>One rejection, counted and named in a diagnostic line with its reason, and the neighbour entry still applied.</summary>
    private static void AssertRejected(AnalysisResult result, string member, string reason)
    {
        var coverage = Assert.Single(result.Coverage);
        Assert.Equal(1, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Contains(coverage.Diagnostics, diagnostic => diagnostic.StartsWith($"library-models: {MODEL_PATH}: {member}: ", StringComparison.Ordinal) &&
                                                            diagnostic.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_PROJECT));
    }

    /// <summary>The declaration id of a member of the library, <c>Lib.Each</c> or <c>Box.#ctor</c>; each has one overload.</summary>
    private static string Id(string member)
    {
        var (type, name) = (member[..member.IndexOf('.')], member[(member.IndexOf('.') + 1)..]);
        var symbol = LibrarySource.Value.GetTypeByMetadataName("Fates." + type)!.GetMembers(name == "#ctor" ? ".ctor" : name).Single();
        return DocumentationCommentId.CreateDeclarationId(symbol)!;
    }

    private static string Entry(string member, string decision) => "{\"member\":\"" + Id(member) + "\"," + decision + "}";

    private static string File(params string[] entries) =>
        "{\"schemaVersion\":1,\"assemblies\":[\"Fates\"],\"models\":[" + string.Join(",", entries) + "]}";

    private static int Counter(AnalysisResult result, string name) => Assert.Single(result.Coverage).Skips.GetValueOrDefault(name);

    private static Solution Solution(string work) =>
        FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", Source(work)));

    private static Task<AnalysisResult> Run(Repository repo, string work = WORK) =>
        PhaseOneAnalyzer.AnalyzeAsync(Solution(work), ROOT_DIRECTORY, repo.Root, CancellationToken.None);

    /// <summary>The models the resolver makes of the repository's files for the fixture's compilation.</summary>
    private static LibraryModels Resolve(Repository repo)
    {
        var compilation = Solution(WORK).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        var files = ProjectModelFiles.Read(repo.Root);
        return ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(repo.Root, files)).Models;
    }

    private static string Source(string work) => Usings + $$"""
        public sealed class State
        {
            public int Count = 1;
        }

        public sealed class StateWorker : BackgroundService
        {
            private readonly State _state;
            public StateWorker(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<StateWorker>();");

    private sealed class Repository : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ch-fates-").FullName;

        public void Model(string text)
        {
            var models = Path.Combine(Root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(models);
            System.IO.File.WriteAllText(Path.Combine(models, "model.json"), text, new UTF8Encoding(false));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
