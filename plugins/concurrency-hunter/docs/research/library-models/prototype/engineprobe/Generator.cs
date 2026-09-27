// Prototype of an offline model generator: for each library member, a driver action calls it with probe lambdas that write
// Probe.A/B/C, and the real engine, run on the library's decompiled source, says where each probe write lands.
//   A: call only                -> a probe write in the caller's execution is invoke-now
//   B: call, then enumerate      -> a probe write that appears only here is iterator
//   C: call, then one member of the returned or received object at a time -> a probe write there is holder, with that trigger
//   nowhere                      -> framework-event: library code is not a closed world, so a delegate kept and not seen
//                                   invoked keeps the unknown execution (open-world rule)
using System.Diagnostics;
using System.Text.Json;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

static class Generator
{
    private sealed record Case(string Key, string Library, string Call, bool Enumerate = false, string Holder = null, string Types = "");

    private static readonly string[] Probes = ["A", "B", "C"];

    private const string MSG = """
        public sealed class Msg : Google.Protobuf.IMessage<Msg>
        {
            public void MergeFrom(Msg message) { }
            public void MergeFrom(Google.Protobuf.CodedInputStream input) { }
            public void WriteTo(Google.Protobuf.CodedOutputStream output) { }
            public int CalculateSize() => 0;
            public Google.Protobuf.Reflection.MessageDescriptor Descriptor => null!;
            public bool Equals(Msg? other) => ReferenceEquals(this, other);
            public Msg Clone() => new();
        }
        """;

    private static readonly Case[] Cases =
    [
        new("All", "System.Linq", "var r = src.All(x => { Probe.A = 1; return true; });"),
        new("FirstOrDefault", "System.Linq", "var r = src.FirstOrDefault(x => { Probe.A = 1; return true; });"),
        new("SingleOrDefault", "System.Linq", "var r = src.SingleOrDefault(x => { Probe.A = 1; return x == 2; });"),
        new("Sum", "System.Linq", "var r = src.Sum(x => { Probe.A = 1; return x; });"),
        new("ToDictionary", "System.Linq", "var r = src.ToDictionary(x => { Probe.A = 1; return x; }, x => { Probe.B = 1; return x; });"),
        new("Where", "System.Linq", "var r = src.Where(x => { Probe.A = 1; return true; });", Enumerate: true),
        new("Select", "System.Linq", "var r = src.Select(x => { Probe.A = 1; return x; });", Enumerate: true),
        new("SelectMany", "System.Linq", "var r = src.SelectMany(x => { Probe.A = 1; return new[] { x }; });", Enumerate: true),
        new("OrderBy", "System.Linq", "var r = src.OrderBy(x => { Probe.A = 1; return x; });", Enumerate: true),
        new("GroupBy", "System.Linq",
            "var r = src.GroupBy(x => { Probe.A = 1; return x; }, x => { Probe.B = 1; return x; }, (k, xs) => { Probe.C = 1; return k; });", Enumerate: true),
        new("FindFirst", "System.Security.Claims",
            "var r = new System.Security.Claims.ClaimsPrincipal().FindFirst(c => { Probe.A = 1; return true; });"),
        new("PolicyExecute", "Polly", "Policy.Handle<Exception>().Retry(3).Execute(() => { Probe.A = 1; });"),
        new("AsyncPolicyExecuteAsync", "Polly",
            "await Policy.Handle<Exception>().RetryAsync(3).ExecuteAsync(() => { Probe.A = 1; return Task.CompletedTask; });"),
        new("WaitAndRetry", "Polly",
            "var holder = Policy.Handle<Exception>().WaitAndRetry(3, i => { Probe.A = 1; return TimeSpan.Zero; }, (Exception e, TimeSpan t, int n, Context c) => { Probe.B = 1; });",
            Holder: "holder"),
        new("WaitAndRetryAsync", "Polly",
            "var holder = Policy.Handle<Exception>().WaitAndRetryAsync(3, i => { Probe.A = 1; return TimeSpan.Zero; }, (Exception e, TimeSpan t, int n, Context c) => { Probe.B = 1; });",
            Holder: "holder"),
        new("WaitAndRetryForeverAsync", "Polly",
            "var holder = Policy.Handle<Exception>().WaitAndRetryForeverAsync(i => { Probe.A = 1; return TimeSpan.Zero; }, (Exception e, int n, TimeSpan t) => { Probe.B = 1; });",
            Holder: "holder"),
        new("MessageParserCtor", "Google.Protobuf", "var holder = new MessageParser<Msg>(() => { Probe.A = 1; return new Msg(); });",
            Holder: "holder", Types: MSG),
        new("ForMessage", "Google.Protobuf",
            "var parser = new MessageParser<Msg>(() => { Probe.A = 1; return new Msg(); }); var holder = FieldCodec.ForMessage(10u, parser);",
            Holder: "holder", Types: MSG),
    ];

    public static void Run(string root)
    {
        var gold = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "gold.json"))).RootElement.EnumerateArray()
                               .ToDictionary(e => e.GetProperty("key").GetString(),
                                             e => (Label: e.GetProperty("label").GetString(),
                                                   Params: e.GetProperty("delegateParams").EnumerateArray().Select(p => p.GetString()).ToArray()));
        var sources = new Dictionary<string, string>();
        var results = new List<object>();
        var total = Stopwatch.StartNew();
        foreach (var @case in Cases)
        {
            sources.TryGetValue(@case.Library, out var library);
            // The fixture's platform is the running .NET 10, whose assemblies reference System.* at 10.0.0.0: the decompiled 8.0
            // framework library takes that version so the references unify (a harness adjustment, not a change to its code).
            library ??= sources[@case.Library] = File.ReadAllText(Path.Combine(root, "decompiled", @case.Library + ".cs"))
                                                    .Replace("[assembly: AssemblyVersion(\"8.0.0.0\")]", "[assembly: AssemblyVersion(\"10.0.0.0\")]");
            var (goldLabel, parameters) = gold[@case.Key];
            var watch = Stopwatch.StartNew();
            var a = Probe(@case, @case.Call, library, out var libraryErrors);
            var b = @case.Enumerate ? Probe(@case, @case.Call + " foreach (var _ in r) { }", library, out _) : null;
            var triggers = new Dictionary<string, Dictionary<string, HashSet<string>>>();
            if (@case.Holder is not null)
            {
                foreach (var trigger in Triggers(@case, library))
                    triggers[trigger.Name] = Probe(@case, @case.Call + " " + trigger.Code, library, out _);
            }

            var perParameter = new Dictionary<string, string>();
            for (var k = 0; k < parameters.Length; k++)
            {
                var probe = Probes[k];
                var now = a.GetValueOrDefault(probe) ?? [];
                string label;
                if (now.Contains("Root") || now.Contains("Startup"))
                    label = "invoke-now" + (now.Contains("Spawn") ? "+spawn" : "") + (now.Contains("UnknownDelegateCall") ? "*" : "");
                else if (now.Count > 0)
                    label = "unknown(" + string.Join("/", now) + ")";
                else if (b?.GetValueOrDefault(probe) is { } later && later.Contains("Root"))
                    label = "iterator";
                else if (triggers.Where(t => t.Value.GetValueOrDefault(probe)?.Contains("Root") == true).Select(t => t.Key).ToArray() is { Length: > 0 } fired)
                    label = $"holder({string.Join(", ", fired.Take(3))}{(fired.Length > 3 ? $", +{fired.Length - 3}" : "")})";
                else
                    label = "framework-event(open world)";
                perParameter[parameters[k]] = label;
            }

            Console.WriteLine($"{@case.Key,-26} gold={goldLabel,-16} {watch.Elapsed.TotalSeconds,5:F1}s lib-errors={libraryErrors,-4} " +
                              $"triggers-tried={triggers.Count,-3} " + string.Join(", ", perParameter.Select(p => $"{p.Key}: {p.Value}")));
            results.Add(new { key = @case.Key, gold = goldLabel, perParameter, triggersTried = triggers.Keys.ToArray() });
        }

        File.WriteAllText(Path.Combine(root, "generator-verdicts.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"total {total.Elapsed.TotalMinutes:F1} min");
    }

    /// <summary>The probe fields each written by the driver, with the kinds of the executions the writes land in.</summary>
    private static Dictionary<string, HashSet<string>> Probe(Case @case, string body, string library, out int libraryErrors)
    {
        var solution = Build(Driver(@case, body), @case.Library, library);
        libraryErrors = solution.Projects.First(p => p.Name == @case.Library).GetCompilationAsync().Result!.GetDiagnostics()
                                .Count(d => d.Severity == DiagnosticSeverity.Error);
        var userErrors = solution.Projects.First(p => p.Name == "Fixture").GetCompilationAsync().Result!.GetDiagnostics()
                                 .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (userErrors.Length > 0)
            return new Dictionary<string, HashSet<string>> { ["error"] = [userErrors[0].GetMessage()] };
        try
        {
            var run = EngineFixture.AnalyzeScope(solution, "scope:Fixture");
            return Probes.ToDictionary(p => p, p => run.Of(p).Select(access => run.Execution.Analysis.Execution(access.ExecutionId).Kind.ToString())
                                                         .ToHashSet());
        }
        catch (Exception ex)
        {
            return new Dictionary<string, HashSet<string>> { ["error"] = [ex.GetType().Name + ": " + ex.Message.Split('\n')[0]] };
        }
    }

    /// <summary>One call per public instance method of the holder's type and its library bases, with typed default arguments;
    /// methods with ref-like modifiers or type parameters of their own are left out.</summary>
    private static IEnumerable<(string Name, string Code)> Triggers(Case @case, string library)
    {
        var solution = Build(Driver(@case, @case.Call), @case.Library, library);
        var compilation = solution.Projects.First(p => p.Name == "Fixture").GetCompilationAsync().Result!;
        var tree = compilation.SyntaxTrees.First();
        var model = compilation.GetSemanticModel(tree);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().First(d => d.Identifier.Text == @case.Holder);
        var type = (model.GetDeclaredSymbol(declarator) as ILocalSymbol)?.Type as INamedTypeSymbol;
        var seen = new HashSet<string>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (current.ContainingAssembly?.Name != @case.Library)
                break;
            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind != MethodKind.Ordinary || method.DeclaredAccessibility != Accessibility.Public || method.IsStatic ||
                    method.IsGenericMethod || method.Parameters.Any(p => p.RefKind != RefKind.None || p.Type.IsRefLikeType && p.Type.Name != "ReadOnlySpan") ||
                    !seen.Add(method.ToDisplayString()))
                    continue;
                var arguments = string.Join(", ", method.Parameters.Select(p => $"default({p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})!"));
                var call = $"{@case.Holder}.{method.Name}({arguments})";
                var awaits = method.ReturnType.Name is "Task" or "ValueTask";
                yield return (method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), (awaits ? "await " : "_ = ") + call + ";");
                if (seen.Count >= 30)
                    yield break;
            }
        }
    }

    private static string Driver(Case @case, string body)
    {
        var usings = @case.Library switch { "Polly" => "using Polly;\n", "Google.Protobuf" => "using Google.Protobuf;\n", _ => "using System.Linq;\n" };
        return EngineFixture.Usings + usings + $$"""
            public static class Probe { public static int A; public static int B; public static int C; }
            public sealed class Marker { }
            {{@case.Types}}

            public sealed class DriverController : ControllerBase
            {
                public async Task<IActionResult> Get()
                {
                    var src = new System.Collections.Generic.List<int> { 1, 2, 3 };
                    {{body}}
                    await Task.Yield();
                    return Ok();
                }
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddControllers();
                    app.MapControllers();
                    services.AddSingleton<Marker>(_ => new Marker());
                }
            }
            """;
    }

    private static Solution Build(string userSource, string libraryName, string librarySource)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var platform = StubAssemblies.PlatformWithout([libraryName]).ToArray();
        var stubs = StubAssemblies.Names.Select(name => StubAssemblies.Get(name, StubAssemblies.DefaultVersion(name))).ToArray();
        var fixtureId = ProjectId.CreateNewId();
        var libraryId = ProjectId.CreateNewId();
        solution = solution.AddProject(ProjectInfo.Create(fixtureId, VersionStamp.Default, "Fixture", "Fixture", LanguageNames.CSharp,
                                                          filePath: @"C:\fixture\Fixture.csproj",
                                                          compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                                                                                                           nullableContextOptions: NullableContextOptions.Enable),
                                                          parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                                                          metadataReferences: platform.Concat(stubs)));
        solution = solution.AddDocument(DocumentId.CreateNewId(fixtureId), "Case.cs", SourceText.From(userSource), filePath: @"C:\fixture\Case.cs");
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
}
