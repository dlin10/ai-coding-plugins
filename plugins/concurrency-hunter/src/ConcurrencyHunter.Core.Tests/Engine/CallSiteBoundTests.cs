using System.Reflection;
using ConcurrencyHunter.Analysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CallSiteBoundTests
{
    [Fact]
    public void Count_includes_the_bodies_of_the_producers()
    {
        var program = new CallSiteTestProgram("L.Make().Execute(() => state.Value++);");
        var run = program.Run();
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations);
        Assert.True(rounds.Grow(run, CallSiteTestProgram.Pairs(run), 1));
        var measurement = Assert.Single(rounds.Measurements);
        Assert.NotEmpty(measurement.Producers);
        Assert.True(measurement.BodyCount >= 3);
        Assert.Contains(rounds.Admission.Calls!.Keys, call => call.Target.Contains("L.Make", StringComparison.Ordinal));
    }

    [Fact]
    public void Body_two_calls_reach_counts_for_each()
    {
        var program = new CallSiteTestProgram("L.Make().Execute(() => state.Value++); L.Make().Execute(() => state.Value++);");
        var run = program.Run();
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations);
        Assert.True(rounds.Grow(run, CallSiteTestProgram.Pairs(run), 1));
        Assert.Equal(2, rounds.Measurements.Count);
        Assert.True(rounds.Measurements[0].BodyCount >= 3);
        Assert.Equal(rounds.Measurements[0].BodyCount, rounds.Measurements[1].BodyCount);
    }

    [Fact]
    public void Call_over_the_bound_is_not_admitted_and_keeps_its_gap()
    {
        var program = new CallSiteTestProgram("L.Make().Execute(() => state.Value++);");
        var run = program.Run();
        var pairs = CallSiteTestProgram.Pairs(run);
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations, bound: 1);
        Assert.False(rounds.Grow(run, pairs, 1));
        Assert.Empty(rounds.Admission.Calls!);
        Assert.Contains(pairs.Pairs, pair => pair.DecidingCalls.Any(call => call.Call?.Target.Contains("Policy.Execute", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Bound_is_a_multiple_of_500()
    {
        var bound = (int)typeof(CallSiteRounds).GetField("CALL_SITE_BODY_BOUND", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.True(bound > 0);
        Assert.Equal(0, bound % 500);
    }
}
