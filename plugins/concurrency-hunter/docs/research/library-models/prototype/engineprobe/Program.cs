// Runs concurrency-hunter's real engine on one user program twice: the library as metadata (today: an opaque call) and the
// library as decompiled source in a project of its own. Research prototype; the repository is not changed.
using System.Diagnostics;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

var root = args[0];
if (args.Length > 1 && args[1] == "gen")
{
    Generator.Run(root);
    return;
}

if (args.Length > 1 && args[1] == "repro")
{
    Repro.Run();
    return;
}

if (args.Length > 1 && args[1] == "repro-e2e")
{
    Repro.EndToEnd();
    return;
}

if (args.Length > 1 && args[1].StartsWith("synth-", StringComparison.Ordinal))
{
    Synth.Run(root, args[1]["synth-".Length..]);
    return;
}

var pkg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var polly = ("Polly", File.ReadAllText(Path.Combine(root, "decompiled", "Polly.cs")),
             MetadataReference.CreateFromFile(Path.Combine(pkg, "polly", "7.2.3", "lib", "netstandard2.0", "Polly.dll")));
var protobuf = ("Google.Protobuf", File.ReadAllText(Path.Combine(root, "decompiled", "Google.Protobuf.cs")),
                MetadataReference.CreateFromFile(Path.Combine(pkg, "google.protobuf", "3.21.9", "lib", "net5.0", "Google.Protobuf.dll")));

var none = ((string)null, (string)null, (MetadataReference)null);
const string RUNNERS = """
    public abstract class Runner
    {
        public TResult Run<TResult>(Func<TResult> work) => Impl(work);
        public void Run(Action work) => Impl(work);
        protected abstract TResult Impl<TResult>(Func<TResult> work);
        protected abstract void Impl(Action work);
    }
    public sealed class DirectRunner : Runner
    {
        protected override TResult Impl<TResult>(Func<TResult> work) => work();
        protected override void Impl(Action work) => work();
    }
    public static class Runners { public static Runner Create() => new DirectRunner(); }
    """;
var scenarios = new (string Name, string Truth, (string, string, MetadataReference) Library, string Field, string Types, string Action, string Startup)[]
{
    ("S0a user code, generic virtual", "runs in the request, no gap", none, "Count", RUNNERS,
     "Runners.Create().Run<int>(() => _state.Count = 1); return Ok(_state.Count);", ""),
    ("S0b user code, non-generic virtual", "runs in the request, no gap", none, "Count", RUNNERS,
     "Runners.Create().Run(() => { _state.Count = 1; }); return Ok(_state.Count);", ""),
    ("S0c non-generic, receiver allocated in the action", "runs in the request, no gap", none, "Count", RUNNERS,
     "var runner = new DirectRunner(); runner.Run(() => { _state.Count = 1; }); return Ok(_state.Count);", ""),
    ("S0d abstract member called directly on a known object", "runs in the request, no gap", none, "Count",
     "public abstract class Base { public abstract void Impl(Action work); } public sealed class Direct : Base { public override void Impl(Action work) => work(); }",
     "Base runner = new Direct(); runner.Impl(() => { _state.Count = 1; }); return Ok(_state.Count);", ""),
    ("S1 Execute in startup", "runs in startup, ordered before requests: no pair", polly, "Sum", "",
     "return Ok(Totals.Sum);", "Policy.Handle<Exception>().Retry(3).Execute(() => Totals.Sum = 1);"),
    ("S2 Execute in an action", "runs in the request: pairs across requests, no gap", polly, "Count", "",
     "Policy.Handle<Exception>().Retry(3).Execute(() => _state.Count = 1); return Ok(_state.Count);", ""),
    ("S3 WaitAndRetry onRetry (holder)", "onRetry runs in the request that executes the policy: Sum++ pairs across requests", polly, "Sum",
     "public static class Policies { public static readonly Policy Retry = Policy.Handle<Exception>().WaitAndRetry(3, i => TimeSpan.Zero, (e, t, n, c) => Totals.Sum++); }",
     "Policies.Retry.Execute(() => { }); return Ok(Totals.Sum);", ""),
    ("S4 pessimistic Timeout", "the action runs on a pool thread (Task.Run) and may outlive the call", polly, "Count", "",
     "Policy.Timeout(1, Polly.Timeout.TimeoutStrategy.Pessimistic).Execute(() => _state.Count = 1); return Ok(_state.Count);", ""),
    ("S5 MessageParser factory (framework calls ParseFrom)", "the framework runs the factory per message: Sum++ pairs across requests", protobuf, "Sum",
     """
     public sealed class Msg : Google.Protobuf.IMessage<Msg>
     {
         public static readonly Google.Protobuf.MessageParser<Msg> Parser = new(() => { Totals.Sum++; return new Msg(); });
         public void MergeFrom(Msg message) { }
         public void MergeFrom(Google.Protobuf.CodedInputStream input) { }
         public void WriteTo(Google.Protobuf.CodedOutputStream output) { }
         public int CalculateSize() => 0;
         public Google.Protobuf.Reflection.MessageDescriptor Descriptor => null!;
         public bool Equals(Msg? other) => ReferenceEquals(this, other);
         public Msg Clone() => new();
     }
     """,
     "var parser = Msg.Parser; return Ok(Totals.Sum);", ""),
};

foreach (var scenario in scenarios)
{
    var (libraryName, librarySource, libraryImage) = scenario.Library;
    var usingLine = libraryName switch { null => "", "Polly" => "using Polly;\n", _ => "using Google.Protobuf;\n" };
    var source = EngineFixture.Usings + usingLine + $$"""
        public static class Totals { public static int Sum; }
        public sealed class Marker { }
        public sealed class State { public int Count; }
        {{scenario.Types}}

        public sealed class StateController : ControllerBase
        {
            private readonly State _state;
            public StateController(State state) => _state = state;
            public IActionResult Get()
            {
                {{scenario.Action}}
            }
        }

        public static class Startup
        {
            public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
            {
                services.AddControllers();
                app.MapControllers();
                services.AddSingleton<State>();
                services.AddSingleton<Marker>(_ => new Marker());
                {{scenario.Startup}}
            }
        }
        """;
    Console.WriteLine($"==== {scenario.Name}");
    Console.WriteLine($"     truth: {scenario.Truth}");
    foreach (var asSource in libraryName is null ? new[] { false } : new[] { false, true })
    {
        var solution = Build(source, libraryName, librarySource, libraryImage, asSource);
        var errors = solution.Projects.ToDictionary(p => p.Name, p => p.GetCompilationAsync().Result!.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error));
        var mode = asSource ? "source  " : "metadata";
        if (errors["Fixture"] > 0)
        {
            Console.WriteLine($"  {mode}: user code does not compile: " +
                              string.Join("; ", solution.Projects.First(p => p.Name == "Fixture").GetCompilationAsync().Result!.GetDiagnostics()
                                                        .Where(d => d.Severity == DiagnosticSeverity.Error).Take(3)));
            continue;
        }

        var watch = Stopwatch.StartNew();
        EngineRun run;
        try
        {
            run = EngineFixture.AnalyzeScope(solution, "scope:Fixture");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {mode}: engine threw {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            continue;
        }

        watch.Stop();
        var program = run.Execution.Heap.Program;
        var libraryErrors = asSource ? $", library errors {errors[libraryName]}" : "";
        Console.WriteLine($"  {mode}: {watch.Elapsed.TotalSeconds:F1}s, reached bodies {program.Result.ReachedBodies.Count}, lowered {program.LoweredMembers.Count}{libraryErrors}");
        var accesses = run.Of(scenario.Field);
        Console.WriteLine($"     accesses to {scenario.Field}: " +
                          (accesses.Count == 0 ? "none" : string.Join(", ", accesses.Select(a => $"{a.Operation} in {Kind(run, a.ExecutionId)}"))));
        var pairs = run.PairsOn(scenario.Field);
        Console.WriteLine($"     pairs on {scenario.Field}: " +
                          (pairs.Count == 0 ? "none" : string.Join(", ", pairs.Select(p => $"{Kind(run, p.First.ExecutionId)} x {Kind(run, p.Second.ExecutionId)}"))));
        var gaps = run.Collection.Coverage.Gaps.Select(g => $"{g.Callee} [{g.Kind}]").Distinct().ToArray();
        Console.WriteLine($"     no-receiver-object: {run.Counter("no-receiver-object")}");
        Console.WriteLine($"     gaps: {gaps.Length}" + (gaps.Length > 0 ? " — " + string.Join("; ", gaps.Take(6)) + (gaps.Length > 6 ? "; ..." : "") : ""));
    }
}

static string Kind(EngineRun run, string executionId)
{
    var execution = run.Execution.Analysis.Execution(executionId);
    return execution.Kind.ToString();
}

static Solution Build(string userSource, string libraryName, string librarySource, MetadataReference libraryImage, bool asSource)
{
    var workspace = new AdhocWorkspace();
    var solution = workspace.CurrentSolution;
    var platform = StubAssemblies.PlatformWithout([]).ToArray();
    var stubs = StubAssemblies.Names.Select(name => StubAssemblies.Get(name, StubAssemblies.DefaultVersion(name))).ToArray();
    var fixtureId = ProjectId.CreateNewId();
    var references = platform.Concat(stubs).Concat(asSource || libraryImage is null ? [] : new[] { libraryImage });
    solution = solution.AddProject(ProjectInfo.Create(fixtureId, VersionStamp.Default, "Fixture", "Fixture", LanguageNames.CSharp,
                                                      filePath: @"C:\fixture\Fixture.csproj",
                                                      compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                                                                                                       nullableContextOptions: NullableContextOptions.Enable),
                                                      parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                                                      metadataReferences: references));
    solution = solution.AddDocument(DocumentId.CreateNewId(fixtureId), "Case.cs", SourceText.From(userSource), filePath: @"C:\fixture\Case.cs");
    if (!asSource)
        return solution;
    var libraryId = ProjectId.CreateNewId();
    solution = solution.AddProject(ProjectInfo.Create(libraryId, VersionStamp.Default, libraryName, libraryName, LanguageNames.CSharp,
                                                      filePath: $@"C:\fixture\{libraryName}\{libraryName}.csproj",
                                                      compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                                                                                       nullableContextOptions: NullableContextOptions.Enable),
                                                      parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
                                                      metadataReferences: platform));
    solution = solution.AddDocument(DocumentId.CreateNewId(libraryId), libraryName + ".cs", SourceText.From(librarySource),
                                    filePath: $@"C:\fixture\{libraryName}\{libraryName}.cs");
    return solution.AddProjectReference(fixtureId, new ProjectReference(libraryId));
}
