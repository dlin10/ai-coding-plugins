using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CallSiteAdmissionTests
{
    private const string LIBRARY = "ProbeLibrary";
    private const string SOURCE = """
        public interface IL { void V(); }
        public class L : IL
        {
            public static int State = 7;
            public static object Seed = new object();
            public static object Read() => Seed;
            public static void M() { State++; }
            public static void Other() { State++; }
            public static void Entry() { Other(); new L().V(); System.Action action = M; action(); }
            public void N() { State++; }
            public virtual void V() { State++; }
            public static void Out(out object value) { value = new object(); }
            public static void Ref(ref object value) { value = new object(); }
            public static void Rewritten(out object value) { value = new object(); }
            public abstract class A { public abstract void Out(out object value); }
            public class C : Microsoft.Extensions.Hosting.BackgroundService
            {
                public C() { State++; }
                protected override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken token) => System.Threading.Tasks.Task.CompletedTask;
                public static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
                {
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<C>(services);
                }
                public static void Main() { State++; }
            }
        }
        """;

    [Fact]
    public void Admitted_entry_called_from_project_code_runs_its_library_body()
    {
        var (before, after) = Run("L.M();", ["M:L.M"]);
        Assert.False(HasInstance(before, "M:L.M"));
        Assert.True(HasInstance(after, "M:L.M"));
        Assert.Null(Call(after, "M:L.M").Library);
    }

    [Fact]
    public void Project_call_to_a_member_not_admitted_keeps_its_model()
    {
        var (before, after) = Run("L.M(); L.Other();", ["M:L.M"]);
        Assert.Equal(Json(Call(before, "M:L.Other").Library), Json(Call(after, "M:L.Other").Library));
        Assert.NotNull(Call(after, "M:L.Other").Library);
        Assert.False(HasInstance(after, "M:L.Other"));
    }

    [Fact]
    public void Interface_call_from_project_code_reaches_no_library_implementation()
    {
        var (before, after) = Run("L.M(); IL x = new L(); x.V();", ["M:L.M", "M:L.V"]);
        Assert.False(HasInstance(before, "M:L.V"));
        Assert.False(HasInstance(after, "M:L.V"));
        Assert.DoesNotContain(after.Reachable.ReachedBodies.Keys, id => id == Id("M:L.V"));
    }

    [Fact]
    public void Delegate_or_work_from_project_code_runs_no_library_body_an_admitted_call_reached()
    {
        var (before, after) = Run("L.M(); var x = new L(); x.N(); Action a = L.M; a(); Action b = x.N; b(); Task.Run(L.M); Task.Run(x.N);",
                                  ["M:L.M", "M:L.N"]);
        Assert.False(HasInstance(before, "M:L.M"));
        Assert.False(HasInstance(before, "M:L.N"));
        Assert.True(HasInstance(after, "M:L.M"));
        Assert.True(HasInstance(after, "M:L.N"));
        Assert.DoesNotContain(after.Heap!.Edges, edge => edge.Reason == "delegate" &&
            after.Heap.Instances[edge.CalleeInstance].BodyId.StartsWith($"body:{LIBRARY}:", StringComparison.Ordinal));
        Assert.All(after.Heap.Spawns, spawn => Assert.Empty(spawn.Callees));
    }

    [Fact]
    public void Call_inside_an_admitted_body_reaches_the_library_member_it_names()
    {
        var (before, after) = Run("L.Entry();", ["M:L.Entry"]);
        Assert.False(HasInstance(before, "M:L.Other"));
        Assert.True(HasInstance(after, "M:L.Other"));
        Assert.True(HasInstance(after, "M:L.V"));
        Assert.True(HasInstance(after, "M:L.M"));
    }

    [Fact]
    public void Library_type_initializer_is_inert_while_the_program_initializer_runs()
    {
        var (before, after) = Run("L.M(); _ = Own.Value;", ["M:L.M"], extra: "public static class Own { public static object Value = new object(); }");
        Assert.DoesNotContain(before.Heap!.TypeInitializers, initializer => initializer.TypeKey.StartsWith(LIBRARY + ":", StringComparison.Ordinal));
        Assert.Contains(after.Heap!.TypeInitializers, initializer => initializer.TypeKey.StartsWith(LIBRARY + ":", StringComparison.Ordinal) && initializer.IsInert);
        Assert.Contains(after.Heap.TypeInitializers, initializer => initializer.TypeKey == "Fixture:Own" && !initializer.IsInert);
        Assert.Contains(before.Heap.TypeInitializers, initializer => initializer.TypeKey == "Fixture:Own" && !initializer.IsInert);
    }

    [Fact]
    public void Admitted_call_writes_no_opaque_out()
    {
        var (before, after) = Run("L.Out(out object value); L.Ref(ref value);", ["M:L.Out(System.Object@)", "M:L.Ref(System.Object@)"]);
        Assert.Contains(Operations(before), operation => operation is IrStoreReferenceOperation { IsUnseenOutput: true });
        Assert.DoesNotContain(Operations(after), operation => operation is IrStoreReferenceOperation { IsUnseenOutput: true });
        Assert.Null(Call(after, "M:L.Out(System.Object@)").Library);
        Assert.DoesNotContain(after.Summaries!.Get("body:Fixture:M:RootController.Get")!.OpaqueCalls, call => call.Callee.Contains("L.Out", StringComparison.Ordinal));
        Assert.True(HasInstance(after, "M:L.Out(System.Object@)"));
    }

    [Fact]
    public void Virtual_call_to_an_admitted_library_member_keeps_its_model()
    {
        var (before, after) = Run("new L().V();", ["M:L.V"]);
        Assert.NotNull(Call(before, "M:L.V").Library);
        Assert.Equal(Json(Call(before, "M:L.V").Library), Json(Call(after, "M:L.V").Library));
        Assert.False(HasInstance(after, "M:L.V"));
    }

    [Fact]
    public void Admitted_member_without_an_executable_body_keeps_its_model_and_opaque_outs()
    {
        var (before, after) = Run("L.A a = null!; a.Out(out object x); L.Rewritten(out object y);", ["M:L.A.Out(System.Object@)", "M:L.Rewritten(System.Object@)"]);
        foreach (var member in new[] { "M:L.A.Out(System.Object@)", "M:L.Rewritten(System.Object@)" })
        {
            Assert.Equal(Json(Call(before, member).Library), Json(Call(after, member).Library));
            Assert.NotNull(Call(after, member).Library);
            Assert.False(HasInstance(after, member));
        }
        Assert.Equal(2, Operations(after).OfType<IrStoreReferenceOperation>().Count(operation => operation.IsUnseenOutput));
    }

    [Fact]
    public void Library_compilation_is_no_project_for_discovery()
    {
        var (before, after) = Run("L.M();", ["M:L.M"], registrations: "services.AddHostedService<L.C>();");
        Assert.Equal(Json(before.Roots), Json(after.Roots));
        Assert.Equal(Json(before.Index.Registrations), Json(after.Index.Registrations));
        Assert.Equal(Json(before.Bindings), Json(after.Bindings));
        Assert.Equal(before.DiscoveryDiagnostics, after.DiscoveryDiagnostics);
        Assert.DoesNotContain(after.Roots, root => root.Entry.BodyKey == Id("M:L.C.Main"));
    }

    [Fact]
    public void Constructor_an_admitted_new_reached_is_not_run_by_DI_or_startup()
    {
        var (before, after) = Run("_ = new L.C();", ["M:L.C.#ctor"], registrations: "services.AddHostedService<L.C>();", libraryRoot: true);
        Assert.False(HasInstance(before, "M:L.C.#ctor"));
        Assert.True(HasInstance(after, "M:L.C.#ctor"));
        Assert.DoesNotContain(after.Reachable.Constructions, construction => construction.ConstructorBodyIds.Contains(Id("M:L.C.#ctor")));
        Assert.False(HasInstance(after, "M:L.C.Main"));
        Assert.All(after.Heap!.Instances.Values.Where(instance => instance.BodyId == Id("M:L.C.#ctor")), instance => Assert.DoesNotContain("startup", instance.Context));
    }

    [Fact]
    public void Library_static_read_by_project_code_runs_no_library_initializer()
    {
        var (before, after) = Run("_ = L.State;", ["M:L.M"]);
        Assert.Empty(before.Heap!.TypeInitializers);
        Assert.Empty(after.Heap!.TypeInitializers);
        var (control, admitted) = Run("_ = L.Seed; Own.Value = L.Read();", ["M:L.Read"],
                                      extra: "public static class Own { public static object? Value; }");
        Assert.Empty(control.Heap!.TypeInitializers);
        Assert.Contains(admitted.Heap!.TypeInitializers, initializer => initializer.TypeKey == LIBRARY + ":L" && initializer.IsInert);
        var read = Assert.Single(admitted.Heap.Instances.Values, instance => instance.BodyId == Id("M:L.Read"));
        var returned = read.Summary.Returns.SelectMany(transfer => transfer.Values.SelectMany(value => admitted.Heap.Resolve(read.Id, value)))
                           .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(returned);
        Assert.All(returned, region => Assert.Equal(Id("M:L.#cctor"), admitted.Heap.Regions[region].SiteBodyId));
        var libraryStatic = Assert.Single(admitted.Heap.Regions.Values, region => region.Kind == HeapRegionKind.Static && region.TypeKey == LIBRARY + ":L");
        var programStatic = Assert.Single(admitted.Heap.Regions.Values, region => region.Kind == HeapRegionKind.Static && region.TypeKey == "Fixture:Own");
        Assert.Equal(returned, admitted.Heap.PointsTo(libraryStatic.Identity, "Seed").Order(StringComparer.Ordinal));
        Assert.Equal(returned, admitted.Heap.PointsTo(programStatic.Identity, "Value").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Another_call_of_the_admitted_member_keeps_its_model_and_its_opaque_outs()
    {
        var (before, after) = Run("L.Out(out object first); L.Out(out object second);", ["M:L.Out(System.Object@)"], entryCount: 1);
        var beforeCalls = Operations(before).OfType<IrCallOperation>().Where(call => call.TargetMethodId == Id("M:L.Out(System.Object@)")).ToArray();
        var afterCalls = Operations(after).OfType<IrCallOperation>().Where(call => call.TargetMethodId == Id("M:L.Out(System.Object@)")).ToArray();
        Assert.Equal(2, afterCalls.Length);
        Assert.Null(afterCalls[0].Library);
        Assert.NotNull(afterCalls[1].Library);
        Assert.Equal(beforeCalls[1].Library!.Layer, afterCalls[1].Library!.Layer);
        Assert.Equal(beforeCalls[1].Library!.Outputs.Keys, afterCalls[1].Library!.Outputs.Keys);
        Assert.Equal(beforeCalls[1].Library!.Outputs[0].Leaf.Kind, afterCalls[1].Library!.Outputs[0].Leaf.Kind);
        Assert.Equal(beforeCalls[1].Library!.Outputs[0].Leaf.Values, afterCalls[1].Library!.Outputs[0].Leaf.Values);
        Assert.Single(Operations(after).OfType<IrStoreReferenceOperation>(), store => store.IsUnseenOutput);
        var summary = after.Summaries!.Get("body:Fixture:M:RootController.Get")!;
        Assert.Single(summary.Calls, call => call.Target == Id("M:L.Out(System.Object@)"));
        Assert.Single(summary.OpaqueCalls, call => call.Callee.Contains("L.Out", StringComparison.Ordinal));
    }

    private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string Id(string member) => $"body:{LIBRARY}:{member}";

    private static bool HasInstance(ScopeRun run, string member) => run.Heap!.Instances.Values.Any(instance => instance.BodyId == Id(member));

    private static IEnumerable<IrOperation> Operations(ScopeRun run) => run.Reachable.Bodies.Values
        .Where(body => body.BodyId.StartsWith("body:Fixture:", StringComparison.Ordinal)).SelectMany(body => body.Blocks).SelectMany(block => block.Operations);

    private static IrCallOperation Call(ScopeRun run, string member) => Assert.Single(Operations(run).OfType<IrCallOperation>(), call => call.TargetMethodId == Id(member));

    private static (ScopeRun Before, ScopeRun After) Run(string work, string[] entries, string extra = "", string registrations = "",
                                                         bool libraryRoot = false, int entryCount = int.MaxValue)
    {
        var original = (CSharpCompilation)FixtureSolution.Create(("Library.cs", SOURCE)).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        original = original.WithAssemblyName(LIBRARY);
        using var pe = new MemoryStream();
        var emitted = original.Emit(pe);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var reference = MetadataReference.CreateFromImage(pe.ToArray());
        // The library compilation has rewritten this body as extern; the project's PE still declares the original member.
        var library = original.RemoveAllSyntaxTrees().AddSyntaxTrees(CSharpSyntaxTree.ParseText(SOURCE.Replace(
            "public static void Rewritten(out object value) { value = new object(); }", "public static extern void Rewritten(out object value);"), path: @"C:\fixture\Library.cs"));
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [reference] },
            ("Case.cs", Usings + "public class RootController : ControllerBase { public void Get() { " + work + " } }" + extra + Startup(registrations)));
        var project = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        var methods = ProgramIndexBuilder.Build("probe", [library], ROOT_DIRECTORY, CancellationToken.None).Methods;
        var ids = methods.Where(method => method.HasSourceBody && entries.Contains(method.MethodId[($"body:{LIBRARY}:").Length..], StringComparer.Ordinal))
                         .Select(method => method.MethodId).ToHashSet(StringComparer.Ordinal);
        var models = new LibraryModels(methods.Where(method => method.MethodId.StartsWith($"body:{LIBRARY}:", StringComparison.Ordinal)).Select(method => new LibraryModel(
            DocumentationCommentId.CreateDeclarationId((IMethodSymbol)DocumentationCommentId.GetFirstSymbolForDeclarationId(
                method.MethodId[($"body:{LIBRARY}:").Length..], original)!)!,
            [new SupportedAssemblyVersion(LIBRARY, new Version(0, 0, 0, 0), new Version(1, 0, 0, 0))], [])
            {
                Outputs = method.MethodId == Id("M:L.Out(System.Object@)") || method.MethodId == Id("M:L.Ref(System.Object@)")
                    ? new Dictionary<string, LibraryResult> { ["value"] = new(LibraryResultKind.New, []) } : new Dictionary<string, LibraryResult>()
            }), []);
        var observed = new List<IReadOnlyList<Compilation>>();
        var selected = new Dictionary<CallIdentity, string>();
        ScopeRun Pipeline(bool admitted) => ScopePipeline.Run("scope:Fixture", [project], [(project, null)], ROOT_DIRECTORY,
            new ProviderRegistry([.. ProviderRegistry.BuiltIn.Providers, new DiscoveryObserver(observed, libraryRoot)]), models,
            AnalysisLimits.Default, new(), null, CancellationToken.None,
            reachability: ReachabilityRules.ClassHierarchy with { Admission = admitted ? new CallSiteAdmission(LIBRARY, ids) { Calls = selected } : null },
            libraryCompilations: admitted ? [library] : []);
        var before = Pipeline(false);
        selected = before.Reachable.Bodies.Values.Where(body => body.BodyId.StartsWith("body:Fixture:", StringComparison.Ordinal))
                         .SelectMany(body => body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                             .Where(call => call.CallKind is not (IrCallKind.Virtual or IrCallKind.Interface) && call.TargetMethodId is { } target && ids.Contains(target))
                             .Select(call => body.CallIdentities[call.Id]))
                         .Take(entryCount).ToDictionary(call => call, call => call.Target);
        var after = Pipeline(true);
        Assert.Equal(2, observed.Count);
        Assert.All(observed, compilations => Assert.Same(project, Assert.Single(compilations)));
        // These are the project-only inputs of model resolution, diagnostics and candidate lookup, beside the pipeline's discovery.
        var files = new ProjectModelFiles([
            new ProjectModelEntry("probe.json", 0, "M:L.Other", [LIBRARY], null, [], false),
            new ProjectModelEntry("probe.json", 1, "M:RootController.Get", ["Fixture"], null, [], false)
        ], [], new HashSet<string>(), false);
        var resolutions = observed.Select(compilations => ProjectModelResolver.Resolve(files, compilations, ModelLock.Read(ROOT_DIRECTORY, files))).ToArray();
        Assert.Equal(Json(resolutions[0].Rejections), Json(resolutions[1].Rejections));
        Assert.Single(resolutions[0].Rejections);
        Assert.Single(resolutions[1].Models.Members, member => member.Layer == ModelLayer.Project);
        Assert.Equal(Json(LibraryModels.BuiltIn.OutOfRangeReferences(observed[0].Single())),
                     Json(LibraryModels.BuiltIn.OutOfRangeReferences(observed[1].Single())));
        using var pass = GenerationPass.Create(new GenerationSetup(ModelGeneration.ReadOnly), ROOT_DIRECTORY, CancellationToken.None)!;
        Assert.All(pass.Candidates(after, observed[1]), candidate => Assert.Same(project, candidate.Compilation));
        return (before, after);
    }
    private sealed class DiscoveryObserver(List<IReadOnlyList<Compilation>> observed, bool libraryRoot) : IExecutionRootProvider
    {
        public string ProviderId => "probe";
        public IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions =>
            [new SupportedAssemblyVersion(LIBRARY, new Version(0, 0, 0, 0), new Version(1, 0, 0, 0))];

        public RootDiscoveryResult Discover(RootDiscoveryContext context)
        {
            observed.Add(context.Compilations);
            if (!libraryRoot)
                return new RootDiscoveryResult(RootDiscoveryStatus.Checked, [], []);
            var root = new ExecutionRootDescriptor("probe:library-constructor", "probe", ProviderId,
                new RootEntry(Id("M:L.C.#ctor"), "L.C..ctor()", "library constructor", new SourceSpan("Library.cs", 1, 1, 1, 1)),
                new InstanceBindings(ReceiverKind.None, []),
                new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, context.ScopeId), [], [], [], []);
            return new RootDiscoveryResult(RootDiscoveryStatus.Checked, [root], []);
        }
    }
}
