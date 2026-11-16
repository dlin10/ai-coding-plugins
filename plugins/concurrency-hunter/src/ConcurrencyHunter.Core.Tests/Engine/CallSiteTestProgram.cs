using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

internal sealed class CallSiteTestProgram
{
    internal const string LIBRARY = "CallSiteLibrary";
    internal const string LIBRARY_SOURCE = """
        using System;
        using System.Threading.Tasks;
        public class Policy
        {
            public Policy() { }
            public Policy(object value) { }
            public void Execute(Action action) { action(); }
            public void ValueSet(int value) { }
            public int Value { get => 0; set { } }
            public virtual Policy Virtual() => this;
        }
        public static class L
        {
            public static Policy Make() => new Policy();
            public static Policy Wrap(this Policy value) => value;
            public static Policy Other() => Make();
            public static void Touch(object value) { }
            public static void Out(out object value) { value = new object(); }
            public static void Invoke(Action action) { action(); }
            public static Task Start(object value) => Task.CompletedTask;
            public static Task WrapTask(Task value, object owner) => value;
            public static object Gate(object owner) => new object();
            public static System.Timers.Timer Timer(object owner) => new();
        }
        """;

    internal Compilation Project { get; }
    internal Solution Solution { get; }
    internal CSharpCompilation Library { get; }
    private CSharpCompilation? Dependency { get; }
    private readonly LibraryModels _models;
    internal Dictionary<LoweringKey, IrLoweredMethod?> Cache { get; } = [];

    internal CallSiteTestProgram(string work, string members = "", string extra = "", string? library = null, bool worker = false, bool injectHolder = false,
                                 string? models = null, string? dependency = null)
    {
        var dependencies = new List<MetadataReference>();
        if (dependency is not null)
        {
            var compiled = ((CSharpCompilation)FixtureSolution.Create(("Dependency.cs", dependency))
                                          .Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!).WithAssemblyName("OtherLibrary");
            Dependency = compiled;
            using var image = new MemoryStream();
            var result = compiled.Emit(image);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            dependencies.Add(MetadataReference.CreateFromImage(image.ToArray()));
        }
        Library = ((CSharpCompilation)FixtureSolution.Create(new FixtureOptions { AllowUnsafe = true, MetadataReferences = dependencies }, ("Library.cs", library ?? LIBRARY_SOURCE))
                                  .Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!).WithAssemblyName(LIBRARY);
        using var pe = new MemoryStream();
        var emitted = Library.Emit(pe);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var reference = MetadataReference.CreateFromImage(pe.ToArray()).WithAliases(["global", "recognized"]);
        var source = "extern alias recognized;\n" + Usings + (worker
            ? "public sealed class RootController(State state) : BackgroundService { protected override async Task ExecuteAsync(CancellationToken token) { " +
              work + " await Task.CompletedTask; } " + members + " }"
            : "public sealed class RootController(State state" + (injectHolder ? ", Holder holder" : string.Empty) + ") : ControllerBase { public void Get() { " + work + " } " + members + " }") +
            "public sealed class State { public int Value; } " + extra +
            Startup("services.AddSingleton<State>();" + (worker ? "services.AddHostedService<RootController>();" : string.Empty) +
                (injectHolder ? "services.AddSingleton<Holder>();" : string.Empty));
        Solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [reference, .. dependencies], AllowUnsafe = true }, ("Case.cs", source));
        Project = Solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.Empty(Project.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        _models = models is null ? LibraryModels.BuiltIn : ModelCellFixture.Resolve(Solution, models);
    }

    internal ScopeRun Run(CallSiteAdmission? admission = null, bool countOnly = false) =>
        ScopePipeline.Run("scope:Fixture", [Project], [(Project, null)], ROOT_DIRECTORY, ProviderRegistry.BuiltIn, _models,
            AnalysisLimits.Default, Cache, countOnly ? 0 : null, CancellationToken.None,
            reachability: ReachabilityRules.ClassHierarchy with { Admission = admission }, libraryCompilations: admission is null ? [] : [Library]);

    internal DecidingCalls Selector(CallSiteCompilations compilations, string origin = "package", NoTargetReason? refusal = null) =>
        new(compilations, new GenerationSetup(ModelGeneration.None), CancellationToken.None, (method, _) => Target(method, origin, refusal));

    internal CallSiteLibrary Target(IMethodSymbol? method, string origin = "package", NoTargetReason? refusal = null)
    {
        var library = Dependency is { } dependency && method?.ContainingAssembly.Name == dependency.AssemblyName ? dependency : Library;
        return new(method is null || method.ContainingAssembly.Name == library.AssemblyName ? origin : "framework",
                refusal is { } reason ? new NoGenerationTarget(reason) : method is null ? new NoGenerationTarget(NoTargetReason.AmbiguousId) :
                LibraryModels.IsRecognizedType(LibraryModels.TypeOf(method.GetDocumentationCommentId()!)) ? new NoGenerationTarget(NoTargetReason.Recognized) :
                new GenerationTarget(method.GetDocumentationCommentId()!,
                    DocumentationCommentId.CreateDeclarationId(method)!, null!, null!),
                new LibraryCompilationResult(library, null, 0, 0, new HashSet<string>(), new HashSet<string>(), []));
    }

    internal CallSiteRounds Rounds(CallSiteCompilations compilations, int? bound = null) => new(Selector(compilations), [Project],
        (admission, _) => Run(admission, countOnly: true), bound);

    internal static PairAnalysis Pairs(ScopeRun run)
    {
        var pairs = InterproceduralPairing.Pair(run.Collection!.Accesses, run.Executions!, run.Heap!);
        return pairs with { Pairs = GapDecisions.Mark(pairs.Pairs, run.Interprocedural!, run.Collection.Coverage.Gaps) };
    }

    internal static IReadOnlyList<CallIdentity> Calls(ScopeRun run, string member) => run.Reachable.Bodies.Values
        .Where(body => body.BodyId.StartsWith("body:Fixture:", StringComparison.Ordinal))
        .SelectMany(body => body.CallIdentities.Values).Where(call => call.Target.Contains(member, StringComparison.Ordinal)).ToArray();

    internal static CallSiteAdmission Admit(params CallIdentity[] calls) => new(calls.ToDictionary(call => call, call => call.Target));
}
