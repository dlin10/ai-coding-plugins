using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The <c>not-run</c> fate and the holder's own run (R7, ADR 0015): a delegate the call was handed that fired nowhere, is not
/// kept, was handed to nothing the analysis cannot follow, and whose call reached no code the analysis did not see is <c>not-run</c>;
/// a holder stands only when a trigger ran the delegate it keeps.</summary>
public sealed class NotRunClassificationTests
{
    private static readonly ClassifiedFate HolderResult = new(FateClassifier.HOLDER, FateClassifier.RESULT);
    private static readonly ClassifiedFate Unknown = new(FateClassifier.UNKNOWN_EXECUTION, null);
    private static readonly ClassifiedFate NotRun = new(FateClassifier.NOT_RUN, null);

    [Fact]
    public void A_member_that_ignores_its_delegate_is_not_run_with_no_inputs_and_no_effect()
    {
        var trace = Trace("public static class Api { public static int Twice(Action a, int n) => n * 2; }", "M:Lib.Api.Twice(System.Action,System.Int32)");

        Assert.Equal(NotRun, FateOf(trace, "a"));
        var fate = Assert.Single(trace.Answer.Model!.Fates);
        Assert.Equal(new LibraryFate("a", LibraryFateKind.NotRun, null, null), fate);
        Assert.DoesNotContain(trace.Answer.Model.Effects, effect => effect.Parameter == "a");
    }

    [Fact]
    public void A_member_that_only_stores_an_in_delegate_in_a_field_is_unknown_execution_never_not_run()
    {
        // The driver hands an in delegate as a temporary the engine cannot name: the call carries no probe into the member, so nothing
        // shows the delegate is not kept.
        var trace = Trace("public sealed class Api { private Action _a; public void Run(in Action a) { _a = a; } }", "M:Lib.Api.Run(System.Action@)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_member_that_only_compares_its_delegate_with_null_is_not_run()
    {
        var trace = Trace("public static class Api { public static void Run(Action a) { if (a is null) return; } }", "M:Lib.Api.Run(System.Action)");

        Assert.Equal(NotRun, FateOf(trace, "a"));
    }

    [Fact]
    public void A_member_that_removes_its_delegate_from_a_field_with_minus_equals_is_not_run()
    {
        // A removal keeps only its left operand: the field never holds the handed delegate, which the receiver's Fire would run.
        var trace = Trace("public sealed class Api { private Action _h; public void Run(Action a) { _h -= a; } public void Fire() { _h?.Invoke(); } }",
                          "M:Lib.Api.Run(System.Action)");

        Assert.Equal(NotRun, FateOf(trace, "a"));
    }

    [Fact]
    public void A_member_with_a_dropped_lowering_in_a_reached_body_is_unknown_execution()
    {
        // No source form is known to make the lowering throw, so the run's diagnostics name a reached body as a dropped lowering would:
        // that body has no IR, and what it does with the delegate is unseen.
        var trace = Trace("public static class Api { public static void Run(Action a) { Helper(); } private static void Helper() { } }",
                          "M:Lib.Api.Run(System.Action)");
        Assert.Equal(NotRun, FateOf(trace, "a"));
        var helper = trace.Run!.Reachable.ReachedBodies.Keys.Single(body => body.EndsWith("Lib.Api.Helper", StringComparison.Ordinal));

        var dropped = trace.Run with { LoweringDiagnostics = [$"lowering: {helper}: injected"] };

        Assert.Equal(Unknown, FateClassifier.Classify(trace.Driver!, dropped).Classified["a"]);
    }

    [Fact]
    public void A_member_whose_reached_code_has_an_unresolved_dispatch_is_unknown_execution()
    {
        // The driver hands an in delegate as a temporary the engine cannot name: invoking it is a dispatch with no receiver object, which
        // may run anything — the other delegate included.
        var dispatched = Trace("public static class Api { public static void Run(Action a, in Action b) { b(); } }",
                               "M:Lib.Api.Run(System.Action,System.Action@)");
        var notDispatched = Trace("public static class Api { public static void Run(Action a, in Action b) { } }",
                                  "M:Lib.Api.Run(System.Action,System.Action@)");

        Assert.Equal(Unknown, FateOf(dispatched, "a"));
        Assert.Equal(NotRun, FateOf(notDispatched, "a"));
    }

    [Fact]
    public void A_member_with_an_unsupported_operation_in_a_reached_body_is_unknown_execution()
    {
        var unsupported = Trace("public static class Api { public static void Run(Action a) { Note(); } private static void Note() { var t = typeof(int); } }",
                                "M:Lib.Api.Run(System.Action)");
        var supported = Trace("public static class Api { public static void Run(Action a) { Note(); } private static void Note() { var t = 1; } }",
                              "M:Lib.Api.Run(System.Action)");

        Assert.Equal(Unknown, FateOf(unsupported, "a"));
        Assert.Equal(NotRun, FateOf(supported, "a"));
    }

    [Fact]
    public void A_holder_whose_members_never_run_the_delegate_is_unknown_execution()
    {
        // An options bag: it stores the delegate for other code, and no trigger runs it.
        var trace = Trace("""
            public sealed class Options { private Action _a; public Options(Action a) { _a = a; } public int Size; public void Reset() { Size = 0; } }
            public static class Api { public static Options Make(Action a) => new Options(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.DoesNotContain("a", trace.Answer.Generation.HolderTriggers.Keys);
    }

    [Fact]
    public void A_holder_whose_Fire_trigger_runs_the_delegate_stays_a_holder_naming_Fire()
    {
        var trace = Trace("public static class Api { public static Holder Keep(Action a) => new Holder(a); }", "M:Lib.Api.Keep(System.Action)");

        Assert.Equal(HolderResult, FateOf(trace, "a"));
        Assert.Equal(["M:Lib.Holder.Fire"], trace.Answer.Generation.HolderTriggers["a"]);
    }
}
