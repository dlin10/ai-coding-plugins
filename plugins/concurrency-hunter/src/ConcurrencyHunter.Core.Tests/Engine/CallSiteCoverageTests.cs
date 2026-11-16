using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Reporting;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CallSiteCoverageTests
{
    [Fact]
    public async Task Call_that_decides_only_before_the_generated_layer_settles_is_not_listed()
    {
        var result = await DemoExpectationTests.SharedDemo.Value;
        var coverage = Assert.Single(result.Coverage, scope => scope.ScopeId == "Demo.Web").CallSiteAnalysis;
        Assert.DoesNotContain(coverage.Calls, call => call.Callee.Contains("ProtoPreconditions.CheckNotNull", StringComparison.Ordinal));
        var admitted = Assert.Single(coverage.Calls, call => call.Callee.Contains("ResiliencePipeline.Execute", StringComparison.Ordinal));
        Assert.Equal(("answered", 270), (admitted.Outcome, admitted.Bodies));
        Assert.Equal(3, coverage.Analyses);
        Assert.Equal(coverage.Analyses, Assert.Single(result.ScopeSizes, scope => scope.ScopeId == "Demo.Web").Seconds.Count);
        Assert.Equal((0, 0), (coverage.PairsRemoved, coverage.PairsAdded));

        var program = new CallSiteTestProgram("Console.WriteLine(state); state.Value++;");
        foreach (var mode in new[] { ModelGeneration.None, ModelGeneration.ReadOnly })
        {
            var unchanged = await PhaseOneAnalyzer.AnalyzeAsync(program.Solution, ROOT_DIRECTORY, ROOT_DIRECTORY, ProviderRegistry.BuiltIn,
                InterproceduralPairing.Pair, AnalysisLimits.Default, () => new UnknownSolver(),
                new GenerationSetup(mode) { LibraryTarget = (method, _) => program.Target(method) }, CancellationToken.None);
            var section = Assert.Single(unchanged.Coverage).CallSiteAnalysis;
            var call = Assert.Single(section.Calls);
            Assert.Equal(("System.Console.WriteLine(object)", "framework"), (call.Callee, call.Outcome));
            Assert.Equal(1, section.Analyses);
            Assert.Equal((0, 0), (section.PairsRemoved, section.PairsAdded));
        }
    }

    [Fact]
    public async Task Deciding_call_inside_library_code_is_not_listed_and_stays_a_deciding_gap()
    {
        var program = new CallSiteTestProgram("L.Invoke(() => state.Value++); Steps.Invoke(() => Steps.Invoke(() => state.Value++));", library:
            CallSiteTestProgram.LIBRARY_SOURCE.Replace("public static void Invoke(Action action) { action(); }",
                "public static void Invoke(Action action) { global::Other.Accept(action); }", StringComparison.Ordinal) +
                "public static class Steps { public static void Invoke(Action action) { action(); } }",
            dependency: "public static class Other { public static void Accept(System.Action action) { action(); } }");
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations);
        var first = program.Run();
        Assert.True(rounds.Grow(first, CallSiteTestProgram.Pairs(first), 1));
        var middle = program.Run(rounds.Admission);
        Assert.Contains(CallSiteTestProgram.Pairs(middle).Pairs.SelectMany(pair => pair.DecidingCalls),
            decision => decision.Call is { } call && rounds.Admission.IsLibraryBody(call.BodyId) &&
                        call.Target.Contains("Other.Accept", StringComparison.Ordinal));
        var result = await Analyze(program);
        var scope = Assert.Single(result.Coverage);
        Assert.Equal(3, scope.CallSiteAnalysis.Analyses);
        Assert.Contains(scope.CallSiteAnalysis.Calls, call => call.Callee.Contains("L.Invoke", StringComparison.Ordinal));
        Assert.DoesNotContain(scope.CallSiteAnalysis.Calls, call => call.Callee.Contains("Other.Accept", StringComparison.Ordinal));
        var gap = Assert.Single(scope.DecidingGaps, gap => gap.Callee.Contains("Other.Accept", StringComparison.Ordinal));
        Assert.Equal(["CallSiteLibrary:M:L.Invoke(System.Action)"], gap.CallSites);
        Assert.NotEmpty(gap.Findings);
    }

    [Fact]
    public async Task Answered_call_is_listed_with_its_producers_and_count()
    {
        var program = new CallSiteTestProgram("L.Make().Execute(() => state.Value++);");
        var result = await Analyze(program);
        var coverage = Assert.Single(result.Coverage).CallSiteAnalysis;
        var call = Assert.Single(coverage.Calls);
        Assert.Contains("Policy.Execute", call.Callee, StringComparison.Ordinal);
        Assert.Equal("answered", call.Outcome);
        Assert.StartsWith("Case.cs:", call.Place, StringComparison.Ordinal);
        Assert.Equal([call.Place], call.Producers);
        Assert.True(call.Bodies >= 3);
        Assert.Equal(2, coverage.Analyses);
        Assert.Equal(coverage.Analyses, Assert.Single(result.ScopeSizes).Seconds.Count);
        Assert.All(Assert.Single(result.ScopeSizes).Seconds, seconds => Assert.True(seconds >= 0));
    }

    [Fact]
    public void Admitted_call_that_still_decides_is_no_answer()
    {
        var program = new CallSiteTestProgram("L.Touch(state); state.Value++;");
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations);
        var first = program.Run();
        var pairs = CallSiteTestProgram.Pairs(first);
        var call = Assert.Single(CallSiteTestProgram.Calls(first, "L.Touch"));
        Assert.True(rounds.Grow(first, pairs, 1));
        var last = program.Run(rounds.Admission);
        // Retain a deciding check in the last analysis, independently of whether this small library answers it.
        var lastPairs = Decides(CallSiteTestProgram.Pairs(last), call);
        var coverage = rounds.Coverage(2, last, lastPairs, rounds.Admission, Identities(pairs));
        Assert.Equal("no-answer", Assert.Single(coverage.Calls).Outcome);
        Assert.Equal("framework", CallSiteOutcomes.Of("framework", true, true, true));
        Assert.Equal("no-body", CallSiteOutcomes.Of("no-body", true, true, true));
    }

    [Fact]
    public void Call_over_the_bound_is_listed_as_bound()
    {
        var program = new CallSiteTestProgram("L.Make().Execute(() => state.Value++);");
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations, bound: 1);
        var run = program.Run();
        var pairs = CallSiteTestProgram.Pairs(run);
        Assert.False(rounds.Grow(run, pairs, 1));
        var coverage = rounds.Coverage(1, run, pairs, rounds.Admission, Identities(pairs));
        var call = Assert.Single(coverage.Calls);
        Assert.Equal("bound", call.Outcome);
        Assert.True(call.Bodies > 1);
        Assert.NotEmpty(call.Producers);
        Assert.Equal((0, 0), (coverage.PairsRemoved, coverage.PairsAdded));
    }

    [Fact]
    public async Task Dispatching_call_is_no_body()
    {
        var program = new CallSiteTestProgram("new Policy().Touch(state); state.Value++;", library:
            CallSiteTestProgram.LIBRARY_SOURCE.Replace("public virtual Policy Virtual() => this;", "public virtual void Touch(object value) { }", StringComparison.Ordinal));
        var result = await Analyze(program);
        var call = Assert.Single(Assert.Single(result.Coverage).CallSiteAnalysis.Calls, call => call.Callee.Contains("Policy.Touch", StringComparison.Ordinal));
        Assert.Equal("no-body", call.Outcome);
        Assert.Equal(0, call.Bodies);
    }

    [Fact]
    public async Task Framework_member_is_listed_and_never_counted()
    {
        var result = await Analyze(new CallSiteTestProgram("Console.WriteLine(state); state.Value++;"));
        var coverage = Assert.Single(result.Coverage).CallSiteAnalysis;
        var call = Assert.Single(coverage.Calls);
        Assert.Equal("System.Console.WriteLine(object)", call.Callee);
        Assert.Equal("framework", call.Outcome);
        Assert.Equal(0, call.Bodies);
        Assert.Equal(1, coverage.Analyses);
        Assert.Equal((0, 0), (coverage.PairsRemoved, coverage.PairsAdded));
    }

    [Fact]
    public async Task Call_first_deciding_in_the_last_round_is_last_round()
    {
        var result = await Analyze(new CallSiteTestProgram("L.Invoke(() => L.Invoke(() => L.Invoke(() => L.Invoke(() => state.Value++))));"));
        var coverage = Assert.Single(result.Coverage).CallSiteAnalysis;
        Assert.Equal(4, coverage.Analyses);
        Assert.Equal(4, coverage.Calls.Count);
        var last = Assert.Single(coverage.Calls, call => call.Outcome == "last-round");
        Assert.Equal(0, last.Bodies);
        Assert.Equal(3, coverage.Calls.Count(call => call.Outcome == "answered"));
    }

    [Fact]
    public void Producer_admitted_earlier_that_decides_in_the_last_analysis_is_no_answer_not_last_round()
    {
        var program = new CallSiteTestProgram("L.Invoke(() => L.Invoke(() => _policy.Execute(() => state.Value++)));", "private Policy _policy = L.Make();");
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations);
        var run = program.Run();
        var layer = CallSiteTestProgram.Pairs(run);
        var producer = Assert.Single(CallSiteTestProgram.Calls(run, "L.Make"));
        for (var round = 1; round <= 3; round++)
        {
            var pairs = CallSiteTestProgram.Pairs(run);
            Assert.DoesNotContain(pairs.Pairs.SelectMany(pair => pair.DecidingCalls), decision => decision.Call == producer);
            Assert.True(rounds.Grow(run, pairs, round));
            Assert.Contains(producer, rounds.Admission.Calls!.Keys);
            run = program.Run(rounds.Admission);
        }
        var last = Decides(CallSiteTestProgram.Pairs(run), producer);
        var coverage = rounds.Coverage(4, run, last, rounds.Admission, Identities(layer));
        var call = Assert.Single(coverage.Calls, call => call.Callee.Contains("L.Make", StringComparison.Ordinal));
        Assert.Equal("no-answer", call.Outcome);
        Assert.Equal(0, call.Bodies);
        Assert.DoesNotContain(coverage.Calls, call => call.Outcome == "last-round");
    }

    [Fact]
    public void Pairs_removed_and_added_compare_the_layer_analysis_and_the_last()
    {
        var program = new CallSiteTestProgram("Calls.Fire(() => state.Value++); L.Touch(state); state.Value++;", library:
            CallSiteTestProgram.LIBRARY_SOURCE + "public static class Calls { private static int _value; public static void Fire(Action action) { _value++; action(); } }");
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations);
        var run = program.Run();
        var layer = CallSiteTestProgram.Pairs(run);
        Assert.True(rounds.Grow(run, layer, 1));
        var last = program.Run(rounds.Admission);
        var pairs = CallSiteTestProgram.Pairs(last);
        var coverage = rounds.Coverage(2, last, pairs, rounds.Admission, Identities(layer));
        Assert.Equal(Identities(layer).Except(Identities(pairs)).Count(), coverage.PairsRemoved);
        Assert.Equal(Identities(pairs).Except(Identities(layer)).Count(), coverage.PairsAdded);
        Assert.True(coverage.PairsRemoved > 0);
        Assert.True(coverage.PairsAdded > 0);
        var settled = rounds.Coverage(3, last, pairs, rounds.Admission, Identities(pairs));
        Assert.Equal((0, 0), (settled.PairsRemoved, settled.PairsAdded));
    }

    [Fact]
    public async Task Calls_are_ordered_by_callee_then_place()
    {
        var program = new CallSiteTestProgram("L.Touch(state);\n" + new string('\n', 8) + "L.Touch(state); L.Invoke(() => state.Value++);");
        var result = await Analyze(program);
        var calls = Assert.Single(result.Coverage).CallSiteAnalysis.Calls;
        Assert.Equal(3, calls.Count);
        Assert.Contains("L.Invoke", calls[0].Callee, StringComparison.Ordinal);
        var touches = calls.Where(call => call.Callee.Contains("L.Touch", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, touches.Length);
        Assert.True(Line(touches[0].Place) < Line(touches[1].Place));

        var setters = new CallSiteTestProgram("var a = L.Make();\nvar b = L.Other();\n(a.Value, b.Value) = (1, 2); state.Value++;");
        var run = setters.Run();
        var identities = CallSiteTestProgram.Calls(run, "Policy.set_Value");
        Assert.Equal(2, identities.Count);
        using var compilations = new CallSiteCompilations();
        var rounds = setters.Rounds(compilations, bound: 1);
        var pairs = Decides(CallSiteTestProgram.Pairs(run), identities.Reverse().ToArray());
        rounds.Grow(run, pairs, 1);
        var coverage = rounds.Coverage(1, run, pairs, rounds.Admission, Identities(pairs));
        Assert.Equal(2, coverage.Calls.Count);
        Assert.Equal(coverage.Calls[0].Place, coverage.Calls[1].Place);
        var make = Assert.Single(CallSiteTestProgram.Calls(run, "L.Make"));
        var other = Assert.Single(CallSiteTestProgram.Calls(run, "L.Other"));
        Assert.Equal([$"{make.Span.Path}:{make.Span.StartLine}"], coverage.Calls[0].Producers);
        Assert.Equal([$"{other.Span.Path}:{other.Span.StartLine}"], coverage.Calls[1].Producers);
    }

    [Fact]
    public async Task Report_lists_the_calls_after_the_deciding_gaps()
    {
        var result = await Analyze(new CallSiteTestProgram("L.Make().Execute(() => state.Value++); Console.WriteLine(state);"));
        var coverage = Assert.Single(result.Coverage).CallSiteAnalysis;
        var report = ReportRenderer.Render(ReportingTestData.CreateReport(result)).ReportMarkdown;
        Assert.True(report.IndexOf("  - Call-site analysis:", StringComparison.Ordinal) > report.IndexOf("  - Deciding gaps:", StringComparison.Ordinal));
        Assert.Contains($"analyses {coverage.Analyses}; pairs removed {coverage.PairsRemoved}, added {coverage.PairsAdded}", report, StringComparison.Ordinal);
        foreach (var call in coverage.Calls)
        {
            Assert.Contains($"{call.Callee}; {call.Place}; {call.Outcome}; bodies {call.Bodies}; producers:", report, StringComparison.Ordinal);
            Assert.All(call.Producers, producer => Assert.Contains(producer, report, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Scope_without_calls_has_an_empty_section_and_no_report_block()
    {
        var result = await Analyze(new CallSiteTestProgram("state.Value++;"));
        var coverage = Assert.Single(result.Coverage).CallSiteAnalysis;
        Assert.Empty(coverage.Calls);
        Assert.Equal(1, coverage.Analyses);
        Assert.Equal((0, 0), (coverage.PairsRemoved, coverage.PairsAdded));
        Assert.DoesNotContain("Call-site analysis:", ReportRenderer.Render(ReportingTestData.CreateReport(result)).ReportMarkdown, StringComparison.Ordinal);
    }

    private static int Line(string place) => int.Parse(place[(place.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlySet<string> Identities(PairAnalysis pairs) => pairs.Pairs.Select(PairIdentity.Of).ToHashSet(StringComparer.Ordinal);

    private static PairAnalysis Decides(PairAnalysis pairs, params CallIdentity[] calls) => pairs with
    {
        Pairs = [pairs.Pairs.First() with { DecidingCalls = calls.Select(call => new DecidingCall(
            new GapCheck(call.Target, "unknown-library", GapComponents.OPERATION, "fixture decision"), call)).ToArray() }]
    };

    internal static Task<AnalysisResult> Analyze(CallSiteTestProgram program) =>
        PhaseOneAnalyzer.AnalyzeAsync(program.Solution, ROOT_DIRECTORY, ROOT_DIRECTORY, ProviderRegistry.BuiltIn,
            InterproceduralPairing.Pair, AnalysisLimits.Default, () => new UnknownSolver(),
            new GenerationSetup(ModelGeneration.None) { LibraryTarget = (method, _) => program.Target(method) }, CancellationToken.None);

    private sealed class UnknownSolver : IConstraintSolver
    {
        public bool IsAvailable => false;
        public SolverOutcome Decide(SolverQuery query, SolverLimit limit) => SolverOutcome.Unknown("fixture");
        public void Dispose() { }
    }
}
