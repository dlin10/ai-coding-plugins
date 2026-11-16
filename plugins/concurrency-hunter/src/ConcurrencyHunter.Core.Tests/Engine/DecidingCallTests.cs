using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class DecidingCallTests
{
    [Fact]
    public void Operation_check_names_the_call_whose_effect_is_the_access() =>
        Names("L.Touch(state); state.Value++;", "L.Touch", GapComponents.OPERATION);

    [Fact]
    public void Unknown_delegate_overlap_names_the_call_the_delegate_was_handed_to() =>
        Names("L.Invoke(() => state.Value++);", "L.Invoke", GapComponents.OVERLAP);

    [Fact]
    public void Join_overlap_names_the_call_whose_result_is_the_handle() =>
        Names("var handle = token.IsCancellationRequested ? Task.Run(() => state.Value++) : L.Start(state); await handle; state.Value = 2;",
            "L.Start", GapComponents.OVERLAP, worker: true);

    [Fact]
    public void Current_pass_overlap_names_the_call_that_wraps_the_handle() =>
        Names("for (var pass = 0; pass < 2; pass++) { var own = Task.Run(() => state.Value++); await L.WrapTask(own, state); }",
            "L.WrapTask", GapComponents.OVERLAP, worker: true);

    [Fact]
    public void Continuation_overlap_names_the_call_whose_result_is_the_antecedent() =>
        Names("var task = Task.Run(() => state.Value++); var handle = token.IsCancellationRequested ? task : L.Start(state); " +
              "await handle.ContinueWith(_ => state.Value++);", "L.Start", GapComponents.OVERLAP, worker: true);

    [Fact]
    public void Timer_overlap_names_the_call_whose_result_is_the_timer() =>
        Names("var timer = token.IsCancellationRequested ? new System.Timers.Timer() : L.Timer(state); " +
              "timer.Elapsed += (_, _) => state.Value++;", "L.Timer", GapComponents.OVERLAP, worker: true);

    [Fact]
    public void Protection_names_the_call_whose_result_is_the_lock() =>
        Names("lock (L.Gate(state)) { state.Value++; }", "L.Gate", GapComponents.PROTECTION);

    [Fact]
    public void Two_calls_of_one_member_in_one_body_name_only_the_deciding_one()
    {
        var program = new CallSiteTestProgram("L.Touch(new object()); L.Touch(state); state.Value++;");
        var run = program.Run();
        var calls = CallSiteTestProgram.Calls(run, "L.Touch");
        Assert.Equal(2, calls.Count);
        var decisions = CallSiteTestProgram.Pairs(run).Pairs.SelectMany(pair => pair.DecidingCalls).Select(call => call.Call).ToHashSet();
        Assert.DoesNotContain(calls[0], decisions);
        Assert.Contains(calls[1], decisions);
    }

    [Fact]
    public void The_named_call_leaves_the_checks_uncertainty_and_the_fingerprint_as_they_were()
    {
        var program = new CallSiteTestProgram("L.Touch(state); state.Value++;");
        var run = program.Run();
        var pairs = CallSiteTestProgram.Pairs(run).Pairs;
        Assert.NotEmpty(pairs);
        foreach (var pair in pairs)
        {
            Assert.Equal(pair.GapChecks.Distinct(), pair.GapChecks);
            Assert.All(pair.DecidingCalls, call => Assert.Contains(call.Check, pair.GapChecks));
            Assert.Equal(PairIdentity.Of(pair), PairIdentity.Of(pair with { DecidingCalls = [] }));
            Assert.Equal(pair.GapChecks.Select(check => check.Uncertainty),
                         (pair with { DecidingCalls = [] }).GapChecks.Select(check => check.Uncertainty));
        }
        var before = ConflictFindings.Create(pairs.Select(pair => pair with { DecidingCalls = [] }), run.Collection!.Accesses, CancellationToken.None).Findings;
        var after = ConflictFindings.Create(pairs, run.Collection.Accesses, CancellationToken.None).Findings;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(after));
    }

    [Fact]
    public void Every_generation_target_answer_has_its_admission()
    {
        var expected = new Dictionary<string, string>
        {
            ["Source"] = "neither", ["OtherPath"] = "neither", ["FunctionPointer"] = "no-body", ["AmbiguousId"] = "no-body",
            ["NoFrameworkPack"] = "no-body", ["Forwarded"] = "no-body", ["Recognized"] = "recognized", ["CoreLib"] = "framework"
        };
        foreach (var reason in Enum.GetValues(typeof(NoTargetReason)).Cast<NoTargetReason>())
            Assert.Equal(expected[reason.ToString()], DecidingCalls.AnswerOf(reason));
        Assert.Equal("no-body", DecidingCalls.AnswerOf(new UnavailableGenerationTarget("M:X", "M:X", null!, "missing asset")));
        Assert.Null(DecidingCalls.AnswerOf(new GenerationTarget("M:X", "M:X", null!, null!)));
    }

    [Fact]
    public void Package_member_with_a_function_pointer_parameter_is_no_body()
    {
        var program = new CallSiteTestProgram("unsafe { PointerCalls.Pointer(null, state); } state.Value++;", library: CallSiteTestProgram.LIBRARY_SOURCE +
            "public static unsafe class PointerCalls { public static void Pointer(delegate*<void> pointer, object value) { } public static void Pointer(object value) { } }");
        var run = program.Run();
        using var compilations = new CallSiteCompilations();
        var call = Assert.Single(CallSiteTestProgram.Calls(run, "PointerCalls.Pointer"));
        Assert.Equal("no-body", program.Selector(compilations).Target(run, call, [program.Project]).Outcome);
        Assert.Contains(program.Selector(compilations).Of(run, CallSiteTestProgram.Pairs(run), [program.Project]),
                        target => target.Call == call && target.Outcome == "no-body");
    }

    [Fact]
    public void Member_of_a_recognized_package_type_keeps_its_recognizer()
    {
        var program = new CallSiteTestProgram("recognized::System.Threading.Monitor.Enter(state);", library: CallSiteTestProgram.LIBRARY_SOURCE +
            "namespace System.Threading { public static class Monitor { public static void Enter(object value) { } } }");
        var run = program.Run();
        using var compilations = new CallSiteCompilations();
        Assert.Empty(CallSiteTestProgram.Calls(run, "Monitor.Enter"));
        Assert.Contains(run.Reachable.Bodies.Values.SelectMany(body => body.Blocks).SelectMany(block => block.Operations),
                        operation => operation is IrAcquireOperation);
        Assert.Empty(program.Selector(compilations).Of(run, CallSiteTestProgram.Pairs(run), [program.Project]));
        Assert.DoesNotContain(run.Heap!.ReachableBodies, body => body.Contains("Monitor.Enter", StringComparison.Ordinal));
    }

    private static void Names(string work, string member, string component, bool worker = false)
    {
        var program = new CallSiteTestProgram(work, worker: worker);
        var run = program.Run();
        var call = Assert.Single(CallSiteTestProgram.Calls(run, member));
        var decisions = CallSiteTestProgram.Pairs(run).Pairs.SelectMany(pair => pair.DecidingCalls).ToArray();
        Assert.Contains(decisions, decision => decision.Call == call && decision.Check.Component == component);
        Assert.All(decisions.Where(decision => decision.Check.Callee.Contains(member, StringComparison.Ordinal)), decision => Assert.Equal(call, decision.Call));
    }
}
