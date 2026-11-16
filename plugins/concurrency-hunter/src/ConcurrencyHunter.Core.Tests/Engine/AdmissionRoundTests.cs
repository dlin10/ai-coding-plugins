using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class AdmissionRoundTests
{
    [Fact]
    public async Task Outer_call_is_admitted_in_the_round_after_the_inner_one()
    {
        var program = new CallSiteTestProgram("L.Invoke(() => L.Invoke(() => state.Value++));");
        var observed = await Analyze(program);
        var calls = Assert.Single(observed.Measurements).Calls;
        Assert.Contains(calls, call => call.Round == 1);
        Assert.Contains(calls, call => call.Round == 2);
        var product = await Analyze(program, noInput: true);
        Assert.Equal(observed.FinalPairs!.Pairs.Select(PairIdentity.Of), product.FinalPairs!.Pairs.Select(PairIdentity.Of));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(observed.Result.Findings), System.Text.Json.JsonSerializer.Serialize(product.Result.Findings));
    }

    [Fact]
    public async Task No_more_than_three_rounds_of_admission_run()
    {
        var observed = await Analyze(new CallSiteTestProgram("L.Invoke(() => L.Invoke(() => L.Invoke(() => L.Invoke(() => L.Invoke(() => state.Value++)))));"));
        var scope = Assert.Single(observed.Measurements);
        Assert.Equal(4, scope.Analyses);
        Assert.All(scope.Calls, call => Assert.InRange(call.Round, 1, 3));
        Assert.Contains(observed.Result.Coverage, coverage => coverage.DecidingGaps.Count != 0);
    }

    [Fact]
    public void Admitted_calls_only_grow()
    {
        var program = new CallSiteTestProgram("L.Invoke(() => L.Invoke(() => state.Value++));");
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations);
        var run = program.Run();
        Assert.True(rounds.Grow(run, CallSiteTestProgram.Pairs(run), 1));
        var first = rounds.Admission.Calls!.Keys.ToHashSet();
        run = program.Run(rounds.Admission);
        Assert.True(rounds.Grow(run, CallSiteTestProgram.Pairs(run), 2));
        Assert.True(first.IsProperSubsetOf(rounds.Admission.Calls!.Keys));
    }

    [Fact]
    public void Admission_does_not_depend_on_the_order_of_calls_or_scopes()
    {
        var programs = new[] { new CallSiteTestProgram("L.Invoke(() => state.Value++); L.Touch(state);"),
                               new CallSiteTestProgram("L.Touch(state); L.Invoke(() => state.Value++);") };
        var forward = programs.Select(Choose).ToArray();
        var backward = programs.Reverse().Select(Choose).Reverse().ToArray();
        Assert.Equal(forward, backward);

        static string Choose(CallSiteTestProgram program)
        {
            using var compilations = new CallSiteCompilations();
            var run = program.Run();
            var pairs = CallSiteTestProgram.Pairs(run);
            var first = program.Rounds(compilations);
            var second = program.Rounds(compilations);
            first.Grow(run, pairs, 1);
            second.Grow(run, pairs with { Pairs = pairs.Pairs.Reverse().ToArray() }, 1);
            Assert.Equal(first.Admission.CacheKey("body:Fixture:M:RootController.Get"), second.Admission.CacheKey("body:Fixture:M:RootController.Get"));
            return first.Admission.CacheKey("body:Fixture:M:RootController.Get");
        }
    }

    [Fact]
    public async Task Scope_without_a_package_deciding_call_is_analysed_as_before()
    {
        var program = new CallSiteTestProgram("state.Value++;");
        var observed = await Analyze(program);
        Assert.Equal(1, Assert.Single(observed.Measurements).Analyses);
        Assert.Empty(Assert.Single(observed.Measurements).Calls);
        var control = await Analyze(program, mode: CallSiteMode.Control);
        Assert.Equal(control.FinalPairs!.Pairs.Select(PairIdentity.Of), observed.FinalPairs!.Pairs.Select(PairIdentity.Of));
    }

    [Fact]
    public async Task Solver_runs_once_on_the_last_analysis()
    {
        var solver = new ObservingSolver();
        var observed = await Analyze(new CallSiteTestProgram("L.Invoke(() => state.Value++); state.Value++;"), solver);
        Assert.Equal(2, Assert.Single(observed.Measurements).Analyses);
        Assert.NotEmpty(observed.FinalPairs!.Pairs);
        Assert.Equal(1, solver.AvailabilityChecks);
        Assert.All(observed.FinalPairs.Pairs, pair => Assert.DoesNotContain(pair.GapChecks, check => check.Callee.Contains("L.Invoke", StringComparison.Ordinal)));
    }

    [Fact]
    public void Admitted_call_is_the_same_call_after_its_body_is_lowered_again()
    {
        var program = new CallSiteTestProgram("L.Out(out var value); L.Invoke(() => state.Value++);");
        var before = program.Run();
        var output = Assert.Single(CallSiteTestProgram.Calls(before, "L.Out"));
        var call = Assert.Single(CallSiteTestProgram.Calls(before, "L.Invoke"));
        var after = program.Run(CallSiteTestProgram.Admit(output));
        Assert.Equal(call, Assert.Single(CallSiteTestProgram.Calls(after, "L.Invoke")));
        var oldId = before.Reachable.Bodies[call.BodyId].CallIdentities.Single(pair => pair.Value == call).Key;
        var newId = after.Reachable.Bodies[call.BodyId].CallIdentities.Single(pair => pair.Value == call).Key;
        Assert.NotEqual(oldId, newId);
    }

    [Fact]
    public void Calls_sharing_a_span_stay_apart_and_only_the_selected_one_is_admitted()
    {
        var program = new CallSiteTestProgram("var a = new Policy(); var b = new Policy(); (a.Value, b.Value) = (1, 2);");
        var before = program.Run();
        var calls = CallSiteTestProgram.Calls(before, "Policy.set_Value");
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Span, calls[1].Span);
        Assert.Equal(calls[0].Target, calls[1].Target);
        Assert.NotEqual(calls[0].Ordinal, calls[1].Ordinal);
        var after = program.Run(CallSiteTestProgram.Admit(calls[0]));
        var body = after.Reachable.Bodies[calls[0].BodyId];
        var setters = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                          .Where(operation => operation.TargetMethodId == calls[0].Target).ToArray();
        Assert.Null(setters[0].Library);
        var summary = after.Summaries!.Get(calls[0].BodyId)!;
        Assert.Contains(summary.Calls, call => call.OperationId == setters[0].Id);
        Assert.Contains(summary.OpaqueCalls, call => call.OperationId == setters[1].Id);

        var enumeration = new CallSiteTestProgram("foreach (var value in new Seq()) { state.Value += value; }", library:
            CallSiteTestProgram.LIBRARY_SOURCE + "public class Seq { public Seq GetEnumerator() => this; public bool MoveNext() => false; public int Current => 0; }");
        var enumerated = enumeration.Run();
        var get = Assert.Single(CallSiteTestProgram.Calls(enumerated, "Seq.GetEnumerator"));
        var move = Assert.Single(CallSiteTestProgram.Calls(enumerated, "Seq.MoveNext"));
        Assert.Equal(get.Span, move.Span);
        Assert.NotEqual(get, move);
        var admitted = enumeration.Run(CallSiteTestProgram.Admit(get));
        Assert.Contains(move, CallSiteTestProgram.Calls(admitted, "Seq.MoveNext"));
        Assert.DoesNotContain(admitted.Heap!.ReachableBodies, bodyId => bodyId == move.Target);
    }

    private sealed record Observed(AnalysisResult Result, IReadOnlyList<CallSiteMeasurement> Measurements, PairAnalysis? FinalPairs);

    private static async Task<Observed> Analyze(CallSiteTestProgram program, IConstraintSolver? solver = null, CallSiteMode mode = CallSiteMode.Withhold,
                                                bool noInput = false)
    {
        var setup = new CallSiteAnalysis(string.Empty, new Version(), new Version(), new HashSet<string>()) { Mode = mode };
        PairAnalysis? final = null;
        var result = await PhaseOneAnalyzer.AnalyzeAsync(program.Solution, ROOT_DIRECTORY, ROOT_DIRECTORY, ProviderRegistry.BuiltIn,
            InterproceduralPairing.Pair, AnalysisLimits.Default, () => solver ?? new ObservingSolver(),
            new GenerationSetup(ModelGeneration.None) { LibraryTarget = (method, _) => program.Target(method) }, CancellationToken.None,
            noInput ? null : setup, (_, pairs) => final = pairs);
        return new(result, setup.Measurements, final);
    }

    private sealed class ObservingSolver : IConstraintSolver
    {
        public int Queries { get; private set; }
        public int AvailabilityChecks { get; private set; }
        public bool IsAvailable { get { AvailabilityChecks++; return true; } }
        public SolverOutcome Decide(SolverQuery query, SolverLimit limit)
        {
            Queries++;
            return SolverOutcome.Unknown("fixture");
        }
        public void Dispose() { }
    }
}
