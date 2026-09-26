using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Reporting;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The unknown call of a delegate handed to an unresolved call (R3, ADR 0011): which delegates get one, what it overlaps and
/// what it leaves confined, what decides its findings' overlap, and how the report names it.</summary>
public sealed class OpaqueDelegateExecutionTests
{
    private const int DECIDED = 10;

    /// <summary>How the unknown call of a lambda the worker hands over is named: by the member it is written in and its place.</summary>
    private const string NAMED = "unknown call of the delegate lambda in Worker.ExecuteAsync(CancellationToken) at Case.cs:";

    // ---- what runs in an unknown execution and what it meets ----

    [Fact]
    public async Task Lambda_handed_to_an_opaque_call_writing_a_singleton_meets_a_read_in_an_action()
    {
        var finding = Assert.Single(await Findings("Opaque.Lib.Run(() => _state.Count = 1);"), IsReadAgainstWrite);

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal(DECIDED, finding.Confidence.Components.ExecutionOverlap);
        Assert.Equal("Medium", finding.Confidence.Label);
        Assert.Contains(finding.Uncertainty, item => item.Contains("Opaque.Lib.Run(Action)", StringComparison.Ordinal) &&
                                                     item.Contains("overlap check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delegate_handed_to_a_dynamic_call_runs_in_an_unknown_execution()
    {
        // The lambda calls a helper nothing else calls: only a reachable set that follows the delegate lowers it.
        var work = "dynamic sink = _sink; sink.Take((Action)(() => _state.Mark()));";

        Assert.Contains(Run(work).Of("Count"), access => KindOf(Run(work), access) == ExecutionKind.UnknownDelegateCall);
        Assert.Equal("DCA1001", Assert.Single(await Findings(work), IsReadAgainstWrite).RuleId);
    }

    [Fact]
    public async Task Delegate_handed_to_an_interface_call_without_a_receiver_object_runs_in_an_unknown_execution()
    {
        var work = "_consumer!.Take(() => _state.Mark());";
        var run = Run(work);

        Assert.Contains(run.Of("Count"), access => KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
        Assert.Equal("DCA1001", Assert.Single(await Findings(work), IsReadAgainstWrite).RuleId);
    }

    [Fact]
    public void Lambda_writing_a_singleton_pairs_with_itself_in_its_unknown_execution()
    {
        // Handed by an action, which overlaps itself: two requests may each have the call run it at once.
        var run = Run("", action: "Opaque.Lib.Run(() => _state.Count = 1); return Ok();");

        var pair = Assert.Single(run.PairsOn("Count"));
        Assert.Equal(pair.First.ExecutionId, pair.Second.ExecutionId);
        Assert.Equal(ExecutionKind.UnknownDelegateCall, run.Execution.Analysis.Execution(pair.First.ExecutionId).Kind);
    }

    [Fact]
    public void Lambda_handed_once_by_an_execution_that_runs_once_does_not_pair_with_itself()
    {
        var run = Run("Opaque.Lib.Run(() => _state.Count = 1);", action: "return Ok();");

        var execution = Assert.Single(UnknownExecutions(run));
        Assert.Equal(new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.Serialized, ""), execution.Policy);
        Assert.Empty(run.PairsOn("Count"));
    }

    [Fact]
    public void Lambda_handed_at_two_sites_pairs_with_itself_in_its_unknown_execution()
    {
        var run = Run("Action work = () => _state.Count = 1; Opaque.Lib.Run(work); Opaque.Lib.Later(work);", action: "return Ok();");

        var pair = Assert.Single(run.PairsOn("Count"));
        Assert.Equal(pair.First.ExecutionId, pair.Second.ExecutionId);
    }

    [Fact]
    public void Static_field_a_non_capturing_lambda_writes_counts_among_the_regions_of_its_gap()
    {
        var run = Run("Opaque.Lib.Run(static () => Totals.Sum = 1);");

        var gap = Assert.Single(run.Collection.Coverage.Gaps, gap => gap.Callee == "Opaque.Lib.Run(Action)");
        Assert.Equal(1, gap.Regions);
    }

    [Fact]
    public void Object_of_one_execution_a_lambda_captures_and_writes_stays_confined_and_pairs_with_nothing()
    {
        var run = Run("", action: "var local = new Item(); local.Value = 1; Opaque.Lib.Run(() => local.Value = 2); return Ok(local.Value);");

        Assert.Contains(run.Of("Value"), access => KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
        Assert.All(run.Of("Value"), access => Assert.Equal(OwnershipKind.ThreadConfined, access.Ownership));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Lock_around_the_opaque_call_does_not_protect_the_body()
    {
        var run = Run("lock (_state.Gate) { Opaque.Lib.Run(() => _state.Count = 1); }", action: "lock (_state.Gate) { _state.Count = 2; } return Ok();");

        var pair = Assert.Single(run.PairsOn("Count"), pair => pair.First.ExecutionId != pair.Second.ExecutionId);
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
    }

    [Fact]
    public void Lambda_handed_to_an_opaque_call_in_startup_still_meets_a_later_root()
    {
        var run = Run("", startup: "Opaque.Lib.Run(() => Totals.Sum = 1);", action: "return Ok(Totals.Sum);");

        var pair = Assert.Single(run.PairsOn("Sum"), pair => pair.First.ExecutionId != pair.Second.ExecutionId);
        Assert.Contains(new[] { pair.First, pair.Second }, access => KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
        Assert.Contains(new[] { pair.First, pair.Second }, access => KindOf(run, access) == ExecutionKind.Root);
    }

    // ---- what is no unknown call of a delegate ----

    [Fact]
    public void Task_Run_and_a_recognized_timer_are_no_unknown_call()
    {
        var run = Run("Task.Run(() => _state.Count = 1); var timer = new System.Threading.Timer(_ => _state.Total = 1, null, 0, 1000); GC.KeepAlive(timer);");

        Assert.Empty(UnknownExecutions(run));
        Assert.Empty(run.Execution.Heap.Heap.DelegateHandoffs);
    }

    [Fact]
    public void Delegate_of_a_DI_factory_is_no_unknown_call()
    {
        var run = Run("", registrations: "services.AddSingleton<Item>(_ => new Item { Value = 1 });");

        Assert.Empty(UnknownExecutions(run));
    }

    [Fact]
    public void Minimal_API_lambda_is_a_root_with_no_unknown_call_and_no_gap()
    {
        var run = Run("", registrations: "app.MapGet(\"/count\", (State state) => state.Count = 3);");

        Assert.Contains(run.Execution.Heap.Program.Input.Roots, root => root.RootKind == "minimal-api");
        Assert.Empty(UnknownExecutions(run));
        Assert.DoesNotContain(run.Collection.Coverage.Gaps, gap => gap.Callee.Contains("MapGet", StringComparison.Ordinal));
    }

    [Fact]
    public void Work_handed_to_Task_Run_and_to_Lazy_runs_as_the_spawn_and_as_the_unknown_call()
    {
        var run = Run("Func<int> work = () => _state.Count = 1; Task.Run(work); GC.KeepAlive(new Lazy<int>(work));", action: "return Ok();");

        Assert.Equal([ExecutionKind.Spawn, ExecutionKind.UnknownDelegateCall], run.Of("Count").Select(access => KindOf(run, access)).Order());
    }

    [Fact]
    public void Lambda_handed_to_two_opaque_calls_is_one_unknown_execution()
    {
        var run = Run("Action work = () => _state.Count = 1; Opaque.Lib.Run(work); Opaque.Lib.Later(work);");

        Assert.Single(UnknownExecutions(run));
        Assert.Equal(2, Assert.Single(run.Execution.Heap.Heap.DelegateHandoffs).Sites.Count);
    }

    [Fact]
    public void Delegate_to_opaque_counter_counts_as_before()
    {
        var run = Run("Opaque.Lib.Run(() => _state.Count = 1); Opaque.Lib.Run(() => _state.Total = 1);");

        Assert.Equal(2, run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE) - Run("").Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
    }

    // ---- how the report names it ----

    [Fact]
    public void Access_in_the_unknown_call_has_the_symbol_of_its_member_and_a_root_kind_of_its_own()
    {
        var run = Run("Opaque.Lib.Run(() => _state.Count = 1);");

        var access = Assert.Single(run.Of("Count"), access => KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
        Assert.Equal("Worker.ExecuteAsync(CancellationToken)", access.Symbol);
        Assert.Equal("unknown-delegate-call", access.PathRoot.RootKind);
        Assert.StartsWith(NAMED, access.PathRoot.Display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Findings_json_and_the_report_name_the_unknown_call_of_the_delegate_with_its_body()
    {
        var result = await Analyze(Source("Opaque.Lib.Run(() => _state.Count = 1);"));
        var rendered = ReportRenderer.Render(ReportingTestData.CreateReport(result));

        using var json = JsonDocument.Parse(rendered.FindingsJson);
        var finding = json.RootElement.GetProperty("findings").EnumerateArray()
                          .Single(item => item.GetProperty("accesses").EnumerateArray().Any(access => access.GetProperty("operation").GetString() == "read"));
        var roots = finding.GetProperty("accesses").EnumerateArray().Select(access => access.GetProperty("root").GetString()!).ToArray();
        Assert.Contains(roots, root => root.StartsWith("unknown-delegate-call:", StringComparison.Ordinal));
        Assert.DoesNotContain(roots, root => root.StartsWith("construction", StringComparison.Ordinal));
        Assert.Contains(finding.GetProperty("concurrencyEvidence").EnumerateArray(),
                        item => item.GetString()!.Contains(NAMED, StringComparison.Ordinal));
        Assert.Contains(NAMED, rendered.ReportMarkdown, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static bool IsReadAgainstWrite(Finding finding) =>
        string.Join(".", finding.Resource.AccessPath) == "Count" &&
        new[] { finding.AccessA.Operation, finding.AccessB.Operation }.Order().SequenceEqual([AccessOperation.Read, AccessOperation.Write]);

    private static ExecutionKind KindOf(EngineRun run, Access access) => run.Execution.Analysis.Execution(access.ExecutionId).Kind;

    private static ExecutionInstance[] UnknownExecutions(EngineRun run) =>
        run.Execution.Analysis.Executions.Where(execution => execution.Kind == ExecutionKind.UnknownDelegateCall).ToArray();

    private static EngineRun Run(string work, string action = "return Ok(_state.Count);", string startup = "", string registrations = "") =>
        AnalyzeScope(FixtureSolution.Create(Options, ("Case.cs", Source(work, action, startup, registrations))), "scope:Fixture");

    private static async Task<IReadOnlyList<Finding>> Findings(string work) => (await Analyze(Source(work))).Findings;

    private static Task<AnalysisResult> Analyze(string source) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(Options, ("Case.cs", source)), ROOT_DIRECTORY, CancellationToken.None);

    private static FixtureOptions Options => new() { MetadataReferences = [OpaqueLibrary.Value] };

    /// <summary>A singleton <c>State</c> a worker does <paramref name="work"/> on and a controller's action does <paramref name="action"/>
    /// on; <paramref name="startup"/> runs in <c>Configure</c>, which a factory registration makes a startup member.</summary>
    private static string Source(string work, string action = "return Ok(_state.Count);", string startup = "", string registrations = "") => Usings + $$"""
        public sealed class Item { public int Value; }
        public static class Totals { public static int Sum; }
        public sealed class Marker { }
        public interface IConsumer { void Take(Action work); }
        public sealed class Consumer : IConsumer { public int Taken; public void Take(Action work) => Taken++; }
        public sealed class Sink { public void Take(Action work) { } }

        public sealed class State
        {
            public int Count;
            public int Total;
            public readonly object Gate = new();
            public void Mark() => Count = 1;
        }

        public sealed class Worker : BackgroundService
        {
            private readonly State _state;
            private readonly IConsumer? _consumer = null;
            private readonly object _sink = new Sink();
            public Worker(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }

        public sealed class StateController : ControllerBase
        {
            private readonly State _state;
            public StateController(State state) => _state = state;

            public IActionResult Get()
            {
                {{action}}
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
                services.AddHostedService<Worker>();
                {{registrations}}
                {{startup}}
            }
        }
        """;

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Run(System.Action work) { }
                public static void Later(System.Action work) { }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
