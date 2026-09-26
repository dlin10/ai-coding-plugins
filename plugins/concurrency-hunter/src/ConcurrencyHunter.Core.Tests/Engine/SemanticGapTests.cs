using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Reporting;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Semantic gaps (R4, R5): which unresolved calls are gaps, what each gap is called and of what kind, their materiality and
/// its order, and where coverage shows them.</summary>
public sealed class SemanticGapTests
{
    // ---- what makes an opaque call a gap ----

    [Fact]
    public void Opaque_call_handed_a_singleton_object_is_an_unknown_library_gap()
    {
        var gap = Assert.Single(Gaps(Run("System.Console.WriteLine(_state);")), gap => gap.Callee == "System.Console.WriteLine(object)");

        Assert.Equal(SemanticGapKinds.UNKNOWN_LIBRARY, gap.Kind);
    }

    [Fact]
    public void Opaque_call_handed_an_object_of_one_execution_is_no_gap()
    {
        // A fresh object the worker fills stays the worker's: the call only reads it, and nothing it reaches is shared (R2, R4).
        var run = Run("var local = new Other(); local.Name = \"x\"; System.Console.WriteLine(local);");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee == "System.Console.WriteLine(object)");
    }

    [Fact]
    public void Opaque_call_handed_a_string_and_a_number_is_no_gap()
    {
        var run = Run("System.Console.Write(_state.Text); System.Console.Write(_state.Count);");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.StartsWith("System.Console.Write(", StringComparison.Ordinal));
        Assert.Equal(2, run.Counter(CoverageCounters.OPAQUE_CALL) - Run("").Counter(CoverageCounters.OPAQUE_CALL));
    }

    [Fact]
    public void Array_created_in_the_argument_of_strings_and_numbers_is_judged_by_its_elements()
    {
        var run = Run("System.Console.WriteLine(\"{0} {1}\", new object[] { _state.Text, 1 });");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.StartsWith("System.Console.WriteLine(", StringComparison.Ordinal));
    }

    [Fact]
    public void Params_array_of_strings_and_numbers_is_judged_by_its_elements()
    {
        var run = Run("System.Console.WriteLine(\"{0} {1} {2} {3}\", _state.Text, 1, \"b\", 2);");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.StartsWith("System.Console.WriteLine(", StringComparison.Ordinal));
    }

    [Fact]
    public void Params_array_holding_a_singleton_object_is_a_gap()
    {
        var run = Run("System.Console.WriteLine(\"{0} {1} {2} {3}\", _state.Text, 1, _state, 2);");

        Assert.Single(Gaps(run), gap => gap.Callee.StartsWith("System.Console.WriteLine(string, ", StringComparison.Ordinal));
    }

    [Fact]
    public void Array_read_from_a_singleton_field_is_a_gap()
    {
        var gap = Assert.Single(Gaps(Run("System.Console.WriteLine(_state.Letters);")), gap => gap.Callee == "System.Console.WriteLine(char[])");

        Assert.Equal(1, gap.Regions);
    }

    [Fact]
    public void Opaque_call_handed_a_delegate_is_a_gap()
    {
        var run = Run("System.Threading.CancellationToken.None.Register(() => { });");

        Assert.Single(Gaps(run), gap => gap.Callee == "System.Threading.CancellationToken.Register(Action)");
    }

    [Fact]
    public void Result_written_to_a_field_of_a_singleton_is_a_gap()
    {
        var run = Run("_state.Text = System.Environment.MachineName;");

        Assert.Single(Gaps(run), gap => gap.Callee == "System.Environment.get_MachineName()");
    }

    [Fact]
    public void Result_written_to_an_object_of_one_execution_is_no_gap()
    {
        var run = Run("var local = new Other(); local.Name = System.Environment.MachineName; System.GC.KeepAlive(local);");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee == "System.Environment.get_MachineName()");
    }

    [Fact]
    public void Call_of_a_partial_method_without_an_implementation_is_neither_opaque_nor_a_gap()
    {
        var run = Run("new Generated().Build(_state);");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.Contains("OnConstruction", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Collection.Coverage.TopOpaqueCallees, callee => callee.Callee.Contains("OnConstruction", StringComparison.Ordinal));
    }

    [Fact]
    public void Arguments_of_a_partial_method_without_an_implementation_are_never_evaluated()
    {
        // The compiler removes the call with its arguments: the increment writes nothing, and the helper is never called.
        var run = Run("new Generated().Count(_state); new Generated().Call(_state);");

        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.StartsWith("Generated.", StringComparison.Ordinal) &&
                                                                 access.Resource.Member.Name == "Count");
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.StartsWith("Generated.Touch", StringComparison.Ordinal));
        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.Contains("Generated.", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_written_to_a_cell_of_a_singleton_collection_is_a_gap()
    {
        var run = Run("_state.Items[0] = System.Environment.TickCount64;");

        Assert.Single(Gaps(run), gap => gap.Callee == "System.Environment.get_TickCount64()");
    }

    [Fact]
    public void Member_of_the_table_out_of_its_version_range_handed_a_singleton_object_is_an_unknown_library_gap()
    {
        var options = new FixtureOptions { MetadataReferences = [StubAssemblies.Get(StubAssemblies.NEWTONSOFT_JSON, 12)] };
        var gap = Assert.Single(Gaps(Run("Newtonsoft.Json.JsonConvert.SerializeObject(_state);", options)),
                                gap => gap.Callee.StartsWith("Newtonsoft.Json.JsonConvert.SerializeObject(", StringComparison.Ordinal));

        Assert.Equal(SemanticGapKinds.UNKNOWN_LIBRARY, gap.Kind);
    }

    // ---- what the receiver lets an opaque call see ----

    [Fact]
    public void Library_members_run_on_this_of_a_source_object_are_no_gap()
    {
        var run = Run("_ = base.StopAsync(stoppingToken);");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.Contains("BackgroundService", StringComparison.Ordinal));
    }

    [Fact]
    public void Library_object_whose_content_the_heap_does_not_know_is_no_gap()
    {
        var run = Run("System.Console.WriteLine(_state.Box);");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee == "System.Console.WriteLine(object)");
    }

    [Fact]
    public void Library_object_holding_a_list_with_a_source_object_in_its_cell_is_a_gap()
    {
        var run = Run("var items = new System.Collections.Generic.List<Other>(); items.Add(new Other()); _state.Box.Value = items; " +
                      "System.Console.WriteLine(_state.Box);");

        // The box, whose Value field the program names, the list it holds and the object in the list's cell are shared through the
        // singleton: a collection's cells are an escape edge as an array's are (ADR 0010, phase 5b second run).
        var gap = Assert.Single(Gaps(run), gap => gap.Callee == "System.Console.WriteLine(object)");
        Assert.Equal(3, gap.Regions);
    }

    // ---- the other unresolved calls ----

    [Fact]
    public void MethodInfo_Invoke_handed_a_singleton_object_is_a_reflection_gap()
    {
        var gap = Assert.Single(Gaps(Run("typeof(State).GetMethod(\"Touch\")!.Invoke(_state, null);")),
                                gap => gap.Callee.StartsWith("System.Reflection.MethodBase.Invoke(", StringComparison.Ordinal));

        Assert.Equal(SemanticGapKinds.REFLECTION, gap.Kind);
    }

    [Fact]
    public void Interface_call_without_a_receiver_object_handed_a_singleton_object_is_an_unresolved_dispatch_gap()
    {
        var gap = Assert.Single(Gaps(Run("_sink!.Put(_state);")), gap => gap.Callee == "ISink.Put(State)");

        Assert.Equal(SemanticGapKinds.UNRESOLVED_DISPATCH, gap.Kind);
    }

    [Fact]
    public void Interface_call_without_a_receiver_object_and_without_arguments_is_no_gap()
    {
        var run = Run("_sink!.Flush();");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee == "ISink.Flush()");
        Assert.Equal(1, run.Counter(CoverageCounters.NO_RECEIVER_OBJECT) - Run("").Counter(CoverageCounters.NO_RECEIVER_OBJECT));
    }

    [Fact]
    public void Dynamic_call_on_a_singleton_field_is_a_dynamic_gap()
    {
        var gap = Assert.Single(Gaps(Run("dynamic sink = _state.Sink; sink.Read();")), gap => gap.Callee == "dynamic invoke Read");

        Assert.Equal(SemanticGapKinds.DYNAMIC, gap.Kind);
    }

    [Fact]
    public void Two_dynamic_calls_of_one_member_are_one_gap_with_two_call_sites()
    {
        var gap = Assert.Single(Gaps(Run("dynamic sink = _state.Sink; sink.Read(); sink.Read();")), gap => gap.Kind == SemanticGapKinds.DYNAMIC);

        Assert.Equal(("dynamic invoke Read", 2), (gap.Callee, gap.CallSites));
    }

    [Fact]
    public void Dynamic_calls_of_two_members_are_two_gaps()
    {
        var run = Run("dynamic sink = _state.Sink; sink.Read(); sink.Write(1);");

        Assert.Equal(["dynamic invoke Read", "dynamic invoke Write"], DynamicCallees(run));
    }

    [Fact]
    public void Dynamic_member_read_and_write_are_two_gaps()
    {
        var run = Run("dynamic sink = _state.Sink; var name = sink.Name; sink.Name = name;");

        Assert.Equal(["dynamic get Name", "dynamic set Name"], DynamicCallees(run));
    }

    [Fact]
    public void Dynamic_compound_assignment_and_increment_read_and_write_the_member()
    {
        Assert.Equal(["dynamic get Count", "dynamic set Count"], DynamicCallees(Run("dynamic sink = _state.Sink; sink.Count += 2;")));
        Assert.Equal(["dynamic get Count", "dynamic set Count"], DynamicCallees(Run("dynamic sink = _state.Sink; sink.Count++;")));
    }

    [Fact]
    public void Dynamic_index_read_and_write_are_two_gaps()
    {
        var run = Run("dynamic sink = _state.Sink; var first = sink[0]; sink[0] = first;");

        Assert.Equal(["dynamic index get", "dynamic index set"], DynamicCallees(run));
    }

    [Fact]
    public void Locator_with_a_type_that_is_not_constant_whose_result_is_written_to_a_singleton_field_is_a_model_gap()
    {
        var gap = Assert.Single(Gaps(Run("_state.Service = _provider.GetService(_state.ServiceType);")),
                                gap => gap.Callee == "System.IServiceProvider.GetService(Type)");

        Assert.Equal(SemanticGapKinds.MODEL_GAP, gap.Kind);
    }

    // ---- one gap per callee, and its materiality ----

    [Fact]
    public void Two_call_sites_of_one_callee_are_one_gap_with_two_call_sites()
    {
        var gap = Assert.Single(Gaps(Run("System.Console.WriteLine(_state); System.Console.WriteLine(_other);")),
                                gap => gap.Callee == "System.Console.WriteLine(object)");

        Assert.Equal(2, gap.CallSites);
        Assert.Equal(2, gap.Sites.Count);
    }

    [Fact]
    public void Materiality_orders_by_roots_then_regions_then_call_sites()
    {
        var gaps = Gaps(Run(Materiality, readerWork: "System.Console.WriteLine(_one);"));

        Assert.Equal(MaterialityOrder, gaps.Where(gap => MaterialityOrder.Any(expected => expected.Callee == gap.Callee))
                                           .Select(gap => (gap.Callee, gap.Roots, gap.Regions, gap.CallSites)));
    }

    // ---- what is no gap ----

    [Fact]
    public void Known_call_and_GC_KeepAlive_are_no_gap()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state); GC.KeepAlive(_state);");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.Contains("JsonSerializer", StringComparison.Ordinal) ||
                                                gap.Callee.Contains("KeepAlive", StringComparison.Ordinal));
        Assert.Equal(2, run.Counter(CoverageCounters.KNOWN_CALL) - Run("").Counter(CoverageCounters.KNOWN_CALL));
    }

    [Fact]
    public void SpinLock_Enter_on_a_singleton_field_is_no_gap()
    {
        var run = Run("var taken = false; _state.Gate.Enter(ref taken);");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.Contains("SpinLock", StringComparison.Ordinal));
    }

    [Fact]
    public void AsSpan_over_a_singleton_array_is_no_gap()
    {
        var run = Run("var span = System.MemoryExtensions.AsSpan(_state.Letters); span[0] = 'a';");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.Contains("AsSpan", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcurrentDictionary_constructed_with_a_comparer_of_the_run_is_no_gap()
    {
        var run = Run("_state.Map = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(new NameComparer());");

        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.Contains("ConcurrentDictionary", StringComparison.Ordinal));
    }

    [Fact]
    public void MapGet_whose_lambda_became_a_root_and_a_factory_registration_are_no_gap()
    {
        // The factory registration makes Configure a startup member, so its calls are reached and could be gaps at all.
        var run = Run("", registrations: "services.AddSingleton<System.Text.StringBuilder>(_ => new System.Text.StringBuilder()); " +
                                         "app.MapGet(\"/state\", (State state) => state.Count);");

        Assert.Contains(run.Execution.Heap.Heap.Instances.Values,
                        instance => instance.Summary.OpaqueCalls.Any(call => call.Callee.Contains(".MapGet(", StringComparison.Ordinal)));
        Assert.Contains(run.Execution.Heap.Program.Input.Roots, root => root.RootKind == "minimal-api");
        Assert.DoesNotContain(Gaps(run), gap => gap.Callee.Contains("MapGet", StringComparison.Ordinal) ||
                                                gap.Callee.Contains("AddSingleton", StringComparison.Ordinal));
    }

    // ---- where coverage shows the gaps ----

    [Fact]
    public async Task Report_lists_the_gaps_of_a_scope_by_materiality_under_their_counter()
    {
        var result = await Analyze(Materiality, readerWork: "System.Console.WriteLine(_one); _state.Count = 2;");
        var lines = ReportRenderer.Render(ReportingTestData.CreateReport(result)).ReportMarkdown.Split('\n');

        var counter = Array.FindIndex(lines, line => line.StartsWith($"  - {CoverageCounters.SEMANTIC_GAP} ", StringComparison.Ordinal));
        var gaps = Assert.Single(result.Coverage).Gaps;
        Assert.StartsWith($"  - {CoverageCounters.SEMANTIC_GAP} {gaps.Count}: ", lines[counter], StringComparison.Ordinal);
        Assert.Equal(gaps.Select(gap => $"    - {gap.Callee} ({gap.Kind}): roots {gap.Roots}, regions {gap.Regions}, call sites {gap.CallSites}"),
                     lines.Skip(counter + 1).Take(gaps.Count));
        Assert.Equal(MaterialityOrder.Select(gap => gap.Callee),
                     gaps.Select(gap => gap.Callee).Where(callee => MaterialityOrder.Any(expected => expected.Callee == callee)));
    }

    [Fact]
    public async Task Findings_json_lists_the_gaps_of_a_finding_scope_by_materiality()
    {
        var result = await Analyze(Materiality, readerWork: "System.Console.WriteLine(_one); _state.Count = 2;");
        using var json = JsonDocument.Parse(ReportRenderer.Render(ReportingTestData.CreateReport(result)).FindingsJson);

        Assert.Equal("2.2", json.RootElement.GetProperty("schemaVersion").GetString());
        var finding = json.RootElement.GetProperty("findings").EnumerateArray().First();
        var listed = finding.GetProperty("analysis").GetProperty("semanticGaps").EnumerateArray()
                            .Select(gap => (gap.GetProperty("scope").GetString(), gap.GetProperty("callee").GetString(), gap.GetProperty("kind").GetString(),
                                            gap.GetProperty("roots").GetInt32(), gap.GetProperty("regions").GetInt32(), gap.GetProperty("callSites").GetInt32()));
        var coverage = Assert.Single(result.Coverage);
        Assert.Equal(coverage.Gaps.Select(gap => ((string?)coverage.ScopeId, (string?)gap.Callee, (string?)gap.Kind, gap.Roots, gap.Regions, gap.CallSites)),
                     listed);
        Assert.False(finding.GetProperty("analysis").TryGetProperty("coverageState", out _));
    }

    [Fact]
    public async Task Report_lists_the_gaps_of_every_scope_and_a_finding_only_those_of_its_own()
    {
        // Web has findings and a gap; Api has a gap and no finding, so its gap shows in report.md and nowhere in findings.json.
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, Microsoft.CodeAnalysis.OutputKind>
                {
                    ["Web"] = Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
                    ["Api"] = Microsoft.CodeAnalysis.OutputKind.ConsoleApplication
                }
            },
            ("Web", "Program.cs", "System.Console.WriteLine();"),
            ("Web", "Case.cs", Usings + """
                public sealed class Item { public int Value; }
                public static class WebState { public static readonly Item Box = new(); }
                public class WebController : ControllerBase { public void Post() => System.Console.WriteLine(WebState.Box); }
                """ + Startup()),
            ("Api", "Program.cs", "System.Console.WriteLine();"),
            ("Api", "Case.cs", Usings + """
                public class ApiController : ControllerBase { public void Get() => System.Threading.CancellationToken.None.Register(() => { }); }
                """ + Startup()));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);
        var rendered = ReportRenderer.Render(ReportingTestData.CreateReport(result));

        var web = Assert.Single(result.Coverage, coverage => coverage.ScopeId == "Web");
        var api = Assert.Single(result.Coverage, coverage => coverage.ScopeId == "Api");
        Assert.Contains(web.Gaps, gap => gap.Callee == "System.Console.WriteLine(object)");
        Assert.Contains(api.Gaps, gap => gap.Callee == "System.Threading.CancellationToken.Register(Action)");
        var lines = rendered.ReportMarkdown.Split('\n');
        Assert.All(web.Gaps.Concat(api.Gaps),
                   gap => Assert.Contains($"    - {gap.Callee} ({gap.Kind}): roots {gap.Roots}, regions {gap.Regions}, call sites {gap.CallSites}", lines));

        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding => Assert.Equal("Web", finding.Resource.Scope));
        using var json = JsonDocument.Parse(rendered.FindingsJson);
        Assert.All(json.RootElement.GetProperty("findings").EnumerateArray(),
                   finding => Assert.Equal(web.Gaps.Select(gap => ("Web", gap.Callee)),
                                           finding.GetProperty("analysis").GetProperty("semanticGaps").EnumerateArray()
                                                  .Select(listed => (listed.GetProperty("scope").GetString()!, listed.GetProperty("callee").GetString()!))));
    }

    // ---- helpers ----

    /// <summary>Four gaps of one worker, with the reader adding a second root to the first: <c>Console.WriteLine</c> reaches two roots
    /// and one region, <c>Console.Write</c> one root and two regions, <c>TextWriter.Write</c> one root, one region and two call sites,
    /// <c>TextWriter.WriteLine</c> one of each.</summary>
    private const string Materiality =
        "System.Console.WriteLine(_one); System.Console.Write(_two); System.Console.Out.Write(_one); System.Console.Out.Write(_one); " +
        "System.Console.Out.WriteLine(_one);";

    private static readonly (string Callee, int Roots, int Regions, int CallSites)[] MaterialityOrder =
    [
        ("System.Console.WriteLine(object)", 2, 1, 2),
        ("System.Console.Write(object)", 1, 2, 1),
        ("System.IO.TextWriter.Write(object)", 1, 1, 2),
        ("System.IO.TextWriter.WriteLine(object)", 1, 1, 1)
    ];

    private static IReadOnlyList<SemanticGap> Gaps(EngineRun run) => run.Collection.Coverage.Gaps;

    private static string[] DynamicCallees(EngineRun run) =>
        Gaps(run).Where(gap => gap.Kind == SemanticGapKinds.DYNAMIC).Select(gap => gap.Callee).Order(StringComparer.Ordinal).ToArray();

    private static EngineRun Run(string work, FixtureOptions? options = null, string readerWork = "", string registrations = "") =>
        AnalyzeScope(FixtureSolution.Create(options ?? new FixtureOptions(), ("Case.cs", Source(work, readerWork, registrations))), "scope:Fixture");

    private static Task<AnalysisResult> Analyze(string work, string readerWork) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Source(work, readerWork, ""))), ROOT_DIRECTORY, CancellationToken.None);

    /// <summary>A singleton <c>State</c> and its neighbours that one worker does <paramref name="work"/> on, while another does
    /// <paramref name="readerWork"/>.</summary>
    private static string Source(string work, string readerWork, string registrations) => Usings + $$"""
        using System.Collections.Generic;

        public sealed class Other { public string Name = ""; }
        public sealed class One { public int Value; }
        public sealed class Two { public One Inner = new(); }
        public sealed class Recorder { public int Count; public string Name = ""; public void Read() { } public void Write(int value) => Count = value; }
        public sealed partial class Generated
        {
            partial void OnConstruction(State state);
            partial void OnCount(int count);
            partial void OnTouched(int touched);
            public void Build(State state) => OnConstruction(state);
            public void Count(State state) => OnCount(state.Count++);
            public void Call(State state) => OnTouched(Touch(state));
            private static int Touch(State state) => state.Count = 3;
        }

        public sealed class State
        {
            public int Count;
            public string Text = "";
            public char[] Letters = new char[2];
            public List<long> Items = new();
            public object Sink = new Recorder();
            public object? Service;
            public Type ServiceType = typeof(Other);
            public SpinLock Gate = new(false);
            public System.Collections.Concurrent.ConcurrentDictionary<string, int>? Map;
            public readonly System.Runtime.CompilerServices.StrongBox<List<Other>> Box = new();
            public void Touch() => Count++;
        }

        public interface ISink { void Put(State state); void Flush(); }
        public sealed class Sink : ISink { public int Puts; public void Put(State state) => Puts++; public void Flush() => Puts = 0; }

        public sealed class NameComparer : IEqualityComparer<string>
        {
            public bool Equals(string? x, string? y) => x == y;
            public int GetHashCode(string value) => value.Length;
        }

        public sealed class Worker : BackgroundService
        {
            private readonly State _state;
            private readonly Other _other;
            private readonly One _one;
            private readonly Two _two;
            private readonly IServiceProvider _provider;
            private readonly ISink? _sink = null;
            public Worker(State state, Other other, One one, Two two, IServiceProvider provider)
            {
                _state = state;
                _other = other;
                _one = one;
                _two = two;
                _provider = provider;
            }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                _state.Count = 1;
                return Task.CompletedTask;
            }
        }

        public sealed class Reader : BackgroundService
        {
            private readonly State _state;
            private readonly One _one;
            public Reader(State state, One one)
            {
                _state = state;
                _one = one;
            }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{readerWork}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Other>(); services.AddSingleton<One>(); services.AddSingleton<Two>(); " +
                      "services.AddHostedService<Worker>(); services.AddHostedService<Reader>(); " + registrations);
}
